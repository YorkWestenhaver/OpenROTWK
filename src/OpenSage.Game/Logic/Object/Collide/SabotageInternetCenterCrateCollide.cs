// SabotageInternetCenterCrateCollide - R12 port off the [ParseOnly] backlog (census: Collide).
//
// The retail module is a saboteur crate whose collision trigger (executeCrateBehavior)
// validates that the target is alive, an enemy-controlled KindOf=FS_INTERNET_CENTER structure,
// and the picking-up unit's current AI goal object, then disables the center's spy-vision
// upgrades and every hacker riding inside it for SabotageDuration frames, marks the center
// DISABLED_HACKED, and plays sabotage FX/EVA feedback.
//
// No BFME/AotR content anywhere in this repo authors this module or KindOf=FS_INTERNET_CENTER
// - both are Generals-only census entries with zero references under data/**/ini - and three
// capabilities the trigger needs are missing from the object model, in both the legacy
// GameObject surface and the audited ISimContext seam:
//   - an AI "goal object" query: AIUpdate here tracks pathfind/locomotor/weapon targets only,
//     with no equivalent of the retail goal-object check that guards against an incidental
//     walk-by triggering the sabotage;
//   - a DISABLED_HACKED-equivalent DisabledType entry, and a live InternetHackContain runtime
//     that could iterate its contained hackers (InternetHackContainModuleData parses today but
//     has no CreateModule / no runtime class to disable);
//   - the EVA announcement and radar-infiltration-event outputs (ISimEvents covers FX/sound/
//     unit-sound only; the legacy IGameEngine has no EVA or radar-infiltration surface either).
//
// Retiring [ParseOnly] here follows the landed sibling pattern already in this directory
// (ConvertToHijackedVehicleCrateCollide, ConvertToCarBombCrateCollide, MoneyCrateCollide,
// SalvageCrateCollide): a structurally real, loadable/persistable module so an authored object
// (if content ever adds one) gets a live module instead of a ModuleNotPortedException, without
// inventing the collision-trigger logic against engine capabilities that do not exist yet.
//
// TODO-spec (unverified, the whole collision-trigger behavior): wire executeCrateBehavior /
// isValidToExecute - goal-object check, dead/kindof/relationship vetoes, spy-vision + hacker
// disable, EVA/radar feedback - once the capabilities above land.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class SabotageInternetCenterCrateCollide : CrateCollide
{
    public SabotageInternetCenterCrateCollide(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

/// <summary>
/// Hardcoded to play the SabotageBuilding sound definition when triggered.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class SabotageInternetCenterCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageInternetCenterCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageInternetCenterCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageInternetCenterCrateCollideModuleData>
        {
            { "SabotageDuration", (parser, x) => x.SabotageDuration = parser.ParseInteger() },
        });

    public int SabotageDuration { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageInternetCenterCrateCollide(gameObject, gameEngine);
    }
}
