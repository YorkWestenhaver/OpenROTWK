// Save visitor: writes each primitive's canonical byte image to the stream, in walk order.
// The stream is a plain concatenation of images - the per-call boundary matters to the CRC,
// not to storage - and a full-state save is, by construction, the same walk the checkpoint
// folds (design-simcore-scaffolding §5.3: full-state Xfer = a save).

using System;
using System.IO;

namespace OpenSage.SimCore.Sync
{
    public sealed class XferSave : XferWriteVisitorBase, IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;

        public XferSave(Stream stream, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            _stream = stream;
            _leaveOpen = leaveOpen;
        }

        public override XferMode Mode => XferMode.Save;

        protected override void Consume(string name, Tolerance tol, XferValueKind kind, ReadOnlySpan<byte> bytes)
        {
            _stream.Write(bytes);
        }

        public void Dispose()
        {
            _stream.Flush();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
