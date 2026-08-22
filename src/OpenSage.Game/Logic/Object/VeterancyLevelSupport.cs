#nullable enable

using OpenSage.Diagnostics;

namespace OpenSage.Logic.Object;

/// <summary>
/// The engine models exactly four veterancy levels (<see cref="VeterancyLevel.Regular"/> …
/// <see cref="VeterancyLevel.Heroic"/>), and every per-level lookup is sized from that enum:
/// <c>GameData.HealthBonus</c>, <c>ObjectDefinition.ExperienceRequired</c> /
/// <c>ExperienceValue</c> (<c>VeterancyValues</c>), and the promotion-sound and armor-set
/// switches in <see cref="ActiveBody"/>.
///
/// BFME2/AotR content is not limited to four: an <c>ExperienceLevelCreate</c> block may name a
/// <c>LevelToGrant</c> above 3, and an <c>ExperienceLevel</c> asset may declare a <c>Rank</c>
/// above 3, both of which reach the engine as an out-of-enum <see cref="VeterancyLevel"/> cast.
/// Observed in the R15 20-map AotR sweep: <c>CampaignCelebrimbor</c> on "map good redhorn" and
/// "map sp good blue mountains" granted such a level during map load, and the resulting
/// <c>ArgumentOutOfRangeException</c> out of <c>ActiveBody.OnVeterancyLevelChanged</c>
/// terminated the process before the sim loop ever started.
///
/// Policy: clamp the requested level into the supported range and report the content gap once
/// per (object template, requested level). The object then behaves as a Heroic unit — the
/// closest thing the engine's four-level model can represent — instead of killing the match.
/// Widening <see cref="VeterancyLevel"/> itself is a separate, much larger piece of work
/// (it resizes every table above and needs the extra armor-set conditions and promotion
/// sounds to exist); this class is the degradation, not that port.
/// </summary>
internal static class VeterancyLevelSupport
{
    private const string Category = "VeterancyLevel.Unsupported";

    /// <summary>
    /// True when <paramref name="level"/> is one of the four levels the engine's per-level
    /// tables are sized for.
    /// </summary>
    internal static bool IsSupported(VeterancyLevel level)
    {
        return level >= VeterancyLevel.First && level <= VeterancyLevel.Last;
    }

    /// <summary>
    /// Clamps <paramref name="level"/> into <see cref="VeterancyLevel.First"/> …
    /// <see cref="VeterancyLevel.Last"/>. Supported levels are returned unchanged.
    /// </summary>
    internal static VeterancyLevel Clamp(VeterancyLevel level)
    {
        if (level < VeterancyLevel.First)
        {
            return VeterancyLevel.First;
        }

        return level > VeterancyLevel.Last ? VeterancyLevel.Last : level;
    }

    /// <summary>
    /// Returns true exactly once per (object template, requested level) pair.
    /// </summary>
    internal static bool ShouldReport(string? objectTemplateName, VeterancyLevel requested)
    {
        return DegradeLog.ShouldReport(Category, $"{DegradeLog.Normalize(objectTemplateName)}#{(int)requested}");
    }

    /// <summary>
    /// The single log line emitted when content asks for a level the engine cannot model.
    /// </summary>
    internal static string FormatMessage(string? objectTemplateName, VeterancyLevel requested)
    {
        return $"Object template '{DegradeLog.Normalize(objectTemplateName)}' requested veterancy " +
               $"level {(int)requested}, but this engine models only {(int)VeterancyLevel.First}.." +
               $"{(int)VeterancyLevel.Last} (Regular..Heroic); clamping to " +
               $"{VeterancyLevel.Last}. Per-level health/experience/armor tables are sized from " +
               "the VeterancyLevel enum, so the extra ranks this content declares are not modelled.";
    }
}
