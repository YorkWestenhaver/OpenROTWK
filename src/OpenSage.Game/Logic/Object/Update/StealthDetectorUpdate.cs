// StealthDetectorUpdate - R9 module port (experiment-round-4 §4.1, template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD StealthDetectorUpdate.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). Behavior facts used:
//   - ctor: an enabled detector staggers its first scan across [1, DetectionRate] frames
//     drawn from the logic stream (S3) so a cluster does not scan in lockstep; a disabled
//     one sleeps forever until enabled.
//   - update(): dead => sleep forever; under construction => wake next frame (detect the
//     instant we finish); sold => sleep forever. A detector inside a container detects only
//     when its INI permits it (CanDetectWhileGarrisoned / CanDetectWhileContained).
//   - the scan: iterate objects within DetectionRange (or the object's vision range when
//     DetectionRange is 0, the GPL "backwards compatible" default), keep the ENEMY and
//     NEUTRAL ones (never allies), keep the STEALTHED ones, and reveal them.
//   - reveal = GPL stealth->markAsDetected: set the target's DETECTED status bit. That bit
//     lives on GameObject's own persisted status, shared across every detector, so it is
//     NOT part of this module's Xfer (mirrors WeaponBonusUpgrade, whose effect also rides
//     the object). Its timed decay + re-stealth is StealthUpdate's job (still a stub) - see
//     research/modules-r9/StealthDetectorUpdate.md finding F-SDU-5.
//   - the IR ping/heat-vision particle systems, the PingSound / LoudPingSound, the radar
//     events and the "MESSAGE:StealthDiscovered" UI text are all client outputs (S8) with no
//     determinism obligation and are deliberately not driven from sim code.
//
// This file is [SimState]: SIMCORE001-010 run here as errors, so every distance/range is
// Fix64 and no float ever appears in the scan. The single Xfer walk carries the module's
// whole mutable inventory (just the enabled flag); tolerances are the field's conformance
// class at its declaration site (§4).

using OpenSage.Audio;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class StealthDetectorUpdate : UpdateModule
{
    private readonly StealthDetectorUpdateModuleData _moduleData;

    /// <summary>Whether the detector is scanning (GPL m_enabled). The whole mutable inventory.</summary>
    public bool Active;

    public StealthDetectorUpdate(GameObject gameObject, ISimContext context, StealthDetectorUpdateModuleData moduleData)
        : base(gameObject, context)
    {
        _moduleData = moduleData;
        Active = !_moduleData.InitiallyDisabled;

        if (Active)
        {
            // GPL ctor: random phasing of the first scan across [1, DetectionRate], from
            // the logic stream (S3) so the stagger is lockstep-identical on every peer.
            var rate = (int)_moduleData.DetectionRate.Value;
            if (rate > 1)
            {
                var stagger = Context.GameLogicRandom.Next(1, rate);
                SetWakeFrame(UpdateSleepTime.Frames(new LogicFrameSpan((uint)stagger)));
            }
            else
            {
                SetWakeFrame(UpdateSleepTime.None);
            }
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public override UpdateSleepTime Update()
    {
        if (!Active)
        {
            return UpdateSleepTime.Forever;
        }

        var self = GameObject;

        // Dead detectors never wake again.
        if (self.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }

        // Still under construction: keep checking every frame; we detect the moment we finish.
        if (self.TestStatus(ObjectStatus.UnderConstruction))
        {
            return UpdateSleepTime.None;
        }

        // Sold: shut down forever.
        if (self.TestStatus(ObjectStatus.Sold))
        {
            return UpdateSleepTime.Forever;
        }

        // Containment eligibility (GPL container branch). Distinguishing a garrisonable
        // structure from a transport needs a container-kind predicate not on the stable
        // seam yet (F-SDU-2); the faithful default is applied: a detector inside ANY
        // container is suppressed unless its INI explicitly permits detection while
        // contained (either flag). Matches the GPL default (both No) and the sole AotR use.
        if (self.ContainedBy != null &&
            !_moduleData.CanDetectWhileGarrisoned &&
            !_moduleData.CanDetectWhileContained)
        {
            return UpdateSleepTime.Frames(_moduleData.DetectionRate);
        }

        // Detection reach: the explicit DetectionRange, else the object's vision range
        // (GPL default). Both are Fix64; the vision-range crossing is quantized exactly once
        // on the seam (D-7), never in this [SimState] body.
        var range = _moduleData.DetectionRange > Fix64.Zero
            ? _moduleData.DetectionRange
            : Context.Partition.GetVisionRange(self);

        if (range > Fix64.Zero)
        {
            foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, range))
            {
                if (candidate == self || candidate.IsEffectivelyDead)
                {
                    continue;
                }

                // Enemies and neutrals only (GPL ALLOW_ENEMIES | ALLOW_NEUTRAL): never
                // allies. Ally = same controlling player or an allied player (the same
                // owner-based test the AutoHealBehavior pilot uses; robust where team
                // alliances are not wired, and equivalent to GPL's relationship != ALLIES).
                if (candidate.Owner == self.Owner || self.Owner.Allies.Contains(candidate.Owner))
                {
                    continue;
                }

                // Only stealthed things are detectable. The garrison-rider sub-case (a
                // transport holding a stealth unit but not itself stealthed) needs a
                // contained-stealth query not on the seam yet - F-SDU-4.
                if (!candidate.TestStatus(ObjectStatus.Stealthed))
                {
                    continue;
                }

                // THE sim effect (GPL stealth->markAsDetected): reveal the stealthed enemy.
                // R9 integration: the target's StealthUpdate (ported in the same round) owns
                // the Detected bit - it recomputes it from its detection timer every tick, so
                // setting the bit alone would be cleared next frame. Arm the timer through
                // its MarkAsDetected seam (exactly GPL's stealth->markAsDetected()); fall
                // back to the raw bit for objects without a ported StealthUpdate.
                var stealth = candidate.FindBehavior<StealthUpdate>();
                if (stealth != null)
                {
                    stealth.MarkAsDetected();
                }
                else
                {
                    candidate.SetObjectStatus(ObjectStatus.Detected, true);
                }
            }
        }

        return UpdateSleepTime.Frames(_moduleData.DetectionRate);
    }

    // ---- the single contract walk (§3/§4). The revealed-enemy DETECTED bits ride each
    // target's own GameObject status persist, so this module's only mutable state is the
    // enabled flag. Field order = declaration order = OUR choice (F9). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Active", ref Active);
    }

    // ---- legacy retail-save reader (outside the contract, F9): version, base, enabled. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref Active);
    }
}

/// <summary>
/// Display MESSAGE:StealthDiscovered when triggered.
/// </summary>
[SimDataAudited]
public sealed class StealthDetectorUpdateModuleData : UpdateModuleData
{
    internal static StealthDetectorUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<StealthDetectorUpdateModuleData> FieldParseTable = new IniParseTable<StealthDetectorUpdateModuleData>
    {
        { "DetectionRate", (parser, x) => x.DetectionRate = parser.ParseTimeMillisecondsToLogicFrames() },
        { "InitiallyDisabled", (parser, x) => x.InitiallyDisabled = parser.ParseBoolean() },
        { "DetectionRange", (parser, x) => x.DetectionRange = parser.ParseFix64() },
        { "CanDetectWhileGarrisoned", (parser, x) => x.CanDetectWhileGarrisoned = parser.ParseBoolean() },
        { "CanDetectWhileContained", (parser, x) => x.CanDetectWhileContained = parser.ParseBoolean() },
        { "ExtraRequiredKindOf", (parser, x) => x.ExtraRequiredKindOf = parser.ParseEnum<ObjectKinds>() },
        { "PingSound", (parser, x) => x.PingSound = parser.ParseAudioEventReference() },
        { "LoudPingSound", (parser, x) => x.LoudPingSound = parser.ParseAudioEventReference() },
        { "IRParticleSysName", (parser, x) => x.IRParticleSysName = parser.ParseFXParticleSystemTemplateReference() },
        { "IRBrightParticleSysName", (parser, x) => x.IRBrightParticleSysName = parser.ParseFXParticleSystemTemplateReference() },
        { "IRGridParticleSysName", (parser, x) => x.IRGridParticleSysName = parser.ParseFXParticleSystemTemplateReference() },
        { "IRBeaconParticleSysName", (parser, x) => x.IRBeaconParticleSysName = parser.ParseFXParticleSystemTemplateReference() },
        { "IRParticleSysBone", (parser, x) => x.IRParticleSysBone = parser.ParseBoneName() },
        { "CancelOneRingEffect", (parser, x) => x.CancelOneRingEffect = parser.ParseBoolean() },
        { "RequiredUpgrade", (parser, x) => x.RequiredUpgrade = parser.ParseAssetReference() },
    };

    /// <summary>
    /// How often, in milliseconds, to scan for stealthed objects in sight range.
    /// </summary>
    public LogicFrameSpan DetectionRate { get; private set; }

    public bool InitiallyDisabled { get; private set; }

    /// <summary>
    /// Detection reach in world units (GPL INI::parseReal, so a real - "200.0" is legal);
    /// 0 means "use the object's vision range". Quantized Q31.32 (S5) for the scan.
    /// </summary>
    public Fix64 DetectionRange { get; private set; }

    public bool CanDetectWhileGarrisoned { get; private set; }

    public bool CanDetectWhileContained { get; private set; }

    public ObjectKinds ExtraRequiredKindOf { get; private set; }

    public LazyAssetReference<BaseAudioEventInfo> PingSound { get; private set; }
    public LazyAssetReference<BaseAudioEventInfo> LoudPingSound { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> IRParticleSysName { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> IRBrightParticleSysName { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> IRGridParticleSysName { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> IRBeaconParticleSysName { get; private set; }
    public string IRParticleSysBone { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool CancelOneRingEffect { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string RequiredUpgrade { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new StealthDetectorUpdate(gameObject, gameEngine.SimContext, this);
    }
}
