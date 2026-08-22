using System.IO;
using OpenSage.Content.Translation;
using OpenSage.FileFormats;
using OpenSage.Mathematics;

namespace OpenSage.Data.Apt.Characters;

public sealed class Text : Character
{
    public RectangleF Bounds { get; private set; }
    public uint Font { get; private set; }
    public uint Alignment { get; private set; }
    public ColorRgba Color { get; private set; }
    public float FontHeight { get; private set; }
    public bool ReadOnly { get; private set; }
    public bool Multiline { get; private set; }
    public bool WordWrap { get; private set; }
    public string Content { get; private set; }
    public string Value { get; private set; }

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Reads one of Text's three 32-bit flags.
    /// </summary>
    /// <remarks>
    /// These are 32-bit C booleans, so the only meaningful test is zero versus non-zero.
    /// <see cref="BinaryReaderExtensions.ReadBooleanUInt32Checked"/> additionally insists the
    /// low byte be exactly 0 or 1 and the upper three bytes be zero, and throws
    /// <see cref="InvalidDataException"/> otherwise. That extra strictness is an OpenSAGE
    /// sanity assertion, not a property of the format: Age of the Ring's Palantir.apt import
    /// chain carries Text characters that trip it, which aborted the whole game the moment the
    /// ROTWK control bar started loading Palantir.apt. Widened to zero/non-zero, with the raw
    /// value logged once per offending field so unusual data is still visible.
    /// </remarks>
    private static bool ReadFlag(BinaryReader reader, string fieldName)
    {
        var raw = reader.ReadUInt32();
        if (raw > 1)
        {
            Logger.Info($"Apt Text.{fieldName} has non-boolean value 0x{raw:X8}; treating as true");
        }

        return raw != 0;
    }

    public static Text Parse(BinaryReader reader)
    {
        var text = new Text
        {
            Bounds = reader.ReadRectangleF(),
            Font = reader.ReadUInt32(),
            Alignment = reader.ReadUInt32(),
            Color = reader.ReadColorRgba(),
            FontHeight = reader.ReadSingle(),
            ReadOnly = ReadFlag(reader, nameof(ReadOnly)),
            Multiline = ReadFlag(reader, nameof(Multiline)),
            WordWrap = ReadFlag(reader, nameof(WordWrap)),
            Content = reader.ReadStringAtOffset(),
            Value = reader.ReadStringAtOffset()
        };
        return text;
    }
}
