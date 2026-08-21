// SimLocomotorSet - the surface-keyed locomotor collection of the S2 system. Fresh code;
// behavioral reference (semantics only): generals-gpl GeneralsMD Locomotor.cpp
// (class LocomotorSet) + LocomotorSet.h.
//
// Selection semantics (GPL findLocomotor): FIRST locomotor in DECLARATION ORDER whose
// legal-surfaces mask intersects the requested mask - order matters and is the INI
// author's order, which is deterministic parse output.
//
// Xfer deviation from GPL (allowed by F9 - original field order/layout is out of the
// contract): the original persists each locomotor's TEMPLATE NAME and re-resolves it at
// load. Our IXfer walk carries no strings; instead the OWNING module xfers the
// LocomotorSetType it built this set from and rebuilds the same membership from the
// object definition (same INI on every peer), then this Xfer walks per-locomotor mutable
// state in membership order plus a count guard.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Locomotion;

[SimState]
public sealed class SimLocomotorSet
{
    private readonly List<SimLocomotor> _locomotors = new();
    private Surfaces _validSurfaces;
    private bool _downhillOnly;

    public IReadOnlyList<SimLocomotor> Locomotors => _locomotors;
    public Surfaces ValidSurfaces => _validSurfaces;
    public bool IsDownhillOnly => _downhillOnly;

    /// <summary>Live-creation add: the locomotor ctor draws its RNG stagger (3 draws).</summary>
    public void AddLocomotor(LocomotorTemplate template, ISimRandom random, LogicFrame now)
    {
        Add(new SimLocomotor(template, random, now));
    }

    /// <summary>Load-path add: NO rng draws; state arrives via Xfer.</summary>
    internal void AddLocomotorForLoad(LocomotorTemplate template)
    {
        Add(new SimLocomotor(template));
    }

    private void Add(SimLocomotor locomotor)
    {
        _locomotors.Add(locomotor);
        _validSurfaces |= locomotor.LegalSurfaces;
        if (locomotor.IsDownhillOnly)
        {
            _downhillOnly = true;
        }
    }

    public void Clear()
    {
        _locomotors.Clear();
        _validSurfaces = Surfaces.None;
        _downhillOnly = false;
    }

    /// <summary>First declared locomotor legal on any of the requested surfaces, else null.</summary>
    public SimLocomotor FindLocomotor(Surfaces surfaces)
    {
        foreach (var locomotor in _locomotors)
        {
            if ((locomotor.LegalSurfaces & surfaces) != 0)
            {
                return locomotor;
            }
        }
        return null;
    }

    public int IndexOf(SimLocomotor locomotor) => _locomotors.IndexOf(locomotor);

    /// <summary>
    /// Walks per-locomotor mutable state in membership order. The membership itself is
    /// rebuilt by the owning module BEFORE this runs on load (see file header).
    /// </summary>
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        var count = _locomotors.Count;
        xfer.XferInt("LocomotorCount", ref count);
        if (count != _locomotors.Count)
        {
            throw new System.InvalidOperationException(
                $"SimLocomotorSet membership mismatch: stream has {count}, definition rebuilt {_locomotors.Count}");
        }
        foreach (var locomotor in _locomotors)
        {
            locomotor.Xfer(xfer);
        }
        xfer.XferEnum("ValidSurfaces", ref _validSurfaces);
        xfer.XferBool("DownhillOnly", ref _downhillOnly);
    }
}
