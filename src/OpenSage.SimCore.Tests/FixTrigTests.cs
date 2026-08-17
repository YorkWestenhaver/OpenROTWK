// LUT trig tests (api-freeze-v1 F2). The spot-check constants below were computed
// INDEPENDENTLY of the TableGen tool and of any platform libm: high-precision decimal
// Taylor series (60-digit Decimal arithmetic; π via the Machin formula, atan via
// half-angle argument reduction), rounded half-even to 2^32 — the same rounding rule
// TableGen uses. A regenerated FixTrig.Tables.g.cs that drifts by even one raw ulp on
// any of these 80 entries fails here.

using System;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests
{
    public class FixTrigTests
    {
        private static Fix64 FromDouble(double v) => Fix64.FromRaw((long)Math.Round(v * 4294967296.0));

        private static double ToDouble(Fix64 v) => v.RawValue / 4294967296.0;

        [Fact]
        public void Tables_HaveFrozenDimensions()
        {
            // F2: 16,384-entry quarter-symmetric sin, 1,025-entry atan.
            Assert.Equal(16384, FixTrig.SinTable.Length);
            Assert.Equal(1025, FixTrig.AtanTable.Length);
        }

        // (index, round(sin((π/2)·index/16384)·2^32)) — independently hand-computed.
        private static readonly (int Index, long Raw)[] SinSpotChecks =
        {
            (0, 0x0L), (257, 0x64E9D80L), (514, 0xC9C4012L), (771, 0x12E7ECEFL),
            (1028, 0x1930A99DL), (1285, 0x1F757C19L), (1542, 0x25B56AF9L), (1799, 0x2BEF7D97L),
            (2056, 0x3222BC38L), (2313, 0x384E302CL), (2570, 0x3E70E3FFL), (2827, 0x4489E393L),
            (3084, 0x4A983C51L), (3341, 0x509AFD46L), (3598, 0x56913750L), (3855, 0x5C79FD3CL),
            (4112, 0x625463F0L), (4369, 0x681F828EL), (4626, 0x6DDA7296L), (4883, 0x7384500FL),
            (5140, 0x791C39A5L), (5397, 0x7EA150CFL), (5654, 0x8412B9EFL), (5911, 0x896F9C79L),
            (6168, 0x8EB72310L), (6425, 0x93E87BA8L), (6682, 0x9902D7AAL), (6939, 0x9E056C0FL),
            (7196, 0xA2EF7183L), (7453, 0xA7C02483L), (7710, 0xAC76C57CL), (7967, 0xB11298E8L),
            (8224, 0xB592E769L), (8481, 0xB9F6FDEDL), (8738, 0xBE3E2DC0L), (8995, 0xC267CCADL),
            (9252, 0xC673351AL), (9509, 0xCA5FC61AL), (9766, 0xCE2CE390L), (10023, 0xD1D9F640L),
            (10280, 0xD5666BE8L), (10537, 0xD8D1B759L), (10794, 0xDC1B508BL), (11051, 0xDF42B4B3L),
            (11308, 0xE2476656L), (11565, 0xE528ED5EL), (11822, 0xE7E6D72DL), (12079, 0xEA80B6ADL),
            (12336, 0xECF62461L), (12593, 0xEF46BE78L), (12850, 0xF17228D8L), (13107, 0xF3780D30L),
            (13364, 0xF5581B04L), (13621, 0xF71207B8L), (13878, 0xF8A58E9FL), (14135, 0xFA127101L),
            (14392, 0xFB587629L), (14649, 0xFC776B6EL), (14906, 0xFD6F2436L), (15163, 0xFE3F7A00L),
            (15420, 0xFEE84C6EL), (15677, 0xFF698141L), (15934, 0xFFC30466L), (16383, 0xFFFFFFECL),
        };

        // (index, round(atan(index/1024)·2^32)) — independently hand-computed.
        private static readonly (int Index, long Raw)[] AtanSpotChecks =
        {
            (0, 0x0L), (1, 0x3FFFFFL), (64, 0xFFAADDCL), (128, 0x1FD5BA9BL),
            (193, 0x2FB0C75AL), (256, 0x3EB6EBF2L), (341, 0x524B0ADFL), (409, 0x6147C17CL),
            (512, 0x76B19C16L), (600, 0x87AF145BL), (683, 0x96961B0BL), (750, 0xA1D4F782L),
            (832, 0xAEAC4C39L), (900, 0xB895F421L), (1000, 0xC606C8A3L), (1024, 0xC90FDAA2L),
        };

        [Fact]
        public void SinTable_MatchesIndependentlyComputedConstants()
        {
            foreach (var (index, raw) in SinSpotChecks)
            {
                Assert.True(FixTrig.SinTable[index] == raw,
                    $"SinTable[{index}] = 0x{FixTrig.SinTable[index]:X} expected 0x{raw:X}");
            }
        }

        [Fact]
        public void AtanTable_MatchesIndependentlyComputedConstants()
        {
            foreach (var (index, raw) in AtanSpotChecks)
            {
                Assert.True(FixTrig.AtanTable[index] == raw,
                    $"AtanTable[{index}] = 0x{FixTrig.AtanTable[index]:X} expected 0x{raw:X}");
            }
        }

        [Fact]
        public void Sin_ExactAnchors()
        {
            Assert.Equal(Fix64.Zero, FixTrig.Sin(Fix64.Zero));
            Assert.Equal(Fix64.One, FixTrig.Cos(Fix64.Zero));
        }

        [Fact]
        public void Sin_AccuracySweep()
        {
            // Raw-indexed 65,536-step table: worst-case error one table step ≈ 9.6e-5,
            // plus reduction rounding. Benchmark-measured typical error ≈ 3.2e-5.
            for (var k = 0; k <= 2000; k++)
            {
                var a = -10.0 + k * 0.01;
                var angle = FromDouble(a);
                AssertClose(Math.Sin(a), ToDouble(FixTrig.Sin(angle)), 1.5e-4, $"Sin({a})");
                AssertClose(Math.Cos(a), ToDouble(FixTrig.Cos(angle)), 1.5e-4, $"Cos({a})");
            }
        }

        [Fact]
        public void SinCos_PythagoreanIdentity()
        {
            for (var k = 0; k < 100; k++)
            {
                var angle = FromDouble(-7.0 + k * 0.14);
                var s = ToDouble(FixTrig.Sin(angle));
                var c = ToDouble(FixTrig.Cos(angle));
                AssertClose(1.0, s * s + c * c, 3e-4, $"sin²+cos² at k={k}");
            }
        }

        [Fact]
        public void Atan2_ExactAxisCases()
        {
            Assert.Equal(Fix64.Zero, FixTrig.Atan2(Fix64.Zero, Fix64.Zero));
            Assert.Equal(Fix64.PiOver2, FixTrig.Atan2(Fix64.One, Fix64.Zero));
            Assert.Equal(-Fix64.PiOver2, FixTrig.Atan2(-Fix64.One, Fix64.Zero));
            Assert.Equal(Fix64.Zero, FixTrig.Atan2(Fix64.Zero, Fix64.One));
            Assert.Equal(Fix64.Pi, FixTrig.Atan2(Fix64.Zero, -Fix64.One));
        }

        [Fact]
        public void Atan2_AccuracySweepAllQuadrants()
        {
            // 1,025-entry ratio table, truncating index: worst-case ≈ 1e-3 rad.
            for (var xi = -8; xi <= 8; xi++)
            {
                for (var yi = -8; yi <= 8; yi++)
                {
                    if (xi == 0 && yi == 0)
                    {
                        continue;
                    }
                    var x = xi * 0.7 + (xi >= 0 ? 0.05 : -0.05);
                    var y = yi * 1.3 + (yi >= 0 ? 0.11 : -0.11);
                    var actual = ToDouble(FixTrig.Atan2(FromDouble(y), FromDouble(x)));
                    AssertClose(Math.Atan2(y, x), actual, 2e-3, $"Atan2({y}, {x})");
                }
            }
        }

        [Fact]
        public void Atan2_HandlesSentinelMagnitudes()
        {
            // Sentinel-scale operands must not overflow the octant reduction (R1/R2).
            var big = Fix64.FromDecimalLiteral("9999999");
            var one = Fix64.One;
            AssertClose(Math.Atan2(1, 9999999), ToDouble(FixTrig.Atan2(one, big)), 2e-3, "Atan2(1, 9999999)");
            AssertClose(Math.Atan2(9999999, 1), ToDouble(FixTrig.Atan2(big, one)), 2e-3, "Atan2(9999999, 1)");
            AssertClose(Math.PI * 3 / 4, ToDouble(FixTrig.Atan2(big, -big)), 2e-3, "Atan2(big, -big)");
        }

        private static void AssertClose(double expected, double actual, double tolerance, string context)
        {
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{context}: expected {expected} got {actual} (|Δ| = {Math.Abs(expected - actual)}, tol {tolerance})");
        }
    }
}
