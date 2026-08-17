// autoheal-v1 - build-order step 7's targeted scenario: the FIRST REAL MODULE through the
// harness pipeline (harness-v1.md finding H2-3 closes here).
//
// Everything around the module is the same real thing scripted-v1 proved: orders enter
// through OrderIngest.SubmitScheduled, SimLoop runs the frozen six-phase sequence, and
// checkpoints run SyncChecker.ComputeDeepCheckpoint in the frozen channel order. What
// changes is the middle: the Objects channel is the REAL GameObjectsChannelSource walking
// REAL GameObjects on a HeadlessSimGame, whose AutoHealBehavior instances are the ported
// pilot module - ctor RNG stagger, upgrade mux, damage re-arm, sole-benefactor radius
// heals, StopHealing - all CRC'd through their contract Xfer. The LogicRandom channel
// folds the ported-module logic stream (the ISimContext-owned one the ctor stagger draws
// from), so a module that starts or stops drawing shows up in the vector.
//
// Order vocabulary (schedule -> sim effects, all mutating CRC'd state):
//   MSG_DO_ATTACK_OBJECT  args [ObjectId target, Integer amount] -> AttemptDamage; the
//                         module's OnDamage re-arm and heal train follow.
//   MSG_DO_SPECIAL_POWER  args [ObjectId target] -> StopHealing() on the target's
//                         AutoHealBehavior (the Stopped flag enters the walk).

using System;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

internal sealed class AutoHealScenario : IDriverScenario
{
    private const string Ini = @"
Object PilotAuraHealer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 2
    HealingDelay = 200
    Radius = 30
    KindOf = INFANTRY
    SkipSelfForHealing = Yes
  End
End

Object PilotSelfHealer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 5
    HealingDelay = 400
    StartHealingDelay = 1000
  End
End

Object PilotGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

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

    public AutoHealScenario(uint seed)
    {
        _game = new HeadlessSimGame(SageGame.Bfme2, seed);
        _game.LoadIniText(Ini);

        var civilian = _game.CivilianPlayer;
        var neutral = _game.PlayerManager.Players[0];

        // Ids are creation order: 1 aura healer, 2 self healer, 3..5 grunts, 6 enemy grunt.
        _game.SpawnObject("PilotAuraHealer", civilian, new Vector3(0, 0, 0));
        _game.SpawnObject("PilotSelfHealer", civilian, new Vector3(200, 0, 0));
        _game.SpawnObject("PilotGrunt", civilian, new Vector3(10, 0, 0));
        _game.SpawnObject("PilotGrunt", civilian, new Vector3(0, 12, 0));
        _game.SpawnObject("PilotGrunt", civilian, new Vector3(-14, 0, 0));
        _game.SpawnObject("PilotGrunt", neutral, new Vector3(18, 0, 0));

        // The ported-module logic stream (S3): the same LogicRandom the ctor staggers drew
        // from, folded every checkpoint exactly like the original folds its RNG state.
        var context = (SimContext)_game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;

        _checker = new SyncChecker(new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(_game.GameLogic),
            new LogicRandomChannelSource(random),
        });
    }

    public void AttachWriter(DeepCrcWriter writer) => _writer = writer;

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
                    foreach (var module in target.BehaviorModules)
                    {
                        if (module is AutoHealBehavior autoHeal)
                        {
                            autoHeal.StopHealing();
                        }
                    }
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
        // The real sleepy-update queue: awake modules run in engine order, then the
        // GameLogic frame advances in step with the SimLoop frame.
        _game.GameLogic.Update();
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
