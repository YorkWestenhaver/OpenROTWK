// delayeddeath-v1 - R8 targeted scenario: the ported DelayedDeathBody + its companion
// DelayedDeathTimer through the REAL harness pipeline (SimLoop's frozen six phases,
// GameObjectsChannelSource walking REAL GameObjects on a HeadlessSimGame, deep CRC
// checkpoints in the frozen channel order).
//
// The point of this scenario: the creation-armed death timer is pure LogicFrame state on the
// [SimState] companion, and its Xfer contribution to the Objects CRC channel must fold
// bit-identically across two independent engine processes (Target A). It needs NO orders - the
// timer is armed at object creation - so the schedule is empty frames that only pace the
// checkpoints across the death frame. Both a plain timed unit (TimedTroll: dies at the timer)
// and an immortal one (ImmortalTroll: floored until the timer, then dies) evolve in the dump:
// the companion's Armed/Fired/DeathFrame advance, the body's health folds, and the death shows
// up as the object leaving the walk.

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

internal sealed class DelayedDeathScenario : IDriverScenario
{
    // DelayedDeathTime = 1000 ms at BFME2's 5 Hz => 5 frames. The trolls are the corpus shape
    // (summoned/temporary units with a delayed death and no respawn / no health-check arming).
    private const string Ini = @"
Object TimedTroll
  KindOf = INFANTRY
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 100
    DelayedDeathTime = 1000
    DoHealthCheck = No
    CanRespawn = No
  End
End

Object ImmortalTroll
  KindOf = INFANTRY
  Body = DelayedDeathBody ModuleTag_Body
    MaxHealth = 100
    DelayedDeathTime = 1000
    ImmortalUntilDeathTime = Yes
  End
End

Object PlainGrunt
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

    public DelayedDeathScenario(uint seed)
    {
        _game = new HeadlessSimGame(SageGame.Bfme2, seed);
        _game.LoadIniText(Ini);

        var civilian = _game.CivilianPlayer;

        // Ids are creation order: 1 timed troll, 2 immortal troll, 3 plain grunt.
        _game.SpawnObject("TimedTroll", civilian, new Vector3(0, 0, 0));
        _game.SpawnObject("ImmortalTroll", civilian, new Vector3(50, 0, 0));
        _game.SpawnObject("PlainGrunt", civilian, new Vector3(100, 0, 0));

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
        // No orders: the death timer is armed at creation.
    }

    public void DispatchOrder(in ScheduledOrder scheduled)
    {
        // No order vocabulary for this scenario.
        Dispatched++;
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        // The real sleepy-update queue: the companion DelayedDeathTimer wakes on the death frame
        // and kills its object, in engine order, then the GameLogic frame advances.
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
