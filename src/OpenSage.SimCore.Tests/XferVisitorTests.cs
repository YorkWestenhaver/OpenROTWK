// Gate tests for scaffolding step 5, part 2: the four IXfer visitors over one shared walk.
// The load-bearing claims: (1) Save -> Load round-trips every primitive bit-exactly; (2) the
// CRC of a restored copy equals the live CRC (the shadow-copy base test shape every module
// port will reuse); (3) the CRC visitor folds each call's canonical buffer INDEPENDENTLY -
// byte-identical to hand-driving XferCrc per call; (4) names, tolerances and module identity
// never reach the accumulator; (5) the deep dump folds the identical bytes it streams.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class XferVisitorTests
{
    private enum TestKind : byte
    {
        None = 0,
        Sharp = 7,
    }

    /// <summary>A fake module state exercising every IXfer primitive exactly once.</summary>
    private sealed class FakeState
    {
        public Fix64 Health = Fix64.FromRaw(0x0000_002A_8000_0000); // 42.5 in Q31.32
        public FixVector3 Position = new(Fix64.One, Fix64.FromRaw(-1L << 32), Fix64.Zero);
        public int Stance = -3;
        public uint Flags = 0xDEADBEEFu;
        public bool Alive = true;
        public LogicFrame NextWake = new(123u);
        public LogicFrameSpan Cooldown = new(45u);
        public ObjectId Target = new(77u);
        public TestKind Kind = TestKind.Sharp;
        public BitArray512 Upgrades = MakeBits();
        public List<uint> Riders = new() { 5u, 6u, 7u };
        public byte Version = 3;

        private static BitArray512 MakeBits()
        {
            var bits = new BitArray512(96);
            bits.Set(0, true);
            bits.Set(65, true);
            bits.Set(95, true);
            return bits;
        }

        public void Xfer(IXfer xfer)
        {
            Version = xfer.XferVersion(Version);
            xfer.XferFix64("Health", ref Health, Tolerance.Quantum);
            xfer.XferFixVector3("Position", ref Position, Tolerance.Band);
            xfer.XferInt("Stance", ref Stance);
            xfer.XferUInt("Flags", ref Flags);
            xfer.XferBool("Alive", ref Alive);
            xfer.XferFrame("NextWake", ref NextWake);
            xfer.XferFrameSpan("Cooldown", ref Cooldown);
            xfer.XferObjectId("Target", ref Target);
            xfer.XferEnum("Kind", ref Kind);
            xfer.XferBitArray("Upgrades", ref Upgrades);
            xfer.XferList("Riders", Riders, static (IXfer x, ref uint item) => x.XferUInt("Rider", ref item));
        }

        public void Scramble()
        {
            Health = Fix64.Zero;
            Position = FixVector3.Zero;
            Stance = 0;
            Flags = 0;
            Alive = false;
            NextWake = LogicFrame.Zero;
            Cooldown = LogicFrameSpan.Zero;
            Target = ObjectId.Invalid;
            Kind = TestKind.None;
            Upgrades = new BitArray512(96);
            Riders.Clear();
            Riders.Add(999u);
            Version = 3;
        }
    }

    private static uint CrcOf(FakeState state)
    {
        var visitor = new XferCrcVisitor();
        state.Xfer(visitor);
        return visitor.Value;
    }

    [Fact]
    public void SaveLoadRoundTripsEveryPrimitive()
    {
        var original = new FakeState();
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            original.Xfer(save);
        }

        var restored = new FakeState();
        restored.Scramble();
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            restored.Xfer(load);
        }

        Assert.Equal(original.Health, restored.Health);
        Assert.Equal(original.Position, restored.Position);
        Assert.Equal(original.Stance, restored.Stance);
        Assert.Equal(original.Flags, restored.Flags);
        Assert.Equal(original.Alive, restored.Alive);
        Assert.Equal(original.NextWake, restored.NextWake);
        Assert.Equal(original.Cooldown, restored.Cooldown);
        Assert.Equal(original.Target, restored.Target);
        Assert.Equal(original.Kind, restored.Kind);
        Assert.True(original.Upgrades.Equals(restored.Upgrades));
        Assert.Equal(original.Riders, restored.Riders);
        Assert.Equal(original.Version, restored.Version);
    }

    [Fact]
    public void ShadowCopyCrcEqualsLiveCrc()
    {
        // The base test every module port inherits (api-freeze-v1 §5): Save -> Load into a
        // shadow copy, then the shadow's CRC must equal the live CRC. A mismatch means state
        // exists outside the walk.
        var live = new FakeState();
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            live.Xfer(save);
        }
        var shadow = new FakeState();
        shadow.Scramble();
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            shadow.Xfer(load);
        }
        Assert.Equal(CrcOf(live), CrcOf(shadow));
    }

    [Fact]
    public void CrcVisitorFoldsEachCallIndependently()
    {
        // Byte-identical to hand-driving XferCrc with one canonical buffer per call: a uint
        // field folds its 4 LE bytes as one word; a bool folds one byte through the byte loop.
        // Bool first, then uint: the 1-byte call must go through the byte loop and the uint
        // must still fold as one word of its OWN buffer. A concatenating implementation would
        // regroup the five bytes as word [01 EF BE AD] + trailing DE and diverge.
        var visitor = new XferCrcVisitor();
        var alive = true;
        uint flags = 0xDEADBEEFu;
        visitor.XferBool("Alive", ref alive);
        visitor.XferUInt("Flags", ref flags);

        var manual = new XferCrc();
        manual.Fold(new byte[] { 0x01 });                   // bool, one trailing byte
        manual.Fold(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }); // LE image of 0xDEADBEEF, one word
        Assert.Equal(manual.Value, visitor.Value);

        // And the concatenated fold differs, proving the visitor kept the call boundary.
        var joined = new XferCrc();
        joined.Fold(new byte[] { 0x01, 0xEF, 0xBE, 0xAD, 0xDE });
        Assert.NotEqual(joined.Value, visitor.Value);
    }

    [Fact]
    public void NamesTolerancesAndIdentityAreNeverFolded()
    {
        var a = new XferCrcVisitor();
        uint valueA = 42;
        a.BeginModule(new XferModuleId(1, 2, "ModuleTag_01", "AutoHealBehavior"));
        a.XferUInt("HealRate", ref valueA, Tolerance.Quantum);
        a.EndModule();

        var b = new XferCrcVisitor();
        uint valueB = 42;
        b.XferUInt("CompletelyDifferentLabel", ref valueB, Tolerance.Band);

        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void ListFoldsCountThenItems()
    {
        var visitor = new XferCrcVisitor();
        var list = new List<uint> { 0x11111111u, 0x22222222u };
        visitor.XferList("L", list, static (IXfer x, ref uint item) => x.XferUInt("Item", ref item));

        var manual = new XferCrc();
        manual.Fold(new byte[] { 0x02, 0x00, 0x00, 0x00 }); // count = 2
        manual.Fold(new byte[] { 0x11, 0x11, 0x11, 0x11 });
        manual.Fold(new byte[] { 0x22, 0x22, 0x22, 0x22 });
        Assert.Equal(manual.Value, visitor.Value);
    }

    [Fact]
    public void VersionGateIsSymmetricAndRejectsNewerStreams()
    {
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            Assert.Equal(3, save.XferVersion(3));
        }

        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            // A newer reader sees the stored (older) version and branches on it.
            Assert.Equal(3, load.XferVersion(5));
        }

        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            // An older reader meeting a newer stream is malformed input.
            Assert.Throws<InvalidDataException>(() => load.XferVersion(2));
        }
    }

    [Fact]
    public void DeepDumpFoldsTheIdenticalBytesItStreams()
    {
        var state = new FakeState();

        var text = new StringWriter();
        uint deepCrc;
        using (var writer = new DeepCrcWriter(text, leaveOpen: true))
        {
            var deep = new XferDeepDump(writer);
            deep.BeginModule(new XferModuleId(9, 0, "ModuleTag_02", "FakeState"));
            state.Xfer(deep);
            deep.EndModule();
            deepCrc = deep.Value;
        }

        Assert.Equal(CrcOf(state), deepCrc);

        var dump = text.ToString();
        Assert.StartsWith(DeepCrcWriter.HeaderLine, dump);
        Assert.Contains("R 9 0 ModuleTag_02 FakeState Health Q", dump);
        // LE image of 42.5 in Q31.32: raw 0x0000002A80000000 -> bytes 00 00 00 80 2a 00 00 00.
        Assert.Contains("00000080 2a000000".Replace(" ", ""), dump);
    }

    [Fact]
    public void DeepDumpIsDeterministicText()
    {
        static string DumpOnce()
        {
            var state = new FakeState();
            var text = new StringWriter();
            using var writer = new DeepCrcWriter(text, leaveOpen: true);
            var deep = new XferDeepDump(writer);
            deep.BeginModule(new XferModuleId(9, 0, "ModuleTag_02", "FakeState"));
            state.Xfer(deep);
            deep.EndModule();
            return text.ToString();
        }

        Assert.Equal(DumpOnce(), DumpOnce());
    }

    [Fact]
    public void DeepDumpLocalizesASingleFieldMutation()
    {
        static string Dump(FakeState state)
        {
            var text = new StringWriter();
            using var writer = new DeepCrcWriter(text, leaveOpen: true);
            var deep = new XferDeepDump(writer);
            deep.BeginModule(new XferModuleId(9, 0, "ModuleTag_02", "FakeState"));
            state.Xfer(deep);
            deep.EndModule();
            return text.ToString();
        }

        var baseline = new FakeState();
        var mutated = new FakeState();
        mutated.Flags ^= 0x1u;

        var a = Dump(baseline).Split('\n');
        var b = Dump(mutated).Split('\n');
        Assert.Equal(a.Length, b.Length);

        var firstDiff = -1;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                firstDiff = i;
                break;
            }
        }

        Assert.True(firstDiff >= 0);
        Assert.Contains("Flags", a[firstDiff]);
    }
}
