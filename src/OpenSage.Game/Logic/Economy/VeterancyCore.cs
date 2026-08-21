// The deterministic experience/veterancy core - GPL ExperienceTracker rebuilt on int
// experience + Fix64 scalar (generals-gpl GeneralsMD GameLogic/Object/
// ExperienceTracker.cpp: ctor / getExperienceValue / isAcceptingExperiencePoints /
// setMinVeterancyLevel / setVeterancyLevel / gainExpForLevel / canGainExpForLevel /
// addExperiencePoints / setExperienceAndLevel / xfer; and the veterancy stat hooks of
// Object::onVeterancyLevelChanged + ActiveBody::onVeterancyLevelChanged (health-bonus
// ratio); semantics only, fresh code).
//
// DESIGN (the surface future ExperienceUpdate/VeterancyGainCreate/level modules call):
//   - The LEVEL TABLE is data: ZH keeps 4 fixed levels with per-template
//     ExperienceRequired/ExperienceValue vectors; BFME2 replaces that with per-template
//     chains of ExperienceLevel INI blocks (RequiredExperience thresholds, awards, Rank,
//     AttributeModifiers, Upgrades - AotR experiencelevels.ini). VeterancyLevelTable is
//     the resolved, immutable form both shapes compile into: ascending thresholds with
//     index 0 as the base level. Per-level modifier/upgrade grants are carried as config
//     for the owner to apply on a level-change edge (the AttributeModifier system itself
//     is a separate port, gated on OPEN-5).
//   - The CORE is the mutable state machine: {experience, levelIndex, scalar, sink}.
//     Level is DERIVED by the GPL scan-from-zero loop on every change - experience can
//     therefore step a unit down (GPL setExperienceAndLevel's own "paradox!" comment),
//     and we preserve that.
//   - The experience SINK (missiles pledging XP to their launcher) needs an object
//     lookup, which the core cannot do: the owner asks PrepareSinkForward() for the
//     scaled amount and forwards it to the sink object's core (falling through to a
//     local add when the sink object is gone, exactly the GPL branch).
//   - SCALING: GPL does `Int amount *= Real scalar` - a float multiply truncated toward
//     zero on the int store. We multiply in Fix64 and truncate toward zero
//     (ProductionMath.TruncateTowardZero), one deterministic rule for both the local
//     gain and the sink forward.
//   - Level-change side effects (weapon-set flags, weapon-bonus conditions, veterancy
//     upgrade grant, Body health-bonus rescale, FX/EVA) are the owner's: every mutator
//     returns a VeterancyChange edge the owner dispatches on. The Body multiplier is
//     computed here (HealthBonusMultiplier = bonus[new]/bonus[old], the GPL ratio) so
//     the division is in exactly one place.

#nullable enable

using System;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Economy;

/// <summary>A level transition edge (GPL onVeterancyLevelChanged trigger data).</summary>
[SimState]
public readonly struct VeterancyChange
{
    public readonly int OldLevel;
    public readonly int NewLevel;

    public VeterancyChange(int oldLevel, int newLevel)
    {
        OldLevel = oldLevel;
        NewLevel = newLevel;
    }

    public bool Changed => OldLevel != NewLevel;

    public static readonly VeterancyChange None = new(0, 0);
}

/// <summary>
/// The resolved, immutable per-template veterancy table. Index 0 is the base level
/// (threshold 0); thresholds are strictly ascending. Compiled from either the ZH
/// ExperienceRequired/ExperienceValue vectors or a sorted BFME2 ExperienceLevel chain.
/// Config data only - never Xfered (F9: config is not state).
/// </summary>
[SimState]
public sealed class VeterancyLevelTable
{
    private readonly int[] _requiredExperience;
    private readonly int[] _experienceAward;
    private readonly int[] _ranks;
    private readonly Fix64[] _healthBonuses;

    /// <param name="requiredExperience">
    /// Ascending thresholds, index 0 = base level. GPL data has [0] = 0; BFME2 chains
    /// start at their first block's threshold with an implicit base prepended by the
    /// builder.
    /// </param>
    /// <param name="experienceAward">
    /// XP granted to the killer per level of the victim (ZH ExperienceValue / BFME2
    /// ExperienceAward). Same length as thresholds.
    /// </param>
    /// <param name="ranks">
    /// Display/logic rank per level (BFME2 Rank field; null = 0..N identity).
    /// </param>
    /// <param name="healthBonuses">
    /// Per-level max-health multiplier (ZH GameData HealthBonus_*; identity when null -
    /// BFME2/AotR ships these commented out and moves the effect to AttributeModifiers).
    /// </param>
    public VeterancyLevelTable(int[] requiredExperience, int[] experienceAward, int[]? ranks = null, Fix64[]? healthBonuses = null)
    {
        ArgumentNullException.ThrowIfNull(requiredExperience);
        ArgumentNullException.ThrowIfNull(experienceAward);
        if (requiredExperience.Length == 0 || experienceAward.Length != requiredExperience.Length)
        {
            throw new ArgumentException("Veterancy table vectors must be non-empty and of equal length.");
        }
        for (var i = 1; i < requiredExperience.Length; i++)
        {
            if (requiredExperience[i] < requiredExperience[i - 1])
            {
                throw new ArgumentException("Veterancy thresholds must be ascending.");
            }
        }

        _requiredExperience = requiredExperience;
        _experienceAward = experienceAward;

        if (ranks == null)
        {
            ranks = new int[requiredExperience.Length];
            for (var i = 0; i < ranks.Length; i++)
            {
                ranks[i] = i;
            }
        }
        _ranks = ranks;

        if (healthBonuses == null)
        {
            healthBonuses = new Fix64[requiredExperience.Length];
            for (var i = 0; i < healthBonuses.Length; i++)
            {
                healthBonuses[i] = Fix64.One;
            }
        }
        _healthBonuses = healthBonuses;
    }

    /// <summary>Number of levels including the base level (ZH: 4).</summary>
    public int LevelCount => _requiredExperience.Length;

    public int LastLevel => _requiredExperience.Length - 1;

    /// <summary>GPL <c>ThingTemplate::getExperienceRequired(level)</c>.</summary>
    public int GetExperienceRequired(int level) => _requiredExperience[level];

    /// <summary>GPL <c>ThingTemplate::getExperienceValue(level)</c>.</summary>
    public int GetExperienceAward(int level) => _experienceAward[level];

    public int GetRank(int level) => _ranks[level];

    public Fix64 GetHealthBonus(int level) => _healthBonuses[level];

    /// <summary>
    /// The GPL ActiveBody rescale ratio on a level change:
    /// <c>mult = healthBonus[new] / healthBonus[old]</c> (Fix64 custom division), fed to
    /// the Body's SetMaxHealth(PreserveRatio).
    /// </summary>
    public Fix64 HealthBonusMultiplier(int oldLevel, int newLevel)
        => _healthBonuses[newLevel] / _healthBonuses[oldLevel];

    /// <summary>
    /// The GPL scan: highest level whose threshold the experience meets, scanning up
    /// from the base (addExperiencePoints / setExperienceAndLevel loop shape).
    /// </summary>
    public int ScanLevelForExperience(int experience)
    {
        var levelIndex = 0;
        while (levelIndex + 1 < _requiredExperience.Length
            && experience >= _requiredExperience[levelIndex + 1])
        {
            levelIndex++;
        }
        return levelIndex;
    }
}

[SimState]
public sealed class ExperienceCore
{
    private readonly VeterancyLevelTable _table;
    private readonly bool _isTrainable;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private int _currentExperience;
    private int _currentLevel;
    private Fix64 _experienceScalar;
    private ObjectId _experienceSink;

    public ExperienceCore(VeterancyLevelTable table, bool isTrainable)
    {
        _table = table;
        _isTrainable = isTrainable;
        _currentExperience = 0;
        _currentLevel = 0;
        _experienceScalar = Fix64.One;
        _experienceSink = ObjectId.Invalid;
    }

    public VeterancyLevelTable Table => _table;

    public int CurrentExperience => _currentExperience;

    public int CurrentLevel => _currentLevel;

    public int CurrentRank => _table.GetRank(_currentLevel);

    /// <summary>GPL <c>isTrainable</c> (template flag).</summary>
    public bool IsTrainable => _isTrainable;

    /// <summary>GPL <c>isAcceptingExperiencePoints</c>.</summary>
    public bool IsAcceptingExperiencePoints => _isTrainable || _experienceSink.IsValid;

    /// <summary>GPL <c>m_experienceScalar</c> (ExperienceScalarUpgrade and friends set it).</summary>
    public Fix64 ExperienceScalar
    {
        get => _experienceScalar;
        set => _experienceScalar = value;
    }

    /// <summary>GPL <c>setExperienceSink</c>.</summary>
    public ObjectId ExperienceSink
    {
        get => _experienceSink;
        set => _experienceSink = value;
    }

    /// <summary>
    /// GPL <c>getExperienceValue</c>: nothing for killing an ally, else the victim's
    /// per-level award.
    /// </summary>
    public int GetExperienceValue(bool killerIsAlly)
        => killerIsAlly ? 0 : _table.GetExperienceAward(_currentLevel);

    /// <summary>
    /// The sink-forward half of GPL <c>addExperiencePoints</c>: when a sink is set, the
    /// forwarded amount is <c>trunc(gain * scalar)</c> and the local add is skipped.
    /// The owner resolves the sink object; when it no longer exists the owner calls
    /// <see cref="AddExperiencePoints"/> locally instead (GPL falls through).
    /// </summary>
    public int PrepareSinkForward(int experienceGain)
        => ProductionMath.TruncateTowardZero(new Fix64(experienceGain) * _experienceScalar);

    /// <summary>
    /// GPL <c>addExperiencePoints</c> local half: scale (optionally), accumulate, rescan
    /// the level from zero, report the edge. Not-trainable is a no-op (safety branch).
    /// </summary>
    public VeterancyChange AddExperiencePoints(int experienceGain, bool canScaleForBonus)
    {
        if (!_isTrainable)
        {
            return VeterancyChange.None;
        }

        var amountToGain = experienceGain;
        if (canScaleForBonus)
        {
            amountToGain = ProductionMath.TruncateTowardZero(new Fix64(amountToGain) * _experienceScalar);
        }

        var oldLevel = _currentLevel;
        _currentExperience += amountToGain;
        _currentLevel = _table.ScanLevelForExperience(_currentExperience);
        return new VeterancyChange(oldLevel, _currentLevel);
    }

    /// <summary>
    /// GPL <c>setExperienceAndLevel</c>: overwrite experience, rescan (may go DOWN -
    /// the GPL "paradox!" branch is real behavior and preserved).
    /// </summary>
    public VeterancyChange SetExperienceAndLevel(int experienceIn)
    {
        if (!_isTrainable)
        {
            return VeterancyChange.None;
        }

        var oldLevel = _currentLevel;
        _currentExperience = experienceIn;
        _currentLevel = _table.ScanLevelForExperience(_currentExperience);
        return new VeterancyChange(oldLevel, _currentLevel);
    }

    /// <summary>
    /// GPL <c>setVeterancyLevel</c>: explicit set (no trainability check - "the setter
    /// is assumed to know what they are doing"); experience snaps to the level's
    /// threshold.
    /// </summary>
    public VeterancyChange SetVeterancyLevel(int newLevel)
    {
        if (newLevel < 0)
        {
            newLevel = 0;
        }
        if (newLevel > _table.LastLevel)
        {
            newLevel = _table.LastLevel;
        }

        if (_currentLevel == newLevel)
        {
            return VeterancyChange.None;
        }

        var oldLevel = _currentLevel;
        _currentLevel = newLevel;
        _currentExperience = _table.GetExperienceRequired(_currentLevel);
        return new VeterancyChange(oldLevel, newLevel);
    }

    /// <summary>GPL <c>setMinVeterancyLevel</c>: upward only.</summary>
    public VeterancyChange SetMinVeterancyLevel(int newLevel)
    {
        if (newLevel > _table.LastLevel)
        {
            newLevel = _table.LastLevel;
        }

        if (_currentLevel >= newLevel)
        {
            return VeterancyChange.None;
        }

        var oldLevel = _currentLevel;
        _currentLevel = newLevel;
        _currentExperience = _table.GetExperienceRequired(_currentLevel);
        return new VeterancyChange(oldLevel, newLevel);
    }

    /// <summary>
    /// GPL <c>gainExpForLevel</c>: grant exactly the experience needed to reach
    /// current + levelsToGain (clamped at the last level), THROUGH the normal add path
    /// (so a scalar under 1 can legally under-shoot, exactly the original).
    /// </summary>
    public VeterancyChange GainExpForLevel(int levelsToGain, bool canScaleForBonus)
    {
        var newLevel = _currentLevel + levelsToGain;
        if (newLevel > _table.LastLevel)
        {
            newLevel = _table.LastLevel;
        }

        if (newLevel > _currentLevel)
        {
            var experienceNeeded = _table.GetExperienceRequired(newLevel) - _currentExperience;
            return AddExperiencePoints(experienceNeeded, canScaleForBonus);
        }

        return VeterancyChange.None;
    }

    /// <summary>GPL <c>canGainExpForLevel</c>.</summary>
    public bool CanGainExpForLevel(int levelsToGain)
    {
        var newLevel = _currentLevel + levelsToGain;
        if (newLevel > _table.LastLevel)
        {
            newLevel = _table.LastLevel;
        }
        return newLevel > _currentLevel;
    }

    // ---- the single walk (save/load + CRC + deep-dump), F9 declaration order ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("CurrentExperience", ref _currentExperience, Tolerance.Quantum);
        xfer.XferInt("CurrentLevel", ref _currentLevel);
        xfer.XferFix64("ExperienceScalar", ref _experienceScalar, Tolerance.Quantum);
        xfer.XferObjectId("ExperienceSink", ref _experienceSink);
    }
}
