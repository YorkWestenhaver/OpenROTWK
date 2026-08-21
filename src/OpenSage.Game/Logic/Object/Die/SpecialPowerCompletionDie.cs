// SpecialPowerCompletionDie - the one GREENFIELD [ParseOnly] class of the Die batch
// (experiment-round-4 §4 row 11): the fork had a 16-line ModuleData and no runtime module,
// so there is no legacy module to replace and - per template v1.1 delta D-9 - no retail
// `.sav` Load to keep and remap. This file is the whole class.
//
// Behavioral reference: generals-gpl GeneralsMD SpecialPowerCompletionDie.cpp/.h (GPL
// semantics reference only; this is fresh code against the frozen contract). Behavior facts
// used, all of them:
//   - mutable state is exactly { creatorID, creatorSet }; ctor sets INVALID_ID / false.
//   - setCreator(id) is LATCHING: the FIRST call wins and every later call is ignored,
//     INCLUDING a first call carrying INVALID_ID. That is not a quirk to smooth over - it
//     is how the creator-assignment sites suppress the notification for all but one member
//     of a formation/payload (ObjectCreationList DeliverPayloadNugget assigns the real
//     creator to formation index 0 / payload 0 and INVALID_ID to every other; Weapon's
//     projectile path assigns INVALID_ID to the projectile when the firing object already
//     carries a completion die, because that object has already reported).
//   - onDie -> notifyScriptEngine(), gated by the shared Die applicability filter
//     (DeathTypes / RequiredStatus / ExemptStatus), which DieModule.OnDie already applies.
//   - notifyScriptEngine() reports ONLY when creatorID is valid, and reports
//     (owning player's index, SpecialPowerTemplate name, creatorID). It is public because
//     the Weapon projectile path calls it directly, at fire time rather than at death.
//   - the class has no update, no RNG draw, no radius query, no FX: it is a pure
//     state-latch + report module. Its GPL crc() extends the base only; its xfer() is
//     version 1 + base + the two fields.
//
// Deviations from the reference, deliberate and recorded (see the deliverable doc):
//   - GPL dereferences m_specialPowerTemplate unconditionally inside notifyScriptEngine
//     (a null SpecialPowerTemplate is a crash there). We hold the template as its INI asset
//     NAME - which is the only thing the notification carries - and skip the report when it
//     is absent, because a parse-layer omission must not be a sim crash.
//   - the report goes to ISimContext.GameLogic.NotifyOfCompletedSpecialPower; OpenSAGE has
//     no ported script engine, so the SimContext adapter holds the log (SPCD-1).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SpecialPowerCompletionDie : DieModule
{
    private readonly SpecialPowerCompletionDieModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>
    /// The object credited with completing the special power, reported to the script
    /// engine at death. Invalid means "nobody cares that we are going" (GPL's own words):
    /// the creator either was never assigned, or was deliberately assigned away.
    /// </summary>
    private ObjectId _creatorObjectId;

    /// <summary>
    /// Latch: true once <see cref="SetCreator"/> has run at all. Distinct from
    /// <see cref="_creatorObjectId"/> being valid, because assigning INVALID_ID is a
    /// meaningful, permanent act (it suppresses the report).
    /// </summary>
    private bool _creatorSet;

    internal SpecialPowerCompletionDie(GameObject gameObject, ISimContext context, SpecialPowerCompletionDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
        _creatorObjectId = ObjectId.Invalid;
        _creatorSet = false;
    }

    /// <summary>
    /// Assign the object credited with this special power. First call wins, permanently
    /// (GPL setCreator), so a spawn site may name the one member that reports and silence
    /// the rest by passing <see cref="ObjectId.Invalid"/>.
    /// </summary>
    public void SetCreator(ObjectId creatorObjectId)
    {
        if (_creatorSet)
        {
            return;
        }

        _creatorSet = true;
        _creatorObjectId = creatorObjectId;
    }

    /// <summary>
    /// Report the completion (GPL notifyScriptEngine). Public because the creator-assignment
    /// sites call it directly at fire time as well as via death.
    /// </summary>
    public void NotifyScriptEngine()
    {
        if (!_creatorObjectId.IsValid)
        {
            return;
        }

        if (string.IsNullOrEmpty(_data.SpecialPowerTemplate))
        {
            // GPL would dereference a null template here; a missing INI field is a data
            // problem, not a reason to take the simulation down.
            return;
        }

        Context.GameLogic.NotifyOfCompletedSpecialPower(
            Context.Players.GetPlayerIndex(GameObject.Owner),
            _data.SpecialPowerTemplate,
            _creatorObjectId);
    }

    /// <summary>
    /// The applicability filter has already passed (DieModule.OnDie), so this is GPL's
    /// onDie body exactly: report.
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput) => NotifyScriptEngine();

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): both fields are conformance channel 1 - an identity and a
    // lifecycle flag - so both are Exact on Target A AND Target B. This module declares no
    // Quantum field at all, which is worth stating: A3's Fix64 quantum question does not
    // arise here.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // DieModule carries no mutable sim state of its own (DieLogicData is parse-side
        // flyweight data), so there is no base walk to extend.
        xfer.XferObjectId("CreatorObjectId", ref _creatorObjectId);  // ch.1: Exact
        xfer.XferBool("CreatorSet", ref _creatorSet);                // ch.1: Exact
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class SpecialPowerCompletionDieModuleData : DieModuleData
{
    internal static SpecialPowerCompletionDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SpecialPowerCompletionDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<SpecialPowerCompletionDieModuleData>
        {
            { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() }
        });

    /// <summary>
    /// Name of the SpecialPower template this object's death completes. S5 audit: the
    /// class's only own field is an asset reference - there is no numeric, duration, angle
    /// or vector field to quantize, so no S5 parse function applies. The inherited
    /// DieLogicData fields (DeathTypes bit array, RequiredStatus / ExemptStatus enums) are
    /// likewise exact discrete vocabulary. GPL resolves this to a SpecialPowerTemplate
    /// pointer and then uses only getName(); the name IS the payload, so holding the name
    /// is not a shortcut.
    /// </summary>
    public string SpecialPowerTemplate { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpecialPowerCompletionDie(gameObject, gameEngine.SimContext, this);
    }
}
