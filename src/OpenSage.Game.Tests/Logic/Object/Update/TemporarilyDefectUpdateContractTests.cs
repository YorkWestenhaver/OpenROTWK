// Mocked-game unit tests for the TemporarilyDefectUpdate port (R13, data-derivable per the
// task packet - see the full source list and F-TDU-1/F-TDU-2/F-TDU-3 in
// bfme2-workbench/research/modules-r13/specs/TemporarilyDefectUpdateModuleData.md). One test
// per behavior branch, [create -> tick/act -> observable effect], plus the shadow-copy base
// test and a mid-defection save/load round-trip (F-TDU-2's FindTeamById addition, exercised
// end to end).
//
// Because StartTemporaryDefect has no in-repo caller yet (DominateEnemySpecialPower, the
// eventual GPL-named caller, is unported - F-TDU-3), every test drives it directly via the
// module's own internal handle (unit.BehaviorModules.OfType&lt;TemporarilyDefectUpdate&gt;()),
// the same "call the module's own public/internal surface directly, no special-power caller
// needed" pattern other TriggeredBy-independent modules' contract tests use.
//
// Sleepy-update caveat (applied to this module's actual shape): a freshly spawned module's
// first Update() runs on the SECOND HeadlessSimGame.Step(), not the first (the module's first
// wake frame is CurrentFrame 1, matching UpdateSleepTime.None/Frames' 1-frame-minimum delay).
// This module's ctor schedules Forever and has no other observable ctor-time effect, so the
// caveat's only bite here is on StartTemporaryDefect calls: the revert-frame math itself is
// exact (it reads CurrentFrame at call time), but the EARLIEST frame at which the revert can
// be observed to have actually happened is still gated by the module's normal dispatch
// cadence. Tests below step one frame beyond the naive DefectDuration-frame count wherever the
// assertion needs the post-revert Update() to have actually run.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class TemporarilyDefectUpdateContractTests
{
    private static readonly Vector3 Origin = new(0, 0, 0);

    // 5 Hz logic rate (F6): 1000ms = 5 frames.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object DefectableUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TemporarilyDefectUpdate ModuleTag_Defect
    DefectDuration = 1000
  End
End

Object DefectableUnitTinyDuration
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TemporarilyDefectUpdate ModuleTag_Defect
    DefectDuration = 1
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x7DEF) // "tdef"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static TemporarilyDefectUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<TemporarilyDefectUpdate>().Single();

    /// <summary>
    /// Teams registered with the game's real TeamFactory via <c>TeamFactory.Initialize</c> (the
    /// production map-load path, driven here with in-memory <see cref="OpenSage.Data.Map.Team"/>
    /// records instead of a parsed map) - required here because this module's revert path
    /// resolves the original team BY ID through <c>Context.GameLogic.FindTeamById</c>
    /// (F-TDU-2), which only ever finds teams whose TeamTemplate the factory itself knows
    /// about. <c>Initialize</c> clears and rebuilds the factory's whole template list, so this
    /// takes every team a test needs in one call; each singleton team's id is assigned
    /// sequentially in array order (1, 2, 3, ...), matching what <c>TeamFactory.AddTeam</c>
    /// (called internally for each singleton entry) itself assigns.
    /// </summary>
    private static Team[] MakeRegisteredTeams(HeadlessSimGame game, params (string Name, Player Owner)[] teams)
    {
        var mapTeams = teams
            .Select(t => new OpenSage.Data.Map.Team { Name = t.Name, Owner = t.Owner.Name, IsSingleton = true })
            .ToArray();

        game.TeamFactory.Initialize(mapTeams);

        var result = new Team[teams.Length];
        for (var i = 0; i < teams.Length; i++)
        {
            result[i] = game.TeamFactory.FindTeamById((uint)(i + 1));
            Assert.NotNull(result[i]);
        }
        return result;
    }

    // ---- test case 1: passive at construction ----

    [Fact]
    public void Construction_StartsDormant_NoStepNeeded()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("OriginalTeam", game.CivilianPlayer));
        unit.Team = teams[0];

        var module = ModuleOf(unit);

        Assert.False(IsActive(module));
        Assert.Equal(teams[0], unit.Team);
    }

    // ---- test case 2: StartTemporaryDefect switches the team synchronously ----

    [Fact]
    public void StartTemporaryDefect_SwitchesTeamImmediately_Synchronous()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("TeamA", game.CivilianPlayer), ("TeamB", game.PlayerManager.NeutralPlayer));
        var (teamA, teamB) = (teams[0], teams[1]);
        unit.Team = teamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB);

        // Synchronous field write (mirrors GPL's own synchronous setTeam() inside
        // Object::defect having no per-frame delay either) - no Step() needed.
        Assert.Equal(teamB, unit.Team);
        Assert.True(IsActive(module));
    }

    // ---- test case 3: revert fires at exactly DefectDuration frames, not before/after ----

    [Fact]
    public void Revert_FiresAtExactlyDefectDurationFrames_NotBeforeNotAfter()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("TeamA", game.CivilianPlayer), ("TeamB", game.PlayerManager.NeutralPlayer));
        var (teamA, teamB) = (teams[0], teams[1]);
        unit.Team = teamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB); // at frame 0; DefectDuration = 1000ms -> 5 frames

        // One frame short of the revert, plus one extra Step() (sleepy-update caveat) to
        // guarantee the module's Update() has actually run and re-armed rather than merely
        // having a stale scheduled wake.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.Equal(teamB, unit.Team);
        Assert.True(IsActive(module));

        // Step to frame 5 and one more (caveat's dispatch-lag margin).
        game.Step();
        game.Step();

        Assert.Equal(teamA, unit.Team);
        Assert.False(IsActive(module));
    }

    // ---- test case 4: save/load survives, resolving the original team fresh by id ----

    [Fact]
    public void Revert_ResolvesOriginalTeamByIdNotReference_SurvivesSaveLoad()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("TeamA", game.CivilianPlayer), ("TeamB", game.PlayerManager.NeutralPlayer));
        var (teamA, teamB) = (teams[0], teams[1]);
        unit.Team = teamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB); // at frame 0

        // Save mid-defection (before the revert frame).
        var state = PortedModuleTestKit.Save(module);
        var wake = module.NextWakeFrameForWalk;
        PortedModuleTestKit.Load(module, state);
        module.NextWakeFrameForWalk = wake;

        // Still mid-defection after the round-trip.
        Assert.True(IsActive(module));
        Assert.Equal(teamB, unit.Team);

        // Step to (and past) the revert frame in the now-loaded instance: the revert resolves
        // the original team fresh via Context.GameLogic.FindTeamById(_originalTeamId), not a
        // stale in-memory Team reference (F-TDU-2 exercised end to end, not just the write
        // path - the module never holds a Team field, only the uint id).
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(teamA, unit.Team);
        Assert.False(IsActive(module));
    }

    // ---- test case 5: re-entrant defection keeps the TRUE original team ----

    [Fact]
    public void Reentrant_SecondDefectBeforeFirstReverts_KeepsTrueOriginalTeam()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(
            game,
            ("TeamA", game.CivilianPlayer),           // true original owner
            ("TeamB", game.PlayerManager.NeutralPlayer), // first dominator
            ("TeamC", game.PlayerManager.NeutralPlayer)); // second dominator
        var (teamA, teamB, teamC) = (teams[0], teams[1], teams[2]);
        unit.Team = teamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB); // frame 0

        for (var i = 0; i < 2; i++)
        {
            game.Step(); // now at frame 2
        }

        module.StartTemporaryDefect(teamC); // second activation, before the frame-5 revert

        // Switch always takes effect immediately; the true original (teamA) is still the one
        // that will eventually be restored, not teamB.
        Assert.Equal(teamC, unit.Team);
        Assert.Equal(teamA.Id, OriginalTeamIdOf(module));

        // New revert frame is 2 + 5 = 7 (re-armed by the second StartTemporaryDefect call).
        // Step to it plus the caveat's dispatch-lag margin.
        for (var i = 0; i < 5; i++)
        {
            game.Step(); // frames 3..7
        }
        game.Step(); // dispatch-lag margin

        Assert.Equal(teamA, unit.Team);
        Assert.NotEqual(teamB, unit.Team);
        Assert.False(IsActive(module));
    }

    // ---- test case 6: object died before the revert frame - no exception, no team write ----

    [Fact]
    public void Revert_ObjectDiedBeforeRevertFrame_NoExceptionNoTeamWrite()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("TeamA", game.CivilianPlayer), ("TeamB", game.PlayerManager.NeutralPlayer));
        var (teamA, teamB) = (teams[0], teams[1]);
        unit.Team = teamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB); // frame 0

        // Mark the object effectively dead before the revert frame (frame 5) - the precise
        // observable ActiveBody itself flips when health crosses to zero (§1 step 3's guard),
        // exercised directly rather than through the full destroy cascade so this test targets
        // only this module's own guard.
        unit.IsEffectivelyDead = true;

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        // No exception (implicit: the loop above completing is the assertion), module still
        // reaches its "done" state, and the team is left as-is (whatever it was at death - no
        // write is attempted, matching §1 step 3's dead-object early-out; GPL gives no
        // guidance on a dead object's team and none is invented here).
        Assert.False(IsActive(module));
        Assert.Equal(teamB, unit.Team);
    }

    // ---- test case 7: original team unresolvable - silent no-op revert ----

    [Fact]
    public void Revert_OriginalTeamDisbanded_SilentNoOpRevert()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);

        // An UNREGISTERED team (the internal Team ctor directly, bypassing TeamFactory.AddTeam)
        // models "the original team was disbanded while the object was defected": its id is
        // never known to the factory, so Context.GameLogic.FindTeamById(_originalTeamId) will
        // return null on revert, exactly like a genuinely disbanded team would.
        var unregisteredTeamA = new Team(
            new TeamTemplate(game.TeamFactory, 99, "DisbandedTeamA", game.CivilianPlayer, isSingleton: true),
            id: 12345);
        var teamB = MakeRegisteredTeams(game, ("TeamB", game.PlayerManager.NeutralPlayer))[0];
        unit.Team = unregisteredTeamA;

        var module = ModuleOf(unit);
        module.StartTemporaryDefect(teamB); // frame 0

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        // No exception, _active clears, and the object stays on its current (defected) team -
        // matches §1 step 3's silent no-op finding.
        Assert.False(IsActive(module));
        Assert.Equal(teamB, unit.Team);
    }

    // ---- test case 8: DefectDuration parses ms -> frames via ceil (S5) ----

    [Fact]
    public void DurationField_ParsesMsToFramesViaCeil()
    {
        var game = NewGame();

        var data = (TemporarilyDefectUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("DefectableUnitTinyDuration").Behaviors["ModuleTag_Defect"].Data;

        // DefectDuration = 1 (ms): ceil(1 * 5 / 1000) = 1, the S5 ceil-quantizing floor case.
        Assert.Equal(new LogicFrameSpan(1), data.DefectDuration);

        var dataFive = (TemporarilyDefectUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("DefectableUnit").Behaviors["ModuleTag_Defect"].Data;
        Assert.Equal(new LogicFrameSpan(5), dataFive.DefectDuration);
    }

    // ---- shared base test: shadow-copy CRC parity, mid-defection ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidDefection()
    {
        var game = NewGame();
        var unit = game.SpawnObject("DefectableUnit", game.CivilianPlayer, Origin);
        var teams = MakeRegisteredTeams(game, ("TeamA", game.CivilianPlayer), ("TeamB", game.PlayerManager.NeutralPlayer));
        var (teamA, teamB) = (teams[0], teams[1]);
        unit.Team = teamA;

        var live = ModuleOf(unit);
        live.StartTemporaryDefect(teamB);

        for (var i = 0; i < 2; i++)
        {
            game.Step();
        }

        // "At rest" (_active == false) has no interesting state to round-trip, so this is
        // deliberately taken mid-defection rather than at spawn.
        var shadowHost = game.SpawnObject("DefectableUnit", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // ---- private observability helpers: read private sim state back through the module's own
    // Xfer walk (the same one the CRC/save-load tests exercise), never reflection. ----

    private static bool IsActive(TemporarilyDefectUpdate module)
    {
        var capture = new FieldCapture();
        module.Xfer(capture);
        return capture.BoolFields["Active"];
    }

    private static uint OriginalTeamIdOf(TemporarilyDefectUpdate module)
    {
        var capture = new FieldCapture();
        module.Xfer(capture);
        return capture.UIntFields["OriginalTeamId"];
    }

    /// <summary>
    /// A minimal <see cref="OpenSage.SimCore.Sync.IXfer"/> that records named Bool/UInt fields
    /// as the walk passes them, ignoring every other primitive kind. TemporarilyDefectUpdate's
    /// walk only ever calls XferVersion, XferBool, XferUInt and XferFrame, so those other
    /// members are legitimately inert here.
    /// </summary>
    private sealed class FieldCapture : OpenSage.SimCore.Sync.IXfer
    {
        public System.Collections.Generic.Dictionary<string, bool> BoolFields { get; } = new();
        public System.Collections.Generic.Dictionary<string, uint> UIntFields { get; } = new();

        public OpenSage.SimCore.Sync.XferMode Mode => OpenSage.SimCore.Sync.XferMode.Save;
        public void BeginModule(in OpenSage.SimCore.Sync.XferModuleId id) { }
        public void EndModule() { }
        public void XferFix64(string name, ref OpenSage.SimCore.Numerics.Fix64 value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Exact) { }
        public void XferFixVector3(string name, ref OpenSage.SimCore.Numerics.FixVector3 value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Exact) { }
        public void XferInt(string name, ref int value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Exact) { }
        public void XferUInt(string name, ref uint value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Exact) => UIntFields[name] = value;
        public void XferBool(string name, ref bool value) => BoolFields[name] = value;
        public void XferFrame(string name, ref LogicFrame value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Quantum) { }
        public void XferFrameSpan(string name, ref LogicFrameSpan value, OpenSage.SimCore.Sync.Tolerance tol = OpenSage.SimCore.Sync.Tolerance.Quantum) { }
        public void XferObjectId(string name, ref ObjectId value) { }
        public void XferEnum<T>(string name, ref T value) where T : struct, System.Enum { }
        public void XferBitArray(string name, ref OpenSage.SimCore.Numerics.BitArray512 value) { }
        public void XferList<T>(string name, System.Collections.Generic.List<T> list, OpenSage.SimCore.Sync.XferItem<T> item) { }
        public byte XferVersion(byte currentVersion) => currentVersion;
    }
}
