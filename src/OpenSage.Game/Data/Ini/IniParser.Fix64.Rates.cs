// Rate-quantizing INI parse functions for the S2 locomotor system (api-freeze-v1 seam S5
// pattern, extended for per-second -> per-logic-frame vocabulary the movement templates use).
//
// SAGE's INI text expresses movement in per-SECOND units (dist/sec, degrees/sec, dist/sec^2);
// the sim runs in per-FRAME units at the title's logic rate (BFME2: the frozen 5 Hz, F6).
// The original engine converts with float multiplies at parse time
// (INI::parseVelocityReal / parseAccelerationReal / parseAngularVelocityReal, GPL
// GeneralsMD Common/INI.cpp - semantics only). Here the conversion happens ONCE at parse,
// integer-only through Fix64.FromDecimalLiteral (F4), exact in Int128.
//
// Rounding rules pinned here (per formula, S5):
//   ParseFix64VelocityPerLogicFrame      v/fps          round-half-up at raw scale
//   ParseFix64AccelerationPerLogicFrame  a/fps^2        round-half-up at raw scale
//   ParseFix64AngularVelocityPerLogicFrame deg*Pi/(180*fps) round-half-up at raw scale
//   ParseFix64FrictionPerLogicFrame      f/fps          round-half-up at raw scale
//     (the original: fricPerSec * SECONDS_PER_LOGICFRAME_REAL, PhysicsUpdate.cpp
//      parseFrictionPerSec - same formula, float there, exact here)

using System;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Data.Ini;

partial class IniParser
{
    private Fix64 ScanFix64DividedBy(in IniToken token, long divisorRaw)
    {
        var raw = ScanFix64(token).RawValue;
        var numerator = (Int128)raw << 32;
        var denominator = (Int128)divisorRaw;
        var half = denominator / 2;
        var rounded = numerator >= 0
            ? (numerator + half) / denominator
            : -((-numerator + half) / denominator);
        if (rounded > long.MaxValue || rounded < long.MinValue)
        {
            throw new IniParseException($"Value out of range: '{token.Text}'", token.Position);
        }
        return Fix64.FromRaw((long)rounded);
    }

    /// <summary>Distance per second text -> Fix64 distance per logic frame (v / fps).</summary>
    public Fix64 ParseFix64VelocityPerLogicFrame()
    {
        var fps = SageGame.LogicFramesPerSecond();
        return ScanFix64DividedBy(GetNextToken(), (long)fps << 32);
    }

    /// <summary>Distance per second^2 text -> Fix64 distance per logic frame^2 (a / fps^2).</summary>
    public Fix64 ParseFix64AccelerationPerLogicFrame()
    {
        var fps = SageGame.LogicFramesPerSecond();
        return ScanFix64DividedBy(GetNextToken(), (long)(fps * fps) << 32);
    }

    /// <summary>
    /// Degrees per second text -> Fix64 radians per logic frame: deg * Pi / (180 * fps),
    /// exact in Int128, round-half-up at raw scale.
    /// </summary>
    public Fix64 ParseFix64AngularVelocityPerLogicFrame()
    {
        var token = GetNextToken();
        var degRaw = ScanFix64(token).RawValue;
        var fps = SageGame.LogicFramesPerSecond();
        var denominator = (Int128)(180 * fps) << 32;
        var numerator = (Int128)degRaw * Fix64.Pi.RawValue;
        var half = denominator / 2;
        var rounded = numerator >= 0
            ? (numerator + half) / denominator
            : -((-numerator + half) / denominator);
        if (rounded > long.MaxValue || rounded < long.MinValue)
        {
            throw new IniParseException($"Angular velocity out of range: '{token.Text}'", token.Position);
        }
        return Fix64.FromRaw((long)rounded);
    }

    /// <summary>
    /// Friction per second text -> Fix64 per-frame friction coefficient (f / fps), the
    /// original's parseFrictionPerSec quantized.
    /// </summary>
    public Fix64 ParseFix64FrictionPerLogicFrame()
    {
        var fps = SageGame.LogicFramesPerSecond();
        return ScanFix64DividedBy(GetNextToken(), (long)fps << 32);
    }
}
