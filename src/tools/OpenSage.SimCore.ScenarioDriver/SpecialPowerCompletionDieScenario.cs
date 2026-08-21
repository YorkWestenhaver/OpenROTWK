// spcd-v1 - the targeted scenario for the SpecialPowerCompletionDie port
// (experiment-round-4 §4.1, DoD item 5b).
//
// WHY THIS IS NOT AN EXTENSION OF die-batch-v1, which the checklist prefers: die-batch-v1's
// object ids are its Spawns table's positions, and its handcrafted schedule addresses them
// by number. Eleven Die tasks run CONCURRENTLY in separate worktrees, and each one that
// appends to Spawns claims the next indices in a namespace it cannot see the rest of - two
// tasks appending "object 8" produce a schedule that silently retargets the other's orders
// after the merge, which is a wrong-but-green failure, not a conflict git will show. So this
// class takes its own scenario and its own schedule; the shared one stays correct for
// whichever task lands in it. Filed as SPCD-2.
//
// Everything around the module is the real thing, exactly as die-batch-v1 has it: orders
// enter through OrderIngest.SubmitScheduled, SimLoop runs the frozen six-phase sequence, and
// each checkpoint runs SyncChecker.ComputeDeepCheckpoint over the real GameObjectsChannelSource
// plus the ported-module LogicRandom stream.
//
// WHAT THE DUMP SHOWS. This module's whole state is a latch, so the interesting trace is
// CreatorSet flipping false->true at a scheduled frame with a scheduled CreatorObjectId, and
// then the object leaving the world at its scheduled death. The beacons carry an (unported,
// therefore walk-invisible) DestroyDie alongside, purely so that death has a footprint IN the
// walk: this module reports and then does nothing observable, so without a destroyer a death
// would leave the dump unchanged and the scenario could not tell a fired Die from a skipped
// one. One beacon latches on the INVALID id first and stays silent forever afterwards, which
// is the suppression the unported spawn sites rely on; one carries a DeathTypes filter and is
// killed by the wrong death type.
//
// Order vocabulary (schedule -> sim effects) - same codes and meanings as die-batch-v1:
//   MSG_DO_ATTACK_OBJECT (1061)           args [ObjectId target, Integer amount]
//   MSG_DO_SPECIAL_POWER (1040)           args [ObjectId target, Integer deathType]
//   MSG_DO_SPECIAL_POWER_AT_OBJECT (1042) args [ObjectId target, Integer creatorIdIndex]
//       -> SpecialPowerCompletionDie.SetCreator, crediting the object with that id index
//          (0 = ObjectId.Invalid). The driver-side stand-in for the unported creator-assignment
//          sites (Weapon's projectile path, ObjectCreationList's DeliverPayloadNugget).
//
// Run: harness.py selfdiff data/schedules/spcd-v1.sched.json --driver-scenario spcd-v1

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

internal sealed class SpecialPowerCompletionDieScenario : IDriverScenario
{
    // 5 Hz (F6): HealingDelay 400 ms -> 2 frames.
    private const string Ini = @"
Object SpcdBeacon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerCompletionDie ModuleTag_Die
    SpecialPowerTemplate = SpecialPowerScenarioAthelas
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

Object SpcdBurnBeacon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerCompletionDie ModuleTag_Die
    SpecialPowerTemplate = SpecialPowerScenarioBalrog
    DeathTypes = NONE +BURNED
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

Object SpcdCreator
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
End

Object SpcdWitness
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
End
";

    /// <summary>
    /// Spawn table - object ids are assigned in this order starting at 1, which is the
    /// contract the handcrafted schedule targets. APPEND only.
    /// </summary>
    private static readonly (string Definition, bool Neutral, float X, float Y)[] Spawns =
    {
        ("SpcdCreator",     false,   0f,   0f),   // 1 - the credited creator; never dies
        ("SpcdBeacon",      false,  10f,   0f),   // 2 - creator set, then killed: reports
        ("SpcdBeacon",      false,   0f,  12f),   // 3 - latched INVALID first: silent forever
        ("SpcdBurnBeacon",  false, -14f,   0f),   // 4 - creator set, NORMAL death: filtered out
        ("SpcdBurnBeacon",  false,   0f, -16f),   // 5 - creator set, BURNED death: reports
        ("SpcdWitness",     false,  18f,   0f),   // 6 - health/RNG churn; no Die module
        ("SpcdBeacon",      true,  200f,   0f),   // 7 - foreign owner: reports a DIFFERENT index
    };

    private readonly HeadlessSimGame _game;
    private readonly SyncChecker _checker;
    private DeepCrcWriter? _writer;

    public int Dispatched { get; private set; }
    public int Checkpoints { get; private set; }
    public uint FinalCombined { get; private set; }

    public int ObjectCount
    {
        get
        {
            var count = 0;
            foreach (var _ in _game.GameLogic.Objects)
            {
                count++;
            }
            return count;
        }
    }

    public SpecialPowerCompletionDieScenario(uint seed)
    {
        _game = new HeadlessSimGame(SageGame.Bfme2, seed);
        _game.LoadIniText(Ini);

        var civilian = _game.CivilianPlayer;
        var neutral = _game.PlayerManager.Players[0];

        foreach (var (definition, isNeutral, x, y) in Spawns)
        {
            _game.SpawnObject(definition, isNeutral ? neutral : civilian, new Vector3(x, y, 0));
        }

        var context = (SimContext)_game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;

        _checker = new SyncChecker(new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(_game.GameLogic),
            new LogicRandomChannelSource(random),
        });
    }

    public void AttachWriter(DeepCrcWriter writer) => _writer = writer;

    public void SetChannelExclusions(IReadOnlyList<CrcChannel> excluded)
    {
        foreach (var channel in excluded)
        {
            _checker.SetExcluded(channel, true);
        }
    }

    public void IngestOrders(LogicFrame frame)
    {
        // Orders are pre-submitted through OrderIngest by the driver.
    }

    public void DispatchOrder(in ScheduledOrder scheduled)
    {
        Dispatched++;
        var order = scheduled.Order;

        switch (order.Type)
        {
            case GameMessageType.MSG_DO_ATTACK_OBJECT:
                {
                    if (TryTarget(order, out var target) && TryFirstInteger(order, out var amount))
                    {
                        target.AttemptDamage(new DamageInfoInput(null)
                        {
                            DamageType = DamageType.Explosion,
                            DeathType = DeathType.Normal,
                            Amount = amount,
                        });
                    }
                    break;
                }
            case GameMessageType.MSG_DO_SPECIAL_POWER:
                {
                    if (TryTarget(order, out var target))
                    {
                        var deathType = TryFirstInteger(order, out var ordinal)
                            ? (DeathType)ordinal
                            : DeathType.Normal;

                        target.AttemptDamage(new DamageInfoInput(null)
                        {
                            DamageType = DamageType.Unresistable,
                            DeathType = deathType,
                            Amount = 0f,
                            Kill = true,
                        });
                    }
                    break;
                }
            case GameMessageType.MSG_DO_SPECIAL_POWER_AT_OBJECT:
                {
                    if (TryTarget(order, out var target))
                    {
                        var die = target.FindBehavior<SpecialPowerCompletionDie>();
                        die?.SetCreator(new ObjectId(TryFirstInteger(order, out var index) ? (uint)index : 0u));
                    }
                    break;
                }
        }
    }

    private bool TryTarget(SimOrder order, out GameObject target)
    {
        foreach (var a in order.Arguments)
        {
            if (a.Kind == SimOrderArgKind.ObjectId && a.ObjectId != 0)
            {
                target = _game.GameLogic.GetObjectById(new ObjectId(a.ObjectId));
                return target is not null;
            }
        }
        target = null!;
        return false;
    }

    private static bool TryFirstInteger(SimOrder order, out int value)
    {
        foreach (var a in order.Arguments)
        {
            if (a.Kind == SimOrderArgKind.Integer)
            {
                value = a.Integer;
                return true;
            }
        }
        value = 0;
        return false;
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        // The real sleepy-update queue, then the destroy-list reap: an object killed during
        // this frame's order dispatch leaves the world here, exactly as a real frame does it.
        _game.Step();
    }

    public void PartitionUpdate(LogicFrame frame)
    {
    }

    public void CrcCheckpoint(LogicFrame frame)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("no DeepCrcWriter attached");
        }
        var message = _checker.ComputeDeepCheckpoint(frame, _writer);
        _writer.CrcVector(frame.Value, message.Combined, message.ChannelCrcs);
        Checkpoints++;
        FinalCombined = message.Combined;
    }
}
