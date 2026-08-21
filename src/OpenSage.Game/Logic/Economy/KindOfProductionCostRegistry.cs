// Per-Player registry of KindOf production-cost-change modifiers (S4 economy sink for
// CostModifierUpgrade). Behavioral reference: generals-gpl GeneralsMD Common/RTS/Player.cpp
// Player::addKindOfProductionCostChange / removeKindOfProductionCostChange /
// getProductionCostChangeBasedOnKindOf (semantics only; fresh code, Q31.32).
//
// GPL semantics reproduced exactly:
//   - a ref-counted list of (KindOf mask, percent) entries;
//   - add: if an entry with the SAME (percent, kindOf) already exists, bump its ref count;
//     else append with ref = 1 (so two structures granting the identical discount collapse
//     to one entry, and removing one leaves the other's effect intact);
//   - remove: find the matching entry, drop its ref count; erase at zero (a remove that
//     finds nothing is a no-op here - GPL only DEBUG_ASSERTCRASHes, never mutates);
//   - get(queryKindOf): start at 1, and for every entry whose mask is a SUBSET of the
//     query's kinds (GPL testSetAndClear(entryMask, NONE) == "query contains all of
//     entryMask", so an empty entry mask matches everything), multiply by (1 + percent).
//     The product is the cost multiplier ThingTemplate::calcCostToBuild applies (mirrors the
//     landed ProductionMath.StackKindOfCostChange, one factor per matching entry).
//
// This registry is transient derived sim state: it is NOT serialized on its own. On load it
// is rebuilt from the triggered CostModifierUpgrade modules that re-apply themselves
// (CostModifierUpgrade.ReapplyAfterLoad), so the Players CRC channel and Player.Persist are
// left untouched (merge hygiene) while the observable state is identical to GPL's
// Player-persisted list. See research/modules-r9/CostModifierUpgradeModuleData.md.

using System.Collections.Generic;
using OpenSage.Logic.Object;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Economy;

[SimState]
public sealed class KindOfProductionCostRegistry
{
    private readonly List<KindOfProductionCostChange> _entries = new();

    internal IReadOnlyList<KindOfProductionCostChange> Entries => _entries;

    /// <summary>GPL <c>Player::addKindOfProductionCostChange</c>: ref-count or append.</summary>
    public void Add(BitArray<ObjectKinds> kindOf, Fix64 percent)
    {
        foreach (var entry in _entries)
        {
            if (entry.Percent == percent && MasksEqual(entry.KindOf, kindOf))
            {
                entry.RefCount++;
                return;
            }
        }

        _entries.Add(new KindOfProductionCostChange(new BitArray<ObjectKinds>(kindOf), percent));
    }

    /// <summary>GPL <c>Player::removeKindOfProductionCostChange</c>: ref-count down, erase at zero.</summary>
    public void Remove(BitArray<ObjectKinds> kindOf, Fix64 percent)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Percent == percent && MasksEqual(entry.KindOf, kindOf))
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _entries.RemoveAt(i);
                }
                return;
            }
        }
        // GPL only asserts here; a not-found remove is a no-op in a shipping build.
    }

    /// <summary>
    /// GPL <c>Player::getProductionCostChangeBasedOnKindOf</c>: the multiplicative cost
    /// factor for an object whose kinds are <paramref name="queryKindOf"/>. One (1 + percent)
    /// factor per entry whose mask is contained in the query (empty mask matches all).
    /// Returns <see cref="Fix64.One"/> when nothing matches.
    /// </summary>
    public Fix64 GetProductionCostChangeBasedOnKindOf(BitArray<ObjectKinds> queryKindOf)
    {
        var factor = Fix64.One;
        foreach (var entry in _entries)
        {
            // GPL testSetAndClear(entry.KindOf, NONE): every bit of entry.KindOf is set in
            // queryKindOf. Count-of-intersection == entry's set-bit count expresses that,
            // and is trivially true (0 == 0) for an empty entry mask.
            if (queryKindOf.CountIntersectionBits(entry.KindOf) == entry.KindOf.NumBitsSet)
            {
                factor = ProductionMath.StackKindOfCostChange(factor, entry.Percent);
            }
        }
        return factor;
    }

    // Value equality of two kind masks. BitArray512 (the wrapper's backing struct) has no
    // Equals(object) override, so BitArray<T>.Equals falls through to ValueType.Equals, which
    // also compares the lazy NumBitsSet cache field - two masks with identical bits but a
    // computed-vs-dirty cache compare unequal there. Same set of bits <=> equal set-bit counts
    // AND a full intersection; both go through recomputing accessors, so this is cache-safe.
    private static bool MasksEqual(BitArray<ObjectKinds> a, BitArray<ObjectKinds> b)
        => a.NumBitsSet == b.NumBitsSet && a.CountIntersectionBits(b) == a.NumBitsSet;
}

/// <summary>One ref-counted (KindOf mask, percent) modifier entry (GPL KindOfPercentProductionChange).</summary>
[SimState]
public sealed class KindOfProductionCostChange
{
    public KindOfProductionCostChange(BitArray<ObjectKinds> kindOf, Fix64 percent)
    {
        KindOf = kindOf;
        Percent = percent;
        RefCount = 1;
    }

    public BitArray<ObjectKinds> KindOf { get; }
    public Fix64 Percent { get; }
    public int RefCount { get; set; }
}
