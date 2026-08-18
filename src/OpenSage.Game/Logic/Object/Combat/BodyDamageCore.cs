// The Fix64 health ledger - GPL ActiveBody's health/damage-state arithmetic, extracted
// as a pure deterministic core (generals-gpl GeneralsMD GameLogic/Object/Body/
// ActiveBody.cpp: internalChangeHealth / attemptDamage (arithmetic half) /
// attemptHealing (arithmetic half) / setMaxHealth / setInitialHealth /
// internalAddSubdualDamage / calcDamageState usage. Semantics only; fresh code).
//
// DESIGN: ActiveBody (still a float-substrate file: audio, FX, AI side effects) OWNS one
// of these and delegates every health mutation to it; this file holds the canonical
// Fix64 state and stays inside the analyzer wall. The split is the D-7 boundary pattern:
// the crossing lives in ActiveBody, never here. When the Body category ports for real,
// the module keeps this core and the float views die.
//
// All clamping, damage-state thresholds and ratio math are Fix64/Int128:
//   - damage state: GPL computes ratio = health/max and compares against the GameData
//     thresholds; we compare health > max * threshold (same predicate, no division).
//   - setMaxHealth(PRESERVE_RATIO): GPL computes ratio then re-multiplies in float; we
//     compute newHealth = current * newMax / prevMax exactly in Int128 (truncating).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>Quantized GameData damage-state thresholds (UnitDamagedThreshold etc.).</summary>
[SimState]
public readonly struct DamageStateThresholds
{
    public readonly Fix64 Damaged;
    public readonly Fix64 ReallyDamaged;

    public DamageStateThresholds(Fix64 damaged, Fix64 reallyDamaged)
    {
        Damaged = damaged;
        ReallyDamaged = reallyDamaged;
    }
}

[SimState]
public sealed class BodyDamageCore
{
    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private Fix64 _currentHealth;
    private Fix64 _previousHealth;
    private Fix64 _maxHealth;
    private Fix64 _initialHealth;
    private Fix64 _currentSubdualDamage;
    private BodyDamageType _currentDamageState = BodyDamageType.Pristine;

    public Fix64 CurrentHealth => _currentHealth;
    public Fix64 PreviousHealth => _previousHealth;
    public Fix64 MaxHealth => _maxHealth;
    public Fix64 InitialHealth => _initialHealth;
    public Fix64 CurrentSubdualDamage => _currentSubdualDamage;
    public BodyDamageType DamageState => _currentDamageState;

    public bool HealthBelowMax => _currentHealth < _maxHealth;

    /// <summary>GPL ActiveBody ctor: health = prev = initial; state recomputed.</summary>
    public void Initialize(Fix64 maxHealth, Fix64 initialHealth, in DamageStateThresholds thresholds)
    {
        _maxHealth = maxHealth;
        _initialHealth = initialHealth;
        _currentHealth = initialHealth;
        _previousHealth = initialHealth;
        _currentSubdualDamage = Fix64.Zero;
        _currentDamageState = CalculateDamageState(_currentHealth, _maxHealth, thresholds);
    }

    /// <summary>
    /// GPL <c>internalChangeHealth</c>: prev = current; current += delta clamped to
    /// [0, max]; damage state recomputed. Returns true when the damage state changed
    /// (the caller re-evaluates visuals / fires state-change callbacks).
    /// </summary>
    public bool ChangeHealth(Fix64 delta, in DamageStateThresholds thresholds)
    {
        _previousHealth = _currentHealth;
        _currentHealth = FixMath.Clamp(_currentHealth + delta, Fix64.Zero, _maxHealth);

        var oldState = _currentDamageState;
        _currentDamageState = CalculateDamageState(_currentHealth, _maxHealth, thresholds);
        return _currentDamageState != oldState;
    }

    /// <summary>
    /// The arithmetic half of GPL <c>attemptDamage</c>, applied AFTER the caller has run
    /// armor adjustment and the damage scalar. <paramref name="applyToHealth"/> is GPL's
    /// !alreadyHandled (special damage types consumed the amount elsewhere).
    /// Kill overrides the amount with all remaining health.
    /// </summary>
    public CombatDamageOutput ApplyDamage(
        Fix64 adjustedAmount,
        bool kill,
        bool applyToHealth,
        in DamageStateThresholds thresholds,
        out bool stateChanged)
    {
        var output = new CombatDamageOutput();
        stateChanged = false;

        if (adjustedAmount <= Fix64.Zero && !kill)
        {
            return output;
        }

        var amount = kill ? _currentHealth : adjustedAmount;

        if (applyToHealth)
        {
            stateChanged = ChangeHealth(-amount, thresholds);
        }
        // else: GPL alreadyHandled - prev/current are deliberately NOT touched, so
        // clipped reads whatever the last health change left (faithful to the original,
        // stale values and all).

        output.ActualDamageDealt = amount;
        output.ActualDamageClipped = _previousHealth - _currentHealth;
        return output;
    }

    /// <summary>
    /// The arithmetic half of GPL <c>attemptHealing</c> (armor-adjusted amount ADDED,
    /// clamped at max health).
    /// </summary>
    public CombatDamageOutput ApplyHealing(
        Fix64 adjustedAmount,
        in DamageStateThresholds thresholds,
        out bool stateChanged)
    {
        var output = new CombatDamageOutput();
        stateChanged = false;

        if (adjustedAmount <= Fix64.Zero)
        {
            return output;
        }

        stateChanged = ChangeHealth(adjustedAmount, thresholds);

        output.ActualDamageDealt = adjustedAmount;
        output.ActualDamageClipped = _previousHealth - _currentHealth;
        return output;
    }

    /// <summary>
    /// GPL <c>setMaxHealth</c>. PRESERVE_RATIO uses exact Int128 mul-div
    /// (current * newMax / prevMax) instead of the original's float ratio.
    /// </summary>
    public void SetMaxHealth(Fix64 newMaxHealth, MaxHealthChangeType changeType, in DamageStateThresholds thresholds)
    {
        var prevMaxHealth = _maxHealth;
        _maxHealth = newMaxHealth;
        _initialHealth = newMaxHealth;

        switch (changeType)
        {
            case MaxHealthChangeType.PreserveRatio:
                {
                    var newHealth = MulDiv(_currentHealth, newMaxHealth, prevMaxHealth);
                    ChangeHealth(newHealth - _currentHealth, thresholds);
                    break;
                }

            case MaxHealthChangeType.AddCurrentHealthToo:
                ChangeHealth(newMaxHealth - prevMaxHealth, thresholds);
                break;

            case MaxHealthChangeType.SameCurrentHealth:
                break;

            case MaxHealthChangeType.FullyHeal:
                ChangeHealth(_maxHealth - _currentHealth, thresholds);
                break;
        }

        // GPL follow-up (OpenSAGE port kept it): when max health shrank below current,
        // clip current straight down.
        if (_currentHealth > newMaxHealth)
        {
            ChangeHealth(newMaxHealth - _currentHealth, thresholds);
        }
    }

    /// <summary>GPL <c>setInitialHealth(percent)</c>: health = initial * percent / 100.</summary>
    public void SetInitialHealthPercent(int initialPercent, in DamageStateThresholds thresholds)
    {
        var newHealth = MulDiv(_initialHealth, new Fix64(initialPercent), new Fix64(100));
        ChangeHealth(newHealth - _currentHealth, thresholds);
    }

    /// <summary>GPL <c>internalAddSubdualDamage</c>: accumulate, capped.</summary>
    public void AddSubdualDamage(Fix64 delta, Fix64 cap)
    {
        _currentSubdualDamage = FixMath.Min(_currentSubdualDamage + delta, cap);
    }

    /// <summary>GPL SubdualDamageHelper heal tick: never below zero.</summary>
    public void HealSubdualDamage(Fix64 amount)
    {
        _currentSubdualDamage = FixMath.Max(_currentSubdualDamage - amount, Fix64.Zero);
    }

    /// <summary>GPL <c>isSubdued</c>: subdual damage has reached max health.</summary>
    public bool IsSubdued => _maxHealth <= _currentSubdualDamage;

    /// <summary>
    /// GPL <c>calcDamageState</c> predicate chain, division-free:
    /// health/max &gt; t  ⇔  health &gt; max * t (max, t ≥ 0; products stay far below
    /// the Q31.32 ceiling: max ≤ ~1e7, t ≤ 1 - overflow rule R1).
    /// </summary>
    public static BodyDamageType CalculateDamageState(
        Fix64 health, Fix64 maxHealth, in DamageStateThresholds thresholds)
    {
        if (health > maxHealth * thresholds.Damaged)
        {
            return BodyDamageType.Pristine;
        }
        if (health > maxHealth * thresholds.ReallyDamaged)
        {
            return BodyDamageType.Damaged;
        }
        if (health > Fix64.Zero)
        {
            return BodyDamageType.ReallyDamaged;
        }
        return BodyDamageType.Rubble;
    }

    /// <summary>a * b / c, exact in Int128 on the raw values, truncating toward zero.</summary>
    private static Fix64 MulDiv(Fix64 a, Fix64 b, Fix64 c)
    {
        if (c == Fix64.Zero)
        {
            return Fix64.Zero;
        }
        var raw = (System.Int128)a.RawValue * b.RawValue / c.RawValue;
        return Fix64.FromRaw((long)raw);
    }

    /// <summary>
    /// Legacy-load entry: the retail .sav reader (F9-exempt) restores quantized values
    /// directly without re-running transition logic.
    /// </summary>
    public void LoadState(
        Fix64 currentHealth, Fix64 subdualDamage, Fix64 previousHealth,
        Fix64 maxHealth, Fix64 initialHealth, BodyDamageType damageState)
    {
        _currentHealth = currentHealth;
        _currentSubdualDamage = subdualDamage;
        _previousHealth = previousHealth;
        _maxHealth = maxHealth;
        _initialHealth = initialHealth;
        _currentDamageState = damageState;
    }

    // ---- the single walk (F9: declaration order, ours). Health fields are the
    // conformance channel-2 quantities (Tolerance.Quantum against the float oracle). ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFix64("CurrentHealth", ref _currentHealth, Tolerance.Quantum);
        xfer.XferFix64("PreviousHealth", ref _previousHealth, Tolerance.Quantum);
        xfer.XferFix64("MaxHealth", ref _maxHealth, Tolerance.Quantum);
        xfer.XferFix64("InitialHealth", ref _initialHealth, Tolerance.Quantum);
        xfer.XferFix64("SubdualDamage", ref _currentSubdualDamage, Tolerance.Quantum);
        xfer.XferEnum("DamageState", ref _currentDamageState);
    }
}
