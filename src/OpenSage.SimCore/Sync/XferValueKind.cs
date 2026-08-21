// The primitive-type vocabulary of a deep-dump record (harness glue for build-order step 6).
//
// The harness's frozen deep-dump schema (bfme2-workbench/tools/harness/schema/
// deep-dump-v1.schema.json, harness-part1.md §2.2) types every field record with
// `t` ∈ bool | int | uint | fix64 | fixv3 | frame | framespan | objectId | enum |
// bitarray512 | bytes, because the comparator's tolerance arithmetic depends on the
// signedness and component count of the raw payload. IXfer's typed calls know this
// statically, so the write-visitor base threads it through to the one consumer that
// needs it (XferDeepDump -> DeepCrcWriter). It is never folded into any checksum,
// exactly like names and tolerances (crc-byteorder §2: type tags never reach the
// accumulator).

namespace OpenSage.SimCore.Sync;

public enum XferValueKind : byte
{
    Bool,
    Int,
    UInt,
    Fix64,
    FixVector3,
    Frame,
    FrameSpan,
    ObjectId,
    Enum,
    BitArray512,
    Bytes,
}

public static class XferValueKinds
{
    // Tokens match the harness schema's `t` vocabulary verbatim.
    private static readonly string[] Tokens =
    {
        "bool", "int", "uint", "fix64", "fixv3", "frame", "framespan",
        "objectId", "enum", "bitarray512", "bytes",
    };

    public static string TokenOf(XferValueKind kind) => Tokens[(byte)kind];
}
