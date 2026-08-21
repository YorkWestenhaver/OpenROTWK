// WallUpgradeUpdate - R13 port (data-derivable; no GPL sibling, no Ghidra material used -
// see modules-r13/specs/WallUpgradeUpdateModuleData.md).
//
// Behavioral spec: FieldParseTable is empty (zero authored fields), and
// `grep -ril "WallUpgrade" generals-gpl generals-community` finds no hits - Generals/ZH has
// no wall tech tree, so there is no GPL sibling to translate. With no fields and no landed
// wake-trigger source, the only defensible port is a stateless marker module that never wakes
// under its own power: it sleeps forever from construction (SetWakeFrame(Forever), the same
// "asleep until someone else wakes me" idiom design-module-api.md documents), holds no mutable
// sim state, and its Update() body is unreachable in practice but re-asserts Forever
// defensively rather than assuming it can never be called. This mirrors the landed
// AllowBannerSpawnUpgrade marker-module idiom (R12), adapted from UpgradeModule to
// UpdateModule since this module's base is UpdateModuleData.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-WUU-1 (real wall-upgrade tick behavior unrecovered): whatever WallUpgradeUpdate
//     actually does in retail - if anything beyond being a marker - is not recoverable from
//     GPL (no sibling) or from this port's clean-room sources. Candidate future collaborators
//     (GeometryUpgrade, CastleUpgrade's filed WallUpgradeRadius gap F-CAS-11, WallHubBehavior
//     once it lands) are NOT wired here: this module has zero fields, so it cannot itself
//     carry a target-upgrade name/radius/parameter to drive any of them. Until one of those
//     lands with actual field/behavior evidence, this module stays a documented no-op.
//   F-WUU-2 (zero-field module, zero conformance-test branch surface): this module has
//     exactly one INI branch (the empty one), so its contract-test list is short by
//     construction, not by shortcut.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class WallUpgradeUpdate : UpdateModule
{
    internal WallUpgradeUpdate(GameObject gameObject, ISimContext context)
        : base(gameObject, context)
    {
        // No fields, no known wake trigger (F-WUU-1). Sleep forever until/unless something
        // external wakes this module; do not invent a self-driven tick.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update()
    {
        // Defensive: this module goes to sleep forever at construction and nothing in this
        // engine snapshot wakes it (F-WUU-1), so this body should be unreachable in
        // practice. Re-assert Forever rather than assume "unreachable" if that ever changes
        // silently.
        SetWakeFrame(UpdateSleepTime.Forever);
        return UpdateSleepTime.Forever;
    }

    // ---- the single walk: no fields, version byte only (§1/F-WUU-1: nothing to xfer) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // No fields: this module owns zero mutable sim state. Version byte only, same shape
        // as any other module's walk - nothing here to add without inventing state.
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class WallUpgradeUpdateModuleData : UpdateModuleData
{
    internal static WallUpgradeUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<WallUpgradeUpdateModuleData> FieldParseTable = new IniParseTable<WallUpgradeUpdateModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new WallUpgradeUpdate(gameObject, gameEngine.SimContext);
    }
}
