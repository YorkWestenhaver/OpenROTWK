// The frozen channel walk (api-freeze-v1 F8; desync-crc-deep-dive §5.2). Declaration order IS
// walk order; the checkpoint message's channel vector is indexed by this ordinal. The names
// are authoritative from the original engine's own -x...CRC exclude-switch strings; each
// channel here is skippable the same way (debug tooling AND the F11 migration mechanism - an
// unmigrated float subsystem's channel stays excluded until it lands).

namespace OpenSage.SimCore.Sync
{
    public enum CrcChannel : byte
    {
        Objects = 0,      // every game object, ascending ObjectId; modules ascending ModuleIndex
        LogicRandom = 1,  // the 24-byte logic RNG state (original: byte-rolling seed fold)
        Partition = 2,
        TerrainLogic = 3,
        Shroud = 4,
        Collision = 5,
        Taint = 6,
        Players = 7,
        AI = 8,
        LivingWorld = 9,  // only when the Living World subsystem is active
    }

    public static class CrcChannels
    {
        public const int Count = 10;

        // Enum.GetValues/GetNames are on the banned surface (unstable ordering); the walk
        // order and names are spelled explicitly and pinned by tests.
        private static readonly string[] Names =
        {
            "Objects",
            "LogicRandom",
            "Partition",
            "TerrainLogic",
            "Shroud",
            "Collision",
            "Taint",
            "Players",
            "AI",
            "LivingWorld",
        };

        public static string NameOf(CrcChannel channel) => Names[(int)channel];
    }
}
