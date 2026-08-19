// Compiles BFME2 ExperienceLevel INI chains (AotR experiencelevels.ini shape:
// per-template TargetNames + ascending RequiredExperience blocks carrying Rank,
// ExperienceAward, AttributeModifiers, Upgrades) into the immutable
// VeterancyLevelTable + per-level grant lists the veterancy core and its owning
// modules consume. Parse-time/config code - runs once per template, never per frame,
// and produces int/Fix64 data only.
//
// No GPL reference exists for the BFME2 chain semantics; the ordering rule implemented
// here (sort by RequiredExperience ascending, stable on equal thresholds, an implicit
// base level 0 prepended when the chain starts above zero experience) is the
// deterministic reading of the INI data and is flagged as a Ghidra-gap finding in
// research/systems/economy-production.md.

using System.Collections.Generic;
using OpenSage.SimCore;

namespace OpenSage.Logic.Economy;

/// <summary>One resolved veterancy level: the table row plus the owner-side grants.</summary>
public sealed class CompiledVeterancyLevel
{
    public int RequiredExperience { get; init; }
    public int ExperienceAward { get; init; }
    public int Rank { get; init; }
    /// <summary>AttributeModifiers names to apply on reaching this level (owner-side).</summary>
    public IReadOnlyList<string> AttributeModifierNames { get; init; } = [];
    /// <summary>Upgrades granted on reaching this level (owner-side).</summary>
    public IReadOnlyList<string> UpgradeNames { get; init; } = [];
}

/// <summary>The compiler output: the sim table + the per-level grant metadata.</summary>
public sealed class CompiledVeterancy
{
    public required VeterancyLevelTable Table { get; init; }
    public required IReadOnlyList<CompiledVeterancyLevel> Levels { get; init; }
}

[SimState]
public static class VeterancyTableCompiler
{
    /// <summary>
    /// Compile a template's level chain. Input rows are (requiredExperience, award,
    /// rank, modifiers, upgrades) tuples the caller extracted from its matching
    /// ExperienceLevel assets (matching = template name listed in TargetNames).
    /// </summary>
    public static CompiledVeterancy Compile(List<CompiledVeterancyLevel> levels)
    {
        // Deterministic stable insertion sort by RequiredExperience (no LINQ/comparer
        // indirection in sim-adjacent code).
        for (var i = 1; i < levels.Count; i++)
        {
            var item = levels[i];
            var j = i - 1;
            while (j >= 0 && levels[j].RequiredExperience > item.RequiredExperience)
            {
                levels[j + 1] = levels[j];
                j--;
            }
            levels[j + 1] = item;
        }

        // Prepend the implicit base level when the chain starts above zero experience
        // (BFME2 level-1 blocks use RequiredExperience = 1; a fresh unit has 0 XP).
        if (levels.Count == 0 || levels[0].RequiredExperience > 0)
        {
            levels.Insert(0, new CompiledVeterancyLevel
            {
                RequiredExperience = 0,
                ExperienceAward = 0,
                Rank = 0,
            });
        }

        var required = new int[levels.Count];
        var awards = new int[levels.Count];
        var ranks = new int[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            required[i] = levels[i].RequiredExperience;
            awards[i] = levels[i].ExperienceAward;
            ranks[i] = levels[i].Rank;
        }

        return new CompiledVeterancy
        {
            Table = new VeterancyLevelTable(required, awards, ranks),
            Levels = levels,
        };
    }
}
