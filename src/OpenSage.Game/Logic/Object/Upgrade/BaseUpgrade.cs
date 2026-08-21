// BaseUpgrade - R14 port (packet g5-baseupgrade), per
// bfme2-workbench/research/modules-r13/specs/BaseUpgradeModuleData.md. No GPL module named
// BaseUpgrade exists (the spec's header confirms this); the shared UpgradeModule/UpgradeLogic
// scaffolding this class rides on is already-ported GPL (GameLogic/Module/UpgradeModule.h ->
// UpgradeModule.cs), unmodified.
//
// Behavior (spec §5.3, all GROUNDED except where noted):
//   - OnUpgrade() resolves BuildingTemplateName to an ObjectDefinition; if not found, no spawn
//     and no other side effect (the upgrade itself still latches triggered via the caller).
//   - Enumerates the carrying object's bones whose name starts with PlacementPrefix, across
//     every draw module on the object (mirrors retail's walk over the Draw-module sub-object
//     list, spec §5.3 step 3), reusing the same StartsWith idiom already ported in
//     W3dSupplyDraw.cs. Retail records at most 32 candidates; ported the same way.
//   - PlacementIndex indexes directly into that 0-based match list (spec §5.3 step 4): index
//     0 is unreachable via this branch (0 < 1 always takes the fallback), so PlacementIndex is
//     effectively 1-based, and out-of-range indices (< 1 or >= match count) silently fall back
//     to the carrying object's own position rather than failing - a genuine retail quirk, not
//     a defensive-only guard, ported faithfully rather than "fixed".
//   - The spawn is synchronous, inline, same-frame - no queue, no player input, no re-arming
//     (the 28-byte stateless retail runtime instance has nowhere to store re-fire/occupancy
//     state - spec §4/§5.4).
//   - Owner is the carrying object's own Owner (spec §5.3 step 5, INFERRED but consistent with
//     every sibling UpgradeModule subtype, none of which carry a separate owner field either).
//   - Facing is inherited from the carrying object's own rotation, and CreatedByObjectID is
//     stamped on the new object (spec §5.3 step 6, INFERRED, low-risk, mirrors
//     ObjectCreationUpgrade.cs's identical CreatedByObjectID line).
//   - The two OnUpgrade() entry guards (spec §5.3 step 1, Q-F1) and the exact
//     retail-vs-fork bone-iteration-order equivalence (Q-F3) are flagged by the spec as
//     INFERRED/open but explicitly non-gating for this port - see the spec for the settling
//     observables.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Data.Ini;
using OpenSage.Graphics;

namespace OpenSage.Logic.Object;

internal sealed class BaseUpgrade : UpgradeModule
{
    // Retail records at most 32 bone-prefix candidates (spec §5.3 step 3,
    // FUN_00672a73(..., 32, 0)).
    internal const int MaxPlacementCandidates = 32;

    private readonly BaseUpgradeModuleData _moduleData;

    internal BaseUpgrade(GameObject gameObject, IGameEngine gameEngine, BaseUpgradeModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    protected override void OnUpgrade()
    {
        var buildingTemplate = GameEngine.AssetLoadContext.AssetStore.ObjectDefinitions.GetByName(_moduleData.BuildingTemplateName);
        if (buildingTemplate == null)
        {
            return;
        }

        List<(ModelBone Bone, Matrix4x4 WorldTransform)> candidates;
        if (GameObject.Drawable != null && !string.IsNullOrEmpty(_moduleData.PlacementPrefix))
        {
            candidates = GameObject.Drawable.FindBonesWithPrefix(_moduleData.PlacementPrefix).Take(MaxPlacementCandidates).ToList();
        }
        else
        {
            candidates = [];
        }

        var spawnPosition = ResolvePlacementPosition(candidates, _moduleData.PlacementIndex, GameObject.Translation);

        var newObject = GameEngine.GameLogic.CreateObject(buildingTemplate, GameObject.Owner);
        if (newObject == null)
        {
            return;
        }

        newObject.UpdateTransform(spawnPosition, GameObject.Rotation);
        newObject.CreatedByObjectID = GameObject.Id;
    }

    /// <summary>
    /// The retail branch (spec §5.3 step 4): <c>candidate[PlacementIndex]</c> is a direct,
    /// unadjusted array index into the 0-based prefix-match list. Index <c>0</c> is therefore
    /// unreachable through this branch (<c>0 &lt; 1</c> always takes the fallback), which is
    /// what makes <see cref="BaseUpgradeModuleData.PlacementIndex"/> effectively 1-based; an
    /// index at or past <paramref name="candidates"/>'s count also silently falls back to
    /// <paramref name="fallbackPosition"/> (the carrying object's own position) instead of
    /// failing. Both branches are genuine retail behavior, ported faithfully rather than
    /// "fixed" - see BaseUpgradeModuleData.md §5.3 step 4 for the decompiled control flow this
    /// mirrors.
    /// </summary>
    internal static Vector3 ResolvePlacementPosition(
        IReadOnlyList<(ModelBone Bone, Matrix4x4 WorldTransform)> candidates,
        int placementIndex,
        Vector3 fallbackPosition)
    {
        if (placementIndex >= 1 && placementIndex < candidates.Count)
        {
            return candidates[placementIndex].WorldTransform.Translation;
        }

        return fallbackPosition;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Spawns a single new building, synchronously and instantly, at a bone position on the
/// carrying object when <see cref="UpgradeLogicData.TriggeredBy"/> completes - the "grow a
/// permanent structure onto an existing building via an upgrade" primitive AotR's MordorBase
/// uses to grow its tents. See BaseUpgradeModuleData.md for the full retail trace.
/// </summary>
[AddedIn(SageGame.Bfme)]
public sealed class BaseUpgradeModuleData : UpgradeModuleData
{
    internal static BaseUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<BaseUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<BaseUpgradeModuleData>
        {
            { "BuildingTemplateName", (parser, x) => x.BuildingTemplateName = parser.ParseString() },
            { "PlacementPrefix", (parser, x) => x.PlacementPrefix = parser.ParseString() },
            { "PlacementIndex", (parser, x) => x.PlacementIndex = parser.ParseInteger() },
        });

    public string BuildingTemplateName { get; private set; }
    public string PlacementPrefix { get; private set; }
    public int PlacementIndex { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BaseUpgrade(gameObject, gameEngine, this);
    }
}
