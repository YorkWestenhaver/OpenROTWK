// map-v1 - the Target-B conformance scenario: a real authored .map end-to-end. SimMapRun
// (OpenSage.Game) does the load half - MapFile -> SimScriptCompiler -> HeadlessSimGame with
// waypoints/teams/objects registered on a SimScriptHostAdapter - and this wrapper gives it
// the driver's dump plumbing: every checkpoint folds the real Objects walk, the logic RNG
// stream, and the OracleView channel (per-object id/template/position/health, the record
// group the workbench diffs against a retail memory dump).
//
// No order vocabulary: the map's compiled scripts are the stimulus. The run ends when the
// script program requests MAP_EXIT (Program reports the exit frame) or --until-frame.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage;
using OpenSage.Data.Map;
using OpenSage.Logic.Object;
using OpenSage.Logic.Script;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

internal sealed class MapScenario : IDriverScenario
{
    private readonly SimMapRun _run;
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
            foreach (var _ in _run.Game.GameLogic.Objects)
            {
                count++;
            }
            return count;
        }
    }

    public MapScenario(uint seed, string mapPath, IReadOnlyList<string> iniPaths)
    {
        MapFile mapFile;
        using (var stream = File.OpenRead(mapPath))
        {
            mapFile = MapFile.FromStream(stream);
        }

        var iniTexts = new List<string>();
        foreach (var iniPath in iniPaths)
        {
            iniTexts.Add(File.ReadAllText(iniPath));
        }

        _run = new SimMapRun(SageGame.Bfme2, seed, mapFile, iniTexts);

        var context = (SimContext)_run.Game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;

        _checker = new SyncChecker(new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(_run.Game.GameLogic),
            new LogicRandomChannelSource(random),
            new OracleViewChannelSource(_run.Game.GameLogic),
        });
    }

    public bool MapExitRequested => _run.MapExitRequested;

    public uint? MapExitFrame =>
        _run.Engine.MapExitRequested ? _run.Engine.MapExitFrame.Value : null;

    public int MapObjectsSpawned => _run.MapObjectsSpawned;

    public int MapObjectsSkipped => _run.MapObjectsSkipped;

    public void AttachWriter(DeepCrcWriter writer) => _writer = writer;

    public void IngestOrders(LogicFrame frame)
    {
        // The map's compiled scripts are the stimulus; no injected orders.
    }

    public void DispatchOrder(in ScheduledOrder scheduled)
    {
        Dispatched++;
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        _run.StepFrame();
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
