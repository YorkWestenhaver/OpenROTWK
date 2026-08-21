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
// OBJECT 14 (KeepObjectDie) is the one object here whose death does NOT remove it:
// everything else that dies leaves the Objects channel, so "the walk got shorter" is the
// batch's usual death signal, and a class whose whole contract is that the corpse stays
// needs the opposite signal. Object 14 must still be in the channel, with its module still
// walking, on every checkpoint after its death frame. (Its own branch numbered it 8; the
// integration merge renumbered spawns.)
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
//   MSG_DO_FORCE_ATTACK_OBJECT (1062) args [ObjectId victim, ObjectId crusher]
//       -> the CRUSH death: lethal DamageType.Crush / DeathType.Crushed carrying a real
//          damage SOURCE, i.e. what PhysicsBehavior delivers when one object drives over
//          another. Added by the CrushDie task, the one Die class that reads both the
//          damage type and the source object, so it is the one verb in this vocabulary
//          whose argument ORDER is significant: [victim, crusher], never "the first id".

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

Object DieBatchCrushVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 10
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = CrushDie ModuleTag_Die
    TotalCrushSound = CrushTotal
    FrontEndCrushSound = CrushFront
    FrontEndCrushSoundPercent = 50
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

; --- FXListDie. The two objects below sit in OPPOSITE mux
; states, which is the only state this class has: the first takes the class's
; default (StartsActive true, so UpgradeTriggered = 1 in the walk), the second
; declares StartsActive = No (UpgradeTriggered = 0). Their CRCs therefore differ
; from each other from frame 0, and both leave the walk when they die - so the
; walk sees a ported Die module's state, not only the witness's.
Upgrade DieBatchDeathTrigger
  Type = PLAYER
End

FXList DieBatchDeathFX
End

Object DieBatchFXCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = FXListDie ModuleTag_Die
    DeathFX = DieBatchDeathFX
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

Object DieBatchFXSilentCorpse
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = FXListDie ModuleTag_Die
    StartsActive = No
    TriggeredBy = DieBatchDeathTrigger
    DeathTypes = NONE +BURNED
    DeathFX = DieBatchDeathFX
    OrientToObject = No
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

Object DieBatchKeepObject
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = KeepObjectDie ModuleTag_IWantRubble
    DeathTypes = ALL -SUICIDED
  End
End

; --- UpgradeDie's slice (appended; see the extension recipe at the top of this file) ---
; The producer/drone pair the GPL comment describes - ranger building scout drones: the
; drone's death frees Upgrade_DieBatchDrone on the object that produced it. The upgrade set
; itself is GameObject state and not part of any ported module's walk, so what the CRC walk
; witnesses here is the death dispatch and the witness module vanishing with its object; the
; upgrade-set effect is asserted in UpgradeDieContractTests.
Upgrade Upgrade_DieBatchDrone
  Type = OBJECT
End

Object DieBatchDroneProducer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
End

Object DieBatchDrone
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = UpgradeDie ModuleTag_Die
    DeathTypes = ALL
    UpgradeToRemove = Upgrade_DieBatchDrone BaseUpgradeTag_01
  End
  ; UpgradeDie frees an upgrade; it does not remove the corpse. DestroyDie alongside it is
  ; what makes the death visible in the dump as the drone leaving the Objects channel.
  Behavior = DestroyDie ModuleTag_Reap
  End
End

ObjectCreationList OCL_DieBatchSpawnling
  CreateObject
    ObjectNames = DieBatchSpawnling
    Count = 1
  End
End

Object DieBatchSpawnling
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

Object DieBatchCreateObject
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_DieBatchSpawnling
    TransferPreviousHealth = Yes
  End
End

; --- CreateCrateDie. The crate itself carries the witness behavior so the
; object the DEATH created shows up in the Objects channel walk on its own: this is an
; extension where the walk gains a member mid-run, and the new object's ctor
; stagger draw is what proves it joined the same RNG stream.
CrateData DieBatchCrateData
  CreationChance = 1.0
  CrateObject = DieBatchCrate 1.0
End

Object DieBatchCrate
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
End

Object DieBatchCrateDropper
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = CreateCrateDie ModuleTag_Die
    CrateData = DieBatchCrateData
  End
  Behavior = DestroyDie ModuleTag_Destroy
  End
End

; --- EjectPilotDie -------------------------------------------
; The ejected pilot is a plain object with no modules of its own: what the walk sees
; is a NEW ObjectId appearing in the Objects channel on the frame its parent dies,
; which is exactly the observable this module owes. It carries the batch witness for the
; same reason every other object here does - only ported modules enter the walk, so a
; module-less pilot would be invisible to the gate. GroundCreationList only, so the
; scheduled ground death takes the configured branch while an airborne death would
; take the empty branch (silent no-op).
Object DieBatchPilot
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 1
    HealingDelay = 400
  End
End

ObjectCreationList OCL_DieBatchEjectPilot
  CreateObject
    ObjectNames = DieBatchPilot
    Count = 1
  End
End

Object DieBatchEjectVictim
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 4
    HealingDelay = 400
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_DieBatchEjectPilot
  End
End

; --- RebuildHoleExposeDie. Another class whose death
; ADDS an object to the world, so the hole carries the witness: a new ObjectId
; entering the Objects channel walk mid-run is the thing this slice proves. The
; rebuild worker respawn delay is 10 minutes so the hole's own update never reaches
; its worker-spawning branch inside this scenario.
Object DieBatchRebuildWorker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object DieBatchRebuildHole
  KindOf = STRUCTURE REBUILD_HOLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = RebuildHoleBehavior ModuleTag_Hole
    WorkerObjectName = DieBatchRebuildWorker
    WorkerRespawnDelay = 600000
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 3
    HealingDelay = 400
  End
End

Object DieBatchRebuildKeep
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Witness
    StartsActive = Yes
    HealingAmount = 2
    HealingDelay = 400
  End
  Behavior = RebuildHoleExposeDie ModuleTag_Die
    HoleName = DieBatchRebuildHole
    HoleMaxHealth = 120
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
        ("DieBatchFXCorpse",   false,  26f,   0f),   // 12 - FXListDie, mux active
        ("DieBatchFXSilentCorpse", false, 26f, 14f), // 13 - FXListDie, mux inactive
        ("DieBatchKeepObject", false, -30f,  20f),   // 14 - dies NORMAL: the corpse STAYS
        // Both crush victims face +X (yaw 0) and have GeometryMajorRadius 10, so their crush
        // points are centre, +5 and -5 along X. Object 1 sits at the origin and does the
        // crushing, so 15 (at +30) is nearest its BACK point and 16 (at -30) its FRONT point:
        // one spawn per crush-point branch, resolved by geometry rather than by a flag.
        // (The CrushDie branch numbered these 8 and 9; the integration merge renumbered.)
        ("DieBatchCrushVictim", false,  30f,   0f),  // 15 - CrushDie: back-end crush by 1
        ("DieBatchCrushVictim", false, -30f,   0f),  // 16 - CrushDie: front-end crush by 1
        ("DieBatchDroneProducer", false, -30f, 30f), // 17 - holds Upgrade_DieBatchDrone
        ("DieBatchDrone",       false, -34f, 30f),   // 18 - UpgradeDie: frees it by dying
        ("DieBatchCreateObject", false, 30f, 30f),   // 19 - CreateObjectDie: its death ADDS an
                                                     //      object to the walk, carrying the
                                                     //      pre-death health deficit with it
        ("DieBatchCrateDropper", false, -20f, 20f),  // 20 - CreateCrateDie: its death ADDS an object
        ("DieBatchEjectVictim", false, -30f, -20f),  // 21 - EjectPilotDie: killed, ejects DieBatchPilot
        ("DieBatchRebuildKeep", false, -55f, 30f),   // 22 - RebuildHoleExposeDie: death ADDS an object
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
            case GameMessageType.MSG_DO_FORCE_ATTACK_OBJECT:
                {
                    // The CRUSH death: lethal DamageType.Crush carrying DeathType.Crushed and a
                    // real damage SOURCE, which is what PhysicsBehavior's collide path delivers
                    // when one object drives over another. CrushDie is the only Die class that
                    // reads both the damage type and the source object, so it needs a verb the
                    // others do not have; every other Die class keeps using MSG_DO_SPECIAL_POWER.
                    // Args are two ObjectIds and their ORDER is the meaning: [victim, crusher].
                    if (TryObjectIdAt(order, 0, out var victim) &&
                        TryObjectIdAt(order, 1, out var crusher))
                    {
                        victim.AttemptDamage(new DamageInfoInput(crusher)
                        {
                            DamageType = DamageType.Crush,
                            DeathType = DeathType.Crushed,
                            Amount = 0f,
                            Kill = true,
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
        => TryObjectIdAt(order, 0, out target);

    /// <summary>
    /// The n-th ObjectId argument. <see cref="TryTarget"/> is the index-0 case, which is every
    /// verb whose object argument is simply "the target"; a verb whose meaning depends on
    /// argument ORDER - such as the crush verb's [victim, crusher] - asks for a later index.
    /// Both go through the same resolution so the DBP-2 diagnostic below covers all of them.
    /// </summary>
    private bool TryObjectIdAt(SimOrder order, int index, out GameObject gameObject)
    {
        var seen = 0;
        foreach (var a in order.Arguments)
        {
            if (a.Kind != SimOrderArgKind.ObjectId || a.ObjectId == 0)
            {
                continue;
            }

            if (seen++ != index)
            {
                continue;
            }

            // Deliberately a scan rather than GameLogic.GetObjectById: that indexer is an
            // unguarded List indexer, so a schedule naming an object this branch does not
            // spawn dies with a bare IndexOutOfRangeException from inside the engine, which
            // says nothing about the actual mistake. The shared die-batch-v1 schedule is
            // APPEND-EXTENDED by each task in the batch, so a branch running it before
            // adding its own Spawns row is a routine, expected error - it deserves a
            // sentence, not a stack trace. (batch finding DBP-2.)
            var wanted = new ObjectId(a.ObjectId);
            foreach (var candidate in _game.GameLogic.Objects)
            {
                if (candidate.Id == wanted)
                {
                    gameObject = candidate;
                    return true;
                }
            }

            throw new InvalidOperationException(
                $"die-batch-v1 schedule targets object id {a.ObjectId}, but this build's " +
                $"Spawns table only creates {Spawns.Length} object(s) (ids 1..{Spawns.Length}). " +
                "The schedule in tools/harness/data/schedules/ is shared by the whole Die " +
                "batch and grows as tasks append spawns: either add your Spawns row, or run " +
                "die-batch-v1.base.sched.json, the snapshot that matches the base branch.");
        }

        gameObject = null!;
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
