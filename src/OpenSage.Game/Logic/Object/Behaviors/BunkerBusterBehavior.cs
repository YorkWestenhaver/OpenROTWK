// BunkerBusterBehavior - R12 port (behavioral reference: generals-gpl GeneralsMD GameLogic/
// Module/BunkerBusterBehavior.cpp/.h; GPL semantics reference only, fresh code against the
// landed OpenSage module contract).
//
// A missile/bomb payload behavior with three jobs:
//   - onObjectCreated: resolve the optional UpgradeRequired name to an UpgradeTemplate once
//     (GPL: TheUpgradeCenter->findUpgrade()).
//   - update() (AI-guided only - GPL's "is this a SMART bomb?" gate on getAI() being non-null):
//     cache the AI's current victim object id the first time one is available, and - while the
//     object carries OBJECT_STATUS_MISSILE_KILLING_SELF - play CrashThroughBunkerFX every
//     CrashThroughBunkerFXFrequency frames (GPL: frame % frequency == 1).
//   - onDie (bustTheBunker): if UpgradeRequired is set and the controlling player lacks it, do
//     nothing at all (not even the FX/shockwave below - GPL returns before any of that runs).
//     Otherwise resolve the cached victim object; if it carries a contain module, apply
//     OccupantDamageWeaponTemplate's damage/death type to every occupant (or kill outright if
//     no such template is configured), then play DetonationFX at the victim (or self, if there
//     was no victim) and fire ShockwaveWeaponTemplate as a position-targeted temp weapon there.
//
// This module is legacy (GameObject, IGameEngine), not [SimState]: it fires weapon/FX effects
// through the float-substrate Weapon/FXList machinery exactly like the landed DemoTrapUpdate/
// FireWeaponWhenDeadBehavior siblings, and this task's reservedNames grants no new SimCore seam
// for it.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-BBB-1 GPL gates occupant-killing on contain->isBustable() (true for Garrison/Tunnel/Cave
//     contain, false for open/transport contain - see ContainModule.h/GarrisonContain.h/
//     OpenContain.h). OpenSage's IContainModule has no such member, and this task's
//     reservedNames adds none, so the closest already-published gate is used instead:
//     IContainModule.IsGarrisonable. Most Contain-category modules are landed by now (e.g.
//     GarrisonContain, TunnelContain, OpenContain, TransportContain are not [ParseOnly] - the
//     old "every Contain module ... is still [ParseOnly]" framing here was stale), but none of
//     them currently implement the IContainModule interface itself (only ParachuteContain does,
//     with IsGarrisonable => false) - so GameObject.Contain is still null for every
//     Garrison/Tunnel/Open/Transport-contained object today, and this path is presently inert
//     end-to-end for the same practical reason as before (no landed IContainModule implementer
//     with IsGarrisonable => true), just not because the Contain modules themselves are
//     unported.
//   F-BBB-2 GPL's harmAndForceExitAllContained() both damages AND ejects each occupant from the
//     container; killAllContained() unconditionally kills and ejects. IContainModule exposes no
//     eject/force-exit verb, so only the damage/kill half is modeled here; occupants remain
//     logically contained.
//   F-BBB-3 the DO_SEISMIC_SIMULATIONS terrain-heave effect (SeismicEffectRadius/Magnitude) has
//     no OpenSage counterpart (no TheTerrainVisual/seismic-simulation port exists anywhere in
//     this codebase yet, and it is itself `#ifdef`'d out in the GPL source); the two fields are
//     parsed (audited vocabulary) but not acted on.

using System.Linq;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FX;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

public sealed class BunkerBusterBehavior : UpdateModule, IDieModule
{
    private readonly BunkerBusterBehaviorModuleData _moduleData;

    private UpgradeTemplate _upgradeRequired;
    private ObjectId _victimId;

    internal BunkerBusterBehavior(GameObject gameObject, IGameEngine gameEngine, BunkerBusterBehaviorModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;

        // "THIS HAS AN UPDATE... BECAUSE I FORESEE THE NEED FOR ONE, BUT RIGHT NOW IT DOES
        // NOTHING" (GPL ctor comment) - wake immediately, matching the original's
        // setWakeFrame(UPDATE_SLEEP_NONE).
        SetWakeFrame(UpdateSleepTime.None);

        _victimId = ObjectId.Invalid;
    }

    protected internal override void OnObjectCreated()
    {
        base.OnObjectCreated();

        // Convert the module's upgrade name to a resolved template, once (GPL onObjectCreated).
        _upgradeRequired = _moduleData.UpgradeRequired?.Value;
    }

    public override UpdateSleepTime Update()
    {
        var ai = GameObject.AIUpdate;
        if (ai != null) // is this a SMART bomb? (GPL: getAI() non-null)
        {
            if (_victimId.IsInvalid)
            {
                var currentVictimId = ai.CurrentVictimId;
                if (currentVictimId.IsValid)
                {
                    _victimId = currentVictimId;
                }
            }

            var frequency = _moduleData.CrashThroughBunkerFXFrequency;
            if (frequency != LogicFrameSpan.Zero
                && GameEngine.GameLogic.CurrentFrame.Value % frequency.Value == 1) // not too much
            {
                var crashFX = _moduleData.CrashThroughBunkerFX?.Value;
                if (GameObject.TestStatus(ObjectStatus.MissingKillingSelf) && crashFX != null)
                {
                    // CrashFX done on the missile/bomb.
                    crashFX.Execute(new FXListExecutionContext(GameObject.Rotation, GameObject.Translation, GameEngine));
                }
            }
        }

        return UpdateSleepTime.None;
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        // Do what we came here to do!
        BustTheBunker();
    }

    private void BustTheBunker()
    {
        if (!GameObject.HasUpgrade(_upgradeRequired))
        {
            // GameObject.HasUpgrade already treats a null template as "always satisfied", so
            // this single check covers both "no upgrade configured" and "upgrade not yet
            // researched" (GPL: `if (m_upgradeRequired != NULL) { ...; if (!weaponUpgraded)
            // return; }`).
            return;
        }

        // Here is where we kill everyone inside any targeted garrisoned buildings.
        var target = GameEngine.GameLogic.GetObjectById(_victimId);

        var objectForFX = GameObject;

        if (target != null) // Was the pilot aiming at an object?
        {
            objectForFX = target;

            var contain = target.Contain;
            if (contain != null && contain.IsGarrisonable) // Was that object something that bunkerbusters bust? (F-BBB-1)
            {
                // Snapshot before mutating: killing an occupant may remove it from the
                // container's live storage, which would otherwise invalidate iteration.
                var occupants = contain.ContainedItems.ToArray();

                var occupantDamageWeapon = _moduleData.OccupantDamageWeaponTemplate?.Value;
                if (occupantDamageWeapon != null)
                {
                    var damageNugget = occupantDamageWeapon.Nuggets.OfType<DamageNugget>().FirstOrDefault();
                    var damageType = damageNugget?.DamageType ?? DamageType.Unresistable;
                    var deathType = damageNugget?.DeathType ?? DeathType.Normal;

                    foreach (var occupant in occupants)
                    {
                        // Ouch! (GPL hardcodes the amount to 100, independent of the weapon's
                        // configured damage value - translated faithfully, not "fixed".)
                        occupant.AttemptDamage(new DamageInfoInput(GameObject)
                        {
                            DamageType = damageType,
                            DeathType = deathType,
                            Amount = 100.0f,
                        });
                    }
                }
                else
                {
                    foreach (var occupant in occupants)
                    {
                        occupant.Kill();
                    }
                }
            }
        }

        var detonationFX = _moduleData.DetonationFX?.Value;
        if (detonationFX != null)
        {
            // DetonationFX done on the building (or on the bomb itself, if no victim).
            detonationFX.Execute(new FXListExecutionContext(objectForFX.Rotation, objectForFX.Translation, GameEngine));
        }

        // F-BBB-3: no port-side seismic terrain simulation exists; SeismicEffectRadius/
        // SeismicEffectMagnitude are parsed but intentionally not acted on here.

        var shockwaveWeaponTemplate = _moduleData.ShockwaveWeaponTemplate?.Value;
        if (shockwaveWeaponTemplate != null)
        {
            var shockwaveWeapon = new Weapon(objectForFX, shockwaveWeaponTemplate, WeaponSlot.Primary, GameEngine);
            shockwaveWeapon.SetTarget(new WeaponTarget(objectForFX.Translation));
            shockwaveWeapon.Fire();
        }
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        // GPL's xfer() (BunkerBusterBehavior.cpp) delegates to UpdateModule::xfer(xfer) only -
        // m_victimID is never read or written by retail's xfer contract, so a real save/load
        // resets any in-flight bomb's cached victim back to INVALID_ID (the ctor default); the
        // object re-adopts whatever ai->getCurrentVictim() happens to be at the next update()
        // after load. Do not persist _victimId - reset it to Invalid on load to match retail,
        // rather than faithfully round-tripping the cached victim (which would diverge from a
        // retail peer that forgets it).
        if (reader.Mode == StatePersistMode.Read)
        {
            _victimId = ObjectId.Invalid;
        }

        var upgradeName = _upgradeRequired?.Name;
        reader.PersistAsciiString(ref upgradeName);
        if (reader.Mode == StatePersistMode.Read)
        {
            _upgradeRequired = string.IsNullOrEmpty(upgradeName)
                ? null
                : reader.AssetStore.Upgrades.GetByName(upgradeName);
        }
    }
}

[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class BunkerBusterBehaviorModuleData : BehaviorModuleData
{
    internal static BunkerBusterBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BunkerBusterBehaviorModuleData> FieldParseTable = new IniParseTable<BunkerBusterBehaviorModuleData>
    {
        { "UpgradeRequired", (parser, x) => x.UpgradeRequired = parser.ParseUpgradeReference() },
        { "DetonationFX", (parser, x) => x.DetonationFX = parser.ParseFXListReference() },
        { "CrashThroughBunkerFX", (parser, x) => x.CrashThroughBunkerFX = parser.ParseFXListReference() },
        { "CrashThroughBunkerFXFrequency", (parser, x) => x.CrashThroughBunkerFXFrequency = parser.ParseDurationLogicFrames() },

        { "SeismicEffectRadius", (parser, x) => x.SeismicEffectRadius = parser.ParseFix64() },
        { "SeismicEffectMagnitude", (parser, x) => x.SeismicEffectMagnitude = parser.ParseFix64() },

        { "ShockwaveWeaponTemplate", (parser, x) => x.ShockwaveWeaponTemplate = parser.ParseWeaponTemplateReference() },
        { "OccupantDamageWeaponTemplate", (parser, x) => x.OccupantDamageWeaponTemplate = parser.ParseWeaponTemplateReference() },
    };

    /// <summary>Upgrade required to allow this bomb to kill garrisoned units. Null = always allowed.</summary>
    public LazyAssetReference<UpgradeTemplate> UpgradeRequired { get; private set; }

    public LazyAssetReference<FXList> DetonationFX { get; private set; }
    public LazyAssetReference<FXList> CrashThroughBunkerFX { get; private set; }

    /// <summary>How often (in logic frames) to play <see cref="CrashThroughBunkerFX"/>.</summary>
    public LogicFrameSpan CrashThroughBunkerFXFrequency { get; private set; } = new LogicFrameSpan(4);

    /// <summary>Parsed but not acted on; see F-BBB-3.</summary>
    public Fix64 SeismicEffectRadius { get; private set; } = Fix64.FromDecimalLiteral("140.0");

    /// <summary>Parsed but not acted on; see F-BBB-3.</summary>
    public Fix64 SeismicEffectMagnitude { get; private set; } = Fix64.FromDecimalLiteral("6.0");

    /// <summary>Fired as a position-targeted temp weapon at the target on death, for its shockwave side effect only.</summary>
    public LazyAssetReference<WeaponTemplate> ShockwaveWeaponTemplate { get; private set; }

    /// <summary>Damage/death type applied to each occupant of a busted container; killAllContained() equivalent when absent.</summary>
    public LazyAssetReference<WeaponTemplate> OccupantDamageWeaponTemplate { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BunkerBusterBehavior(gameObject, gameEngine, this);
    }
}
