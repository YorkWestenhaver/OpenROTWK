// DemoTrapUpdate - R12 port (behavioral reference: generals-gpl GameLogic/Module/
// DemoTrapUpdate.cpp/.h; GPL semantics reference only, fresh code against the landed OpenSage
// module contract).
//
// A proximity-triggered explosive trap with two detonation modes:
//   - Automatic (proximity): every ScanRate frames, scan for a live ground enemy within
//     TriggerDetonationRange (ignoring IgnoreTargetTypes kinds) and detonate.
//   - Manual: detonate immediately, proximity ignored, the instant the object's current weapon
//     is the DetonationWeaponSlot (a command button external to this module selects it).
// Either way, detonation fires DetonationWeapon at the trap's own position and kills the trap.
// DetonateWhenKilled additionally fires the detonation weapon if the trap dies from any other
// cause (external damage) before it detonates itself.
//
// This module is legacy (GameObject, IGameEngine), not [SimState]: detonation fires the
// legacy Weapon/WeaponSet machinery (position-targeted, float substrate) exactly like the
// landed FireWeaponWhenDeadBehavior/StickyBombUpdate siblings - ISimEvents has no weapon-fire
// member to route it through Fix64-safe, and this task's reservedNames grants no new seam to
// add one. The proximity SCAN, though, uses the deterministic Fix64 partition query
// (Context.Partition.QueryObjectsInRadius) that EnemyNearUpdate/StealthDetectorUpdate already
// use - it is the one proximity query actually wired end-to-end in the headless test host - so
// TriggerDetonationRange is parsed Fix64 and the query itself never touches a float; only the
// weapon-fire tail (Detonate) crosses back onto the legacy substrate.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-DTU-1 onObjectCreated's initial weapon-slot lock (GPL setWeaponLock, arming either the
//     proximity or manual slot from DefaultProximityMode) and its three-distinct-slots
//     DEBUG_CRASH validation are not modeled: OpenSage's WeaponSet has no landed weapon-slot
//     selection/lock mechanism yet (WeaponSetUpdate, LockWeaponCreate and
//     WeaponModeSpecialPowerUpdate are all still stubs), so nothing exists for this module to
//     drive. Update() still faithfully reads whatever GameObject.CurrentWeapon.Slot the (today,
//     effectively fixed-at-Primary) engine weapon-set selection produces.
//   F-DTU-2 the "dozer disarming me" exception (GPL: skip detonating on a DOZER whose current
//     weapon getDamageType() is DAMAGE_DISARM and who is actively attacking) is not modeled:
//     WeaponTemplate has no single "this weapon's damage type" facade (damage types live per
//     nugget), so a disarming dozer is filtered by the ordinary relationship/kind/airborne
//     checks like any other candidate, same as everything else in range.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

public sealed class DemoTrapUpdate : UpdateModule
{
    private readonly DemoTrapUpdateModuleData _moduleData;

    private int _nextScanFrames;
    private bool _detonated;

    internal DemoTrapUpdate(GameObject gameObject, IGameEngine gameEngine, DemoTrapUpdateModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    public override UpdateSleepTime Update()
    {
        if (_detonated)
        {
            return UpdateSleepTime.None;
        }

        var me = GameObject;

        if (me.TestStatus(ObjectStatus.UnderConstruction) || me.TestStatus(ObjectStatus.Sold))
        {
            return UpdateSleepTime.None;
        }

        if (me.IsEffectivelyDead)
        {
            if (_moduleData.DetonateWhenKilled)
            {
                Detonate();
            }
            return UpdateSleepTime.None;
        }

        // The current weapon slot determines the mode (GPL getCurrentWeapon()->getWeaponSlot()).
        var weaponSlot = me.CurrentWeapon?.Slot;

        if (weaponSlot == _moduleData.DetonationWeaponSlot)
        {
            // Externally triggered by the press of a command button.
            Detonate();
            return UpdateSleepTime.None;
        }

        // Don't scan every frame for performance reasons.
        if (_nextScanFrames > 0)
        {
            _nextScanFrames--;
            return UpdateSleepTime.None;
        }

        if (weaponSlot == _moduleData.ManualModeWeaponSlot)
        {
            // Don't scan!
            return UpdateSleepTime.None;
        }

        // Reset the timer here: switching out of manual mode should scan right away.
        _nextScanFrames = (int)_moduleData.ScanFrames.Value;

        var shallDetonate = false;

        // Scan for a valid enemy in proximity range.
        foreach (var other in GameEngine.SimContext.Partition.QueryObjectsInRadius(me, _moduleData.TriggerDetonationRange))
        {
            if (other.Definition.KindOf != null && other.Definition.KindOf.Intersects(_moduleData.IgnoreTargetTypes))
            {
                // Skip specified types to ignore.
                continue;
            }

            if (other.IsEffectivelyDead)
            {
                continue;
            }

            // Order matters: we want to know if WE consider it an enemy, not vice versa.
            if (me.GetRelationship(other) != RelationshipType.Enemies)
            {
                if (!_moduleData.AutoDetonationWithFriendsInvolved)
                {
                    // Not allowed to proximity-detonate with friends nearby.
                    return UpdateSleepTime.None;
                }
                // Don't shoot our friends.
                continue;
            }

            if (other.IsAboveTerrain)
            {
                // Don't detonate on anything airborne.
                continue;
            }

            // Anyone close enough? (QueryObjectsInRadius already bounds this to
            // TriggerDetonationRange, GPL's own redundant distance check is therefore implicit.)
            shallDetonate = true;

            if (_moduleData.AutoDetonationWithFriendsInvolved)
            {
                // No need to keep looking for friends; an enemy in range is all we need.
                break;
            }
        }

        if (shallDetonate)
        {
            // Enemy in proximity while in proximity-detonation mode: trigger the explosion.
            Detonate();
        }

        return UpdateSleepTime.None;
    }

    private void Detonate()
    {
        var me = GameObject;

        // Only fire the weapon if we're not being built or sold.
        if (!me.TestStatus(ObjectStatus.UnderConstruction) && !me.TestStatus(ObjectStatus.Sold))
        {
            var weaponTemplate = _moduleData.DetonationWeapon?.Value;
            if (weaponTemplate != null)
            {
                var detonationWeapon = new Weapon(me, weaponTemplate, _moduleData.DetonationWeaponSlot, GameEngine);
                detonationWeapon.SetTarget(new WeaponTarget(me.Translation));
                detonationWeapon.Fire();
            }
        }

        me.Kill();
        _detonated = true;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistInt32(ref _nextScanFrames);
        reader.PersistBoolean(ref _detonated);
    }
}

public sealed class DemoTrapUpdateModuleData : UpdateModuleData
{
    internal static DemoTrapUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<DemoTrapUpdateModuleData> FieldParseTable = new IniParseTable<DemoTrapUpdateModuleData>
    {
        { "DefaultProximityMode", (parser, x) => x.DefaultProximityMode = parser.ParseBoolean() },
        { "DetonationWeaponSlot", (parser, x) => x.DetonationWeaponSlot = parser.ParseEnum<WeaponSlot>() },
        { "ProximityModeWeaponSlot", (parser, x) => x.ProximityModeWeaponSlot = parser.ParseEnum<WeaponSlot>() },
        { "ManualModeWeaponSlot", (parser, x) => x.ManualModeWeaponSlot = parser.ParseEnum<WeaponSlot>() },
        { "TriggerDetonationRange", (parser, x) => x.TriggerDetonationRange = parser.ParseFix64() },
        { "IgnoreTargetTypes", (parser, x) => x.IgnoreTargetTypes = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ScanRate", (parser, x) => x.ScanFrames = parser.ParseDurationLogicFrames() },
        { "AutoDetonationWithFriendsInvolved", (parser, x) => x.AutoDetonationWithFriendsInvolved = parser.ParseBoolean() },
        { "DetonationWeapon", (parser, x) => x.DetonationWeapon = parser.ParseWeaponTemplateReference() },
        { "DetonateWhenKilled", (parser, x) => x.DetonateWhenKilled = parser.ParseBoolean() }
    };

    public bool DefaultProximityMode { get; private set; }
    public WeaponSlot DetonationWeaponSlot { get; private set; } = WeaponSlot.Primary;
    public WeaponSlot ProximityModeWeaponSlot { get; private set; } = WeaponSlot.Primary;
    public WeaponSlot ManualModeWeaponSlot { get; private set; } = WeaponSlot.Primary;
    public Fix64 TriggerDetonationRange { get; private set; }
    public BitArray<ObjectKinds> IgnoreTargetTypes { get; private set; } = new BitArray<ObjectKinds>();
    public LogicFrameSpan ScanFrames { get; private set; }
    public bool AutoDetonationWithFriendsInvolved { get; private set; }
    public LazyAssetReference<WeaponTemplate> DetonationWeapon { get; private set; }
    public bool DetonateWhenKilled { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DemoTrapUpdate(gameObject, gameEngine, this);
    }
}
