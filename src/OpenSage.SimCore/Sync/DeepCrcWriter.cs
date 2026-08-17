// The streaming deep-CRC dump (design-simcore-scaffolding §5.3; the 0 A.D. -ooslog pattern).
// One text record per xfer call: channel / objectId / moduleIndex / tag / class / field /
// tolerance / raw bytes. The dump doubles as the conformance harness's comparator input, and
// `ddiff` (bfme2-workbench/tools/ddiff.py) prints the first divergent record of two dumps.
//
// Format "opensage-deepdump v1", line-oriented, ASCII, invariant, deterministic:
//   # opensage-deepdump v1        header
//   F <frame>                     begin checkpoint frame
//   C <ordinal> <channelName>     begin channel
//   R <objectId> <moduleIndex> <tag> <class> <field> <tolLetter> <hexBytes>
//   E <ordinal> <crc8hex>         end channel, with the channel's folded CRC
// Identity strings have spaces replaced by '_' so every record stays one whitespace-split line.

using System;
using System.IO;
using System.Text;

namespace OpenSage.SimCore.Sync
{
    public sealed class DeepCrcWriter : IDisposable
    {
        public const string HeaderLine = "# opensage-deepdump v1";

        private readonly TextWriter _writer;
        private readonly bool _leaveOpen;
        private readonly StringBuilder _line = new();

        public DeepCrcWriter(TextWriter writer, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(writer);
            _writer = writer;
            _leaveOpen = leaveOpen;
            _writer.Write(HeaderLine);
            _writer.Write('\n');
        }

        public void BeginFrame(uint frame)
        {
            _writer.Write("F ");
            WriteUInt(frame);
            _writer.Write('\n');
        }

        public void BeginChannel(CrcChannel channel)
        {
            _writer.Write("C ");
            WriteUInt((byte)channel);
            _writer.Write(' ');
            _writer.Write(CrcChannels.NameOf(channel));
            _writer.Write('\n');
        }

        public void EndChannel(CrcChannel channel, uint channelCrc)
        {
            _writer.Write("E ");
            WriteUInt((byte)channel);
            _writer.Write(' ');
            WriteHex8(channelCrc);
            _writer.Write('\n');
        }

        public void Record(in XferModuleId module, string fieldName, Tolerance tol, ReadOnlySpan<byte> rawBytes)
        {
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
            for (var i = 0; i < rawBytes.Length; i++)
            {
                AppendHexByte(rawBytes[i]);
            }
            _line.Append('\n');
            _writer.Write(_line);
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
}
