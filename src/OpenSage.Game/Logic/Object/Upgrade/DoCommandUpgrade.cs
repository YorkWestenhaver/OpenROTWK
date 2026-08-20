// DoCommandUpgrade - R12 port. BFME2-only (no generals-gpl sibling, and no clean-room
// behavioral spec in bfme2-workbench/research/), so this is the minimal behavior the module's
// own INI schema and the shared upgrade-mux contract (design-module-api §6) already commit to:
// a pure upgrade-mux module with no update tick and no client-side effect of its own. Its only
// job is to expose, to whatever consumes it (the not-yet-ported command/order layer), which
// command button becomes available once triggered (GetUpgradeCommandButtonName) versus which
// one it replaces/removes (RemoveUpgradeCommandButtonName) - see IsCommandAvailable /
// ActiveCommandButtonName below, the queryable surface a command-availability check (the
// GameObject.CanPurchase family) would consult once that layer exists.
//
// This module does NOT route through the shared UpgradeModule/UpgradeLogic base (BaseUpgrade.cs
// sibling pattern): UpgradeLogic's triggered flag is one-way (untriggered -> triggered only,
// see UpgradeModule.cs), but the Permanent flag requires the OTHER direction - reverting to
// untriggered when a non-Permanent trigger is later removed. That reset has no supported path
// through the shared mux, so this follows the R12 AttributeModifierAuraUpdate precedent
// (Update/AttributeModifierAuraUpdate.cs file header): a module-local trigger flag built over
// the same shared UpgradeLogicData (TriggeredBy/ConflictsWith/RequiresAllTriggers/
// RequiresAllConflictingTriggers/StartsActive/Permanent/ActiveDuringConstruction fields, reused
// verbatim - no field-name collision here, unlike that module), with removal support added
// locally instead of on the shared class.
//
// TODO-spec (unverified/unmodeled retail behavior, filed not invented):
//   - "upgrade removed" engine notification: no landed module is called back when a triggering
//     upgrade is later stripped from the object (GameObject.RemoveUpgrade carries a standing
//     TODO for this - see GameObject.cs). OnUpgradeRemoved below is the module-local reaction
//     (Permanent=Yes is a no-op; Permanent=No reverts to untriggered), reachable and tested the
//     same way AttributeModifierAuraUpdate.OnTriggerRemoved is, but not yet wired to a real
//     engine-side removal trigger;
//   - the actual command-button add/remove UI wiring (the control-bar consumer of
//     GetUpgradeCommandButtonName/RemoveUpgradeCommandButtonName) is a client/GUI-side concern
//     with no sim-side seam yet; this port supplies the deterministic availability signal
//     (IsCommandAvailable) that consumer would gate on, not the UI wiring itself.
//
// Every mutable sim field appears in Xfer exactly once; the trigger flag is Tolerance.Exact by
// construction (XferBool is always exact).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DoCommandUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly DoCommandUpgradeModuleData _data;

    // ---- mutable sim state (the whole inventory) ----

    /// <summary>Whether the upgrade trigger has fired (module-local mux; see file header for
    /// why this does not reuse the shared UpgradeLogic - it needs the reset that class does not
    /// support). Never resets on its own once set, matching UpgradeLogic's own one-way
    /// contract; only <see cref="OnUpgradeRemoved"/> can revert it, and only when not
    /// Permanent.</summary>
    private bool _triggered;

    public DoCommandUpgrade(GameObject gameObject, ISimContext context, DoCommandUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        if (_data.UpgradeData.StartsActive)
        {
            OnUpgrade();
        }
    }

    /// <summary>Whether the trigger has fired (mirrors the shared UpgradeModule.Triggered
    /// surface for the not-yet-ported command layer).</summary>
    public bool Triggered => _triggered;

    /// <summary>
    /// Whether <see cref="DoCommandUpgradeModuleData.GetUpgradeCommandButtonName"/> is
    /// currently available for execution on this object: triggered, and - unless
    /// ActiveDuringConstruction says otherwise - not while the object is still under
    /// construction (GameObject.IsBeingConstructed), matching the plain reading of
    /// ActiveDuringConstruction ("stays active through construction" implies the default is
    /// "does not").
    /// </summary>
    public bool IsCommandAvailable =>
        _triggered && (_data.UpgradeData.ActiveDuringConstruction || !GameObject.IsBeingConstructed());

    /// <summary>The command button name exposed while available, else null (nothing to run).</summary>
    public string ActiveCommandButtonName => IsCommandAvailable ? _data.GetUpgradeCommandButtonName : null;

    public bool CanUpgrade(UpgradeSet existingUpgrades)
    {
        if (_triggered)
        {
            return false;
        }

        var data = _data.UpgradeData;

        // Does the object / player have the prerequisite upgrades that trigger this upgrade?
        var triggered = data.RequiresAllTriggers
            ? existingUpgrades.SetEquals(data.TriggeredByHashSet)
            : existingUpgrades.Overlaps(data.TriggeredByHashSet);

        if (!triggered)
        {
            return false;
        }

        // Does the object / player have any upgrades that conflict with this upgrade?
        var conflicts = data.RequiresAllConflictingTriggers
            ? existingUpgrades.SetEquals(data.ConflictsWithHashSet)
            : existingUpgrades.Overlaps(data.ConflictsWithHashSet);

        return !conflicts;
    }

    public void TryUpgrade(UpgradeSet completedUpgrades)
    {
        if (!CanUpgrade(completedUpgrades))
        {
            return;
        }

        OnUpgrade();
    }

    /// <summary>
    /// Fires exactly once per object (the CanUpgrade gate above makes every later call to
    /// TryUpgrade a no-op): flips the trigger, making GetUpgradeCommandButtonName available
    /// through <see cref="IsCommandAvailable"/>.
    /// </summary>
    private void OnUpgrade()
    {
        _triggered = true;
    }

    /// <summary>
    /// Module-local reaction to the triggering upgrade going away (TODO-spec: no engine
    /// callback exists yet that calls this - see the file header). Permanent=Yes ("stays
    /// active even if the triggering upgrade is removed") is a no-op; Permanent=No reverts to
    /// untriggered, so the command becomes unavailable again.
    /// </summary>
    public void OnUpgradeRemoved()
    {
        if (_data.UpgradeData.Permanent)
        {
            return;
        }

        _triggered = false;
    }

    // ---- the single walk (declaration order, F9): the trigger flag is the entire per-module
    // inventory; GetUpgradeCommandButtonName/RemoveUpgradeCommandButtonName are immutable parse
    // data, not sim state.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Triggered", ref _triggered);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class DoCommandUpgradeModuleData : UpgradeModuleData
{
    internal static DoCommandUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<DoCommandUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<DoCommandUpgradeModuleData>
        {
            { "GetUpgradeCommandButtonName", (parser, x) => x.GetUpgradeCommandButtonName = parser.ParseAssetReference() },
            { "RemoveUpgradeCommandButtonName", (parser, x) => x.RemoveUpgradeCommandButtonName = parser.ParseAssetReference() },
        });

    public string GetUpgradeCommandButtonName { get; private set; }
    public string RemoveUpgradeCommandButtonName { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DoCommandUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
