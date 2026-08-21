// FixVector / FixMath / FixMatrix4x3 tests, centered on overflow rule R2
// (design-simcore-scaffolding §1.2, api-freeze-v1 F3): all distance-vs-range
// comparisons run 128-bit wide and never materialize a Fix64 square, so the
// shipping 9,999,999 AttackRange sentinel compares exactly.

using System;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class FixVectorMathTests
{
    private static Fix64 F(string literal) => Fix64.FromDecimalLiteral(literal);

    private static FixVector3 V(string x, string y, string z) => new FixVector3(F(x), F(y), F(z));

    [Fact]
    public void VectorArithmetic_KnownValues()
    {
        var a = V("1", "2", "3");
        var b = V("4", "-5", "6");
        Assert.Equal(V("5", "-3", "9"), a + b);
        Assert.Equal(V("-3", "7", "-3"), a - b);
        Assert.Equal(V("2", "4", "6"), a * Fix64.Two);
        Assert.Equal(V("2", "4", "6"), Fix64.Two * a);
        Assert.Equal(V("-1", "-2", "-3"), -a);
        Assert.Equal(F("12"), FixVector3.Dot(a, b));   // 4 − 10 + 18
    }

    [Fact]
    public void Cross_IsRightHanded()
    {
        var x = V("1", "0", "0");
        var y = V("0", "1", "0");
        var z = V("0", "0", "1");
        Assert.Equal(z, FixVector3.Cross(x, y));
        Assert.Equal(x, FixVector3.Cross(y, z));
        Assert.Equal(-z, FixVector3.Cross(y, x));
    }

    [Fact]
    public void Distance_PythagoreanTriple_IsExact()
    {
        Assert.Equal(F("5"), FixMath.Distance(FixVector3.Zero, V("3", "4", "0")));
        Assert.Equal(F("13"), FixMath.Distance(V("1", "1", "1"), V("4", "5", "13")));   // 3-4-12-13
    }

    [Fact]
    public void IsWithin_SentinelRange_DoesNotOverflow()
    {
        // rangeSq = 1e14 overflows Q31.32 (proven in Fix64CoreTests): the wide
        // compare must still get this right for the shipping sentinel.
        var sentinel = F("9999999");
        var origin = FixVector3.Zero;
        var acrossTheMap = V("5000", "5000", "0");
        Assert.True(FixMath.IsWithin(origin, acrossTheMap, sentinel));

        // And a genuinely out-of-range pair stays out.
        Assert.False(FixMath.IsWithin(origin, acrossTheMap, F("7000")));
    }

    [Fact]
    public void IsWithin_BoundaryIsInclusive()
    {
        var a = FixVector3.Zero;
        var b = V("3", "4", "0");
        Assert.True(FixMath.IsWithin(a, b, F("5")));
        Assert.True(FixMath.IsWithin(a, b, F("5.0000001")));
        Assert.False(FixMath.IsWithin(a, b, F("4.9999999")));
        Assert.False(FixMath.IsWithin(a, b, F("-1")));
    }

    [Fact]
    public void CompareDistance_TotalOrder()
    {
        var o = FixVector3.Zero;
        var near = V("1", "1", "0");
        var far = V("10", "10", "0");
        Assert.Equal(-1, FixMath.CompareDistance(o, near, o, far));
        Assert.Equal(1, FixMath.CompareDistance(o, far, o, near));
        Assert.Equal(0, FixMath.CompareDistance(o, near, o, near));
        // Sentinel-scale distances compare without overflow.
        var sentinelAway = V("9999999", "0", "0");
        Assert.Equal(1, FixMath.CompareDistance(o, sentinelAway, o, far));
    }

    [Fact]
    public void Length_And_NormalizedOrZero()
    {
        var v = V("3", "4", "0");
        Assert.Equal(F("5"), v.Length());

        var n = v.NormalizedOrZero();
        var lengthError = Math.Abs(n.Length().RawValue - Fix64.One.RawValue);
        Assert.True(lengthError <= 16, $"normalized length off by {lengthError} raw ulps");

        Assert.Equal(FixVector3.Zero, FixVector3.Zero.NormalizedOrZero());
    }

    [Fact]
    public void FixVector2_Basics()
    {
        var a = new FixVector2(F("3"), F("4"));
        Assert.Equal(F("5"), a.Length());
        Assert.Equal(F("11"), FixVector2.Dot(a, new FixVector2(F("1"), F("2"))));
        Assert.Equal(new FixVector2(F("4"), F("6")), a + new FixVector2(F("1"), F("2")));
    }

    [Fact]
    public void ScalarHelpers_MatchSystemMath()
    {
        Assert.Equal(3, FixMath.Min(3, 7));
        Assert.Equal(7, FixMath.Max(3, 7));
        Assert.Equal(5, FixMath.Clamp(9, 0, 5));
        Assert.Equal(0, FixMath.Clamp(-9, 0, 5));
        Assert.Equal(-4L, FixMath.Min(-4L, 4L));
        Assert.Equal(4u, FixMath.Max(3u, 4u));
        Assert.Equal(F("2"), FixMath.Min(F("2"), F("3")));
        Assert.Equal(F("3"), FixMath.Max(F("2"), F("3")));
        Assert.Equal(F("2.5"), FixMath.Clamp(F("9"), F("0"), F("2.5")));
    }

    [Fact]
    public void Matrix_IdentityAndTranslation_AreExact()
    {
        var p = V("1", "2", "3");
        Assert.Equal(p, FixMatrix4x3.Identity.Transform(p));

        var t = FixMatrix4x3.CreateTranslation(V("10", "-20", "30"));
        Assert.Equal(V("11", "-18", "33"), t.Transform(p));
        Assert.Equal(p, t.TransformNormal(p));   // directions ignore translation
    }

    [Fact]
    public void Matrix_RotationZ_QuarterTurn()
    {
        var rot = FixMatrix4x3.CreateRotationZ(Fix64.PiOver2);
        var rotated = rot.Transform(V("1", "0", "0"));
        AssertComponentClose(F("0"), rotated.X);
        AssertComponentClose(F("1"), rotated.Y);
        Assert.Equal(F("0"), rotated.Z);
    }

    [Fact]
    public void Matrix_Composition_MatchesSequentialTransform()
    {
        var rot = FixMatrix4x3.CreateRotationZ(F("0.7"));
        var trans = FixMatrix4x3.CreateTranslation(V("5", "6", "7"));
        var composed = rot * trans;
        var p = V("1", "2", "3");
        Assert.Equal(trans.Transform(rot.Transform(p)), composed.Transform(p));
    }

    private static void AssertComponentClose(Fix64 expected, Fix64 actual)
    {
        // LUT trig resolution: allow ~one 65,536-step table step (≈ 1e-4).
        var delta = Math.Abs(expected.RawValue - actual.RawValue);
        Assert.True(delta <= 700_000, $"expected {expected} got {actual} (Δ {delta} raw)");
    }
}
