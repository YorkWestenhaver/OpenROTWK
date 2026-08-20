// AttributeModifierPoolUpdate - R12 port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/, so this is the minimal behavior the R12
// spec packet describes: a per-object pool of time-limited, keyed float modifiers that
// combine by screen-blend composition rather than plain addition.
//
// Composition rule: the first surviving modifier seeds the accumulator; every further
// surviving modifier folds in as acc <- 1 - (1 - acc) * (1 - v), the same "opacity stacking"
// shape used elsewhere for stacking percentage bonuses without ever exceeding 1.0. A record
// survives when (a) it has not expired as of the current frame, (b) it matches the caller's
// key, and (c) its source type is not one of the excluded AI-only types unless the caller
// opts in. Walk order is the pool's insertion order and must be preserved end to end -
// screen-blend is commutative in real-number math but not bit-exact in fixed-point, so a
// re-ordered walk can miss the recorded tolerance band.
//
// TODO-spec (unverified retail behavior, filed not invented): the caller-side wiring that
// pushes modifiers into the pool (which gameplay systems grant them, and which of the two
// scaled unit fields each key maps to) is not covered by any landed spec; this port lands
// the pool + accumulate core (independently testable, matches every packet test case) and
// leaves the producer/consumer wiring for the system that owns the scaled fields.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AttributeModifierPoolUpdate : UpdateModule
{
    /// <summary>One pooled modifier: a value, a caller-matched key, a source type used for
    /// the AI-type exclusion, and the frame it stops being active on.</summary>
    internal struct PoolRecord
    {
        public Fix64 Value;
        public int Key;
        public int SourceType;
        public LogicFrame ExpiryFrame;
    }

    /// <summary>Source types treated as AI-only and excluded from a query unless the caller
    /// opts in via <see cref="GetAccumulatedModifier"/>'s includeAiTypes argument.</summary>
    private const int AiTypeRangeStart = 10;
    private const int AiTypeRangeEnd = 14;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private readonly List<PoolRecord> _pool = new();

    public AttributeModifierPoolUpdate(GameObject gameObject, ISimContext context, AttributeModifierPoolUpdateModuleData data)
        : base(gameObject, context)
    {
        // The ModuleData carries no authored fields (empty parse table): the pool is purely
        // runtime state fed by AddModifier, and reads (GetAccumulatedModifier) are on-demand,
        // so there is nothing to schedule on its own. Periodic pruning keeps the list from
        // growing unbounded across a long match.
        SetWakeFrame(UpdateSleepTime.Frames(PruneIntervalFrames));
    }

    private static readonly LogicFrameSpan PruneIntervalFrames = new(150);

    public override UpdateSleepTime Update()
    {
        PruneExpired(Context.CurrentFrame);
        return UpdateSleepTime.Frames(PruneIntervalFrames);
    }

    /// <summary>Adds one modifier record to the pool. <paramref name="expiryFrame"/> is the
    /// first frame the record is no longer active (active while frame &lt; expiryFrame).</summary>
    public void AddModifier(Fix64 value, int key, int sourceType, LogicFrame expiryFrame)
    {
        _pool.Add(new PoolRecord
        {
            Value = value,
            Key = key,
            SourceType = sourceType,
            ExpiryFrame = expiryFrame,
        });
    }

    /// <summary>Walks the pool in insertion order and screen-blends every record that is
    /// still active, matches <paramref name="key"/>, and passes the AI-type predicate.</summary>
    public Fix64 GetAccumulatedModifier(int key, bool includeAiTypes = false)
    {
        return Accumulate(_pool, Context.CurrentFrame, key, includeAiTypes);
    }

    /// <summary>The pure accumulation core (kept static + internal so it can be exercised
    /// directly against hand-built pools, independent of a live GameObject).</summary>
    internal static Fix64 Accumulate(IReadOnlyList<PoolRecord> pool, LogicFrame currentFrame, int key, bool includeAiTypes)
    {
        var acc = Fix64.Zero;
        var hasMatch = false;

        foreach (var record in pool)
        {
            if (currentFrame >= record.ExpiryFrame)
            {
                continue;
            }
            if (record.Key != key)
            {
                continue;
            }
            if (!includeAiTypes && IsAiType(record.SourceType))
            {
                continue;
            }

            if (!hasMatch)
            {
                acc = record.Value;
                hasMatch = true;
            }
            else
            {
                acc = Fix64.One - (Fix64.One - acc) * (Fix64.One - record.Value);
            }
        }

        return acc;
    }

    private static bool IsAiType(int sourceType) => sourceType >= AiTypeRangeStart && sourceType <= AiTypeRangeEnd;

    private void PruneExpired(LogicFrame currentFrame)
    {
        _pool.RemoveAll(record => currentFrame >= record.ExpiryFrame);
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("Pool", _pool, XferRecord);
    }

    private static void XferRecord(IXfer xfer, ref PoolRecord record)
    {
        xfer.XferFix64("Value", ref record.Value, Tolerance.Band);
        xfer.XferInt("Key", ref record.Key);
        xfer.XferInt("SourceType", ref record.SourceType);
        xfer.XferFrame("ExpiryFrame", ref record.ExpiryFrame);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class AttributeModifierPoolUpdateModuleData : UpdateModuleData
{
    internal static AttributeModifierPoolUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AttributeModifierPoolUpdateModuleData> FieldParseTable = new IniParseTable<AttributeModifierPoolUpdateModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AttributeModifierPoolUpdate(gameObject, gameEngine.SimContext, this);
    }
}
