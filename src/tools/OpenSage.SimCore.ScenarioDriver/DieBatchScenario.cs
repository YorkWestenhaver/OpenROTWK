// die-batch-v1 - THE shared targeted scenario for the eleven-class Die batch
// (experiment-round-4 §4.1, DoD item 5b: "extend rather than clone: one scenario with
// damage->death orders can host several of these classes").
//
// Shape is autoheal-v1's: orders enter through OrderIngest.SubmitScheduled, SimLoop runs the
// frozen six-phase sequence, and every checkpoint runs SyncChecker.ComputeDeepCheckpoint over
// the real GameObjectsChannelSource plus the ported-module LogicRandom stream. What is
// specific here is the middle: objects are spawned to be KILLED, on a schedule, by damage
// orders - so the walk shows a Die module's state before the death, the death itself, and the
// object leaving the object list afterwards.
//
// HOW A DIE TASK EXTENDS THIS (the whole point - do not clone the file):
//   1. add one INI Object block below carrying your ported Die module (plus the witness
//      Behavior, see below), named DieBatch<Class>;
//   2. add one line to the Spawns table naming it;
//   3. add the frames that kill it to
//      tools/harness/data/schedules/die-batch-v1.sched.json (object ids are creation order,
//      so appending to Spawns never renumbers the objects already scheduled);
//   4. run: harness.py selfdiff data/schedules/die-batch-v1.sched.json
//           --driver-scenario die-batch-v1
// Your module joins the CRC walk automatically the moment it overrides HasSimXfer (D-2).
//
// THE WITNESS. Only ported modules (HasSimXfer) enter the Objects channel walk, and at batch
// start no Die class is ported yet - the walk would be empty and the gate vacuous. So every
// object here also carries AutoHealBehavior, the one module already ported, whose state
// (SoonestHealFrame / Stopped) reacts to damage and disappears with its object. It is
// scaffolding for the batch's first task only: once real Die modules are in the walk the
// witness has done its job, and the last task in the batch may drop it.
//
// Order vocabulary (schedule -> sim effects):
//   MSG_DO_ATTACK_OBJECT (1061) args [ObjectId target, Integer amount]
//       -> AttemptDamage, DamageType Explosion / DeathType Normal. Sub-lethal amounts are
//          the interesting ones: they re-arm the witness and leave the object alive.
//   MSG_DO_SPECIAL_POWER (1040) args [ObjectId target, Integer deathType]
//       -> the death trigger: unresistable lethal damage carrying that DeathType, i.e. the
//          driver-side twin of PortedModuleTestKit.TriggerDeath. deathType is the DeathType
//          enum's value: 0 NORMAL, 1 NONE, 2 CRUSHED, 3 BURNED, 4 EXPLODED, 5 POISONED,
//          6 TOPPLED, 7 FLOODED, 8 SUICIDED. This is what exercises a Die module's
//          DeathTypes filter from the harness side.

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

internal sealed class DieBatchScenario : IDriverScenario
{
    // 5 Hz (F6): HealingDelay 400 ms -> 2 frames.
    private const string Ini = @"
Object DieBatchVictim
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = DestroyDie ModuleTag_Die
  End
End

Object DieBatchBurnVictim
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = DestroyDie ModuleTag_Die
    DeathTypes = NONE +BURNED
  End
End

Object DieBatchSurvivor
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

Object DieBatchHealer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 2
    HealingDelay = 400
    Radius = 40
    KindOf = INFANTRY
    SkipSelfForHealing = Yes
  End
End

Object DieBatchWave
  KindOf = WAVEGUIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
End

Object DieBatchDam
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = DamDie ModuleTag_Die
    DeathTypes = NONE +FLOODED
  End
End
";

    /// <summary>
    /// Objects that start the scenario disabled with DISABLED_DEFAULT, by spawn index.
    ///
    /// DamDie's whole observable effect is clearing that bit on every WAVEGUIDE object, and
    /// the original's dam maps ship their wave objects pre-placed and disabled - there is no
    /// module that disables them, so the scenario has to stand them up that way, exactly as
    /// the map file would. This is CRC-observable rather than cosmetic: GameLogic skips the
    /// update of any module whose object has a disabled bit set (unless the module opts in
    /// via DisabledTypesToProcess), so a disabled wave's AutoHealBehavior witness never
    /// pulses. The frame DamDie releases the waves is the frame their witness state starts
    /// moving in the Objects channel.
    /// </summary>
    private static readonly int[] InitiallyDisabledSpawnIndices = { 7, 8 };

    /// <summary>
    /// Spawn table - object ids are assigned in this order starting at 1, which is the
    /// contract the handcrafted schedule targets. APPEND only.
    /// </summary>
    private static readonly (string Definition, bool Neutral, float X, float Y)[] Spawns =
    {
        ("DieBatchHealer",     false,   0f,   0f),   // 1 - aura, keeps the walk moving
        ("DieBatchVictim",     false,  10f,   0f),   // 2 - killed by damage orders
        ("DieBatchVictim",     false,   0f,  12f),   // 3 - killed by a typed death (EXPLODED)
        ("DieBatchBurnVictim", false, -14f,   0f),   // 4 - NORMAL death: dies, Die filtered out
        ("DieBatchSurvivor",   false,  18f,   0f),   // 5 - control: damaged, never killed
        ("DieBatchVictim",     true,  200f,   0f),   // 6 - foreign owner, out of aura range
        ("DieBatchBurnVictim", false,   0f, -16f),   // 7 - BURNED death: the Die module fires
        ("DieBatchWave",       false, -40f,  30f),   // 8 - DamDie: released wave (starts disabled)
        ("DieBatchWave",       false, -40f,  45f),   // 9 - DamDie: released wave (starts disabled)
        ("DieBatchDam",        false, -40f,   0f),   // 10 - DamDie: NORMAL death, filtered out
        ("DieBatchDam",        false, -55f,   0f),   // 11 - DamDie: FLOODED death, waves released
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

    public DieBatchScenario(uint seed)
    {
        _game = new HeadlessSimGame(SageGame.Bfme2, seed);
        _game.LoadIniText(Ini);

        var civilian = _game.CivilianPlayer;
        var neutral = _game.PlayerManager.Players[0];

        var spawned = new GameObject[Spawns.Length];
        for (var i = 0; i < Spawns.Length; i++)
        {
            var (definition, isNeutral, x, y) = Spawns[i];
            spawned[i] = _game.SpawnObject(definition, isNeutral ? neutral : civilian, new Vector3(x, y, 0));
        }

        foreach (var index in InitiallyDisabledSpawnIndices)
        {
            spawned[index].SetDisabled(DisabledType.Default);
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

                    // The death trigger: Kill makes ActiveBody spend exactly the remaining
                    // health, so the >0 -> <=0 crossing runs the object's Die modules with
                    // this DeathType, whatever its armor would have said.
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
