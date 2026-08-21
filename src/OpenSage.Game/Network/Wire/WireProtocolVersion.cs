// The wire's own version identity (design-netcode.md R-N4; §3.2 BuildIdentity carries this
// value into the join-time content gate, which is out of N2's scope). Retail wire
// compatibility is a non-goal (R-N4) - this is OpenROTWK's own protocol, and reordering any
// field on the wire (this codec's byte layout, or any Xfer-visible field elsewhere) is a bump
// here, never a silent format drift.

namespace OpenSage.Network.Wire;

public static class WireProtocolVersion
{
    /// <summary>
    /// The protocol version this build's codec speaks. A peer whose <see cref="WireFrame"/>
    /// header carries a different value is refused at decode
    /// (<see cref="WireDecodeStatus.UnsupportedProtocolVersion"/>), never partially parsed.
    /// </summary>
    public const ushort Current = 1;
}
