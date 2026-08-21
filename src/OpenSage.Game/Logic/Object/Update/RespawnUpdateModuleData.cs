// RespawnUpdateModuleData - the parsed data half of the R14 respawn seam.
//
// It lives in its own file, apart from the [SimState] RespawnUpdate, for the reason the
// GiveUpgradeUpdate/GiveUpgradeUpdateModuleData split already records: the SIMCORE analyzer
// attaches to a FILE that declares any [SimState] type (docs/simcore-analyzer.md, Scoped
// mode), so keeping ModuleData beside the module would drag the whole INI vocabulary behind
// the Fix64 wall. Splitting is the landed remedy, not an exemption.
//
// Behavioral reference: BFME2/ROTWK-only. generals-gpl and generals-community contain NO
// RespawnUpdate (grep -rli respawn returns only RebuildHoleBehavior and SpawnBehavior), so
// every field meaning below comes from the shipped INI vocabulary and the written seam design
// (bfme2-workbench/research/design-respawn-seam.md §1-§2) - never from the retail binary.
//
// AUDIT NOTES (the fields whose types changed as part of this port):
//   * RespawnRules Health: was a float-backed Percentage; it is now an INTEGER percent,
//     because it feeds BodyModule.SetInitialHealth(int percent) whose application is
//     BodyDamageCore's exact Int128 mul-div. Census: all 547 shipped AotR RespawnRules
//     declarations read Health:100%, so no fractional case exists to lose.
//   * Every millisecond duration (DeathAnimationTime, RespawnAnimationTime, RespawnRules Time,
//     RespawnEntry Time) is now LogicFrameSpan through the S5 integer-only boundary
//     (ceil(ms * fps / 1000)), not a raw int of milliseconds: they are [SimState] timer
//     inputs and must quantize identically on every architecture.
//
// CENSUS FINDING recorded here because it decides how much of this data is live: AotR 8.0
// declares RespawnEntry ONLY inside comments - every `RespawnEntry = Level:N ...` line in the
// shipped data is preceded by ';'. The level table is therefore parsed and honoured but
// exercised by zero shipped objects; RespawnRules Cost/Time is the live pricing path.

#nullable enable

using System.Collections.Generic;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RespawnUpdateModuleData : UpdateModuleData
{
    internal static RespawnUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<RespawnUpdateModuleData> FieldParseTable = new IniParseTable<RespawnUpdateModuleData>
    {
        { "DeathAnim", (parser, x) => x.DeathAnim = parser.ParseEnum<ModelConditionFlag>() },
        { "DeathFX", (parser, x) => x.DeathFX = parser.ParseAssetReference() },
        { "DeathAnimationTime", (parser, x) => x.DeathAnimationTime = parser.ParseDurationLogicFrames() },
        { "InitialSpawnFX", (parser, x) => x.InitialSpawnFX = parser.ParseAssetReference() },
        { "RespawnAnim", (parser, x) => x.RespawnAnim = parser.ParseEnum<ModelConditionFlag>() },
        { "RespawnFX", (parser, x) => x.RespawnFX = parser.ParseAssetReference() },
        { "RespawnAnimationTime", (parser, x) => x.RespawnAnimationTime = parser.ParseDurationLogicFrames() },
        { "AutoRespawnAtObjectFilter", (parser, x) => x.AutoRespawnAtObjectFilter = ObjectFilter.Parse(parser) },
        { "ButtonImage", (parser, x) => x.ButtonImage = parser.ParseAssetReference() },
        { "RespawnRules", (parser, x) => x.RespawnRules = RespawnRules.Parse(parser) },
        { "RespawnEntry", (parser, x) => x.RespawnEntries.Add(RespawnEntry.Parse(parser)) },
        { "RespawnAsTemplate", (parser, x) => x.RespawnAsTemplate = parser.ParseAssetReference() },
    };

    /// <summary>Model condition held for <see cref="DeathAnimationTime"/> after a claimed death.</summary>
    public ModelConditionFlag DeathAnim { get; private set; }

    public string? DeathFX { get; private set; }

    /// <summary>How long the death presentation runs before the object goes hidden.</summary>
    public LogicFrameSpan DeathAnimationTime { get; private set; }

    /// <summary>
    /// Parsed and held, deliberately unconsumed: it names the FX for the hero's FIRST
    /// appearance (object creation), not for a revive, and this seam owns nothing at creation
    /// time. Recorded as a gap rather than guessed into the revive path.
    /// </summary>
    public string? InitialSpawnFX { get; private set; }

    /// <summary>Model condition held for <see cref="RespawnAnimationTime"/> after a revive.</summary>
    public ModelConditionFlag RespawnAnim { get; private set; }

    public string? RespawnFX { get; private set; }

    /// <summary>How long the respawn presentation runs after the object is alive again.</summary>
    public LogicFrameSpan RespawnAnimationTime { get; private set; }

    /// <summary>
    /// Parsed and held, deliberately unconsumed (OQ-7/OQ-8, filed). It selects WHERE an
    /// auto-respawning hero reappears. OQ-1 was decided in favour of in-place survival
    /// (dr-0033), so the hero is revived exactly where it fell and no placement is chosen;
    /// honouring this filter needs both an unbounded object search (IPartitionQuery offers
    /// only QueryObjectsInRadius) and a "move an existing object" member that ISimContext does
    /// not have, since transforms are still float substrate (D-7). Both are separate packets.
    /// </summary>
    public ObjectFilter? AutoRespawnAtObjectFilter { get; private set; }

    /// <summary>The revive slot's button art. Client-side only; the sim never reads it.</summary>
    public string? ButtonImage { get; private set; }

    /// <summary>The default (level-independent) revive rules. Null when the block is absent.</summary>
    public RespawnRules? RespawnRules { get; private set; }

    /// <summary>
    /// Per-level cost/time overrides, in declaration order. Empty for every shipped AotR
    /// object - see the census finding in the file header.
    /// </summary>
    public List<RespawnEntry> RespawnEntries { get; } = new List<RespawnEntry>();

    /// <summary>
    /// Parsed and held, deliberately unconsumed. It names a DIFFERENT template to come back
    /// as (e.g. CreateAHeroScaled -> CreateAHero), which an in-place revive cannot express -
    /// changing template means destroy-and-recreate, which is what OQ-1 decided against
    /// (dr-0033). Recorded as a known divergence for the objects that declare it rather than
    /// silently ignored.
    /// </summary>
    public string? RespawnAsTemplate { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RespawnUpdate(gameObject, gameEngine.SimContext, this);
    }
}

/// <summary>
/// <c>RespawnRules = AutoSpawn:&lt;bool&gt; Cost:&lt;int&gt; Time:&lt;ms&gt; Health:&lt;percent&gt;%</c>.
/// </summary>
public sealed class RespawnRules
{
    internal static RespawnRules Parse(IniParser parser)
    {
        return new RespawnRules()
        {
            AutoSpawn = parser.ParseAttributeBoolean("AutoSpawn"),
            Cost = parser.ParseAttributeInteger("Cost"),
            Time = parser.ParseAttributeDurationLogicFrames("Time"),
            HealthPercent = parser.ParseAttributeIntegerPercentage("Health")
        };
    }

    /// <summary>
    /// Yes: the hero comes back on a plain countdown, with no order and no money. No (the
    /// shipped default for buyable heroes): the hero waits for a revive purchase.
    /// </summary>
    public bool AutoSpawn { get; private set; }

    /// <summary>Gold cost of a revive purchase. Integer money (F3), never Fix64.</summary>
    public int Cost { get; private set; }

    /// <summary>Revive countdown, quantized to whole logic frames at parse time.</summary>
    public LogicFrameSpan Time { get; private set; }

    /// <summary>Percent of InitialHealth the revived hero comes back at (100 for "100%").</summary>
    public int HealthPercent { get; private set; }
}

/// <summary>
/// <c>RespawnEntry = Level:&lt;n&gt; Cost:&lt;int&gt; Time:&lt;ms&gt;</c> - a per-level override of
/// <see cref="RespawnRules"/>' cost and time.
/// </summary>
public sealed class RespawnEntry
{
    internal static RespawnEntry Parse(IniParser parser)
    {
        return new RespawnEntry()
        {
            Level = parser.ParseAttributeInteger("Level"),
            Cost = parser.ParseAttributeInteger("Cost"),
            Time = parser.ParseAttributeDurationLogicFrames("Time")
        };
    }

    public int Level { get; private set; }

    public int Cost { get; private set; }

    public LogicFrameSpan Time { get; private set; }
}
