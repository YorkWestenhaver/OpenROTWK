namespace OpenSage.Mods.Bfme;

/// <summary>
/// Pure, headlessly-testable policy decisions used by <c>AptControlBar</c>'s command-button
/// slot handling. Extracted so that the clear and update paths provably share one rule set:
/// before this existed the two paths disagreed about which games own the slots, and only the
/// clear path checked that the Apt CommandButtons object had actually been populated.
/// </summary>
public static class AptControlBarPolicy
{
    /// <summary>
    /// Number of Palantir command-button slots (1-based indices 1..SlotCount).
    /// </summary>
    public const int SlotCount = 6;

    /// <summary>
    /// Whether the Apt control bar drives the six command-button slots for this game.
    /// BFME1's Palantir lays these out differently and is not handled yet.
    /// </summary>
    public static bool SupportsCommandButtonSlots(SageGame game) => game != SageGame.Bfme;

    /// <summary>
    /// Whether the Apt "CommandButtons" object is populated enough to index its slots.
    /// </summary>
    public static bool CommandButtonsUsable(int constantsCount) => constantsCount > 0;

    /// <summary>
    /// Whether the update path (as opposed to the clear path) should run for a selection.
    /// </summary>
    public static bool ShouldUpdateButtons(int selectedUnitCount) => selectedUnitCount > 0;
}
