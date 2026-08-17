// The byte-level sink seam (design-simcore-scaffolding §5.2): SimCore defines the primitive
// sink; the typed IXfer above it turns primitives into canonical byte images and hands each
// call's buffer to a sink exactly once - which is what makes the F7 per-call word/remainder
// split hold by construction for every consumer.

namespace OpenSage.SimCore.Sync
{
    public interface IXferSink
    {
        /// <summary>Receives one xfer call's canonical byte image. Never a concatenation.</summary>
        void Bytes(System.ReadOnlySpan<byte> bytes);
    }
}
