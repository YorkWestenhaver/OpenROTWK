// CitadelSlaughterHordeContain - R12 module port. BFME2 One Ring citadel "slaughterhouse":
// a horde that enters the citadel is normally consumed by the base SlaughterHordeContain
// mechanic (unit loot/destruction). This module layers the Ring-entry extension on top: if
// the entering horde is carrying an object that matches ObjectToDestroyForRingEntry (the
// dropped One Ring), the horde is spared the slaughter and instead becomes the ring-bearer -
// StatusForRingEntry (HOLDING_THE_RING) is applied, every upgrade in UpgradeForRingEntry is
// granted, the matched ring object is destroyed, and FXForRingEntry plays. Only when no such
// object is present does the standard slaughter/loot processing run.
//
// No GPL reference exists for this module (clean-room; task packet + spec-hordes.md census
// row 0xc0bae8 give only the module's address, not decompiled logic - see
// bfme2-workbench/research/spec-hordes.md S:52). The entry-permission and ring-entry shapes
// below are implemented directly from the task packet's behavioral summary and test cases,
// not from any decompiled source.
//
// SHARED-FILE NOTE: SlaughterHordeContainModuleData (the base class) is a separate porting
// task's file (branch r12/slaughter-horde-contain) and stays [ParseOnly] here - this module
// only reads its already-parsed fields (PassengerFilter, AllowAlliesInside/EnemiesInside/
// NeutralInside) and supplies its own full runtime module rather than chaining through a
// base-class runtime module that does not exist yet.
//
// TODO-spec (unverified): CashBackPercent's monetary refund on a non-ring slaughter has no
// confirmed ISimContext seam - a [SimState] module cannot reach a player's BankAccount
// through the frozen ISimContext member list (see ISimContext.cs S8 header: the context
// grows one member at a time per porting need, and "give a player money" has not been
// requested by any port yet). The object-destruction half of "loot/destruction" is real and
// exercised; the cash-back half is parked pending that seam.
//
// ENTRY TARGET (interpretation, unverified): "the entering horde" tests
// ObjectToDestroyForRingEntry against objects CARRIED BY the horde (any live object whose
// ParentHorde points at it - the same link HordeContainBehavior.Unpack sets on a horde's own
// members), not against the horde object itself. This keeps "upgrades... persist on horde
// after entry" (test case) consistent: the ring object is destroyed, the horde that carried
// it survives and gains the Ring status/upgrades.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CitadelSlaughterHordeContain : BehaviorModule, IUpgradeableModule
{
    private readonly CitadelSlaughterHordeContainModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public CitadelSlaughterHordeContain(GameObject gameObject, ISimContext context, CitadelSlaughterHordeContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive, matching
        // every other UpgradeLogic-driven module in this codebase (base contract: this class
        // extends UpgradeModuleData purely to inherit TriggeredBy/ConflictsWith/StartsActive -
        // the citadel's entry gating below does not itself consult Triggered).
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Whether this module's own upgrade mux has fired (test/inspector visibility only).</summary>
    internal bool Triggered => _upgradeLogic.Triggered;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // No side effect: this mux only tracks whether the module's own gating upgrade
        // (if any) has fired. See file header.
    }

    /// <summary>
    /// Entry-permission test (design-module-api shape mirrors the sibling Contain modules'
    /// AllowAlliesInside/AllowEnemiesInside/AllowNeutralInside + PassengerFilter gate). The
    /// citadel owner's own hordes are always relationship-eligible; AllowOwnPlayerInsideOverride
    /// additionally bypasses PassengerFilter for them (task packet: "overrides PassengerFilter
    /// for owner-faction hordes"). Any other player's horde is gated by the relationship-keyed
    /// Allow*Inside flag AND must still pass PassengerFilter.
    /// </summary>
    public bool CanEnter(GameObject horde)
    {
        if (horde == null)
        {
            return false;
        }

        var owner = GameObject.Owner;
        var enteringOwner = horde.Owner;

        if (enteringOwner == owner)
        {
            return _data.AllowOwnPlayerInsideOverride || PassesPassengerFilter(horde);
        }

        // Player.Allies/Player.Enemies (not the Team-keyed GetRelationship/SetRelationship
        // pair): the same one-directional "is the entrant's owner an ally/enemy of the
        // citadel's owner" test AutoHealBehavior already uses for its own ally gate. Neither
        // set containing the entrant's owner means Neutral (the default relationship).
        var relationshipAllowed = owner != null && owner.Enemies.Contains(enteringOwner) ? _data.AllowEnemiesInside
            : owner != null && owner.Allies.Contains(enteringOwner) ? _data.AllowAlliesInside
            : _data.AllowNeutralInside;

        return relationshipAllowed && PassesPassengerFilter(horde);
    }

    private bool PassesPassengerFilter(GameObject horde) => _data.PassengerFilter?.Matches(horde) ?? true;

    /// <summary>
    /// Runs the citadel's entry processing for a horde that has already been granted entry
    /// (callers are expected to have checked <see cref="CanEnter"/> - parked: the order/command
    /// path that drives a horde into a citadel contain is not part of this port). Returns
    /// false when entry is not permitted and nothing happens.
    /// </summary>
    public bool TryEnterHorde(GameObject horde)
    {
        if (!CanEnter(horde))
        {
            return false;
        }

        var ringMatches = FindRingEntryMatches(horde);
        if (ringMatches.Count > 0)
        {
            ApplyRingEntry(horde, ringMatches);
        }
        else
        {
            ProcessSlaughter(horde);
        }

        return true;
    }

    private List<GameObject> FindRingEntryMatches(GameObject horde)
    {
        var matches = new List<GameObject>();

        var filter = _data.ObjectToDestroyForRingEntry;
        if (filter == null)
        {
            return matches;
        }

        // Cargo carried by the entering horde: any live object whose ParentHorde link
        // (the same field HordeContainBehavior.Unpack sets on a horde's own members) points
        // at this horde. Deterministic - ObjectsAscendingId is the blessed whole-world scan
        // order (design-module-api §6) - and needs no Contain-module machinery on the horde
        // itself, unlike IContainModule (reserved for structures/vehicles that hold hordes,
        // spec-hordes.md §2 row 63 - not what a horde's own carried cargo uses).
        foreach (var candidate in Context.GameLogic.ObjectsAscendingId)
        {
            if (candidate != null && candidate.ParentHorde == horde && filter.Matches(candidate))
            {
                matches.Add(candidate);
            }
        }

        return matches;
    }

    private void ApplyRingEntry(GameObject horde, List<GameObject> ringObjects)
    {
        horde.SetObjectStatus(_data.StatusForRingEntry, true);

        foreach (var upgradeReference in _data.UpgradeForRingEntry)
        {
            var upgrade = upgradeReference?.Value;
            if (upgrade != null)
            {
                horde.Upgrade(upgrade);
            }
        }

        foreach (var ringObject in ringObjects)
        {
            Context.GameLogic.DestroyObject(ringObject);
        }

        if (!string.IsNullOrEmpty(_data.FXForRingEntry))
        {
            Context.Events.FireFXAtObject(_data.FXForRingEntry, horde.Id);
        }
    }

    /// <summary>
    /// Base SlaughterHordeContain semantics (unit loot/destruction): the entering horde is
    /// consumed. See file header TODO-spec for the parked cash-back half of "loot".
    /// </summary>
    private void ProcessSlaughter(GameObject horde)
    {
        Context.GameLogic.DestroyObject(horde);
    }

    // ---- the single walk: the mux trigger flag is the only mutable sim state this module
    // owns (api-freeze-v1 S4 / §3). ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class CitadelSlaughterHordeContainModuleData : SlaughterHordeContainModuleData
{
    internal static new CitadelSlaughterHordeContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static new readonly IniParseTable<CitadelSlaughterHordeContainModuleData> FieldParseTable = SlaughterHordeContainModuleData.FieldParseTable
        .Concat(new IniParseTable<CitadelSlaughterHordeContainModuleData>
        {
            { "AllowOwnPlayerInsideOverride", (parser, x) => x.AllowOwnPlayerInsideOverride = parser.ParseBoolean() },
            { "StatusForRingEntry", (parser, x) => x.StatusForRingEntry = parser.ParseEnum<ObjectStatus>() },
            { "UpgradeForRingEntry", (parser, x) => x.UpgradeForRingEntry = parser.ParseUpgradeReferenceArray() },
            { "ObjectToDestroyForRingEntry", (parser, x) => x.ObjectToDestroyForRingEntry = ObjectFilter.Parse(parser) },
            { "FXForRingEntry", (parser, x) => x.FXForRingEntry = parser.ParseAssetReference() }
        });

    public bool AllowOwnPlayerInsideOverride { get; private set; }
    public ObjectStatus StatusForRingEntry { get; private set; }

    /// <summary>
    /// All upgrades granted to a horde that enters carrying the dropped ring. Was a single
    /// ParseAssetReference on the [ParseOnly] stub; widened to the standard multi-name
    /// upgrade-reference array (matches UpgradeLogicData.TriggeredBy's own grammar) because
    /// the task packet's test cases require multiple upgrades to be grantable from one
    /// UpgradeForRingEntry declaration.
    /// </summary>
    public LazyAssetReference<UpgradeTemplate>[] UpgradeForRingEntry { get; private set; } = System.Array.Empty<LazyAssetReference<UpgradeTemplate>>();

    public ObjectFilter ObjectToDestroyForRingEntry { get; private set; }
    public string FXForRingEntry { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CitadelSlaughterHordeContain(gameObject, gameEngine.SimContext, this);
    }
}
