// ReplaceSelfUpgrade - R12 port. No generals-gpl sibling and no clean-room spec in
// bfme2-workbench/research/ (gplRef empty in the task packet), so this is the minimal
// faithful behavior the task packet describes: a triggered upgrade that metamorphoses the
// owning object into another object.
//
// Behavior (task packet, no GPL to translate against):
//   - On trigger: spawn ReplaceWith at the object's own position/orientation/layer, owned
//     by the same player; spawn every AndThenAddAs entry the same way, in declaration
//     order; then remove the original object.
//   - Ownership/team: the replacement and every AndThenAddAs spawn are created with the
//     original object's Owner (CreateObjectAt's owner parameter). Team is a separate
//     GameObject-owned field (not part of the donor-copy that CreateObjectAt performs) -
//     TODO-spec: carrying Team across the swap is unverified retail behavior, filed not
//     invented, since no source names it as a Player-only concept here.
//   - Veterancy: the replacement's ExperienceTracker level is set to at least the
//     original's, when the original is above the base rank. AndThenAddAs spawns are NOT
//     given the original's veterancy (the packet only asks for it on ReplaceWith - they are
//     new units being added, not the metamorphosed unit).
//   - Health ratio: TODO-spec, NOT carried forward. BodyModule.Health/MaxHealth are float
//     substrate (D-7); this module's file declares [SimState] so the whole tree is under
//     the float quarantine (SIMCORE001), and no Fix64 health-ratio facade exists yet on
//     GameObject (compare VisionRange/CollisionMinorRadius). Adding one is a shared-file
//     identifier the task packet's reservedNames list does not carry, so it is deferred
//     rather than guessed at.
//   - Ordering (spawn before destroy, not destroy-then-spawn): CreateObjectAt reads the
//     donor's live Transform/Layer, so the swap creates every new object off the still-live
//     original and destroys it last. GameLogic.DestroyObject is deferred-reap (the object
//     stays queryable, with an intact transform, until end of frame), so either order would
//     produce the same end state; spawn-first is chosen because it never depends on reading
//     a destroyed donor.
//   - Permanent (UpgradeLogicData.Permanent, the workbench-identified shared vocabulary
//     gap): parsed by the shared UpgradeLogicData already and inherited here unchanged.
//     UpgradeModule (the legacy base) does not act on it yet, and this module goes through
//     its own UpgradeLogic the same way every other R12 upgrade port does (StatusBitsUpgrade,
//     LevelUpUpgrade) - acting on Permanent is a change to the shared upgrade mux, out of
//     scope for a single module's port.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ReplaceSelfUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly ReplaceSelfUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public ReplaceSelfUpgrade(GameObject gameObject, ISimContext context, ReplaceSelfUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// The metamorphosis: spawn ReplaceWith (carrying veterancy forward), spawn every
    /// AndThenAddAs entry in declaration order, then remove the original. All spawns stand
    /// at the original's own position/orientation/layer and are owned by its player (see the
    /// file header for the ordering and TODO-spec notes).
    /// </summary>
    private void OnUpgradeTriggered()
    {
        var donor = GameObject;
        var owner = donor.Owner;

        if (!string.IsNullOrEmpty(_data.ReplaceWith))
        {
            var replaceWithDefinition = Context.Assets.GetObjectDefinition(_data.ReplaceWith);
            if (replaceWithDefinition is not null)
            {
                var replacement = Context.GameLogic.CreateObjectAt(replaceWithDefinition, owner, donor);
                if (replacement is not null)
                {
                    CarryForwardVeterancy(donor, replacement);
                }
            }
        }

        foreach (var andThenAddAs in _data.AndThenAddAs)
        {
            if (string.IsNullOrEmpty(andThenAddAs))
            {
                continue;
            }

            var addAsDefinition = Context.Assets.GetObjectDefinition(andThenAddAs);
            if (addAsDefinition is null)
            {
                continue;
            }

            Context.GameLogic.CreateObjectAt(addAsDefinition, owner, donor);
        }

        Context.GameLogic.DestroyObject(donor);
    }

    /// <summary>
    /// GPL-unreferenced (no source for this task): raises the replacement to at least the
    /// donor's veterancy level. SetMinVeterancyLevel is a no-op when the replacement is
    /// already at or above that level, so a replacement spawned at a HIGHER base rank than
    /// the donor is never demoted.
    /// </summary>
    private static void CarryForwardVeterancy(GameObject donor, GameObject replacement)
    {
        var donorLevel = donor.ExperienceTracker.VeterancyLevel;
        if (donorLevel > VeterancyLevel.Regular)
        {
            replacement.ExperienceTracker.SetMinVeterancyLevel(donorLevel);
        }
    }

    // Field order = declaration order (F9). The only mutable sim field this module owns is
    // the upgrade mux triggered flag; the objects it creates/destroys are GameLogic-owned
    // state, persisted by GameLogic's own walk.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class ReplaceSelfUpgradeModuleData : UpgradeModuleData
{
    internal static ReplaceSelfUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ReplaceSelfUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ReplaceSelfUpgradeModuleData>
        {
            { "ReplaceWith", (parser, x) => x.ReplaceWith = parser.ParseAssetReference() },
            { "AndThenAddA", (parser, x) => x.AndThenAddAs.Add(parser.ParseAssetReference()) }
        });

    public string ReplaceWith { get; private set; }
    public List<string> AndThenAddAs { get; } = new List<string>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ReplaceSelfUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
