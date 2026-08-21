// RebuildHoleExposeDie - Die-batch port #9 against the frozen module contract
// (api-freeze-v1 §3/§5, template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: generals-gpl GeneralsMD RebuildHoleExposeDie.cpp/.h (GPL semantics
// reference only; this is fresh code). Behavior facts used:
//   - MUTABLE SIM STATE IS EMPTY. The GPL class declares no members and its xfer() is
//     "version 1, then extend the base" - nothing else. So this module's whole contract
//     surface is the version stamp: the walk is the fact, not an omission (see §3 below).
//   - onDie(): after the shared isDieApplicable() filter, a hole is spawned only when ALL of
//       (a) the controlling player is not the neutral player,
//       (b) that player is still active,
//       (c) the dying object is NOT OBJECT_STATUS_UNDER_CONSTRUCTION
//     hold - i.e. a structure that dies while being built, or while being rebuilt out of a
//     hole, must not leave a second hole behind.
//   - the hole is created from HoleName owned by the dying object's team, stood at the dying
//     object's position and orientation, given HoleMaxHealth as its max health, and told
//     which structure it is rebuilding (startRebuildProcess).
//   - the whole body is one-shot and reads no clock and draws no RNG, which is why the class
//     has no state to carry.
//
// NOT PORTED, deliberately - the services do not exist behind ISimContext and inventing them
// is out of a porting task's authority (findings in research/die/RebuildHoleExposeDie.md):
//   F-RH-1 startRebuildProcess' template argument and the rebuild process itself are
//          RebuildHoleBehavior INTERNALS (the batch note's explicit flag case); the handoff
//          here goes through the one-verb IRebuildHoleBehavior seam, GPL's own shape.
//   F-RH-2 isPlayerActive() has no OpenSAGE equivalent - guard (b) is a behavior-fact gap.
//   F-RH-3 setGeometryInfo (hole inherits the structure's extents), transferObjectName
//          (script engine), addObjectToPathfindMap (pathfinder), and TransferAttackers
//          (AIUpdate::transferAttack, which OpenSAGE has no equivalent of at all) all need
//          engine surfaces the context does not expose. TransferAttackers is parsed and
//          unconsumed, exactly as it was before this port.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RebuildHoleExposeDie : DieModule
{
    private readonly RebuildHoleExposeDieModuleData _data;

    // ---- mutable sim state: NONE. See the header note; this is a GPL fact, not an
    // oversight, and it is what makes Xfer below a version stamp and nothing more. ----

    public RebuildHoleExposeDie(GameObject gameObject, ISimContext context, RebuildHoleExposeDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        // GPL guard: no hole for a structure that dies mid-construction (that includes the
        // scaffold a hole is already rebuilding - otherwise holes would breed), and none for
        // an object that has fallen to the neutral player.
        if (GameObject.TestStatus(ObjectStatus.UnderConstruction))
        {
            return;
        }

        if (GameObject.Owner == Context.Players.NeutralPlayer)
        {
            return;
        }

        var holeDefinition = _data.HoleDefinition?.Value;
        if (holeDefinition == null)
        {
            // HoleName is not defaulted by the GPL data class either; a block without it is
            // malformed data, and a missing hole is preferable to a crash on death.
            return;
        }

        var hole = Context.GameLogic.CreateObjectAt(holeDefinition, GameObject.Owner, GameObject);

        // Max health from our own data, not the hole template's (GPL: body->setMaxHealth).
        hole.SetMaxHealth(_data.HoleMaxHealth);

        // The one thing the hole needs to know. GPL asserts the interface is present and
        // skips the handoff when it is not; a hole template without a RebuildHoleBehavior is
        // simply an inert object, so the null-conditional call is the same semantics.
        hole.FindBehavior<IRebuildHoleBehavior>()?.StartRebuildProcess(GameObject);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). There are no fields; the version
    // stamp alone is the walk, exactly as GPL's xfer() is.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept per template rule D-9
    // (a port that REPLACES an existing module keeps its Load and remaps it). The original
    // stream carries a version and the base object and nothing else, matching the empty
    // state inventory above. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Requires the object specified in <see cref="HoleDefinition"/> to have the REBUILD_HOLE KindOf
/// and an <see cref="IRebuildHoleBehavior"/> module in order to work.
/// </summary>
[SimDataAudited]
public sealed class RebuildHoleExposeDieModuleData : DieModuleData
{
    internal static RebuildHoleExposeDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<RebuildHoleExposeDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<RebuildHoleExposeDieModuleData>
        {
            { "HoleName", (parser, x) => x.HoleDefinition = parser.ParseObjectReference() },
            { "HoleMaxHealth", (parser, x) => x.HoleMaxHealth = parser.ParseFix64() },
            { "FadeInTimeSeconds", (parser, x) => x.FadeInTimeSeconds = parser.ParseFix64() },
            { "TransferAttackers", (parser, x) => x.TransferAttackers = parser.ParseBoolean() }
        });

    /// <summary>Template of the hole to expose; resolved at parse time, no runtime lookup.</summary>
    public LazyAssetReference<ObjectDefinition> HoleDefinition { get; private set; }

    /// <summary>Max health handed to the freshly created hole (quantized Q31.32).</summary>
    public Fix64 HoleMaxHealth { get; private set; }

    /// <summary>
    /// BFME2-only, seconds. Client-side fade of the exposed hole; no GPL reference and no
    /// written behavioral spec, so it is audited vocabulary and deliberately unconsumed.
    /// Quantized rather than frame-converted because S5's duration function takes
    /// milliseconds and this field is not one.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 FadeInTimeSeconds { get; private set; }

    /// <summary>
    /// GPL default is TRUE: objects attacking the structure should re-target the hole.
    /// Parsed and unconsumed - OpenSAGE has no AIUpdate transfer-attack equivalent (F-RH-3).
    /// </summary>
    public bool TransferAttackers { get; private set; } = true;

    internal override RebuildHoleExposeDie CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RebuildHoleExposeDie(gameObject, gameEngine.SimContext, this);
    }
}
