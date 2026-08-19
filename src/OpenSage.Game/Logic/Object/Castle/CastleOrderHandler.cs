// Castle/build-plot order handlers (R9 castles task): the sim-side handlers for
// MSG_FOUNDATION_CONSTRUCT (1049), FOUNDATION_CONSTRUCT_CANCEL (~1050),
// MSG_CASTLE_UNPACK (1085), MSG_CASTLE_PACK (1086) and
// MSG_CASTLE_UNPACK_EXPLICIT_OBJECT (1087) - spec-castles.md §2.3/§5.6/§5.9.
//
// The MSG_CASTLE_UNPACK guard SEQUENCE is retail's, in retail's order (FUN_007bed64):
//   object exists -> issuing player owns it -> it has a CastleBehavior -> canUnpack(1)
//   -> isPlayerAllowedToPackOrUnpack -> (non-explicit form only) affordability, charging
//   UnpackCost from the matched CastleToUnpackForFaction entry -> initiateUnpack.
//
// Economy: the S4 ResourceBank is the ledger (research/systems/economy-production.md §1:
// callers check CanAfford BEFORE the charge; the withdraw clamps). Banks are resolved per
// player through a delegate so the future SimPlayer wires in without touching this file;
// tests construct banks directly. Money is int/uint end-to-end (F3).
//
// Result codes exist so tests (and the future dispatch layer) can assert WHICH guard
// rejected an order - the original silently drops, ours drops loudly.

using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Orders;

namespace OpenSage.Logic.Object.Castle;

public enum CastleOrderResult
{
    Ok = 0,
    NoSuchObject,
    NotOwner,
    NoCastleBehavior,
    CannotUnpack,
    NotAllowed,
    CannotAfford,
    NotAFoundation,
    TemplateNotBuildableOnFoundation,
    FoundationOccupied,
    NothingToCancel,
}

/// <summary>Resolves the money ledger for a player (the future SimPlayer's ResourceBank).</summary>
public delegate Economy.ResourceBank CastleBankResolver(Player player);

[SimState]
public sealed class CastleOrderHandler
{
    private readonly IGameEngine _gameEngine;
    private readonly CastleBankResolver _bankResolver;

    /// <summary>Plot id -> structure id for in-flight foundation constructions (xfered).</summary>
    private readonly List<FoundationConstruction> _constructions = new();

    private struct FoundationConstruction
    {
        public ObjectId PlotId;
        public ObjectId StructureId;
        public int CostPaid;
    }

    public CastleOrderHandler(IGameEngine gameEngine, CastleBankResolver bankResolver)
    {
        _gameEngine = gameEngine;
        _bankResolver = bankResolver;
    }

    // ------------------------------------------------------------------
    // MSG_CASTLE_UNPACK (1085) / MSG_CASTLE_UNPACK_EXPLICIT_OBJECT (1087)
    // ------------------------------------------------------------------

    public CastleOrderResult HandleCastleUnpack(Player issuingPlayer, ObjectId objectId)
        => HandleCastleUnpackCore(issuingPlayer, objectId, explicitCampName: null);

    /// <summary>The explicit-object form (1087): a named camp overrides the faction choice and skips the charge.</summary>
    public CastleOrderResult HandleCastleUnpackExplicitObject(Player issuingPlayer, ObjectId objectId, string campName)
        => HandleCastleUnpackCore(issuingPlayer, objectId, campName);

    private CastleOrderResult HandleCastleUnpackCore(Player issuingPlayer, ObjectId objectId, string explicitCampName)
    {
        // Guard 1: object exists.
        var obj = FindObjectById(objectId);
        if (obj == null || obj.IsDestroyed)
        {
            return CastleOrderResult.NoSuchObject;
        }

        // Guard 2: issuing player == object owner.
        if (obj.Owner != issuingPlayer)
        {
            return CastleOrderResult.NotOwner;
        }

        // Guard 3: object has a CastleBehavior.
        var castle = obj.FindBehavior<CastleBehavior>();
        if (castle == null)
        {
            return CastleOrderResult.NoCastleBehavior;
        }

        // Guard 4: canUnpack(checkTimer = 1).
        if (!castle.CanUnpack(checkTimer: true))
        {
            return CastleOrderResult.CannotUnpack;
        }

        // Guard 5: isPlayerAllowedToPackOrUnpack.
        if (!castle.IsPlayerAllowedToPackOrUnpack(issuingPlayer))
        {
            return CastleOrderResult.NotAllowed;
        }

        var entryIndex = -1;

        if (explicitCampName == null)
        {
            // Guard 6 (non-explicit form only): affordability - charge UnpackCost from the
            // matched CastleToUnpackForFaction entry (retail FUN_0079969f).
            entryIndex = castle.FindEntryIndexForPlayer(issuingPlayer);
            var cost = castle.GetUnpackCost(issuingPlayer);
            if (cost > 0)
            {
                var bank = _bankResolver?.Invoke(issuingPlayer);
                if (bank == null || !bank.CanAfford((uint)cost))
                {
                    return CastleOrderResult.CannotAfford;
                }

                bank.Withdraw((uint)cost);
            }
        }
        else
        {
            entryIndex = castle.FindEntryIndexForCamp(explicitCampName);
        }

        castle.InitiateUnpack(issuingPlayer, entryIndex, instant: false);
        return CastleOrderResult.Ok;
    }

    // ------------------------------------------------------------------
    // MSG_CASTLE_PACK (1086)
    // ------------------------------------------------------------------

    public CastleOrderResult HandleCastlePack(Player issuingPlayer, ObjectId objectId)
    {
        var obj = FindObjectById(objectId);
        if (obj == null || obj.IsDestroyed)
        {
            return CastleOrderResult.NoSuchObject;
        }

        var castle = obj.FindBehavior<CastleBehavior>();
        if (castle == null)
        {
            return CastleOrderResult.NoCastleBehavior;
        }

        if (!castle.IsPlayerAllowedToPackOrUnpack(issuingPlayer))
        {
            return CastleOrderResult.NotAllowed;
        }

        castle.InitiatePack();
        return CastleOrderResult.Ok;
    }

    // ------------------------------------------------------------------
    // MSG_FOUNDATION_CONSTRUCT (1049) - spec §5.9
    // ------------------------------------------------------------------

    public CastleOrderResult HandleFoundationConstruct(Player issuingPlayer, ObjectId plotId, string templateName)
    {
        var plot = FindObjectById(plotId);
        if (plot == null || plot.IsDestroyed)
        {
            return CastleOrderResult.NoSuchObject;
        }

        if (plot.Owner != issuingPlayer)
        {
            return CastleOrderResult.NotOwner;
        }

        if (!plot.Definition.KindOf.Get(ObjectKinds.BaseFoundation))
        {
            return CastleOrderResult.NotAFoundation;
        }

        // Validate the template: it must exist and be flagged NEED_BASE_FOUNDATION.
        var definition = _gameEngine.AssetLoadContext.AssetStore.ObjectDefinitions.GetByName(templateName);
        if (definition == null || !definition.KindOf.Get(ObjectKinds.NeedBaseFoundation))
        {
            return CastleOrderResult.TemplateNotBuildableOnFoundation;
        }

        // Socket must be free (FoundationAIUpdate's occupancy rule, spec §3.4).
        if (FindConstructionIndexByPlot(plotId) >= 0
            || CastleUnpackStamper.FindStructureOnPlot(plot, _gameEngine) != null)
        {
            return CastleOrderResult.FoundationOccupied;
        }

        // Money: check, then charge (S4 shape).
        var cost = CastleUnpackStamper.GetBuildCost(definition);
        var playerBank = _bankResolver?.Invoke(issuingPlayer);
        if (cost > 0)
        {
            if (playerBank == null || !playerBank.CanAfford(cost))
            {
                return CastleOrderResult.CannotAfford;
            }

            playerBank.Withdraw(cost);
        }

        var structure = CastleUnpackStamper.BuildOnFoundation(plot, _gameEngine, definition);
        if (structure == null)
        {
            // Spawn failure after the charge would be a data error; refund to stay honest.
            playerBank?.Deposit(cost);
            return CastleOrderResult.TemplateNotBuildableOnFoundation;
        }

        _constructions.Add(new FoundationConstruction
        {
            PlotId = plotId,
            StructureId = structure.Id,
            CostPaid = (int)cost,
        });

        return CastleOrderResult.Ok;
    }

    // ------------------------------------------------------------------
    // FOUNDATION_CONSTRUCT_CANCEL (~1050): full refund while still constructing
    // (spec §5.9 "refunds during construction"; the refund fraction is unrecovered -
    // default full refund, finding F-CAS-3).
    // ------------------------------------------------------------------

    public CastleOrderResult HandleFoundationConstructCancel(Player issuingPlayer, ObjectId plotId)
    {
        var index = FindConstructionIndexByPlot(plotId);
        if (index < 0)
        {
            return CastleOrderResult.NothingToCancel;
        }

        var construction = _constructions[index];
        var structure = _gameEngine.GameLogic.GetObjectById(construction.StructureId);

        if (structure == null || structure.IsDestroyed || !structure.IsBeingConstructed())
        {
            // Finished or already gone: nothing cancellable; drop the tracking row.
            _constructions.RemoveAt(index);
            return CastleOrderResult.NothingToCancel;
        }

        if (structure.Owner != issuingPlayer)
        {
            return CastleOrderResult.NotOwner;
        }

        _bankResolver?.Invoke(issuingPlayer)?.Deposit((uint)construction.CostPaid);
        _gameEngine.GameLogic.DestroyObject(structure);
        _constructions.RemoveAt(index);
        return CastleOrderResult.Ok;
    }

    /// <summary>End-of-frame style sweep: drop tracking rows whose structure completed or died.</summary>
    public void PruneFinishedConstructions()
    {
        for (var i = _constructions.Count - 1; i >= 0; i--)
        {
            var structure = _gameEngine.GameLogic.GetObjectById(_constructions[i].StructureId);
            if (structure == null || structure.IsDestroyed || !structure.IsBeingConstructed())
            {
                _constructions.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Order-payload id lookup: an order can name ANY id (malformed input is legal wire
    /// data, F6), so this never throws - unlike GameLogic.GetObjectById, whose index must
    /// already be allocated. Ascending-id enumeration, deterministic.
    /// </summary>
    private GameObject FindObjectById(ObjectId id)
    {
        if (id.IsInvalid)
        {
            return null;
        }

        foreach (var obj in _gameEngine.GameLogic.Objects)
        {
            if (obj.Id == id)
            {
                return obj;
            }
        }

        return null;
    }

    private int FindConstructionIndexByPlot(ObjectId plotId)
    {
        for (var i = 0; i < _constructions.Count; i++)
        {
            if (_constructions[i].PlotId == plotId)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- Xfer: the in-flight construction table is sim state ----

    public void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Constructions", _constructions, static (SimCore.Sync.IXfer x, ref FoundationConstruction c) =>
        {
            x.XferObjectId("PlotId", ref c.PlotId);
            x.XferObjectId("StructureId", ref c.StructureId);
            x.XferInt("CostPaid", ref c.CostPaid);
        });
    }
}
