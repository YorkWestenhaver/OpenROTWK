using System;
using System.Numerics;
using OpenSage.Content;
using OpenSage.Logic.Object;
using OpenSage.Mathematics;

namespace OpenSage.Data.Ini;

partial class IniParser
{
    public T ParseAttributeList<T>(
       IIniFieldParserProvider<T> fieldParserProvider)
       where T : class, new()
    {
        var result = new T();

        var done = false;

        while (!done)
        {
            if (_tokenReader.EndOfFile)
            {
                throw new InvalidOperationException();
            }

            var nameToken = GetNextTokenOptional(SeparatorsColon);
            if (!nameToken.HasValue)
            {
                break;
            }

            var fieldName = nameToken.Value.Text;
            if (fieldParserProvider.TryGetFieldParser(fieldName, out var fieldParser))
            {
                _currentBlockOrFieldStack.Push(fieldName);

                fieldParser(this, result);

                _currentBlockOrFieldStack.Pop();
            }
            else
            {
                throw new IniParseException($"Unexpected field '{fieldName}' in block '{_currentBlockOrFieldStack.Peek()}'.", nameToken.Value.Position);
            }
        }

        return result;
    }

    public T ParseAttribute<T>(string label, Func<IniParser, T> parse)
    {
        var nameToken = GetNextToken(SeparatorsColon);
        if (!string.Equals(nameToken.Text, label, StringComparison.OrdinalIgnoreCase))
        {
            throw new IniParseException($"Expected attribute name '{label}'", nameToken.Position);
        }

        return parse(this);
    }

    public delegate T ParseValueDelegate<T>(in IniToken token);

    public T ParseAttribute<T>(string label, ParseValueDelegate<T> parseValue)
    {
        var nameToken = GetNextToken(SeparatorsColon);
        if (!string.Equals(nameToken.Text, label, StringComparison.OrdinalIgnoreCase))
        {
            throw new IniParseException($"Expected attribute name '{label}'", nameToken.Position);
        }

        return parseValue(GetNextToken(SeparatorsColon));
    }

    public T ParseAttribute<T>(string label, Func<T> parseValue)
    {
        var nameToken = GetNextToken(SeparatorsColon);
        if (!string.Equals(nameToken.Text, label, StringComparison.OrdinalIgnoreCase))
        {
            throw new IniParseException($"Expected attribute name '{label}'", nameToken.Position);
        }

        return parseValue();
    }

    public bool ParseAttributeOptional<T>(string label, Func<T> parseValue, out T parsed)
    {
        var nameToken = GetNextTokenOptional(SeparatorsColon);

        if (!nameToken.HasValue)
        {
            parsed = default;
            return false;
        }

        if (!string.Equals(nameToken.Value.Text, label, StringComparison.OrdinalIgnoreCase))
        {
            throw new IniParseException($"Expected attribute name '{label}'", nameToken.Value.Position);
        }

        parsed = parseValue();
        return true;
    }

    public Percentage ParseAttributePercentage(string label)
    {
        return ParseAttribute(label, ParsePercentage);
    }

    public int ParseAttributeInteger(string label)
    {
        return ParseAttribute(label, ScanInteger);
    }

    /// <summary>
    /// Whole-percent attribute (e.g. <c>Health:100%</c>) as an INTEGER percent - 100 for
    /// "100%", not the 1.0 fraction <see cref="ParseAttributePercentage"/> produces.
    /// </summary>
    /// <remarks>
    /// Grown for the RespawnUpdate port (R14). <c>RespawnRules Health:</c> feeds
    /// <c>BodyModule.SetInitialHealth(int percent)</c>, whose percent application is
    /// <c>BodyDamageCore.SetInitialHealthPercent</c>'s exact Int128 mul-div, so the value must
    /// reach the sim as an integer and never through the float-backed
    /// <see cref="Mathematics.Percentage"/>. The text is quantized through the S5 blessed
    /// boundary (<see cref="ScanFix64"/>, exact decimal -> Q31.32) and then truncated toward
    /// zero, so any fractional percent is deterministic rather than platform-dependent. The
    /// shipped corpus has no fractional case: all 547 AotR <c>RespawnRules</c> declarations
    /// read <c>Health:100%</c>.
    /// </remarks>
    public int ParseAttributeIntegerPercentage(string label)
    {
        return ParseAttribute(label, ScanIntegerPercentage);
    }

    public int ScanIntegerPercentage(in IniToken token)
    {
        // GetFloatText (inside ScanFix64) stops at the '%', so "100%" quantizes to Q31.32
        // 100.0. Integer division by the scale truncates toward zero for either sign.
        return (int)(ScanFix64(token).RawValue / (1L << 32));
    }

    public LogicFrameSpan ParseAttributeTimeMillisecondsToLogicFrames(string label)
    {
        return ParseAttribute(label, ScanTimeMillisecondsToLogicFrames);
    }

    /// <summary>
    /// Millisecond attribute (e.g. <c>Time:60000</c>) through the S5 integer-only duration
    /// boundary - <c>ceil(ms * fps / 1000)</c> computed on the quantized Q31.32 value, never
    /// through a float like <see cref="ParseAttributeTimeMillisecondsToLogicFrames"/> does.
    /// Grown for the RespawnUpdate port (R14): the revive countdown is <c>[SimState]</c> timer
    /// state and must quantize identically on every architecture.
    /// </summary>
    public LogicFrameSpan ParseAttributeDurationLogicFrames(string label)
    {
        return ParseAttribute(label, ScanDurationLogicFrames);
    }

    public string ParseAttributeIdentifier(string label)
    {
        string GetText(in IniToken token) => token.Text;

        return ParseAttribute(label, GetText);
    }

    public LazyAssetReference<ObjectDefinition> ParseAttributeObjectReference(string label)
    {
        LazyAssetReference<ObjectDefinition> GetText(in IniToken token) => ParseObjectReference(token.Text);

        return ParseAttribute(label, GetText);
    }

    public T ScanAttributeEnum<T>(string label, in IniToken token)
        where T : Enum
    {
        return ParseAttribute<T>(label, ScanEnum<T>);
    }

    public T ParseAttributeEnum<T>(string label)
        where T : Enum
    {
        T GetValue(in IniToken token) => ScanEnum<T>(token);

        return ParseAttribute(label, GetValue);
    }

    public BitArray<T> ParseAttributeEnumBitArray<T>(string label)
        where T : Enum
    {
        return ParseAttribute(label, ParseEnumBitArray<T>);
    }

    public byte ParseAttributeByte(string label)
    {
        return ParseAttribute(label, ScanByte);
    }

    public bool ParseAttributeBoolean(string label)
    {
        return ParseAttribute(label, ScanBoolean);
    }

    public float ParseAttributeFloat(string label)
    {
        return ParseAttribute(label, ScanFloat);
    }

    public Point2D ParseAttributePoint2D(string label)
    {
        return ParseAttribute(label, ParsePoint);
    }

    public Vector2 ParseAttributeVector2(string label)
    {
        return ParseAttribute(label, ParseVector2);
    }

    public Vector3 ParseAttributeVector3(string label)
    {
        return ParseAttribute(label, ParseVector3);
    }
}
