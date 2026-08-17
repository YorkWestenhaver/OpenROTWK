// The quantizing INI parse functions (api-freeze-v1 seam S5, design-module-api §2.2).
//
// Sim-relevant numerics leave the INI text layer as Fix64 / LogicFrameSpan, quantized ONCE
// at parse time through the single blessed text boundary Fix64.FromDecimalLiteral (F4):
// decimal literal -> scaled Q31.32 integer, never through platform float/double, so the
// same INI bytes produce the same raw bits on every machine.
//
// Rounding rules pinned here (per formula, S5):
//   ParseFix64            round-half-up on the magnitude (FromDecimalLiteral's contract)
//   ParseFix64Percentage  text / 100 exactly (decimal exponent shift, no division)
//   ParseDurationLogicFrames  ceil(ms * fps / 1000)  - S5 default ceil; fps is the title's
//                             logic rate (5 for BFME2, F6). Negative durations are malformed.
//   ParseAngleDegrees     deg * Pi / 180, round-half-up on the raw scale
//   ParseFixVector3       X:/Y:/Z: attribute triple, each component ParseFix64

using System;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Data.Ini;

partial class IniParser
{
    public Fix64 ScanFix64(in IniToken token)
    {
        var text = GetFloatText(token);
        if (string.IsNullOrEmpty(text))
        {
            throw new IniParseException($"Invalid Fix64 value: '{token.Text}'", token.Position);
        }
        try
        {
            return Fix64.FromDecimalLiteral(text);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new IniParseException($"Invalid Fix64 value: '{token.Text}' ({e.Message})", token.Position);
        }
    }

    /// <summary>Plain real (e.g. <c>Radius = 140.5</c>) as exact-decimal Q31.32.</summary>
    public Fix64 ParseFix64() => ScanFix64(GetNextToken());

    /// <summary>
    /// Percentage (e.g. <c>Boost = 25%</c>) as an exact fraction (0.25). The division by 100
    /// is a decimal exponent shift inside the literal parse, so it is exact for any input
    /// that is exact in decimal - which every INI percentage is.
    /// </summary>
    public Fix64 ScanFix64Percentage(in IniToken token)
    {
        var text = GetFloatText(token);
        if (string.IsNullOrEmpty(text))
        {
            throw new IniParseException($"Invalid percentage value: '{token.Text}'", token.Position);
        }
        try
        {
            return Fix64.FromDecimalLiteral(text + "E-2");
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new IniParseException($"Invalid percentage value: '{token.Text}' ({e.Message})", token.Position);
        }
    }

    public Fix64 ParseFix64Percentage() => ScanFix64Percentage(GetNextToken());

    /// <summary>
    /// Milliseconds text -> whole logic frames, ceil(ms * fps / 1000) computed on the
    /// quantized Q31.32 millisecond value with integer arithmetic only. fps is the title's
    /// logic rate (BFME2: the frozen 5 Hz, F6). A negative duration is malformed input.
    /// </summary>
    public LogicFrameSpan ScanDurationLogicFrames(in IniToken token)
    {
        var msRaw = ScanFix64(token).RawValue;
        if (msRaw < 0)
        {
            throw new IniParseException($"Negative duration: '{token.Text}'", token.Position);
        }

        // frames = ceil(msRaw * fps / (1000 << 32)), exact in Int128.
        var fps = SageGame.LogicFramesPerSecond();
        var denominator = (Int128)1000 << 32;
        var numerator = (Int128)msRaw * fps;
        var frames = (numerator + denominator - 1) / denominator;
        if (frames > uint.MaxValue)
        {
            throw new IniParseException($"Duration out of range: '{token.Text}'", token.Position);
        }
        return new LogicFrameSpan((uint)frames);
    }

    public LogicFrameSpan ParseDurationLogicFrames() => ScanDurationLogicFrames(GetNextToken());

    /// <summary>
    /// Degrees text -> Fix64 radians (S2: there is no FixedAngle type; angles are plain
    /// Fix64 radians and the degree conversion happens here). rad = deg * Pi / 180 with
    /// round-half-up at the raw scale, exact in Int128.
    /// </summary>
    public Fix64 ScanAngleDegrees(in IniToken token)
    {
        var degRaw = ScanFix64(token).RawValue;
        var denominator = (Int128)180 << 32;
        var numerator = (Int128)degRaw * Fix64.Pi.RawValue;
        // Round half up on the magnitude, matching FromDecimalLiteral.
        var half = denominator / 2;
        var rounded = numerator >= 0
            ? (numerator + half) / denominator
            : -((-numerator + half) / denominator);
        if (rounded > long.MaxValue || rounded < long.MinValue)
        {
            throw new IniParseException($"Angle out of range: '{token.Text}'", token.Position);
        }
        return Fix64.FromRaw((long)rounded);
    }

    public Fix64 ParseAngleDegrees() => ScanAngleDegrees(GetNextToken());

    /// <summary>Coordinate triple <c>X:.. Y:.. Z:..</c> with each component quantized.</summary>
    public FixVector3 ParseFixVector3()
    {
        return new FixVector3(
            ParseAttribute("X", ScanFix64),
            ParseAttribute("Y", ScanFix64),
            ParseAttribute("Z", ScanFix64));
    }
}
