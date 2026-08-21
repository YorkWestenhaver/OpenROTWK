// The streaming deep-CRC dump (design-simcore-scaffolding §5.3; the 0 A.D. -ooslog pattern).
// One text record per xfer call: channel / objectId / moduleIndex / tag / class / field /
// tolerance / raw bytes. The dump doubles as the conformance harness's comparator input, and
// `ddiff` (bfme2-workbench/tools/ddiff.py) prints the first divergent record of two dumps.
//
// Format "opensage-deepdump v2", line-oriented, ASCII, invariant, deterministic:
//   # opensage-deepdump v2        header
//   # <text>                      provenance comment (arch stamp, exclusion echo, ...)
//   F <frame>                     begin checkpoint frame
//   C <ordinal> <channelName>     begin channel
//   R <objectId> <moduleIndex> <tag> <class> <field> <tolLetter> <type> <hexBytes>
//   E <ordinal> <crc8hex>         end channel, with the channel's folded CRC
//   V <frame> <combined8hex> <crc8hex>...   checkpoint vector (one entry per CrcChannel ordinal)
// Identity strings have spaces replaced by '_' so every record stays one whitespace-split line.
// v2 (harness glue, build-order step 6) adds the <type> token on R records -- the harness
// deep-dump schema (bfme2-harness/ddump/v1) types every field record so the comparator's
// tolerance arithmetic knows signedness and component count -- and the V vector line, which
// carries the CrcCheckpointMessage's channel vector for the harness's crcVector record.
// N14a (driver CLI) adds two additive, backward-compatible pieces: Comment() emits a `#` line
// (an existing consumer that already tolerates a header comment tolerates any `#` line, and one
// that strips comments before comparing is unaffected either way), and a stream-only mode that
// omits every F/C/R/E line, leaving header + comments + V lines only -- a byte-subset of the
// full dump's own output, never a divergent encoding of it.

using System;
using System.IO;
using System.Text;

namespace OpenSage.SimCore.Sync;

public sealed class DeepCrcWriter : IDisposable
{
    public const string HeaderLine = "# opensage-deepdump v2";

    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private readonly bool _streamOnly;
    private readonly StringBuilder _line = new();

    /// <param name="streamOnly">When true, suppresses the F/C/R/E record lines - only the
    /// header, any <see cref="Comment"/> lines, and the checkpoint <see cref="CrcVector"/>
    /// lines are written. The channel walk still runs underneath (callers still get correct
    /// CRCs back), so this is a pure output-size trim: the resulting file is exactly the
    /// dr-0005 stream-equality artifact, a subset of the full dump's own V lines.</param>
    public DeepCrcWriter(TextWriter writer, bool leaveOpen = false, bool streamOnly = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _leaveOpen = leaveOpen;
        _streamOnly = streamOnly;
        _writer.Write(HeaderLine);
        _writer.Write('\n');
    }

    /// <summary>Writes one `# &lt;text&gt;` provenance line (arch stamp, exclusion echo, ...).
    /// Not gated by stream-only: comments are metadata, not per-field content, and a
    /// comment-stripping comparator still byte-compares the V lines cleanly either way.</summary>
    public void Comment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\n') || text.Contains('\r'))
        {
            throw new ArgumentException("Comment text must not contain embedded newlines.", nameof(text));
        }
        _writer.Write("# ");
        _writer.Write(text);
        _writer.Write('\n');
    }

    public void BeginFrame(uint frame)
    {
        if (_streamOnly)
        {
            return;
        }
        _writer.Write("F ");
        WriteUInt(frame);
        _writer.Write('\n');
    }

    public void BeginChannel(CrcChannel channel)
    {
        if (_streamOnly)
        {
            return;
        }
        _writer.Write("C ");
        WriteUInt((byte)channel);
        _writer.Write(' ');
        _writer.Write(CrcChannels.NameOf(channel));
        _writer.Write('\n');
    }

    public void EndChannel(CrcChannel channel, uint channelCrc)
    {
        if (_streamOnly)
        {
            return;
        }
        _writer.Write("E ");
        WriteUInt((byte)channel);
        _writer.Write(' ');
        WriteHex8(channelCrc);
        _writer.Write('\n');
    }

    public void Record(in XferModuleId module, string fieldName, Tolerance tol, XferValueKind kind, ReadOnlySpan<byte> rawBytes)
    {
        if (_streamOnly)
        {
            return;
        }
        _line.Clear();
        _line.Append("R ");
        AppendUInt(module.ObjectId);
        _line.Append(' ');
        AppendInt(module.ModuleIndex);
        _line.Append(' ');
        AppendToken(module.Tag);
        _line.Append(' ');
        AppendToken(module.ClassName);
        _line.Append(' ');
        AppendToken(fieldName);
        _line.Append(' ');
        _line.Append(TolLetter(tol));
        _line.Append(' ');
        _line.Append(XferValueKinds.TokenOf(kind));
        _line.Append(' ');
        for (var i = 0; i < rawBytes.Length; i++)
        {
            AppendHexByte(rawBytes[i]);
        }
        _line.Append('\n');
        _writer.Write(_line);
    }

    /// <summary>
    /// Writes the checkpoint vector line: the frame, the combined CRC, and one entry per
    /// CrcChannel ordinal (excluded/inactive/unregistered channels hold 0, matching the
    /// checkpoint message's positional-zero ruling). The harness decodes this into its
    /// crcVector record.
    /// </summary>
    public void CrcVector(uint frame, uint combined, System.Collections.Generic.IReadOnlyList<uint> channelCrcs)
    {
        ArgumentNullException.ThrowIfNull(channelCrcs);
        _writer.Write("V ");
        WriteUInt(frame);
        _writer.Write(' ');
        WriteHex8(combined);
        for (var i = 0; i < channelCrcs.Count; i++)
        {
            _writer.Write(' ');
            WriteHex8(channelCrcs[i]);
        }
        _writer.Write('\n');
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        _writer.Flush();
        if (!_leaveOpen)
        {
            _writer.Dispose();
        }
    }

    private static char TolLetter(Tolerance tol) => tol switch
    {
        Tolerance.Exact => 'E',
        Tolerance.Quantum => 'Q',
        Tolerance.Band => 'B',
        Tolerance.Outcome => 'O',
        Tolerance.DrawCount => 'D',
        _ => '?',
    };

    // Integer-to-text without culture involvement: digits are computed, not formatted.

    private void WriteUInt(uint value)
    {
        _line.Clear();
        AppendUInt(value);
        _writer.Write(_line);
    }

    private void WriteHex8(uint value)
    {
        _line.Clear();
        AppendHexByte((byte)(value >> 24));
        AppendHexByte((byte)(value >> 16));
        AppendHexByte((byte)(value >> 8));
        AppendHexByte((byte)value);
        _writer.Write(_line);
    }

    private void AppendUInt(uint value)
    {
        Span<char> digits = stackalloc char[10];
        var n = 0;
        do
        {
            digits[n++] = (char)('0' + value % 10);
            value /= 10;
        } while (value != 0);
        for (var i = n - 1; i >= 0; i--)
        {
            _line.Append(digits[i]);
        }
    }

    private void AppendInt(int value)
    {
        if (value < 0)
        {
            _line.Append('-');
            AppendUInt((uint)-(long)value);
        }
        else
        {
            AppendUInt((uint)value);
        }
    }

    private void AppendHexByte(byte b)
    {
        const string hex = "0123456789abcdef";
        _line.Append(hex[b >> 4]);
        _line.Append(hex[b & 0xF]);
    }

    private void AppendToken(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            _line.Append('~');
            return;
        }
        foreach (var c in s)
        {
            _line.Append(char.IsWhiteSpace(c) ? '_' : c);
        }
    }
}
