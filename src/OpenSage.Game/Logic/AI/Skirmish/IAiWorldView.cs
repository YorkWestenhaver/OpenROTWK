#nullable enable

// S9-01 (R15 L3): the skirmish AI's ONLY read of the world.
//
// Every manager the brain owns (economy, base, team, attack) reads through this interface and
// nothing else - no GameObject, no GameLogic, no Scene3D, no AssetStore lookups of its own.
// That is what makes the managers unit-testable with no game: a test hands the manager a
// FakeAiWorldView and asserts on the orders it emits. The live implementation
// (LiveAiWorldView) is the single place that touches engine state, so if a manager is
// deterministic over this snapshot, the AI is deterministic.
//
// Growth rule for later packets: this interface is APPEND-ONLY within a campaign round. Adding
// a member forces every fake to grow with it, so add a member only when a manager genuinely
// cannot be written without it, and say which packet needs it.

using System.Collections.Generic;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// A read-only, per-player snapshot of everything the skirmish AI is allowed to know.
/// Implementations must be side-effect free: reading a property never mutates game state.
/// </summary>
public interface IAiWorldView
{
    /// <summary>The logic frame this view is reporting. Managers must never read a clock of their own.</summary>
    uint CurrentFrame { get; }

    /// <summary>Index of the player this brain plays for (matches <see cref="Player.Id"/> / PlayerManager order).</summary>
    int PlayerIndex { get; }

    /// <summary>The player's name as the map/lobby defined it. Trace and report text only.</summary>
    string PlayerName { get; }

    /// <summary>The player's faction side name (e.g. "FactionMordor"), or null when unknown.</summary>
    string? Side { get; }

    /// <summary>Difficulty this AI plays at; selects the matching <see cref="DifficultyTuning"/>.</summary>
    Difficulty Difficulty { get; }

    /// <summary>
    /// Current funds, as a non-negative int. The engine stores money as uint; the AI does int
    /// arithmetic throughout (S9-03) so that "can I afford this" subtraction cannot wrap.
    /// </summary>
    int Money { get; }

    /// <summary>Objects owned by this player, in ascending object-id order. Rebuilt per frame.</summary>
    IReadOnlyList<AiObjectView> OwnObjects { get; }

    /// <summary>
    /// Objects owned by players this player is at war with, in ascending object-id order.
    /// No fog-of-war filtering yet - the visibility slice is a later packet's job, and the
    /// interface is the place it will land (rename to VisibleEnemyObjects when it does).
    /// </summary>
    IReadOnlyList<AiObjectView> EnemyObjects { get; }

    /// <summary>
    /// The mod's SkirmishAIData block (AotR ships one in Default/skirmishaidata.ini), or null
    /// when the data never loaded. Managers tune off this rather than off hardcoded constants
    /// (ruling S9-R15-B); a null here means "AI runs on its built-in defaults", never a crash.
    /// </summary>
    SkirmishAIData? SkirmishAIData { get; }

    /// <summary>The global AIData block, or null when absent. Same null policy as above.</summary>
    AIData? AIData { get; }

    /// <summary>
    /// The <see cref="DifficultyTuning"/> from <see cref="SkirmishAIData"/> matching
    /// <see cref="Difficulty"/>, or null when the data has no entry for it.
    /// </summary>
    DifficultyTuning? DifficultyTuning { get; }

    // ==== BEGIN (S9-06) base/plot slice =================================================
    //
    // Added because AiBaseManager cannot be written without it: the members above describe
    // objects and money, and none of them can answer "which of my objects is an empty castle
    // build plot" or "what am I allowed to put on one". Both facts need KINDOF flags and an
    // AssetStore walk, which a manager is forbidden to do (see the file header).
    //
    // Region markers are here so S9-08 can append its own slice below this one without the two
    // packets colliding in the middle of the interface. Append a NEW region; do not grow this
    // one.

    /// <summary>
    /// The player's own castle build plots (KINDOF BASE_FOUNDATION objects), in ascending
    /// object-id order, rebuilt per frame alongside <see cref="OwnObjects"/>.
    /// </summary>
    /// <remarks>
    /// Empty is normal and not an error: before a packed castle is unpacked the plot ring does
    /// not exist yet, which is precisely why <see cref="AiPlotKind.PackedCastle"/> is reported
    /// through this same list.
    /// </remarks>
    IReadOnlyList<AiPlotView> Plots { get; }

    /// <summary>
    /// Structures this player may place on a free plot, cheapest first (ties by ordinal name).
    /// Static mod data: resolved once per match, never per frame.
    /// </summary>
    /// <remarks>
    /// The membership rule is the sim's own: the definition carries KINDOF NEED_BASE_FOUNDATION,
    /// the exact test whose failure makes CastleOrderHandler return
    /// <c>TemplateNotBuildableOnFoundation</c>. Side filtering on top of it is a v1 heuristic
    /// that packet S9-13 (.bse castle templates) is expected to replace.
    /// </remarks>
    IReadOnlyList<AiBuildableTemplate> BuildableStructures { get; }

    // ==== END (S9-06) base/plot slice ===================================================

    // ==== BEGIN (S9-08) production slice ================================================
    //
    // Added because AiProductionManager cannot be written without it. "Which of my buildings can
    // train something right now, and what may it train" needs a ProductionUpdate module lookup,
    // a CommandSet walk and an ObjectDefinition cost read - three things a manager is forbidden
    // to do (see the file header). The team manager needs no slice of its own: it recruits out
    // of OwnObjects, using the horde facts S9-08 added to AiObjectView.
    //
    // Append a NEW region below this one; do not grow this one.

    /// <summary>
    /// The player's own finished unit-producing structures, in ascending object-id order,
    /// rebuilt per frame alongside <see cref="OwnObjects"/>.
    /// </summary>
    /// <remarks>
    /// Membership is the sim's own rule: the object has a ProductionUpdate module. Whether it
    /// will actually accept another entry right now is reported separately as
    /// <see cref="AiProducerView.CanEnqueue"/>, so a manager can tell "no producers" (build a
    /// barracks) from "producers all full" (wait), which are opposite decisions.
    /// </remarks>
    IReadOnlyList<AiProducerView> Producers { get; }

    // ==== END (S9-08) production slice ==================================================
}
