#nullable enable

// S9-04 (R15 L3) gate tests: AiOrderEmitter's three rules.
//
// The whole point of the emitter is that three invariants hold no matter which manager is
// calling and no matter how busy the frame is:
//   1. PAIRING  - a command is never emitted without an immediately preceding SetSelection,
//                 and the pair is never split across a frame boundary;
//   2. IDENTITY - every order carries the AI's own player index, and actor ids are ascending
//                 and deduplicated so the emitted stream depends on WHICH units were chosen,
//                 not on the order the manager walked them in;
//   3. BUDGET   - a per-frame cap with a FIFO backlog, so a looping manager degrades into a
//                 slow AI rather than a buried order pipe.
//
// Rule 1 is the one that is invisible when it breaks: a split pair still submits two valid
// orders, and the command simply acts on whatever the player was holding a frame later. So the
// pairing tests here assert on stream SHAPE (even-sized frames, alternating types) rather than
// on any single call, because that is the only form in which the bug is detectable.
//
// As with S9-01's spine tests: no IGame, no GraphicsDevice, no INI data, no map.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiOrderEmitterTests
{
    private const int AiPlayerIndex = 2;

    private static ObjectId Id(uint index) => new(index);

    private static (AiOrderEmitter Emitter, FakeAiWorldView World, RecordingOrderSink Sink, RecordingAiTraceSink Trace) Build(
        int ordersPerFrame = AiOrderEmitter.DefaultOrdersPerFrame,
        int maxBacklogBatches = AiOrderEmitter.DefaultMaxBacklogBatches)
    {
        var world = new FakeAiWorldView { PlayerIndex = AiPlayerIndex };
        var sink = new RecordingOrderSink();
        var traceSink = new RecordingAiTraceSink();
        var trace = new AiTrace(AiPlayerIndex, traceSink);

        return (new AiOrderEmitter(world, sink, trace, ordersPerFrame, maxBacklogBatches), world, sink, traceSink);
    }

    /// <summary>Advances the fake clock and rolls the emitter, exactly as the brain's tick would.</summary>
    private static void NextFrame(AiOrderEmitter emitter, FakeAiWorldView world)
    {
        world.AdvanceFrame();
        emitter.Update(null!);
    }

    private static ObjectId[] SelectionIds(Order order)
    {
        Assert.Equal(OrderType.SetSelection, order.OrderType);

        // Argument 0 is the command bar's always-true boolean; the object ids follow it.
        Assert.Equal(OrderArgumentType.Boolean, order.Arguments[0].ArgumentType);

        return order.Arguments.Skip(1).Select(a => a.Value.ObjectId).ToArray();
    }

    // ---- rule 2: identity ----------------------------------------------------------------

    [Fact]
    public void EveryOrderCarriesTheAiOwnPlayerIndex()
    {
        var (emitter, _, sink, _) = Build();

        Assert.True(emitter.MoveGroup([Id(4), Id(5)], new Vector3(10, 20, 0)));
        Assert.True(emitter.QueueUnit(Id(9), objectDefinitionId: 77));

        Assert.Equal(4, sink.Count);
        Assert.All(sink.Orders, o => Assert.Equal(AiPlayerIndex, o.PlayerIndex));
        Assert.Equal(AiPlayerIndex, emitter.PlayerIndex);
    }

    [Fact]
    public void ActorIdsAreSortedAscendingAndDeduplicated()
    {
        var (emitter, _, sink, _) = Build();

        Assert.True(emitter.MoveGroup([Id(9), Id(3), Id(9), Id(7), Id(3)], Vector3.Zero));

        Assert.Equal(new[] { Id(3), Id(7), Id(9) }, SelectionIds(sink.Orders[0]));
    }

    [Fact]
    public void InvalidActorIdsAreDroppedAndAnAllInvalidIntentIsRejected()
    {
        var (emitter, _, sink, trace) = Build();

        Assert.True(emitter.MoveGroup([ObjectId.Invalid, Id(6)], Vector3.Zero));
        Assert.Equal(new[] { Id(6) }, SelectionIds(sink.Orders[0]));

        sink.Clear();

        Assert.False(emitter.MoveGroup([ObjectId.Invalid], Vector3.Zero));
        Assert.Empty(sink.Orders);
        Assert.Equal(1, emitter.TotalIntentsRejected);
        Assert.Contains("[AI p2] orders reject f=0 intent=MoveGroup reason=no valid actor", trace.Lines);
    }

    [Fact]
    public void MalformedIntentsAreRejectedWithoutEmittingOrQueueing()
    {
        var (emitter, _, sink, _) = Build();

        Assert.False(emitter.AttackWith([Id(1)], ObjectId.Invalid));
        Assert.False(emitter.QueueUnit(ObjectId.Invalid, 5));
        Assert.False(emitter.QueueUnit(Id(1), objectDefinitionId: 0));
        Assert.False(emitter.BuildStructure(ObjectId.Invalid, 5, Vector3.Zero, 0f));
        Assert.False(emitter.BuildStructure(Id(1), objectDefinitionId: -1, Vector3.Zero, 0f));
        Assert.False(emitter.SetRallyPoint(ObjectId.Invalid, Vector3.Zero));

        Assert.Empty(sink.Orders);
        Assert.Equal(0, emitter.BacklogCount);
        Assert.Equal(6, emitter.TotalIntentsRejected);
        Assert.Equal(0, emitter.TotalIntentsAccepted);
    }

    // ---- rule 1: pairing, and the exact shape of each command --------------------------

    [Fact]
    public void MoveGroupEmitsSelectionThenMoveToWithTheTargetPosition()
    {
        var (emitter, _, sink, _) = Build();
        var target = new Vector3(12.5f, -3f, 1f);

        Assert.True(emitter.MoveGroup([Id(4)], target));

        Assert.Equal(2, sink.Count);
        Assert.Equal(new[] { Id(4) }, SelectionIds(sink.Orders[0]));
        Assert.Equal(OrderType.MoveTo, sink.Orders[1].OrderType);
        Assert.Equal(target, sink.Orders[1].Arguments[0].Value.Position);
    }

    [Fact]
    public void AttackWithUsesForceAttackOnlyWhenForced()
    {
        var (emitter, _, sink, _) = Build();

        Assert.True(emitter.AttackWith([Id(4)], Id(11)));
        Assert.True(emitter.AttackWith([Id(4)], Id(11), force: true));

        Assert.Equal(OrderType.AttackObject, sink.Orders[1].OrderType);
        Assert.Equal(Id(11), sink.Orders[1].Arguments[0].Value.ObjectId);
        Assert.Equal(OrderType.ForceAttackObject, sink.Orders[3].OrderType);
    }

    [Fact]
    public void QueueUnitSelectsExactlyOneProducerAndMirrorsTheCommandBarArguments()
    {
        var (emitter, _, sink, _) = Build();

        Assert.True(emitter.QueueUnit(Id(30), objectDefinitionId: 412));

        Assert.Equal(new[] { Id(30) }, SelectionIds(sink.Orders[0]));

        var createUnit = sink.Orders[1];
        Assert.Equal(OrderType.CreateUnit, createUnit.OrderType);
        Assert.Equal(2, createUnit.Arguments.Count);
        Assert.Equal(412, createUnit.Arguments[0].Value.Integer);

        // The command bar always sends 1 here; the AI must not invent a different value.
        Assert.Equal(1, createUnit.Arguments[1].Value.Integer);
    }

    [Fact]
    public void BuildStructureSelectsExactlyOneBuilderThenBuildObject()
    {
        var (emitter, _, sink, _) = Build();
        var position = new Vector3(64f, 96f, 0f);

        Assert.True(emitter.BuildStructure(Id(21), objectDefinitionId: 900, position, angle: 1.25f));

        // Exactly one: OrderProcessor's BuildObject case resolves the dozer with SingleOrDefault,
        // which throws on a two-builder selection.
        Assert.Equal(new[] { Id(21) }, SelectionIds(sink.Orders[0]));

        var build = sink.Orders[1];
        Assert.Equal(OrderType.BuildObject, build.OrderType);
        Assert.Equal(900, build.Arguments[0].Value.Integer);
        Assert.Equal(position, build.Arguments[1].Value.Position);
        Assert.Equal(1.25f, build.Arguments[2].Value.Float);
    }

    [Fact]
    public void SetRallyPointUsesTheTwoArgumentFormThatActuallyAppliesThePosition()
    {
        var (emitter, _, sink, _) = Build();
        var rally = new Vector3(5f, 6f, 7f);

        Assert.True(emitter.SetRallyPoint(Id(40), rally));

        Assert.Equal(new[] { Id(40) }, SelectionIds(sink.Orders[0]));

        // Two arguments exactly: OrderProcessor's other SetRallyPoint branch substitutes
        // new Vector3() for the carried position, i.e. throws the rally point away.
        var rallyOrder = sink.Orders[1];
        Assert.Equal(OrderType.SetRallyPoint, rallyOrder.OrderType);
        Assert.Equal(2, rallyOrder.Arguments.Count);
        Assert.Equal(Id(40), rallyOrder.Arguments[0].Value.ObjectId);
        Assert.Equal(rally, rallyOrder.Arguments[1].Value.Position);
    }

    [Fact]
    public void EverySelectionIsFollowedByItsCommandOnTheSameFrame()
    {
        // A budget of 3 cannot hold two pairs, so every frame here is forced to make the
        // split-or-defer decision at least once.
        var (emitter, world, sink, _) = Build(ordersPerFrame: 3);

        var perFrameCounts = new List<int>();
        var emittedBefore = 0;

        for (var frame = 0; frame < 6; frame++)
        {
            if (frame > 0)
            {
                NextFrame(emitter, world);
            }

            emitter.MoveGroup([Id(1), Id(2)], new Vector3(frame, 0, 0));
            emitter.QueueUnit(Id(3), objectDefinitionId: 100 + frame);
            emitter.AttackWith([Id(1)], Id(50));

            perFrameCounts.Add(sink.Count - emittedBefore);
            emittedBefore = sink.Count;
        }

        // No frame ever ends holding half a pair.
        Assert.All(perFrameCounts, count => Assert.Equal(0, count % 2));

        // And the stream itself alternates selection, command, selection, command...
        for (var i = 0; i < sink.Count; i += 2)
        {
            Assert.Equal(OrderType.SetSelection, sink.Orders[i].OrderType);
            Assert.NotEqual(OrderType.SetSelection, sink.Orders[i + 1].OrderType);
        }

        Assert.Equal(sink.Count, emitter.TotalOrdersEmitted);
    }

    // ---- rule 3: budget and backlog -----------------------------------------------------

    [Fact]
    public void ABatchThatDoesNotFitTheFrameBudgetIsDeferredWholeAndDrainedNextFrame()
    {
        var (emitter, world, sink, trace) = Build(ordersPerFrame: 3);

        Assert.True(emitter.MoveGroup([Id(1)], Vector3.Zero));
        Assert.True(emitter.QueueUnit(Id(2), objectDefinitionId: 5));

        // Only the first pair fit; the second waits rather than being split 1/1.
        Assert.Equal(2, sink.Count);
        Assert.Equal(1, emitter.BacklogCount);
        Assert.Equal(1, emitter.TotalBatchesDeferred);
        Assert.Contains("[AI p2] orders defer f=0 intent=QueueUnit n=2 spent=2/3 backlog=1", trace.Lines);

        NextFrame(emitter, world);

        Assert.Equal(4, sink.Count);
        Assert.Equal(0, emitter.BacklogCount);
        Assert.Equal(OrderType.CreateUnit, sink.Orders[3].OrderType);
        Assert.Contains("[AI p2] orders drain f=1 intent=QueueUnit n=2 waited=1 backlog=0", trace.Lines);
    }

    [Fact]
    public void TheBacklogDrainsInFifoOrder()
    {
        var (emitter, world, sink, _) = Build(ordersPerFrame: 2);

        // One pair fits per frame; the other two queue behind it.
        emitter.MoveGroup([Id(1)], Vector3.Zero);
        emitter.QueueUnit(Id(2), objectDefinitionId: 5);
        emitter.BuildStructure(Id(3), objectDefinitionId: 6, Vector3.Zero, 0f);

        Assert.Equal(2, emitter.BacklogCount);

        NextFrame(emitter, world);
        NextFrame(emitter, world);

        Assert.Equal(0, emitter.BacklogCount);
        Assert.Equal(
            new[] { OrderType.MoveTo, OrderType.CreateUnit, OrderType.BuildObject },
            sink.Orders.Where(o => o.OrderType != OrderType.SetSelection).Select(o => o.OrderType));
    }

    [Fact]
    public void BacklogOverflowDropsTheOldestWaitingBatch()
    {
        var (emitter, world, sink, trace) = Build(ordersPerFrame: 2, maxBacklogBatches: 2);

        emitter.MoveGroup([Id(1)], Vector3.Zero);                            // emitted now
        emitter.QueueUnit(Id(2), objectDefinitionId: 5);                     // queued, then evicted
        emitter.BuildStructure(Id(3), objectDefinitionId: 6, Vector3.Zero, 0f);
        emitter.AttackWith([Id(4)], Id(50));                                 // overflows the backlog

        Assert.Equal(2, emitter.BacklogCount);
        Assert.Equal(1, emitter.TotalBatchesDropped);
        Assert.Contains("[AI p2] orders drop f=0 intent=QueueUnit n=2 queued=0 reason=backlogfull", trace.Lines);

        NextFrame(emitter, world);
        NextFrame(emitter, world);

        // The evicted QueueUnit never reaches the pipe; the survivors keep their relative order.
        Assert.Equal(
            new[] { OrderType.MoveTo, OrderType.BuildObject, OrderType.AttackObject },
            sink.Orders.Where(o => o.OrderType != OrderType.SetSelection).Select(o => o.OrderType));
    }

    [Fact]
    public void ABatchLargerThanTheWholeBudgetIsEmittedWholeRatherThanSplit()
    {
        // Every intent expands to two orders, so a budget of one can never fit a batch.
        // Pairing outranks the budget: the pair goes out whole on an otherwise-untouched frame.
        var (emitter, world, sink, trace) = Build(ordersPerFrame: 1);

        Assert.True(emitter.MoveGroup([Id(1)], Vector3.Zero));
        Assert.Equal(2, sink.Count);
        Assert.Equal(1, emitter.TotalOverBudgetBatches);
        Assert.Contains("[AI p2] orders overbudget f=0 intent=MoveGroup n=2 budget=1", trace.Lines);

        // The frame is now spent, so a second intent waits instead of piling on.
        Assert.True(emitter.QueueUnit(Id(2), objectDefinitionId: 5));
        Assert.Equal(2, sink.Count);
        Assert.Equal(1, emitter.BacklogCount);

        NextFrame(emitter, world);

        Assert.Equal(4, sink.Count);
        Assert.Equal(2, emitter.TotalOverBudgetBatches);
        Assert.Equal(0, emitter.BacklogCount);
    }

    [Fact]
    public void TheBudgetResetsOnEveryFrameEvenWithoutAnUpdateCall()
    {
        // A manager registered before the emitter would submit an intent before Update ran;
        // the intent path must roll the frame itself rather than spend last frame's budget.
        var (emitter, world, sink, _) = Build(ordersPerFrame: 2);

        emitter.MoveGroup([Id(1)], Vector3.Zero);
        Assert.Equal(2, emitter.OrdersEmittedThisFrame);

        world.AdvanceFrame();

        emitter.MoveGroup([Id(1)], Vector3.One);

        Assert.Equal(1u, emitter.CurrentFrame);
        Assert.Equal(2, emitter.OrdersEmittedThisFrame);
        Assert.Equal(4, sink.Count);
        Assert.Equal(0, emitter.BacklogCount);
    }

    // ---- wiring and construction --------------------------------------------------------

    [Fact]
    public void TheEmitterTicksAsABrainManagerWithoutDecidingAnything()
    {
        var world = new FakeAiWorldView { PlayerIndex = AiPlayerIndex };
        var sink = new RecordingOrderSink();
        var brain = new SkirmishAIBrain(world, sink);
        var emitter = new AiOrderEmitter(brain, ordersPerFrame: 2);

        brain.RegisterManager(emitter);

        emitter.MoveGroup([Id(1)], Vector3.Zero);
        emitter.QueueUnit(Id(2), objectDefinitionId: 5);
        Assert.Equal(1, emitter.BacklogCount);

        world.AdvanceFrame();
        brain.Update();

        Assert.Equal("orders", emitter.Name);
        Assert.Equal(AiPlayerIndex, emitter.PlayerIndex);
        Assert.Equal(0, emitter.BacklogCount);
        Assert.Equal(4, sink.Count);
    }

    [Fact]
    public void ConstructionRejectsNullSeamsAndNonPositiveLimits()
    {
        var world = new FakeAiWorldView { PlayerIndex = AiPlayerIndex };
        var sink = new RecordingOrderSink();

        Assert.Throws<ArgumentNullException>(() => new AiOrderEmitter(null!, sink));
        Assert.Throws<ArgumentNullException>(() => new AiOrderEmitter(world, null!));
        Assert.Throws<ArgumentNullException>(() => new AiOrderEmitter((SkirmishAIBrain)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiOrderEmitter(world, sink, null, ordersPerFrame: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiOrderEmitter(world, sink, null, maxBacklogBatches: 0));
    }

    [Fact]
    public void TheEmitterSpeaksLegacyOrderOnlyAndNeverSimOrderOrOrderIngest()
    {
        // dr-0040: the SimCore swap is S9-16's, made by replacing the IAiOrderSink under this
        // class. If a SimOrder or OrderIngest type ever appears in the emitter's own surface or
        // state, the seam has been bypassed and the swap is no longer a one-file change.
        var forbidden = new[] { "SimOrder", "OrderIngest", "OrderConverter" };
        var emitter = typeof(AiOrderEmitter);
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var names = new List<string>();

        void Collect(Type type)
        {
            var name = type.FullName ?? type.Name;

            if (names.Contains(name))
            {
                return;
            }

            names.Add(name);

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Collect(argument);
                }
            }
        }

        foreach (var field in emitter.GetFields(All))
        {
            Collect(field.FieldType);
        }

        foreach (var method in emitter.GetMethods(All))
        {
            Collect(method.ReturnType);

            foreach (var parameter in method.GetParameters())
            {
                Collect(parameter.ParameterType);
            }
        }

        Assert.NotEmpty(names);

        foreach (var typeName in names)
        {
            Assert.DoesNotContain(forbidden, f => typeName.Contains(f, StringComparison.Ordinal));
        }
    }
}
