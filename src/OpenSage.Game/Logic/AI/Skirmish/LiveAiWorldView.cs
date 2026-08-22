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

using System;
using System.Collections.Generic;
using OpenSage.Gui.ControlBar;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Castle;
using OpenSage.Logic.Object.Update;

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

    // (S9-06) Rebuilt with the object snapshot; resolved once for the whole match.
    private readonly List<AiPlotView> _plots = new();
    private readonly List<AiBuildableTemplate> _buildableStructures = new();

    // (S9-08) Rebuilt with the object snapshot. The trainable list hanging off each producer is
    // a property of the DEFINITION (its CommandSet), not of the instance, so it is resolved once
    // per definition name and shared by every producer of that type for the rest of the match -
    // a CommandSet walk per barracks per frame would be pure waste in a 20-minute soak.
    private readonly List<AiProducerView> _producers = new();
    private readonly Dictionary<string, IReadOnlyList<AiTrainableUnit>> _trainableByDefinition =
        new(StringComparer.OrdinalIgnoreCase);

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

    // ---- (S9-06) base/plot slice ----

    public IReadOnlyList<AiPlotView> Plots
    {
        get
        {
            EnsureSnapshot();
            return _plots;
        }
    }

    public IReadOnlyList<AiBuildableTemplate> BuildableStructures => _buildableStructures;

    // ---- (S9-08) production slice ----

    public IReadOnlyList<AiProducerView> Producers
    {
        get
        {
            EnsureSnapshot();
            return _producers;
        }
    }

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

        // (S9-06) Buildable templates are static mod data for the whole match - the AssetStore
        // does not gain object definitions mid-game - so this walk happens once, not per frame.
        BuildBuildableStructures(game, player, _buildableStructures);
    }

    // ---- (S9-06) buildable-template resolution ----

    /// <summary>
    /// Collects the structures this player may place on a castle plot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership is the SIM's own rule, not a second opinion: a definition qualifies iff it
    /// carries KINDOF NEED_BASE_FOUNDATION, which is exactly the test
    /// CastleOrderHandler.HandleFoundationConstruct applies before it will accept a
    /// FoundationConstruct (its TemplateNotBuildableOnFoundation guard). Reusing the handler's
    /// acceptance test as the AI's candidate filter is what stops the two drifting apart.
    /// </para>
    /// <para>
    /// Side filtering on top of that is a v1 heuristic (packet S9-13 replaces it with .bse
    /// castle templates, which is where per-plot faction contents actually live). It compares
    /// the definition's Side against the player's PlayerTemplate Side, and if that yields
    /// NOTHING it falls back to the unfiltered list - an AI with zero candidates builds nothing
    /// at all, which is the one outcome worth degrading loudly away from.
    /// </para>
    /// </remarks>
    private static void BuildBuildableStructures(IGame game, Player player, List<AiBuildableTemplate> into)
    {
        var side = player.Template?.Side;

        CollectBuildableStructures(game, side, into);

        if (into.Count == 0 && !string.IsNullOrEmpty(side))
        {
            CollectBuildableStructures(game, null, into);
        }

        // Cheapest first, ties by ordinal name: the order the plan reads and a stable one.
        into.Sort(static (a, b) =>
        {
            var byCost = a.Cost.CompareTo(b.Cost);
            return byCost != 0 ? byCost : string.CompareOrdinal(a.TemplateName, b.TemplateName);
        });
    }

    private static void CollectBuildableStructures(IGame game, string? side, List<AiBuildableTemplate> into)
    {
        foreach (var definition in game.AssetStore.ObjectDefinitions)
        {
            if (definition?.KindOf == null || !definition.KindOf.Get(ObjectKinds.NeedBaseFoundation))
            {
                continue;
            }

            if (side != null && !string.Equals(definition.Side, side, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var role = AiStructureRoles.Classify(
                definition.KindOf.Get(ObjectKinds.EconomyStructure),
                definition.KindOf.Get(ObjectKinds.FSCashProducer),
                definition.KindOf.Get(ObjectKinds.FSFactory));

            into.Add(new AiBuildableTemplate(
                definition.InternalId,
                definition.Name,
                (int)CastleUnpackStamper.GetBuildCost(definition),
                role));
        }
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
        _plots.Clear();
        _producers.Clear();

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

                // (S9-06) A plot is also an ordinary owned object, so it appears in BOTH lists.
                // That is intended: OwnObjects stays the complete inventory (the fill order
                // counts structures out of it) and Plots is the filtered view the base manager
                // acts on.
                //
                // Two ways in, because they answer different questions:
                //   * KINDOF BASE_FOUNDATION is the sim's own definition of "a thing a
                //     FoundationConstruct may target" (CastleOrderHandler's NotAFoundation
                //     guard), so every one of those is a plot;
                //   * a still-packed castle is reported even if it is NOT flagged
                //     BASE_FOUNDATION, because unpacking it is what creates the plot ring and an
                //     AI that could not see it would never build anything at all. An object with
                //     a CastleBehavior that is neither a foundation nor packed is not a plot and
                //     is skipped - offering it as a build target would only earn a NotAFoundation
                //     rejection every cooldown.
                var castle = gameObject.FindBehavior<CastleBehavior>();
                var isFoundation = gameObject.IsKindOf(ObjectKinds.BaseFoundation);

                if (isFoundation || (castle != null && castle.CanUnpack(checkTimer: false)))
                {
                    _plots.Add(SnapshotPlot(gameObject, castle));
                }

                // (S9-08) A producer is any owned, finished object carrying a ProductionUpdate.
                // Membership is the module's presence and nothing else; whether the queue will
                // take another entry right now is the module's own CanEnqueue answer
                // (ProductionUpdate.cs:554), carried through rather than re-derived here.
                var production = gameObject.ProductionUpdate;

                if (production != null && !gameObject.IsBeingConstructed())
                {
                    _producers.Add(SnapshotProducer(gameObject, production));
                }
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
        _plots.Sort(ComparePlotById);
        _producers.Sort(CompareProducerById);

        _snapshotFrame = frame;
        _hasSnapshot = true;
    }

    private static int CompareById(AiObjectView a, AiObjectView b) => a.Id.Index.CompareTo(b.Id.Index);

    private static int ComparePlotById(AiPlotView a, AiPlotView b) => a.Id.Index.CompareTo(b.Id.Index);

    private static int CompareProducerById(AiProducerView a, AiProducerView b) => a.Id.Index.CompareTo(b.Id.Index);

    // ---- (S9-08) producer snapshot ----

    /// <summary>Snapshots one owned producer structure.</summary>
    private AiProducerView SnapshotProducer(GameObject producer, ProductionUpdate production)
    {
        return new AiProducerView(
            producer.Id,
            producer.Definition.Name,
            producer.Translation,
            production.CanEnqueue(),
            production.ProductionQueue.Count,
            TrainableFor(producer.Definition));
    }

    /// <summary>
    /// What a producer definition may train, cheapest first, ties by ordinal name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source is the definition's CommandSet: every button whose Command is
    /// <c>CommandType.UnitBuild</c> names the ObjectDefinition that button trains, which is
    /// exactly the set a human player can click at that building. Reusing the command bar's own
    /// data is what keeps the AI's options and the player's options from drifting - and it means
    /// AotR's renamed and re-costed units need no AI change at all.
    /// </para>
    /// <para>
    /// Prerequisites and upgrade gating are NOT evaluated here: the AI may therefore propose a
    /// unit the sim refuses, which costs it one cooldown and is traced. Modelling prerequisites
    /// properly needs the science/upgrade state and belongs to a later packet; guessing at them
    /// here would risk the opposite failure, an AI that thinks it can build nothing.
    /// </para>
    /// </remarks>
    private IReadOnlyList<AiTrainableUnit> TrainableFor(ObjectDefinition definition)
    {
        var key = definition.Name ?? string.Empty;

        if (_trainableByDefinition.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var units = new List<AiTrainableUnit>();
        var commandSet = definition.CommandSet?.Value;

        if (commandSet != null)
        {
            // Buttons is a Dictionary keyed by slot number; walking it directly would be an
            // iteration over an unordered hash collection, which the determinism rule forbids.
            // Sorting the slots first makes the walk - and therefore the tie-breaks below -
            // independent of dictionary internals.
            var slots = new List<int>(commandSet.Buttons.Keys);
            slots.Sort();

            foreach (var slot in slots)
            {
                var button = commandSet.Buttons[slot]?.Value;

                if (button == null || button.Command != CommandType.UnitBuild)
                {
                    continue;
                }

                var unit = button.Object?.Value;

                if (unit == null || unit.InternalId <= 0)
                {
                    continue;
                }

                units.Add(new AiTrainableUnit(
                    unit.InternalId,
                    unit.Name,
                    (int)Math.Max(0f, unit.BuildCost),
                    unit.KindOf != null && unit.KindOf.Get(ObjectKinds.Horde)));
            }

            units.Sort(static (a, b) =>
            {
                var byCost = a.Cost.CompareTo(b.Cost);
                return byCost != 0 ? byCost : string.CompareOrdinal(a.TemplateName, b.TemplateName);
            });
        }

        _trainableByDefinition[key] = units;
        return units;
    }

    /// <summary>
    /// Snapshots one owned KINDOF BASE_FOUNDATION object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kind: a foundation carrying a CastleBehavior that still reports CanUnpack is a packed
    /// castle - the AI has to unpack it before any build plots exist, because unpacking is what
    /// stamps the plot ring into the world. checkTimer is false here on purpose: this is a
    /// "should I want to unpack" question, and the post-pack fade timer is the SIM's business
    /// (CastleOrderHandler's guard 4 re-asks with checkTimer true and refuses if it has not
    /// expired). Asking with the timer would make the AI stop wanting to unpack during the
    /// countdown and forget about the castle entirely.
    /// </para>
    /// <para>
    /// Occupancy: the same probe the order guard uses
    /// (<c>CastleUnpackStamper.FindStructureOnPlot</c>), so "occupied here" and
    /// "FoundationOccupied there" can never disagree. It is O(objects) per plot, which is why it
    /// runs once per frame inside the shared snapshot rather than per manager query.
    /// </para>
    /// </remarks>
    private AiPlotView SnapshotPlot(GameObject plot, CastleBehavior? castle)
    {
        var kind = castle != null && castle.CanUnpack(checkTimer: false)
            ? AiPlotKind.PackedCastle
            : AiPlotKind.BuildPlot;

        var occupant = CastleUnpackStamper.FindStructureOnPlot(plot, _game.GameEngine);

        return new AiPlotView(
            plot.Id,
            plot.Definition.Name,
            plot.Translation,
            kind,
            occupant != null,
            occupant != null ? occupant.Id : default(ObjectId));
    }

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
            healthFraction,
            gameObject.IsKindOf(ObjectKinds.Horde),
            // (S9-08) The one fact the whole team lane turns on. ParentHorde is set on every
            // object a horde contains, and AIUpdate.SetTargetPoint early-outs on it, so an order
            // addressed to such an object is silently discarded. Reported here so managers can
            // exclude them without ever touching a GameObject.
            gameObject.ParentHorde != null);
    }
}
