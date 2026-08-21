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
}
