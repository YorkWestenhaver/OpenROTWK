// The deterministic weapon fire-timing core - GPL Weapon's reload/clip/status machine
// rebuilt on logic frames and Fix64 (generals-gpl GeneralsMD GameLogic/Object/Weapon.cpp:
// Weapon ctor / getStatus / privateFireWeapon / reloadWithBonus / setClipPercentFull /
// onWeaponBonusChange / getPercentReadyToFire; WeaponTemplate::getDelayBetweenShots /
// getClipReloadTime. Semantics only; fresh code).
//
// DESIGN (the surface future WeaponModule ports call):
//   - One SimWeapon per (object, weapon slot). It owns ONLY timing/ammo state; targeting,
//     range checks, projectiles and damage delivery belong to the calling module and
//     DamagePipeline.
//   - All time is LogicFrame/LogicFrameSpan at the title's logic rate; the template's
//     durations were quantized at parse (RangeDuration is already LogicFrameSpan).
//   - Methods take `now` (the caller's Context.CurrentFrame) and, where the original
//     draws randomness, an ISimRandom - so draw COUNTS match GPL call-for-call:
//     a random delay is drawn only when min != max (GPL guard), once per shot.
//   - Rate-of-fire bonuses arrive as a Fix64 multiplier (default One). GPL divides the
//     delay by the bonus and floors (getDelayBetweenShots/getClipReloadTime); we do the
//     same division in Fix64 and floor to whole frames.
//   - The weapon-bonus SYSTEM (WeaponBonus condition flags -> field multipliers) is not
//     ported yet: callers pass the multiplier. When the bonus system ports, it computes
//     the Fix64 multiplier and nothing here changes.
//
// Status is DERIVED from frames exactly like GPL Weapon::getStatus(): PreAttack while
// now < preAttackFinished; then ready/out-of-ammo once now >= whenWeCanFireAgain.
//
// Deviations from GPL (recorded in research/systems/weapon-damage-armor.md):
//   - OUT_OF_AMMO's "never" sentinel is LogicFrame.MaxValue (GPL: 0x7fffffff) - same
//     unreachable-frame effect, no magic constant collision with real frames.
//   - Barrel count comes from the caller (GPL reads the Drawable - a client object the
//     sim may not touch); default 1.
//   - BFME2 ClipReloadTime/DelayBetweenShots are Min:Max ranges (ZH had scalars);
//     the ranged reload draw mirrors the delay draw (flagged as a Ghidra gap).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>Weapon readiness, GPL <c>WeaponStatus</c>.</summary>
public enum SimWeaponStatus
{
    ReadyToFire,
    OutOfAmmo,
    BetweenFiringShots,
    ReloadingClip,
    PreAttack,
}

[SimState]
public sealed class SimWeapon
{
    /// <summary>GPL NO_MAX_SHOTS_LIMIT.</summary>
    public const int NoMaxShotsLimit = int.MaxValue;

    private readonly WeaponTemplate _template;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private int _ammoInClip;
    private SimWeaponStatus _status;
    private LogicFrame _whenWeCanFireAgain;
    private LogicFrame _whenPreAttackFinished;
    private LogicFrame _whenLastReloadStarted;
    private LogicFrame _lastFireFrame;
    private int _maxShotCount = NoMaxShotsLimit;

    /// <summary>
    /// Weapons start EMPTY; the first reload is free of delay (GPL Weapon ctor:
    /// "Weapons start empty; you must reload before use. (however, there is no delay
    /// for reloading the first time.)").
    /// </summary>
    public SimWeapon(WeaponTemplate template)
    {
        _template = template;
        _status = SimWeaponStatus.OutOfAmmo;
        _ammoInClip = 0;
        _whenWeCanFireAgain = LogicFrame.Zero;
        _whenPreAttackFinished = LogicFrame.Zero;
        _whenLastReloadStarted = LogicFrame.Zero;
        _lastFireFrame = LogicFrame.Zero;
    }

    public WeaponTemplate Template => _template;

    public int AmmoInClip => _ammoInClip;

    /// <summary>Clip size 0 means unlimited ammo (GPL: clip filled to 0x7fffffff).</summary>
    public bool UsesClip => _template.ClipSize > 0;

    public LogicFrame WhenWeCanFireAgain => _whenWeCanFireAgain;

    public LogicFrame WhenLastReloadStarted => _whenLastReloadStarted;

    public LogicFrame LastFireFrame => _lastFireFrame;

    /// <summary>Shots remaining before the current attack order is exhausted (AI budget).</summary>
    public int MaxShotCount
    {
        get => _maxShotCount;
        set => _maxShotCount = value;
    }

    /// <summary>
    /// GPL <c>Weapon::getStatus()</c>: PRE_ATTACK while inside the pre-attack window,
    /// otherwise ready/out-of-ammo the moment the fire-again frame arrives. Like the
    /// original, the stored status is refreshed as a side effect.
    /// </summary>
    public SimWeaponStatus GetStatus(LogicFrame now)
    {
        if (now < _whenPreAttackFinished)
        {
            return SimWeaponStatus.PreAttack;
        }

        if (now >= _whenWeCanFireAgain)
        {
            _status = _ammoInClip > 0 ? SimWeaponStatus.ReadyToFire : SimWeaponStatus.OutOfAmmo;
        }

        return _status;
    }

    /// <summary>
    /// Fires one round: the GPL <c>privateFireWeapon</c> ammo/rescheduling core. The caller
    /// has already validated target/range and delivers the shot's payload (nuggets,
    /// projectile, DamagePipeline) itself. Returns true when the clip auto-reloaded
    /// (GPL return value).
    /// </summary>
    /// <param name="now">The caller's current logic frame.</param>
    /// <param name="random">Logic RNG stream, for the delay-between-shots draw.</param>
    /// <param name="rateOfFireMultiplier">
    /// WeaponBonus RATE_OF_FIRE field; bigger = faster (delays are DIVIDED by it, GPL
    /// getDelayBetweenShots). Pass Fix64.One when no bonus applies.
    /// </param>
    public bool FireShot(LogicFrame now, ISimRandom random, Fix64 rateOfFireMultiplier)
    {
        if (GetStatus(now) != SimWeaponStatus.ReadyToFire)
        {
            return false;
        }

        if (_ammoInClip <= 0)
        {
            return false;
        }

        _lastFireFrame = now;
        _ammoInClip--;
        if (_maxShotCount != NoMaxShotsLimit)
        {
            _maxShotCount--;
        }

        var reloaded = false;
        if (_ammoInClip <= 0)
        {
            if (_template.AutoReloadsClip != WeaponReloadType.None)
            {
                Reload(now, random, rateOfFireMultiplier, loadInstantly: false);
                reloaded = true;
            }
            else
            {
                _status = SimWeaponStatus.OutOfAmmo;
                _whenWeCanFireAgain = LogicFrame.MaxValue;
            }
        }
        else
        {
            _status = SimWeaponStatus.BetweenFiringShots;
            var delay = GetDelayBetweenShots(random, rateOfFireMultiplier);
            _whenLastReloadStarted = now;
            _whenWeCanFireAgain = now + delay;
        }

        return reloaded;
    }

    /// <summary>
    /// GPL <c>Weapon::reloadWithBonus</c>: refill the clip (0 = unlimited) and hold
    /// fire for the (bonus-divided) reload time. A full clip does not restart its
    /// reload delay.
    /// </summary>
    public void Reload(LogicFrame now, ISimRandom random, Fix64 rateOfFireMultiplier, bool loadInstantly)
    {
        if (_template.ClipSize > 0 && _ammoInClip == _template.ClipSize)
        {
            // Don't restart our reload delay. (The GPL shared-reload-timers exception is
            // the caller's concern: WeaponSet-level sharing re-stamps via SetSharedReload.)
            return;
        }

        _ammoInClip = _template.ClipSize > 0 ? _template.ClipSize : int.MaxValue;

        _status = SimWeaponStatus.ReloadingClip;
        var reloadTime = loadInstantly ? LogicFrameSpan.Zero : GetClipReloadTime(random, rateOfFireMultiplier);
        _whenLastReloadStarted = now;
        _whenWeCanFireAgain = now + reloadTime;
    }

    /// <summary>
    /// The GPL shared-reload-timers hook (<c>setPossibleNextShotFrame</c> +
    /// <c>setStatus</c>): when the owner shares reload time across its weapon set, the
    /// set walks every weapon and stamps the reloading weapon's schedule onto it.
    /// </summary>
    public void SetSharedReload(LogicFrame whenWeCanFireAgain, SimWeaponStatus status)
    {
        _whenWeCanFireAgain = whenWeCanFireAgain;
        _status = status;
    }

    /// <summary>
    /// GPL <c>Weapon::setClipPercentFull</c>: ammo = floor(clipSize * percent); only
    /// upward changes apply unless <paramref name="allowReduction"/>.
    /// (GPL note: its status assignment is inverted - ammo != 0 gets OUT_OF_AMMO - and
    /// getStatus() immediately repairs it on the next query; we set the repaired value.)
    /// </summary>
    public void SetClipPercentFull(LogicFrame now, Fix64 percent, bool allowReduction)
    {
        if (_template.ClipSize == 0)
        {
            return;
        }

        var ammo = (int)(long)Fix64.Floor(new Fix64(_template.ClipSize) * percent);
        if (ammo > _ammoInClip || (allowReduction && ammo < _ammoInClip))
        {
            _ammoInClip = ammo;
            _status = _ammoInClip > 0 ? SimWeaponStatus.ReadyToFire : SimWeaponStatus.OutOfAmmo;
            _whenLastReloadStarted = now;
            _whenWeCanFireAgain = now;
        }
    }

    /// <summary>
    /// GPL <c>Weapon::preFireWeapon</c> timing half: hold in PRE_ATTACK until
    /// now + delay. The caller computes the (bonus-multiplied) delay - GPL
    /// getPreAttackDelay MULTIPLIES by the PRE_ATTACK bonus field.
    /// </summary>
    public void StartPreAttack(LogicFrame now, LogicFrameSpan delay)
    {
        if (delay > LogicFrameSpan.Zero)
        {
            _status = SimWeaponStatus.PreAttack;
            _whenPreAttackFinished = now + delay;
        }
    }

    /// <summary>
    /// GPL <c>Weapon::onWeaponBonusChange</c>: a rate-of-fire change mid-delay restarts
    /// the current wait with the new delay (reload or between-shots; other states are
    /// untouched).
    /// </summary>
    public void OnRateOfFireChange(LogicFrame now, ISimRandom random, Fix64 rateOfFireMultiplier)
    {
        LogicFrameSpan newDelay;
        switch (GetStatus(now))
        {
            case SimWeaponStatus.ReloadingClip:
                newDelay = GetClipReloadTime(random, rateOfFireMultiplier);
                break;
            case SimWeaponStatus.BetweenFiringShots:
                newDelay = GetDelayBetweenShots(random, rateOfFireMultiplier);
                break;
            default:
                return;
        }

        _whenLastReloadStarted = now;
        _whenWeCanFireAgain = now + newDelay;
    }

    /// <summary>
    /// GPL <c>Weapon::getPercentReadyToFire</c> as an exact Fix64 ratio in [0, 1]
    /// (display/AI ordering value; the original returns float).
    /// </summary>
    public Fix64 GetPercentReadyToFire(LogicFrame now)
    {
        switch (GetStatus(now))
        {
            case SimWeaponStatus.OutOfAmmo:
            case SimWeaponStatus.PreAttack:
                return Fix64.Zero;

            case SimWeaponStatus.ReadyToFire:
                return Fix64.One;

            default:
                {
                    var nextShot = _whenWeCanFireAgain;
                    if (now >= nextShot)
                    {
                        return Fix64.One;
                    }

                    var totalTime = nextShot - _whenLastReloadStarted;
                    if (totalTime == LogicFrameSpan.Zero)
                    {
                        return Fix64.One;
                    }

                    var timeLeft = nextShot - now;
                    var timeSoFar = totalTime - timeLeft;
                    if (timeSoFar >= totalTime)
                    {
                        return Fix64.One;
                    }

                    return new Fix64((int)timeSoFar.Value) / new Fix64((int)totalTime.Value);
                }
        }
    }

    /// <summary>
    /// GPL <c>WeaponTemplate::getDelayBetweenShots</c>: uniform draw in [min, max]
    /// frames - drawn ONLY when min != max (the GPL guard, preserving draw counts) -
    /// then floor(delay / rateOfFireMultiplier).
    /// </summary>
    private LogicFrameSpan GetDelayBetweenShots(ISimRandom random, Fix64 rateOfFireMultiplier)
    {
        return DrawAndDivide(_template.CoolDownDelayBetweenShots, random, rateOfFireMultiplier);
    }

    /// <summary>
    /// GPL <c>WeaponTemplate::getClipReloadTime</c>: floor(reload / bonus). ZH's reload
    /// time is a scalar; BFME2 data may carry Min:Max (range draw mirrors the shot
    /// delay - Ghidra gap, see the design note).
    /// </summary>
    private LogicFrameSpan GetClipReloadTime(ISimRandom random, Fix64 rateOfFireMultiplier)
    {
        return DrawAndDivide(_template.ClipReloadTime, random, rateOfFireMultiplier);
    }

    private static LogicFrameSpan DrawAndDivide(in RangeDuration range, ISimRandom random, Fix64 rateOfFireMultiplier)
    {
        uint frames;
        if (range.Min == range.Max)
        {
            frames = range.Min.Value;
        }
        else
        {
            frames = (uint)random.Next((int)range.Min.Value, (int)range.Max.Value);
        }

        if (rateOfFireMultiplier == Fix64.One || frames == 0)
        {
            return new LogicFrameSpan(frames);
        }

        // Floor(frames / bonus): GPL REAL_TO_INT_FLOOR(delayToUse / bonusROF).
        var divided = new Fix64((int)frames) / rateOfFireMultiplier;
        var floored = (long)Fix64.Floor(divided);
        if (floored < 0)
        {
            floored = 0;
        }
        return new LogicFrameSpan((uint)floored);
    }

    // ---- the single walk (save/load + CRC + deep-dump). Field order = declaration
    // order = OUR choice (F9), never the original's. The engine-side owner (a future
    // WeaponModule) calls this from its own Xfer. ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("AmmoInClip", ref _ammoInClip);
        xfer.XferEnum("Status", ref _status);
        xfer.XferFrame("WhenWeCanFireAgain", ref _whenWeCanFireAgain);
        xfer.XferFrame("WhenPreAttackFinished", ref _whenPreAttackFinished);
        xfer.XferFrame("WhenLastReloadStarted", ref _whenLastReloadStarted);
        xfer.XferFrame("LastFireFrame", ref _lastFireFrame);
        xfer.XferInt("MaxShotCount", ref _maxShotCount);
    }
}
