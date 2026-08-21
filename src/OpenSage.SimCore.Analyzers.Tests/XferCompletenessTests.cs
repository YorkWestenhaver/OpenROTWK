using System.Linq;
using Xunit;

namespace OpenSage.SimCore.Analyzers.Tests;

/// <summary>
/// SIMCORE011 - Xfer/save-load completeness. A module field that is mutated as live sim state
/// but never referenced by the serialization walk (Load/Save/Persist/Xfer) desyncs save-load and
/// cross-machine lockstep. The R12 review swarm found this class of bug repeatedly; the canonical
/// case is TurretAIUpdate, reproduced verbatim-in-shape by <see cref="TurretShape"/>.
///
/// The rule is deliberately quiet: it fires only when a field is (a) declared on a module type,
/// (b) non-readonly / non-const / non-static, (c) actually mutated outside the ctor/OnCreate, and
/// (d) absent from every persist method. Each negative below removes exactly one of those.
///
/// These run in scoped mode with an empty scoped-dirs list, which suppresses the float
/// quarantine (SIMCORE001-010) so the fixtures can use the real float/enum field shapes without
/// noise; SIMCORE011 is scope-independent by design and still fires.
/// </summary>
public class XferCompletenessTests
{
    private const string GamePath = "/repo/src/OpenSage.Game/Logic/Object/Update/Sample.cs";

    // Minimal stand-ins for the engine's persistence substrate, so a fixture compiles against the
    // BCL alone. The analyzer keys off the type *name* "ModuleBase" / "IPersistableObject", the
    // same by-name convention the scope uses for [SimState].
    private const string Prelude = @"
namespace OpenSage
{
    public interface IPersistableObject { }

    public class StatePersister
    {
        public void PersistVersion(int v) { }
        public void PersistSingle(ref float f) { }
        public void PersistInt32(ref int i) { }
        public void PersistUInt32(ref uint i) { }
        public void PersistBoolean(ref bool b) { }
        public void BeginObject(string n) { }
        public void EndObject() { }
    }

    public abstract class ModuleBase : IPersistableObject
    {
        protected virtual void OnObjectCreated() { }
        internal virtual void Load(StatePersister reader) { reader.PersistVersion(1); }
    }
}
";

    private static string[] Fire(string moduleSource)
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { (GamePath, Prelude + moduleSource) },
            mode: "scoped",
            additionalFiles: new[] { ("/repo/src/OpenSage.Game/SimCoreScopedDirs.txt", "# empty\n") });

        return diagnostics
            .Where(d => d.Id == "SIMCORE011")
            .Select(d => d.GetMessage())
            .ToArray();
    }

    private static bool Mentions(string[] messages, string field) =>
        messages.Any(m => m.Contains("'" + field + "'"));

    // ------------------------------------------------------------------ positive

    [Fact]
    public void MutatedUnpersistedFieldFires()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private int _timer;

        public void Tick() { _timer = _timer + 1; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.True(Mentions(messages, "_timer"), "expected SIMCORE011 for _timer; got: " + string.Join(" | ", messages));
    }

    // ------------------------------------------------------------------ negatives

    [Fact]
    public void ReadonlyFieldDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private readonly int _config = 5;

        public int Read() { return _config; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void ConstructorOnlyFieldDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private int _initial;

        public SampleUpdate() { _initial = 7; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void OnObjectCreatedAssignmentIsTreatedAsConstruction()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private int _resolved;

        protected override void OnObjectCreated() { _resolved = 3; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void PersistedFieldDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private int _timer;

        public void Tick() { _timer = _timer + 1; }

        internal override void Load(StatePersister reader)
        {
            base.Load(reader);
            reader.PersistInt32(ref _timer);
        }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void NotXferedFieldDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public sealed class NotXferedAttribute : System.Attribute { }

    public class SampleUpdate : ModuleBase
    {
        [NotXfered]
        private int _cache;

        public void Tick() { _cache = _cache + 1; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void NonModuleClassDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class PlainHelper
    {
        private int _timer;
        public void Tick() { _timer = _timer + 1; }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void InjectedDependencyFieldDoesNotFire()
    {
        var messages = Fire(@"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleModuleData { }

    public class SampleUpdate : ModuleBase
    {
        // Not readonly, but a template handle, not sim state.
        private SampleModuleData _moduleData;

        public void Rebind(SampleModuleData d) { _moduleData = d; }

        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}");

        Assert.Empty(messages);
    }

    [Fact]
    public void SeverityIsInfo()
    {
        var diagnostics = AnalyzerHarness.Run(
            new[] { (GamePath, Prelude + @"
namespace OpenSage.Logic.Object
{
    using OpenSage;
    public class SampleUpdate : ModuleBase
    {
        private int _timer;
        public void Tick() { _timer = _timer + 1; }
        internal override void Load(StatePersister reader) { base.Load(reader); }
    }
}") },
            mode: "scoped",
            additionalFiles: new[] { ("/repo/src/OpenSage.Game/SimCoreScopedDirs.txt", "# empty\n") });

        var only = Assert.Single(diagnostics.Where(d => d.Id == "SIMCORE011"));
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, only.Severity);
    }

    // ------------------------------------------------------------------ real-world fixture

    // The shape of the R12 known-positive: TurretAIUpdate. Its Load() persists a run of unknown
    // scratch fields but never _turretAIstate / _waitUntil / _currentTarget - the state machine
    // that actually drives the turret. Those three must fire; the persisted scratch fields and the
    // readonly template handle must stay silent.
    private const string TurretShape = @"
namespace OpenSage.Logic.Object
{
    using OpenSage;

    public sealed class WeaponTarget { }
    public struct LogicFrame { }
    public sealed class TurretAIUpdateModuleData { public bool InitiallyDisabled; }

    public class TurretAIUpdate : ModuleBase
    {
        private readonly TurretAIUpdateModuleData _moduleData;

        private WeaponTarget _currentTarget;      // EXPECT SIMCORE011
        private LogicFrame _waitUntil;            // EXPECT SIMCORE011
        private TurretAIStates _turretAIstate;    // EXPECT SIMCORE011

        private float _unknownFloat1;             // persisted -> silent
        private float _unknownFloat2;             // persisted -> silent
        private uint _unknownFrame1;              // persisted -> silent
        private uint _unknownInt1;                // persisted -> silent

        public enum TurretAIStates { Disabled, Idle, ScanningForTargets, Turning, Attacking, Recentering }

        public TurretAIUpdate(TurretAIUpdateModuleData moduleData)
        {
            _moduleData = moduleData;
            _turretAIstate = _moduleData.InitiallyDisabled ? TurretAIStates.Disabled : TurretAIStates.ScanningForTargets;
        }

        public void Update(WeaponTarget target, LogicFrame now)
        {
            _turretAIstate = TurretAIStates.Turning;
            _currentTarget = target;
            _waitUntil = now;
        }

        internal override void Load(StatePersister reader)
        {
            reader.PersistVersion(2);
            reader.PersistSingle(ref _unknownFloat1);
            reader.PersistSingle(ref _unknownFloat2);
            reader.PersistUInt32(ref _unknownFrame1);
            reader.PersistUInt32(ref _unknownInt1);
        }
    }
}";

    [Fact]
    public void TurretAIUpdateFixtureFlagsTheUnpersistedStateMachine()
    {
        var messages = Fire(TurretShape);

        Assert.True(Mentions(messages, "_turretAIstate"), "expected _turretAIstate; got: " + string.Join(" | ", messages));
        Assert.True(Mentions(messages, "_waitUntil"), "expected _waitUntil; got: " + string.Join(" | ", messages));
        Assert.True(Mentions(messages, "_currentTarget"), "expected _currentTarget; got: " + string.Join(" | ", messages));

        // The persisted scratch fields and the readonly template handle must NOT fire.
        Assert.False(Mentions(messages, "_unknownFloat1"));
        Assert.False(Mentions(messages, "_unknownFloat2"));
        Assert.False(Mentions(messages, "_unknownFrame1"));
        Assert.False(Mentions(messages, "_unknownInt1"));
        Assert.False(Mentions(messages, "_moduleData"));

        Assert.Equal(3, messages.Length);
    }
}
