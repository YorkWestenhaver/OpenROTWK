// ReplaceObjectUpgrade - R12 port (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD ReplaceObjectUpgrade.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). GPL upgradeImplementation:
// on trigger, save the current position/team, destroy the original (pathfind-visible same
// frame, api-freeze-v1's IGameLogic.DestroyObject contract), create a fresh instance of
// ReplaceObject at the saved transform, restore team, run OnBuildComplete on the replacement's
// create modules ("onCreates were called at the constructor... this magically created thing
// needs to be considered as Built for Game specific stuff"), and queue it for pathfinding.
//
// This is the plain UpgradeModule sibling of the already-landed BFME2 special-power module
// ReplaceObjectUpdate (Logic/Object/Update/ReplaceObjectUpdate.cs) - that file's own header
// explains the split: BFME2 rebuilt the same core action behind a timed SpecialPowerTemplate
// shell, but the original GPL module is triggered by the ordinary TriggeredBy upgrade mux like
// every other UpgradeModule in this directory (AttributeModifierUpgrade, LevelUpUpgrade). This
// port reuses that same trigger shape and the same Context.GameLogic seam
// (CreateObjectAt/DestroyObject/PathfindQueueForPath) ReplaceObjectUpdate already exercises,
// with none of the BFME2-only phase/scatter/XP machinery GPL's ReplaceObjectUpgrade never had.
//
// TODO-spec (audited gap, not invented; matches ReplaceObjectUpdate's own note):
//   - GPL's onStructureConstructionComplete callback to the new owner's Player has no landed
//     Player member anywhere in this codebase (grep confirms) - when a construction-complete
//     notification surface lands, this is the module to wire it through.
//   - a missing/unresolvable ReplaceObject template (GetByName returns null when the object
//     doesn't exist) is GPL's own findTemplate-returned-NULL guard: a no-op, not a crash. No
//     exception-throwing "assertion" surface exists in this asset-lookup path to hook into.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). This module carries no mutable sim state of
// its own beyond the shared upgrade-trigger flag (UpgradeLogic.Xfer).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ReplaceObjectUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly ReplaceObjectUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public ReplaceObjectUpgrade(GameObject gameObject, ISimContext context, ReplaceObjectUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// GPL upgradeImplementation: destroy the original (pathfind-visible same frame), create
    /// the replacement at the original's transform, restore team, run the replacement's
    /// onBuildComplete pass, and queue it for pathfinding.
    /// </summary>
    private void OnUpgradeTriggered()
    {
        var replacementDefinition = _data.ReplaceObject?.Value;
        if (replacementDefinition == null)
        {
            // GPL's own findTemplate-returned-NULL guard: no-op, not a crash. See the
            // Template-validation TODO-spec note at the top of this file.
            return;
        }

        var me = GameObject;
        var owner = me.Owner;
        var team = me.Team;

        // GPL order: remove/destroy the original FIRST, then create the replacement - "if I
        // don't remove, then the new thing will be placed, and then on deletion I will remove
        // 'his' marks". IGameLogic.DestroyObject documents same-frame visibility (and already
        // un-stamps the pathfind obstacle footprint, S5), so `me` stays a valid position/team
        // donor for the CreateObjectAt call below.
        Context.GameLogic.DestroyObject(me);

        // Donor-matrix overload: exact position AND rotation copy, matching GPL's own
        // myMatrix = *me->getTransformMatrix(); replacementObject->setTransformMatrix(...).
        var replacement = Context.GameLogic.CreateObjectAt(replacementDefinition, owner, me);
        if (replacement == null)
        {
            return;
        }

        replacement.Team = team;

        // GPL: onCreates already ran in the constructor; this loop is the "consider it Built"
        // pass every CreateModule needs to see once.
        foreach (var createModule in replacement.FindBehaviors<ICreateModule>())
        {
            createModule.OnBuildComplete();
        }

        // S5 pathfinding integration: queue the replacement for a path so it (and anything
        // routing around it) is grid-visible from here on.
        Context.GameLogic.PathfindQueueForPath(replacement.Id);

        // GPL onStructureConstructionComplete: see the TODO-spec note at the top of this file
        // (no landed Player notification surface to call through yet).
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9): the mux flag is the
    // entire per-module inventory; there is no other mutable sim state to carry.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class ReplaceObjectUpgradeModuleData : UpgradeModuleData
{
    internal static ReplaceObjectUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ReplaceObjectUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ReplaceObjectUpgradeModuleData>
        {
            { "ReplaceObject", (parser, x) => x.ReplaceObject = parser.ParseObjectReference() },
        });

    public LazyAssetReference<ObjectDefinition> ReplaceObject { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ReplaceObjectUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
