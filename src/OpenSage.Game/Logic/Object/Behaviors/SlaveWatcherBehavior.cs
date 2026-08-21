// SlaveWatcherBehavior - R13 port, data-derivable (no GPL sibling; census confirmed
// `grep -rli slavewatcher generals-gpl generals-community` -> zero hits). Full grounding in
// bfme2-workbench/research/modules-r13/specs/SlaveWatcherBehaviorModuleData.md §0/§1.
//
// Mechanism (closed-form composition of two already-landed engine primitives, no invented
// behavior - see the spec's §0 citation list for every claim below):
//   - This module lives on a PRODUCER object that also carries an ObjectCreationUpgrade (or
//     other CreatedByObjectID-stamping spawn mechanism) elsewhere on the same object. It has no
//     direct reference to what it spawns, so it discovers its "slave" by scanning
//     Context.GameLogic.ObjectsAscendingId (the blessed whole-world iteration) for the first
//     live object whose CreatedByObjectID == this object's Id - re-arming every tick while no
//     slave is tracked, so a later-spawned REPLACEMENT slave (the corpus's "buy a new one" flow)
//     is picked up on a subsequent tick, not only the first one ever spawned.
//   - ShareUpgrades: every tick a live slave is tracked, mirrors
//     GameObject.CompletedUpgradesIncludingPlayer onto the slave via UpgradeTemplate.GrantUpgrade
//     (HordeSiegeEngineContain's resolve-and-apply shape). Every-tick rather than edge-triggered
//     is behaviorally identical to a diff, because GrantUpgrade is idempotent for an
//     already-granted/already-completed upgrade (HashSet.Add / Player.AddUpgrade on the same
//     owner) - this engine has no "which upgrades changed" idiom to invent, unlike
//     ShareExperienceBehavior's numeric delta.
//   - Death handling: once the tracked slave.IsEffectivelyDead (the same predicate
//     SlavedUpdate.DieOnMastersDeath already polls from the opposite direction), resolve
//     RemoveUpgrade/GrantUpgrade by name (Context.Assets.GetUpgradeTemplate, silent no-op on a
//     null name or unresolved template - HordeSiegeEngineContain's own `if (template == null)
//     continue;` shape) and apply each to SELF (the producer) - matching the corpus comments
//     verbatim ("when our slave dies, remove this upgrade, so we can get the upgrade again" /
//     "...enable the button that allows us to buy a new one"). This fires exactly once per
//     tracked slave: the same branch that applies the mutations also resets the tracked id, so
//     the next tick re-enters discovery instead of re-observing the same dead slave.
//   - LetSlaveLive: OnDestroy() cascade (FloodUpdate.OnDestroy's verbatim shape) - when
//     !LetSlaveLive (the CLR bool default, so this is the parse-table default when the INI key
//     is omitted - 100% of the corpus's live uses take this default; every author who wanted the
//     opposite spelled out `LetSlaveLive = Yes`, spec §1.3) and a slave is still tracked,
//     destroys it on the producer's own destruction. This is the producer-side equivalent of
//     SlavedUpdate.DieOnMastersDeath for a slave template that does not carry its own
//     SlavedUpdate block at all (e.g. the armedminers.ini horde-member use) - the two mechanisms
//     are independent and both idempotent against an already-destroyed object when a slave
//     template happens to carry both.
//
// FINDINGS (filed, not invented - spec §1.3):
//   F-SW-1 (ShareUpgrades cadence): mirrored every tick rather than event-diffed; proven
//     behaviorally equivalent above via GrantUpgrade's idempotency. Not a performance concern
//     flag, a documentation one - so a reviewer does not mistake "every tick" for wasted work.
//   F-SW-2 (LetSlaveLive default): rests on parse-table-default convention + corpus-authoring
//     pattern (nobody in the live corpus authors the default explicitly) rather than a directly
//     observed default-value comment. Flagged for port-review sign-off; non-blocking, and the
//     field's type/parsing is unchanged either way.
//
// Every mutable sim field appears in Xfer exactly once (below); the tracked slave id is the
// module's ENTIRE mutable sim-state inventory.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SlaveWatcherBehavior : UpdateModule
{
    private readonly SlaveWatcherBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; the one field is in Xfer) ----

    /// <summary>The currently-tracked slave, or ObjectId.Invalid when nothing is tracked.</summary>
    private ObjectId _slaveId = ObjectId.Invalid;

    internal SlaveWatcherBehavior(GameObject gameObject, ISimContext context, SlaveWatcherBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No slave tracked at construction: a freshly-placed producer has not necessarily
        // spawned its slave yet (spec §2). Ticks every frame - no re-arm/delay field is
        // authored on this module (spec §1.3).
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var self = GameObject;

        // Step 1: slave discovery, edge-triggered on "no slave currently tracked" (spec §1.1).
        if (_slaveId.IsInvalid)
        {
            foreach (var candidate in Context.GameLogic.ObjectsAscendingId)
            {
                if (candidate.CreatedByObjectID == self.Id
                    && !candidate.IsDestroyed
                    && !candidate.IsEffectivelyDead)
                {
                    _slaveId = candidate.Id;
                    break;
                }
            }
        }

        if (_slaveId.IsValid)
        {
            var slave = Context.GameLogic.GetObjectById(_slaveId);

            if (slave != null && !slave.IsDestroyed && !slave.IsEffectivelyDead)
            {
                // Step 2: ShareUpgrades mirroring, every tick a live slave is tracked (spec
                // §1.2). Idempotent against re-granting, so no change-diff is needed.
                if (_data.ShareUpgrades)
                {
                    foreach (var upgrade in self.CompletedUpgradesIncludingPlayer)
                    {
                        upgrade.GrantUpgrade(slave);
                    }
                }
            }
            else if (slave != null && slave.IsEffectivelyDead)
            {
                // Step 3: death handling, exactly once per tracked slave (spec §1.3).
                if (!string.IsNullOrEmpty(_data.RemoveUpgrade))
                {
                    var template = Context.Assets.GetUpgradeTemplate(_data.RemoveUpgrade);
                    template?.RemoveUpgrade(self);
                }

                if (!string.IsNullOrEmpty(_data.GrantUpgrade))
                {
                    var template = Context.Assets.GetUpgradeTemplate(_data.GrantUpgrade);
                    template?.GrantUpgrade(self);
                }

                // Reset so the next tick re-enters discovery instead of re-observing this
                // same dead slave (spec §1.3, no re-entrant double-fire by construction).
                _slaveId = ObjectId.Invalid;
            }
            else
            {
                // The tracked slave has already been fully reaped (IsDestroyed) without this
                // module ever observing its IsEffectivelyDead edge - e.g. destroyed by the
                // LetSlaveLive cascade on a different producer, or some other direct destroy.
                // Nothing to react to; just stop tracking it so discovery can find a successor.
                _slaveId = ObjectId.Invalid;
            }
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// LetSlaveLive cascade (spec §1.4): on the PRODUCER's own destroy, force-kill a still-live
    /// tracked slave unless LetSlaveLive is set. Verbatim FloodUpdate.OnDestroy shape.
    /// </summary>
    protected internal override void OnDestroy()
    {
        if (_data.LetSlaveLive || _slaveId.IsInvalid)
        {
            return;
        }

        var slave = Context.GameLogic.GetObjectById(_slaveId);
        if (slave != null && !slave.IsDestroyed)
        {
            Context.GameLogic.DestroyObject(slave);
        }
    }

    // ---- the single walk: save/load + CRC + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("SlaveId", ref _slaveId);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Watches for an object this one produced (via CreatedByObjectID, e.g. an ObjectCreationUpgrade
/// spawn) and reacts to its death: optionally mirrors this object's completed upgrades onto the
/// live slave (ShareUpgrades), removes/grants named upgrades on itself when the slave dies
/// (RemoveUpgrade/GrantUpgrade), and optionally force-kills a still-live slave when this object
/// itself is destroyed (LetSlaveLive).
/// </summary>
[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class SlaveWatcherBehaviorModuleData : UpdateModuleData
{
    internal static SlaveWatcherBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<SlaveWatcherBehaviorModuleData> FieldParseTable = new IniParseTable<SlaveWatcherBehaviorModuleData>
    {
        { "RemoveUpgrade", (parser, x) => x.RemoveUpgrade = parser.ParseAssetReference() },
        { "GrantUpgrade", (parser, x) => x.GrantUpgrade = parser.ParseAssetReference() },
        { "ShareUpgrades", (parser, x) => x.ShareUpgrades = parser.ParseBoolean() },
        { "LetSlaveLive", (parser, x) => x.LetSlaveLive = parser.ParseBoolean() },
    };

    /// <summary>Upgrade removed from SELF (the producer) when the tracked slave dies.</summary>
    public string RemoveUpgrade { get; private set; }

    /// <summary>Upgrade granted to SELF (the producer) when the tracked slave dies.</summary>
    public string GrantUpgrade { get; private set; }

    /// <summary>Mirror this object's completed upgrades onto the live slave every tick.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool ShareUpgrades { get; private set; }

    /// <summary>
    /// When false (the default), a still-live tracked slave is force-destroyed when this
    /// object itself is destroyed.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public bool LetSlaveLive { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SlaveWatcherBehavior(gameObject, gameEngine.SimContext, this);
    }
}
