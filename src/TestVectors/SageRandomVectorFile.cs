using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OpenSage.TestVectors;

/// <summary>
/// Reader for <c>SageRandomVectors.txt</c>, the shared SAGE-RNG vector file (api-freeze-v1 F5/S3).
/// This source file and the vector file are linked into <c>OpenSage.Mathematics.Tests</c> and
/// <c>OpenSage.SimCore.Tests</c> alike, so both the client-stream port (<c>SageRandom</c>) and the
/// logic-stream one (<c>LogicRandom</c>) are checked against the same bytes. If the two
/// implementations ever drift apart, one of the two projects goes red.
/// </summary>
public static class SageRandomVectorFile
{
    public sealed record RawVector(uint Seed, int Index, uint Value);

    public sealed record NextVector(uint Seed, int Lo, int Hi, int[] Values);

    public sealed record FixVector(uint Seed, long LoRaw, long HiRaw, long[] Values);

    public sealed record VectorSet(
        IReadOnlyList<RawVector> Raw,
        IReadOnlyList<NextVector> Next,
        IReadOnlyList<FixVector> Fix);

    public const string FileName = "SageRandomVectors.txt";

    private static readonly char[] Whitespace = { ' ', '\t', '\r' };

    public static VectorSet Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"shared RNG vector file '{FileName}' was not copied to the test output directory",
                path);
        }

        var raw = new List<RawVector>();
        var next = new List<NextVector>();
        var fix = new List<FixVector>();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line.Substring(0, comment);
            }

            var fields = line.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            switch (fields[0])
            {
                case "RAW":
                    raw.Add(new RawVector(
                        ParseHex32(fields[1]),
                        int.Parse(fields[2], CultureInfo.InvariantCulture),
                        ParseHex32(fields[3])));
                    break;

                case "NEXT":
                    next.Add(new NextVector(
                        ParseHex32(fields[1]),
                        int.Parse(fields[2], CultureInfo.InvariantCulture),
                        int.Parse(fields[3], CultureInfo.InvariantCulture),
                        ParseInts(fields, 4)));
                    break;

                case "FIX":
                    fix.Add(new FixVector(
                        ParseHex32(fields[1]),
                        ParseHex64(fields[2]),
                        ParseHex64(fields[3]),
                        ParseHex64s(fields, 4)));
                    break;

                default:
                    throw new InvalidDataException($"unknown record type '{fields[0]}' in {FileName}");
            }
        }

        if (raw.Count == 0 || next.Count == 0 || fix.Count == 0)
        {
            throw new InvalidDataException($"{FileName} is missing one of the RAW/NEXT/FIX sections");
        }

        return new VectorSet(raw, next, fix);
    }

    private static uint ParseHex32(string text) =>
        uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static long ParseHex64(string text) =>
        unchecked((long)ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static int[] ParseInts(string[] fields, int start)
    {
        var values = new int[fields.Length - start];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = int.Parse(fields[start + i], CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static long[] ParseHex64s(string[] fields, int start)
    {
        var values = new long[fields.Length - start];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ParseHex64(fields[start + i]);
        }
        return values;
    }
}
