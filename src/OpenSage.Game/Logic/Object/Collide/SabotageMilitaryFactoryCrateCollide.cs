// SabotageMilitaryFactoryCrateCollide - R12 port. GPL ref: GeneralsMD/Code/GameEngine/
// Source/GameLogic/Object/Collide/CrateCollide/SabotageMilitaryFactoryCrateCollide.cpp
// (+ base CrateCollide.cpp). Retail behavior: on collide with a live, enemy-owned
// KINDOF_FS_BARRACKS/FS_WARFACTORY/FS_AIRFIELD building (and not KINDOF_AIRCRAFT_CARRIER),
// it fires TheRadar->tryInfiltrationEvent, plays the sabotage feedback FX/sound, queues the
// EVA_BuildingSabotaged message when the target is locally controlled, and disables the
// target under DISABLED_HACKED for SabotageDuration frames before self-destructing.
//
// This entry retires the [ParseOnly] marker so authored templates carry a live module
// (module indexing/counts) instead of a parse hole, matching the landed shell shape of
// every other CrateCollide sibling in this file (MoneyCrateCollide, SalvageCrateCollide,
// ConvertToCarBombCrateCollide, ConvertToHijackedVehicleCrateCollide): none of them override
// OnCollide yet. R13.5 (crate-gate) landed the shared CrateCollide::isValidToExecute GATE on
// the base class - this module inherits it and it applies the instant executeCrateBehavior is
// wired - but the onCollide DISPATCH (gate/execute/FX/self-destroy) is still per-leaf, and
// this module's own execute step additionally needs subsystems that don't exist yet - a Radar
// service (tryInfiltrationEvent), an EVA message queue, and a DISABLED_HACKED DisabledType
// value. Porting any of those is a shared-surface change outside this module's scope
// (reservedNames is empty for this task), so the gameplay effect is parked here pending that
// base-class + subsystem work, rather than guessed at.
//
// TODO-spec (unverified, gated on the above): wire CrateCollide.OnCollide's execute pipeline,
// then implement isValidToExecute/executeCrateBehavior here per the GPL ref once Radar/EVA/
// DisabledType.Hacked exist.

using OpenSage.Data.Ini;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

public sealed class SabotageMilitaryFactoryCrateCollide : CrateCollide
{
    public SabotageMilitaryFactoryCrateCollide(GameObject gameObject, IGameEngine gameEngine, SabotageMilitaryFactoryCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class SabotageMilitaryFactoryCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageMilitaryFactoryCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageMilitaryFactoryCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageMilitaryFactoryCrateCollideModuleData>
        {
            { "SabotageDuration", (parser, x) => x.SabotageDuration = parser.ParseDurationLogicFrames() },
        });

    public LogicFrameSpan SabotageDuration { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageMilitaryFactoryCrateCollide(gameObject, gameEngine, this);
    }
}
