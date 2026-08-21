// Contract tests for the AttributeModifierPoolUpdate port (R12): the screen-blend
// accumulation core exercised directly against hand-built pools (packet test cases), plus
// the shadow-copy base test on a real spawned object.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class AttributeModifierPoolUpdateContractTests
{
    private const int KeyA = 1;
    private const int KeyB = 2;

    private const int NonAiType = 0;
    private const int AiTypeLow = 10;
    private const int AiTypeHigh = 14;

    private static AttributeModifierPoolUpdate.PoolRecord Record(
        decimal value, int key, int sourceType, uint expiryFrame)
    {
        return new AttributeModifierPoolUpdate.PoolRecord
        {
            Value = Fix64.FromDecimalLiteral(value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Key = key,
            SourceType = sourceType,
            ExpiryFrame = new LogicFrame(expiryFrame),
        };
    }

    [Fact]
    public void EmptyPool_ReturnsZero()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord>();
        var result = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        Assert.Equal(Fix64.Zero, result);
    }

    [Fact]
    public void ZeroFrame_NoActiveRecordsYet_ReturnsZero()
    {
        // A record only becomes active once frame < expiry; at frame 0 with expiry 0 it is
        // never active, so the accumulator stays at its zero seed.
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord> { Record(0.5m, KeyA, NonAiType, 0) };
        var result = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        Assert.Equal(Fix64.Zero, result);
    }

    [Fact]
    public void SingleMatchingModifier_ReturnsItsValue()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord> { Record(0.5m, KeyA, NonAiType, 100) };
        var result = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        Assert.Equal(Fix64.FromDecimalLiteral("0.5"), result);
    }

    [Fact]
    public void TwoHalfValueModifiers_CompositeViaScreenBlend_NotAdditive()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord>
        {
            Record(0.5m, KeyA, NonAiType, 100),
            Record(0.5m, KeyA, NonAiType, 100),
        };
        var result = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);

        // Screen-blend: 1 - (1-0.5)(1-0.5) = 0.75, NOT the additive 1.0.
        Assert.Equal(Fix64.FromDecimalLiteral("0.75"), result);
        Assert.NotEqual(Fix64.One, result);
    }

    [Fact]
    public void FrameBoundary_ActiveBeforeExpiry_InactiveAtOrAfter()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord> { Record(0.5m, KeyA, NonAiType, 10) };

        var beforeExpiry = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(9), KeyA, includeAiTypes: false);
        var atExpiry = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(10), KeyA, includeAiTypes: false);
        var afterExpiry = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(11), KeyA, includeAiTypes: false);

        Assert.Equal(Fix64.FromDecimalLiteral("0.5"), beforeExpiry);
        Assert.Equal(Fix64.Zero, atExpiry);
        Assert.Equal(Fix64.Zero, afterExpiry);
    }

    [Fact]
    public void AiType_ExcludedByDefault_IncludedWithOverride()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord>
        {
            Record(0.5m, KeyA, AiTypeLow, 100),
            Record(0.4m, KeyA, AiTypeHigh, 100),
        };

        var withoutOverride = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        Assert.Equal(Fix64.Zero, withoutOverride);

        var withOverride = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: true);
        // 1 - (1-0.5)(1-0.4) = 0.7
        Assert.Equal(Fix64.FromDecimalLiteral("0.7"), withOverride);
    }

    [Fact]
    public void NonAiType_AlwaysIncluded()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord> { Record(0.3m, KeyA, NonAiType, 100) };

        var withoutOverride = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        var withOverride = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: true);

        Assert.Equal(Fix64.FromDecimalLiteral("0.3"), withoutOverride);
        Assert.Equal(Fix64.FromDecimalLiteral("0.3"), withOverride);
    }

    [Fact]
    public void WrongKey_NeverMatches()
    {
        var pool = new List<AttributeModifierPoolUpdate.PoolRecord> { Record(0.5m, KeyB, NonAiType, 100) };
        var result = AttributeModifierPoolUpdate.Accumulate(pool, new LogicFrame(0), KeyA, includeAiTypes: false);
        Assert.Equal(Fix64.Zero, result);
    }

    [Fact]
    public void RecordWalkOrder_TwoPermutations_AgreeWithinTolerance()
    {
        var forward = new List<AttributeModifierPoolUpdate.PoolRecord>
        {
            Record(0.5m, KeyA, NonAiType, 100),
            Record(0.3m, KeyA, NonAiType, 100),
            Record(0.2m, KeyA, NonAiType, 100),
        };
        var reversed = new List<AttributeModifierPoolUpdate.PoolRecord>
        {
            Record(0.2m, KeyA, NonAiType, 100),
            Record(0.3m, KeyA, NonAiType, 100),
            Record(0.5m, KeyA, NonAiType, 100),
        };

        var forwardResult = AttributeModifierPoolUpdate.Accumulate(forward, new LogicFrame(0), KeyA, includeAiTypes: false);
        var reversedResult = AttributeModifierPoolUpdate.Accumulate(reversed, new LogicFrame(0), KeyA, includeAiTypes: false);

        // Screen-blend is mathematically commutative; fixed-point rounding may still differ
        // by a representational quantum between walk orders (packet: "within floating-point
        // tolerance band"), so compare with a small Fix64 epsilon rather than exact equality.
        var epsilon = Fix64.FromDecimalLiteral("0.0001");
        var delta = forwardResult - reversedResult;
        if (delta < Fix64.Zero)
        {
            delta = -delta;
        }
        Assert.True(delta <= epsilon, $"forward={forwardResult} reversed={reversedResult}");
    }

    // ---- module-level: registration, wake, and the shadow-copy base test ----

    private const string Definitions = @"
Object ModifierPoolHost
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierPoolUpdate ModuleTag_ModifierPool
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x960);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void AddModifier_IsQueryableUntilExpiry()
    {
        var game = NewGame();
        var host = game.SpawnObject("ModifierPoolHost", game.CivilianPlayer, new Vector3(0, 0, 0));
        var module = host.BehaviorModules.OfType<AttributeModifierPoolUpdate>().Single();

        module.AddModifier(Fix64.FromDecimalLiteral("0.5"), KeyA, NonAiType, new LogicFrame(1000));

        Assert.Equal(Fix64.FromDecimalLiteral("0.5"), module.GetAccumulatedModifier(KeyA));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_WithPooledRecords()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("ModifierPoolHost", game.CivilianPlayer, new Vector3(0, 0, 0));
        var live = liveHost.BehaviorModules.OfType<AttributeModifierPoolUpdate>().Single();
        live.AddModifier(Fix64.FromDecimalLiteral("0.5"), KeyA, NonAiType, new LogicFrame(1000));
        live.AddModifier(Fix64.FromDecimalLiteral("0.3"), KeyB, AiTypeLow, new LogicFrame(500));

        var shadowHost = game.SpawnObject("ModifierPoolHost", game.CivilianPlayer, new Vector3(50, 50, 0));
        var shadow = shadowHost.BehaviorModules.OfType<AttributeModifierPoolUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
