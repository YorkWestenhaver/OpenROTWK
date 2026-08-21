#nullable enable

// S9-01 (R15 L3): the ONE class in the AI that touches the running game.
//
// Everything else in OpenSage.Logic.AI.Skirmish sees only IAiWorldView. That containment is the
// point: if a manager needs a new fact, the fact is added here and to the fakes, and the
// manager stays testable with no game. Reviewers should treat any `using OpenSage.Logic.Object`
// or `_game.` appearing in a manager file as a defect against this packet.
//
// Per-frame snapshot policy: the object lists are rebuilt at most once per logic frame and
// cached, because several managers read them and walking every object in the world per manager
// would be needless quadratic work in a 20-minute soak. The cache key is the logic frame, so a
// manager can never see a half-updated list mid-frame.

using System.Collections.Generic;
using OpenSage.Logic.Object;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// <see cref="IAiWorldView"/> over a live <see cref="IGame"/> and one <see cref="Player"/>.
/// </summary>
public sealed class LiveAiWorldView : IAiWorldView
{
    private readonly IGame _game;
    private readonly Player _player;

    private readonly List<AiObjectView> _ownObjects = new();
    private readonly List<AiObjectView> _enemyObjects = new();

    private uint _snapshotFrame;
    private bool _hasSnapshot;

    public int PlayerIndex { get; }

    public string PlayerName { get; }

    public Difficulty Difficulty { get; }

    public string? Side => _player.Side;

    public uint CurrentFrame => _game.GameLogic.CurrentFrame.Value;

    public int Money
    {
        get
        {
            // BankAccount stores uint; the AI does int arithmetic so that an over-spend
            // subtraction produces a negative number instead of wrapping to ~4 billion.
            var money = _player.BankAccount.Money;
            return money > int.MaxValue ? int.MaxValue : (int)money;
        }
    }

    public IReadOnlyList<AiObjectView> OwnObjects
    {
        get
        {
            EnsureSnapshot();
            return _ownObjects;
        }
    }

    public IReadOnlyList<AiObjectView> EnemyObjects
    {
        get
        {
            EnsureSnapshot();
            return _enemyObjects;
        }
    }

    public SkirmishAIData? SkirmishAIData { get; }

    public AIData? AIData => _game.AssetStore.AIData.Current;

    public DifficultyTuning? DifficultyTuning { get; }

    public LiveAiWorldView(IGame game, Player player, Difficulty difficulty)
    {
        _game = game;
        _player = player;

        PlayerIndex = (int)player.Id;
        PlayerName = player.Name ?? string.Empty;
        Difficulty = difficulty;

        // Resolved once: these are static mod data for the whole match. A null SkirmishAIData
        // means the mod's Default/skirmishaidata.ini never loaded (see blackboard A1-G3) - the
        // AI must still run, on its built-in defaults, rather than crash the match.
        SkirmishAIData = FindSkirmishAIData(game);
        DifficultyTuning = FindDifficultyTuning(SkirmishAIData, difficulty);
    }

    private static SkirmishAIData? FindSkirmishAIData(IGame game)
    {
        foreach (var data in game.AssetStore.SkirmishAIDatas)
        {
            return data;
        }

        return null;
    }

    private static DifficultyTuning? FindDifficultyTuning(SkirmishAIData? data, Difficulty difficulty)
    {
        if (data == null)
        {
            return null;
        }

        foreach (var tuning in data.DifficultyTunings)
        {
            if (tuning.Difficulty == difficulty)
            {
                return tuning;
            }
        }

        return null;
    }

    private void EnsureSnapshot()
    {
        var frame = CurrentFrame;
        if (_hasSnapshot && _snapshotFrame == frame)
        {
            return;
        }

        _ownObjects.Clear();
        _enemyObjects.Clear();

        foreach (var gameObject in _game.GameLogic.Objects)
        {
            var owner = gameObject.Owner;
            if (owner == null || gameObject.IsDestroyed)
            {
                continue;
            }

            if (owner == _player)
            {
                _ownObjects.Add(Snapshot(gameObject, owner));
            }
            else if (_player.Enemies != null && _player.Enemies.Contains(owner))
            {
                _enemyObjects.Add(Snapshot(gameObject, owner));
            }
        }

        // GameLogic.Objects enumeration order is an implementation detail; sorting by object id
        // makes the snapshot - and therefore every decision made from it - order-independent.
        _ownObjects.Sort(CompareById);
        _enemyObjects.Sort(CompareById);

        _snapshotFrame = frame;
        _hasSnapshot = true;
    }

    private static int CompareById(AiObjectView a, AiObjectView b) => a.Id.Index.CompareTo(b.Id.Index);

    private static AiObjectView Snapshot(GameObject gameObject, Player owner)
    {
        // An object with no body (or a zero max) reports full health rather than 0/0: the AI
        // treats "unknown health" as healthy so that a missing body never looks like a target
        // worth finishing off.
        var body = gameObject.BodyModule;
        var maxHealth = body is null ? 0.0f : body.MaxHealth;
        var healthFraction = body is not null && maxHealth > 0.0f ? body.Health / maxHealth : 1.0f;

        return new AiObjectView(
            gameObject.Id,
            gameObject.Definition.Name,
            gameObject.Translation,
            (int)owner.Id,
            gameObject.IsKindOf(ObjectKinds.Structure),
            gameObject.IsBeingConstructed(),
            healthFraction);
    }
}
