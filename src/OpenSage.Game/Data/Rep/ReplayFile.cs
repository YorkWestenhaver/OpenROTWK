using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenSage.IO;

namespace OpenSage.Data.Rep;

public sealed class ReplayFile
{
    public ReplayHeader Header { get; private set; }
    public IReadOnlyList<ReplayChunk> Chunks { get; private set; }

    /// <summary>
    /// Builds a replay from chunks alone, with no header. Test visibility only (internal, per
    /// <c>InternalsVisibleTo</c>): <see cref="OpenSage.Network.ReplayConnection"/> reads
    /// nothing but <see cref="Chunks"/>, and R15 packet BR-P4B's replay canary needs exact
    /// chosen timecodes that no recorded .rep can provide.
    /// </summary>
    internal static ReplayFile FromChunksForTests(IReadOnlyList<ReplayChunk> chunks)
    {
        return new ReplayFile { Chunks = chunks };
    }

    public static ReplayFile FromFileSystemEntry(FileSystemEntry entry, bool onlyHeader = false)
    {
        using (var stream = entry.Open())
        using (var reader = new BinaryReader(stream, Encoding.Unicode, true))
        {
            var result = new ReplayFile
            {
                Header = ReplayHeader.Parse(reader)
            };

            if (onlyHeader)
            {
                return result;
            }

            var chunks = new List<ReplayChunk>();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                chunks.Add(ReplayChunk.Parse(reader));
            }
            result.Chunks = chunks;

            if (result.Header.NumTimecodes != chunks[chunks.Count - 1].Header.Timecode)
            {
                throw new InvalidDataException();
            }

            return result;
        }
    }
}
