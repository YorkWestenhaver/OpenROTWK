// Per-Player registry of special-power recharge-time-discount modifiers (the economy sink for
// SpellRechargeModifierUpgrade). Structurally the sibling KindOfProductionCostRegistry with the
// KindOf mask removed - this module has no EffectKindOf-equivalent field to carry one (spec
// research/modules-r13/specs/SpellRechargeModifierUpgradeModuleData.md §1). Ref-count semantics
// copied verbatim from KindOfProductionCostRegistry.Add/Remove minus the mask-equality check.
//
// Semantics reproduced (by analogy to the sibling registry):
//   - a ref-counted list of percent entries;
//   - add: if an entry with the SAME percent already exists, bump its ref count; else append
//     with ref = 1 (so two structures granting the identical discount collapse to one entry,
//     and removing one leaves the other's effect intact);
//   - remove: find the matching entry, drop its ref count; erase at zero (a remove that finds
//     nothing is a no-op, mirroring the sibling);
//   - get(): start at Fix64.One, multiply by (1 + percent) per entry via the same
//     ProductionMath.StackKindOfCostChange fold the sibling registry uses.
//
// This registry is transient derived sim state: it is NOT serialized on its own. On load it is
// rebuilt from the triggered SpellRechargeModifierUpgrade modules that re-apply themselves
// (SpellRechargeModifierUpgrade.ReapplyAfterLoad), so the Players CRC channel and
// Player.Persist are left untouched (merge hygiene).
//
// Unconsumed by any landed recharge timer as of this port (no such timer exists in this engine
// snapshot) - exposed for the future consumer and for tests. See spec finding F-SRM-1.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Economy;

[SimState]
public sealed class SpecialPowerRechargeDiscountRegistry
{
    private readonly List<SpecialPowerRechargeDiscountChange> _entries = new();

    internal IReadOnlyList<SpecialPowerRechargeDiscountChange> Entries => _entries;

    /// <summary>Ref-count or append (mirrors KindOfProductionCostRegistry.Add, mask removed).</summary>
    public void Add(Fix64 percent)
    {
        foreach (var entry in _entries)
        {
            if (entry.Percent == percent)
            {
                entry.RefCount++;
                return;
            }
        }

        _entries.Add(new SpecialPowerRechargeDiscountChange(percent));
    }

    /// <summary>Ref-count down, erase at zero (mirrors KindOfProductionCostRegistry.Remove).</summary>
    public void Remove(Fix64 percent)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Percent == percent)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _entries.RemoveAt(i);
                }
                return;
            }
        }
        // A not-found remove is a no-op, mirroring the sibling registry.
    }

    /// <summary>
    /// The multiplicative recharge-time factor from all currently-registered discounts. One
    /// (1 + percent) factor per entry, folded via <see cref="ProductionMath.StackKindOfCostChange"/>
    /// (same fold the sibling registry's cost-change query uses). Returns <see cref="Fix64.One"/>
    /// when empty. Not yet wired into a landed recharge-timer path - see spec finding F-SRM-1.
    /// </summary>
    public Fix64 GetSpecialPowerRechargeDiscountFactor()
    {
        var factor = Fix64.One;
        foreach (var entry in _entries)
        {
            factor = ProductionMath.StackKindOfCostChange(factor, entry.Percent);
        }
        return factor;
    }
}

/// <summary>One ref-counted percent modifier entry (mirrors KindOfProductionCostChange, mask removed).</summary>
[SimState]
public sealed class SpecialPowerRechargeDiscountChange
{
    public SpecialPowerRechargeDiscountChange(Fix64 percent)
    {
        Percent = percent;
        RefCount = 1;
    }

    public Fix64 Percent { get; }
    public int RefCount { get; set; }
}
