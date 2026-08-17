// The shared base-test kit every Round-4 module port clones (api-freeze-v1 §5): the
// shadow-copy check (Save -> Load -> CRC == live CRC) is what turns Xfer completeness into
// a failing test instead of a review hope. Mirrors SimCore's XferVisitorTests shapes, but
// over REAL BehaviorModules on a real (headless) game.

using System.IO;
using OpenSage.Logic.Object;
using OpenSage.SimCore.Sync;
using Xunit;

namespace OpenSage.Tests.Logic.Object;

internal static class PortedModuleTestKit
{
    /// <summary>The module's live CRC: the same walk the Objects channel folds (F7/F8).</summary>
    public static uint LiveCrc(BehaviorModule module)
    {
        var visitor = new XferCrcVisitor();
        module.Xfer(visitor);
        return visitor.Value;
    }

    public static byte[] Save(BehaviorModule module)
    {
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            module.Xfer(save);
        }
        return stream.ToArray();
    }

    public static void Load(BehaviorModule module, byte[] state)
    {
        var stream = new MemoryStream(state);
        using var load = new XferLoad(stream, leaveOpen: true);
        module.Xfer(load);
    }

    /// <summary>
    /// THE shadow-copy base test (design-module-api §6): live state saved, loaded into a
    /// shadow instance, and the shadow's CRC must equal the live CRC. A mismatch means
    /// mutable sim state exists outside the Xfer walk.
    /// </summary>
    public static void AssertShadowCopyCrcEqualsLiveCrc(BehaviorModule live, BehaviorModule shadow)
    {
        var liveCrc = LiveCrc(live);
        Load(shadow, Save(live));
        Assert.Equal(liveCrc, LiveCrc(shadow));

        // And the round-trip is byte-stable: saving the shadow reproduces the stream.
        Assert.Equal(Save(live), Save(shadow));
    }
}
