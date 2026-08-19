// The deterministic production queue - GPL ProductionUpdate's queue/timer/refund
// machine, extracted as a pure core (generals-gpl GeneralsMD
// GameLogic/Object/Update/ProductionUpdate.cpp: queueCreateUnit / queueUpgrade /
// cancelUnitCreate / cancelUpgrade / cancelAndRefundAllProduction / update (the
// progress-accounting half) / addToProductionQueue / removeFromProductionQueue /
// isUpgradeInQueue / countUnitTypeInQueue / ProductionEntry; semantics only, fresh
// code).
//
// DESIGN (the surface a future ProductionUpdate module port calls):
//   - The core owns ONLY deterministic accounting: the FIFO entry list, per-entry frame
//     counters, quantity bookkeeping, money withdraw/refund against a ResourceBank, and
//     the completion predicate. Everything the GPL update() does with doors, model
//     condition flags, exits, audio, EVA, radar and the script engine stays in the
//     owning module (client/orchestration side), driven by the returned results.
//   - Templates and upgrades are identified by an owner-supplied uint key (asset
//     instance id) - the core never sees ThingTemplate/UpgradeTemplate, and the IXfer
//     surface deliberately has no string op (names are resolved by the owner on load,
//     mirroring GPL's name-based ProductionEntry xfer under our F9 our-order rule).
//   - Costs are computed by the CALLER (ProductionMath + its player modifiers) at queue
//     time and at cancel time - GPL recomputes calcCostToBuild at cancel, so a refund
//     can legally differ from the sum withdrawn if modifiers changed in between. The
//     core preserves that by never remembering the withdrawn amount.
//   - Affordability is checked by the caller before queueing (GPL BuildAssistant /
//     canAffordUpgrade shape); the withdraw itself clamps (GPL Money::withdraw).
//   - GPL's per-frame percent float (m_percentComplete) is display state derived from
//     the two ints; we keep only the ints and expose an exact Fix64 ratio.
//
// Completion predicate: GPL computes `percent = frames / totalFrames * 100` in float and
// completes at >= 100. For positive integers that predicate is exactly
// `frames >= totalFrames`; the core compares the ints and never divides.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Economy;

/// <summary>GPL <c>ProductionType</c>.</summary>
public enum ProductionKind
{
    Invalid = 0,
    Unit = 1,
    Upgrade = 2,
}

/// <summary>Result of one frame of production on the head entry.</summary>
public enum ProductionAdvanceResult
{
    /// <summary>Queue empty - nothing advanced.</summary>
    Idle,
    /// <summary>Head entry advanced one frame, not complete yet.</summary>
    InProgress,
    /// <summary>Head entry reached its build time this frame (frames >= total).</summary>
    Complete,
}

/// <summary>
/// GPL <c>ProductionEntry</c>, the deterministic fields only. A struct so the frozen
/// XferList item walk round-trips it by value.
/// </summary>
[SimState]
public struct ProductionEntry
{
    /// <summary>GPL <c>PRODUCTIONID_INVALID</c> (upgrades carry it; only units get real ids).</summary>
    public const int InvalidProductionId = 0;

    public ProductionKind Kind;
    /// <summary>Owner-side key of the ThingTemplate (Unit) or UpgradeTemplate (Upgrade).</summary>
    public uint TemplateKey;
    /// <summary>Unit production id (caller-assigned, GPL shape); InvalidProductionId for upgrades.</summary>
    public int ProductionId;
    /// <summary>GPL <c>m_framesUnderConstruction</c>.</summary>
    public int FramesUnderConstruction;
    /// <summary>GPL <c>m_productionQuantityTotal</c> (QuantityModifier; 1 normally).</summary>
    public int QuantityTotal;
    /// <summary>GPL <c>m_productionQuantityProduced</c>.</summary>
    public int QuantityProduced;

    public readonly int QuantityRemaining => QuantityTotal - QuantityProduced;

    public void Xfer(IXfer xfer)
    {
        xfer.XferEnum("Kind", ref Kind);
        xfer.XferUInt("TemplateKey", ref TemplateKey);
        xfer.XferInt("ProductionId", ref ProductionId);
        xfer.XferInt("FramesUnderConstruction", ref FramesUnderConstruction, Tolerance.Quantum);
        xfer.XferInt("QuantityTotal", ref QuantityTotal);
        xfer.XferInt("QuantityProduced", ref QuantityProduced);
    }
}

[SimState]
public sealed class ProductionQueueCore
{
    /// <summary>GPL default MaxQueueEntries.</summary>
    public const int DefaultMaxQueueEntries = 9;

    private readonly int _maxQueueEntries;

    // ---- mutable sim state (every field is in Xfer) ----
    private readonly List<ProductionEntry> _queue = new();
    private int _uniqueId;   // GPL m_uniqueID, starts at 1

    /// <param name="maxQueueEntries">0 means unlimited (existing OpenSAGE convention).</param>
    public ProductionQueueCore(int maxQueueEntries = DefaultMaxQueueEntries)
    {
        _maxQueueEntries = maxQueueEntries;
        _uniqueId = 1;
    }

    public int Count => _queue.Count;

    public bool IsProducing => _queue.Count > 0;

    /// <summary>Read-only view for owner-side iteration (UI counts, cancel-all sweeps).</summary>
    public IReadOnlyList<ProductionEntry> Entries => _queue;

    /// <summary>GPL <c>canQueueCreateUnit</c>/<c>canQueueUpgrade</c> queue-full half.</summary>
    public bool CanQueue => _maxQueueEntries == 0 || _queue.Count < _maxQueueEntries;

    /// <summary>
    /// Allocate the next production id (GPL m_uniqueID++ shape; the original's unit ids
    /// come from the UI message, ours come from here so replays and AI share one path).
    /// </summary>
    public int AllocateProductionId() => _uniqueId++;

    /// <summary>
    /// GPL <c>queueCreateUnit</c> accounting half: withdraw the (caller-computed) cost
    /// and append the entry. The caller has already run every can-make check.
    /// Returns false when the queue is full.
    /// </summary>
    public bool QueueUnit(uint templateKey, int productionId, int cost, int quantity, ResourceBank bank)
    {
        if (!CanQueue)
        {
            return false;
        }

        bank.Withdraw((uint)cost);

        _queue.Add(new ProductionEntry
        {
            Kind = ProductionKind.Unit,
            TemplateKey = templateKey,
            ProductionId = productionId,
            FramesUnderConstruction = 0,
            QuantityTotal = quantity > 0 ? quantity : 1,
            QuantityProduced = 0,
        });
        return true;
    }

    /// <summary>
    /// GPL <c>queueUpgrade</c> accounting half: one entry per upgrade at most
    /// (isUpgradeInQueue), withdraw, append. Player/object upgrade validity and the
    /// has-upgrade / in-production-elsewhere checks are the caller's.
    /// </summary>
    public bool QueueUpgrade(uint upgradeKey, int cost, ResourceBank bank)
    {
        if (IsUpgradeInQueue(upgradeKey) || !CanQueue)
        {
            return false;
        }

        bank.Withdraw((uint)cost);

        _queue.Add(new ProductionEntry
        {
            Kind = ProductionKind.Upgrade,
            TemplateKey = upgradeKey,
            ProductionId = ProductionEntry.InvalidProductionId,
            FramesUnderConstruction = 0,
            QuantityTotal = 1,
            QuantityProduced = 0,
        });
        return true;
    }

    /// <summary>
    /// GPL <c>cancelUnitCreate</c> accounting half: refund the (caller-RECOMPUTED) cost
    /// and drop the entry. Returns true when the id was found.
    /// </summary>
    public bool CancelUnit(int productionId, int refund, ResourceBank bank)
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Kind == ProductionKind.Unit && _queue[i].ProductionId == productionId)
            {
                bank.Deposit((uint)refund);
                _queue.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>GPL <c>cancelUpgrade</c> accounting half.</summary>
    public bool CancelUpgrade(uint upgradeKey, int refund, ResourceBank bank)
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Kind == ProductionKind.Upgrade && _queue[i].TemplateKey == upgradeKey)
            {
                bank.Deposit((uint)refund);
                _queue.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>GPL <c>isUpgradeInQueue</c>.</summary>
    public bool IsUpgradeInQueue(uint upgradeKey)
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Kind == ProductionKind.Upgrade && _queue[i].TemplateKey == upgradeKey)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>GPL <c>countUnitTypeInQueue</c>.</summary>
    public int CountUnitTypeInQueue(uint templateKey)
    {
        var count = 0;
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Kind == ProductionKind.Unit && _queue[i].TemplateKey == templateKey)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>The head entry (only valid while <see cref="IsProducing"/>).</summary>
    public ProductionEntry Front => _queue[0];

    /// <summary>
    /// One logic frame of production on the head entry - GPL update()'s
    /// `m_framesUnderConstruction++` plus the completion predicate against the
    /// (caller-recomputed, modifiers may change mid-build) total build frames.
    /// </summary>
    public ProductionAdvanceResult AdvanceFront(int totalProductionFrames)
    {
        if (_queue.Count == 0)
        {
            return ProductionAdvanceResult.Idle;
        }

        var front = _queue[0];
        front.FramesUnderConstruction++;
        _queue[0] = front;

        return front.FramesUnderConstruction >= totalProductionFrames
            ? ProductionAdvanceResult.Complete
            : ProductionAdvanceResult.InProgress;
    }

    /// <summary>
    /// GPL <c>oneProductionSuccessful</c>: one unit of the head entry actually exited.
    /// Returns the remaining quantity (0 = the entry is spent; caller removes it).
    /// </summary>
    public int MarkFrontUnitProduced()
    {
        var front = _queue[0];
        front.QuantityProduced++;
        _queue[0] = front;
        return front.QuantityRemaining;
    }

    /// <summary>Remove the head entry WITHOUT refund (production finished, or player gone).</summary>
    public void RemoveFront() => _queue.RemoveAt(0);

    /// <summary>
    /// Exact progress ratio of the head entry in [0, 1] (GPL m_percentComplete / 100;
    /// float only at display). Total 0 counts as complete.
    /// </summary>
    public Fix64 GetFrontProgress(int totalProductionFrames)
    {
        if (_queue.Count == 0)
        {
            return Fix64.Zero;
        }
        if (totalProductionFrames <= 0 || _queue[0].FramesUnderConstruction >= totalProductionFrames)
        {
            return Fix64.One;
        }
        return new Fix64(_queue[0].FramesUnderConstruction) / new Fix64(totalProductionFrames);
    }

    // ---- the single walk (save/load + CRC + deep-dump), F9 declaration order ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Queue", _queue, static (IXfer x, ref ProductionEntry item) => item.Xfer(x));
        xfer.XferInt("UniqueId", ref _uniqueId);
    }
}
