// respawn-v1 - the R14 respawn seam through the REAL harness pipeline, added to the cross-arch
// corpus so the M3 gate actually covers this seam (review finding H3c).
//
// Why a sibling scenario rather than an extension of job009_creep_fight_subset.ini: map-v1's
// stimulus is the compiled scripts of a BINARY .map file. Putting a respawn-carrying hero into
// that run means editing the .map's object list, not the INI subset beside it - map surgery
// this packet has no reason to do. A self-stimulating scenario in the same driver, folding the
// same channels through the same SimLoop, gives the gate identical evidence about this seam
// without touching job-009's own baseline at all.
//
// WHAT IT EXERCISES, and why each arm belongs in a cross-arch gate:
//   * A claimed (non-permanent) death, so the Objects channel contains a dead-but-un-reaped
//     hero for many frames. That object's continued membership in the ascending-ObjectId walk
//     is the single largest CRC consequence of the seam.
//   * A purchased revive through ReviveApplicator - the SAME code path OrderProcessor's
//     OrderType.Revive case runs - including the anchor's HeroRevive CostMultiplier/
//     TimeMultiplier, which is the seam's only float->Fix64 crossing and therefore the arm
//     most likely to differ between arm64 and x64 if it were ever done wrong.
//   * An AutoSpawn:Yes hero, which revives on a plain countdown with no order and no money.
//   * A PERMANENT death after a completed revive - the second-death latch - which resolves to
//     the ordinary corpse path and leaves the walk. This is the arm that would silently regress
//     if the permanence resolver stopped being re-armed by Revive().
//
// Determinism: the stimulus is a fixed frame schedule compiled into this file, applied in the
// SimLoop's IngestOrders phase; no wall clock, no file input, no RNG of its own. Every peer and
// every architecture runs the identical sequence.

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

internal sealed class RespawnSeamScenario : IDriverScenario
{
    // DeathAnimationTime 1000 ms = 5 frames at the frozen 5 Hz rate; RespawnRules Time 2000 ms
    // = 10 frames, halved to 5 by the fortress's TimeMultiplier; RespawnAnimationTime 400 ms
    // = 2 frames.
    private const string Ini = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object ReviveHero
  KindOf = INFANTRY HERO SELECTABLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
  Behavior = RespawnUpdate ModuleTag_Respawn
    DeathAnimationTime = 1000
    RespawnAnimationTime = 400
    RespawnRules = AutoSpawn:No Cost:500 Time:2000 Health:100%
  End
End

Object AutoReviveHero
  KindOf = INFANTRY HERO SELECTABLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
  Behavior = RespawnUpdate ModuleTag_Respawn
    DeathAnimationTime = 1000
    RespawnAnimationTime = 0
    RespawnRules = AutoSpawn:Yes Cost:0 Time:2000 Health:100%
  End
End

Object ReviveFortress
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 20
  GeometryHeight = 20
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = ProductionUpdate ModuleTag_Prod
    ProductionModifier
      CostMultiplier = 0.80
      TimeMultiplier = 0.50
      HeroRevive = Yes
      ModifierFilter = NONE +HERO
    End
  End
End

Object StructureKiller
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End

Object InfantryKiller
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End
";

    private const uint KillFrame = 2;
    private const uint PurchaseFrame = 10;
    private const uint PermanentKillFrame = 25;

    private readonly HeadlessSimGame _game;
    private readonly SyncChecker _checker;
    private DeepCrcWriter? _writer;

    private readonly GameObject _hero;
    private readonly GameObject _autoHero;
    private readonly GameObject _fortress;
    private readonly GameObject _infantryKiller;
    private readonly GameObject _structureKiller;

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

    public RespawnSeamScenario(uint seed)
    {
        _game = new HeadlessSimGame(SageGame.Bfme2, seed);
        _game.LoadIniText(Ini);

        var civilian = _game.CivilianPlayer;

        // A fixed treasury, not a default: the revive purchase must be affordable identically
        // on every leg, and the affordability guard is part of what this scenario grades.
        civilian.BankAccount.Money = 10000;

        // Ids are creation order: 1 hero, 2 auto hero, 3 fortress, 4 infantry, 5 structure.
        _hero = _game.SpawnObject("ReviveHero", civilian, new Vector3(0, 0, 0));
        _autoHero = _game.SpawnObject("AutoReviveHero", civilian, new Vector3(60, 0, 0));
        _fortress = _game.SpawnObject("ReviveFortress", civilian, new Vector3(120, 0, 0));
        _infantryKiller = _game.SpawnObject("InfantryKiller", civilian, new Vector3(180, 0, 0));
        _structureKiller = _game.SpawnObject("StructureKiller", civilian, new Vector3(240, 0, 0));

        var context = (SimContext)_game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;

        _checker = new SyncChecker(new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(_game.GameLogic),
            new LogicRandomChannelSource(random),
            new OracleViewChannelSource(_game.GameLogic),
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

    /// <summary>
    /// The scripted stimulus. It lives in the IngestOrders phase, where a real order would
    /// arrive, so the kill and the purchase land at a fixed point in the frozen phase sequence
    /// rather than wherever a module happened to run.
    /// </summary>
    public void IngestOrders(LogicFrame frame)
    {
        switch (frame.Value)
        {
            case KillFrame:
                // Non-permanent (an infantry killer does not match PermanentlyKilledByFilter),
                // so RespawnUpdate claims both deaths and neither hero is reaped.
                Kill(_hero, _infantryKiller);
                Kill(_autoHero, _infantryKiller);
                break;

            case PurchaseFrame:
                // The real order path: identical code to OrderProcessor's OrderType.Revive case.
                ReviveApplicator.Apply(_game, _game.CivilianPlayer, _hero.Id, _fortress.Id);
                break;

            case PermanentKillFrame:
                // The revived hero dies again, this time to a filter-matching source. The
                // permanence resolver was re-armed by the revive, so this death is NOT claimed
                // and the object leaves the world through the ordinary corpse path.
                Kill(_hero, _structureKiller);
                break;
        }
    }

    private static void Kill(GameObject target, GameObject source)
    {
        target.AttemptDamage(new DamageInfoInput(source)
        {
            DamageType = DamageType.Magic,
            DeathType = DeathType.Normal,
            Amount = 9999,
        });
    }

    public void DispatchOrder(in ScheduledOrder scheduled)
    {
        // No injected-order vocabulary: this scenario's stimulus is its own frame schedule.
        Dispatched++;
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        // The real sleepy-update queue, then the destroy-list reap - exactly as a real frame
        // does it, so a permanently-killed hero actually leaves the Objects walk here and a
        // claimed one demonstrably does not.
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
