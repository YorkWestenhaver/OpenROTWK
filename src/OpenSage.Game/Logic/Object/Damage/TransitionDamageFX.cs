// TransitionDamageFX - Round-7 Damage-batch port (full task packet, experiment-round-4 §4.1).
//
// Behavioral reference: generals-gpl GeneralsMD Damage/TransitionDamageFX.cpp/.h (GPL
// semantics reference only; this is fresh code against the frozen contract). Behavior facts
// used:
//   - onBodyDamageStateChange(damageInfo, oldState, newState): fires a set of effects for the
//     NEW damage state, but ONLY when the transition is to a WORSE state
//     (IS_CONDITION_WORSE(newState, oldState); healing/improvement fires nothing).
//   - the effects fired are keyed by newState alone (not by the (old,new) pair), so a hit
//     that skips a state - Pristine -> ReallyDamaged in one blow - still fires the reached
//     state's effects. There are three effect kinds per state: FXLists, particle systems,
//     and (ZH-only) OCLs.
//   - each effect kind has its own damage-type gate: an FXList fires only if the last damage's
//     DamageType is set in DamageFXTypes; a particle system only if it is set in
//     DamageParticleTypes. The original seeds all three masks to ALL bits (ctor flip of NONE),
//     so an unconfigured module fires on every damage type; a null (unspecified) mask here
//     means "all pass", matching that default.
//   - the original ALSO destroys the emitters it created for the state being LEFT, which is
//     why it keeps m_particleSystemID[state][slot]. Those ids are CLIENT particle-manager
//     handles, assigned client-side and identical on no two peers; they are pure render
//     lifecycle, never sim state. Keeping them in the sim CRC would desync by construction
//     (F9/S8), so this port holds NO particle-system id: the emitter's lifetime is the
//     client's (finding F-TDF-1). The effect firing is an ISimEvents output (S8) - it leaves
//     the sim and never re-enters, so it carries no determinism obligation.
//
// Not [SimState]: OnBodyDamageStateChange rides the legacy float DamageInfo callback surface
// (DamageInfoInput.Amount et al.), which cannot be marked [SimState] until the Body-batch
// flag-day (amendments A2) migrates the DamageModule callback surface to Fix64 - the same
// straddle the S1-landed ActiveBody and the R7 Body ports record as their finding F-2. This
// class adds NO float field and NO mutable state of its own; its whole output is a set of
// client-bound events.
//
// BFME2-only INI additions (RubbleNeighbor, Pristine/Damaged/ReallyDamaged Show/HideSubObject)
// select which sub-objects/geometry the client shows per state; they are parsed (audited
// vocabulary) but have no GPL reference for their runtime effect and are a client-render
// concern, so they are deliberately not acted on here (finding F-TDF-3).
//
// F-R7-3 (carried): the per-state OCL slot (DamagedOCL1 / ReallyDamagedOCL1 / RubbleOCL1) is a
// gap this port left open - the parse table had no OCL keys at all, though GPL's per-state slot
// array covers FXLists, particle systems, AND OCLs alike. Unlike the other two effect kinds, the
// GPL OCL effect (ObjectCreationList::create) SPAWNS SIM OBJECTS, so acting on it is sim-affecting
// and out of scope for a parse-only fix. This pass adds the parse-side keys/fields (audited, same
// Loc:/OCL: attribute shape as the existing FXList slots) so the data round-trips and the gapmap
// stays byte-identical (every corpus occurrence is commented out); OnBodyDamageStateChange
// deliberately does NOT spawn from it yet - that is scheduled with the OCL/BoneFXUpdate round.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FX;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Mathematics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

public sealed class TransitionDamageFX : DamageModule
{
    private readonly TransitionDamageFXModuleData _data;

    // ---- mutable sim state: NONE ----
    // The original's m_particleSystemID array is client render-lifecycle state (F-TDF-1),
    // not sim state. This module carries no field into the Xfer walk.

    public TransitionDamageFX(GameObject gameObject, ISimContext context, TransitionDamageFXModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    public override void OnBodyDamageStateChange(in DamageInfo damageInfo, BodyDamageType oldState, BodyDamageType newState)
    {
        // GPL IS_CONDITION_WORSE(newState, oldState): only a worsening transition fires
        // effects (higher BodyDamageType ordinal = worse condition).
        if (!newState.IsWorseThan(oldState))
        {
            return;
        }

        // The effect set is keyed by the reached state alone (GPL indexes m_fxList[newState]).
        var fxList = FXListForState(newState);
        var particleSystems = ParticleSystemsForState(newState);

        // GPL reads getBodyModule()->getLastDamageInfo() for the type gate; at this call site
        // that is exactly the transition's own damage (ActiveBody set it immediately before
        // dispatching), so the passed-in damageInfo is the last damage.
        var damageType = damageInfo.Request.DamageType;

        if (fxList?.FXList != null && PassesTypeGate(_data.DamageFXTypes, damageType))
        {
            // GPL does FXList::doFXPos(fx, &worldPos): position only, unoriented. The
            // bone-relative offset in the FXLocInfo is a client placement detail resolved on
            // the far side of the seam.
            Context.Events.FireFXAtObjectPosition(fxList.FXList.Value.Name, GameObject.Id);
        }

        if (particleSystems != null && PassesTypeGate(_data.DamageParticleTypes, damageType))
        {
            foreach (var particleSystem in particleSystems)
            {
                if (particleSystem.ParticleSystem == null)
                {
                    continue;
                }

                Context.Events.FireParticleSystemAtObject(
                    particleSystem.ParticleSystem.Value.Name,
                    GameObject.Id,
                    particleSystem.Bone,
                    particleSystem.RandomBone);
            }
        }
    }

    private TransitionDamageFXList FXListForState(BodyDamageType state) => state switch
    {
        BodyDamageType.Damaged => _data.DamagedFXList1,
        BodyDamageType.ReallyDamaged => _data.ReallyDamagedFXList1,
        BodyDamageType.Rubble => _data.RubbleFXList1,
        _ => null,
    };

    private List<TransitionDamageParticleSystem> ParticleSystemsForState(BodyDamageType state) => state switch
    {
        BodyDamageType.Damaged => _data.DamagedParticleSystems,
        BodyDamageType.ReallyDamaged => _data.ReallyDamagedParticleSystems,
        BodyDamageType.Rubble => _data.RubbleParticleSystems,
        _ => null,
    };

    /// <summary>
    /// GPL getDamageTypeFlag(mask, type): fire when the type is set. A null mask is the
    /// unconfigured default, which the original seeds to ALL bits, so null = pass.
    /// </summary>
    private static bool PassesTypeGate(BitArray<DamageType> mask, DamageType damageType)
        => mask == null || mask.Get(damageType);

    // ---- the single walk (§3/§4): version only. This class has no mutable sim state, so the
    // walk carries nothing but its own version layer (GPL's TransitionDamageFX::xfer wrote the
    // client particle-system id array, which is deliberately not sim state here - F-TDF-1).
    // The shadow-copy base test still exercises the walk's symmetry. Field order = OUR choice
    // (F9); there is no Quantum-class field, so ruling A3 is vacuously satisfied.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept per the template's
    // replace-an-existing-module rule (D-9). The retail stream wrote the 48-entry
    // (BODYDAMAGETYPE_COUNT x DAMAGE_MODULE_MAX_FX) client particle-system id array after the
    // base; we consume and discard those bytes so the reader stays byte-compatible with saves
    // that already exist, but store nothing (F-TDF-1). ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        const int legacyParticleIdCount = 4 * 12; // BODYDAMAGETYPE_COUNT * DAMAGE_MODULE_MAX_FX
        reader.SkipUnknownBytes(sizeof(uint) * legacyParticleIdCount);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2).
//
// Audit note: this ModuleData has NO magnitude field to quantize. Every field is either a
// client-render selector (FXList / particle-system references, bone names, sub-object names),
// a damage-type mask (BitArray<DamageType>, an enum set, not a magnitude), or a bone-relative
// placement offset (Vector3, pure client draw geometry). There is therefore no Fix64 /
// LogicFrameSpan / angle to convert; the S5 quantizing functions have nothing to bite on
// here, exactly like FXListDie. Recorded, not skipped. The parse-table KEY coverage is
// unchanged from the pre-port table (gapmap G1 byte-identical).
// ============================================================================
[SimDataAudited]
public sealed class TransitionDamageFXModuleData : DamageModuleData
{
    internal static TransitionDamageFXModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<TransitionDamageFXModuleData> FieldParseTable = new IniParseTable<TransitionDamageFXModuleData>
    {
        { "DamageFXTypes", (parser, x) => x.DamageFXTypes = parser.ParseEnumBitArray<DamageType>() },

        { "DamagedFXList1", (parser, x) => x.DamagedFXList1 = TransitionDamageFXList.Parse(parser) },

        { "ReallyDamagedFXList1", (parser, x) => x.ReallyDamagedFXList1 = TransitionDamageFXList.Parse(parser) },

        { "RubbleFXList1", (parser, x) => x.RubbleFXList1 = TransitionDamageFXList.Parse(parser) },

        // F-R7-3: per-state OCL slot, audited to the same Loc:/OCL: attribute shape as the
        // FXList slots above (ParseObjectCreationListReference, the shared OCL-reference parse
        // helper). Parse-only - see the file-header note on why OnBodyDamageStateChange does not
        // act on it yet.
        { "DamagedOCL1", (parser, x) => x.DamagedOcl1 = TransitionDamageOcl.Parse(parser) },
        { "ReallyDamagedOCL1", (parser, x) => x.ReallyDamagedOcl1 = TransitionDamageOcl.Parse(parser) },
        { "RubbleOCL1", (parser, x) => x.RubbleOcl1 = TransitionDamageOcl.Parse(parser) },

        { "DamageParticleTypes", (parser, x) => x.DamageParticleTypes = parser.ParseEnumBitArray<DamageType>() },

        { "DamagedParticleSystem1", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "DamagedParticleSystem2", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "DamagedParticleSystem3", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "DamagedParticleSystem4", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "DamagedParticleSystem5", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "DamagedParticleSystem6", (parser, x) => x.DamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },

        { "ReallyDamagedParticleSystem1", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem2", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem3", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem4", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem5", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem6", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem7", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "ReallyDamagedParticleSystem8", (parser, x) => x.ReallyDamagedParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },

        { "RubbleParticleSystem1", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem2", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem3", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem4", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem5", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem6", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },
        { "RubbleParticleSystem7", (parser, x) => x.RubbleParticleSystems.Add(TransitionDamageParticleSystem.Parse(parser)) },

        { "RubbleNeighbor", (parser, x) => x.RubbleNeighbors.Add(RubbleNeighbor.Parse(parser)) },
        { "PristineShowSubObject", (parser, x) => x.PristineShowSubObject = parser.ParseAssetReferenceArray() },
        { "PristineHideSubObject", (parser, x) => x.PristineHideSubObject = parser.ParseAssetReferenceArray() },
        { "DamagedShowSubObject", (parser, x) => x.DamagedShowSubObject = parser.ParseAssetReferenceArray() },
        { "DamagedHideSubObject", (parser, x) => x.DamagedHideSubObject = parser.ParseAssetReferenceArray() },
        { "ReallyDamagedHideSubObject", (parser, x) => x.ReallyDamagedHideSubObject = parser.ParseAssetReferenceArray() },
        { "ReallyDamagedShowSubObject", (parser, x) => x.ReallyDamagedShowSubObject = parser.ParseAssetReferenceArray() },
    };

    /// <summary>Damage types that enable the FXList effects; null = all types (GPL default).</summary>
    public BitArray<DamageType> DamageFXTypes { get; private set; }

    public TransitionDamageFXList DamagedFXList1 { get; private set; }

    public TransitionDamageFXList ReallyDamagedFXList1 { get; private set; }

    public TransitionDamageFXList RubbleFXList1 { get; private set; }

    /// <summary>
    /// Per-state OCL slot (F-R7-3, parse-only). GPL's OCL effect spawns sim objects and is
    /// therefore not driven by <see cref="TransitionDamageFX.OnBodyDamageStateChange"/> yet -
    /// see the file-header note; deferred to the OCL/BoneFXUpdate round.
    /// </summary>
    public TransitionDamageOcl DamagedOcl1 { get; private set; }

    /// <inheritdoc cref="DamagedOcl1"/>
    public TransitionDamageOcl ReallyDamagedOcl1 { get; private set; }

    /// <inheritdoc cref="DamagedOcl1"/>
    public TransitionDamageOcl RubbleOcl1 { get; private set; }

    /// <summary>Damage types that enable the particle-system effects; null = all (GPL default).</summary>
    public BitArray<DamageType> DamageParticleTypes { get; private set; }

    public List<TransitionDamageParticleSystem> DamagedParticleSystems { get; } = new List<TransitionDamageParticleSystem>();

    public List<TransitionDamageParticleSystem> ReallyDamagedParticleSystems { get; } = new List<TransitionDamageParticleSystem>();

    public List<TransitionDamageParticleSystem> RubbleParticleSystems { get; } = new List<TransitionDamageParticleSystem>();

    [AddedIn(SageGame.Bfme)]
    public List<RubbleNeighbor> RubbleNeighbors { get; private set; } = new List<RubbleNeighbor>();

    [AddedIn(SageGame.Bfme)]
    public string[] PristineShowSubObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string[] PristineHideSubObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string[] DamagedShowSubObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string[] DamagedHideSubObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string[] ReallyDamagedShowSubObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string[] ReallyDamagedHideSubObject { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new TransitionDamageFX(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class TransitionDamageFXList
{
    internal static TransitionDamageFXList Parse(IniParser parser)
    {
        return new TransitionDamageFXList
        {
            Location = parser.ParseAttribute("Loc", () => parser.ParseVector3()),
            FXList = parser.ParseAttribute("FXList", parser.ScanFXListReference)
        };
    }

    public Vector3 Location { get; private set; }
    public LazyAssetReference<FXList> FXList { get; private set; }
}

/// <summary>
/// F-R7-3 (parse-only): a per-state OCL slot. Same shape as <see cref="TransitionDamageFXList"/>
/// (a Loc: placement plus one asset reference), but the referenced asset spawns sim objects
/// (<c>ObjectCreationList</c>) rather than a client-only effect, so it is not fired from
/// <see cref="TransitionDamageFX.OnBodyDamageStateChange"/> here - see the file-header note.
/// </summary>
public sealed class TransitionDamageOcl
{
    internal static TransitionDamageOcl Parse(IniParser parser)
    {
        return new TransitionDamageOcl
        {
            Location = parser.ParseAttribute("Loc", () => parser.ParseVector3()),
            OCL = parser.ParseAttribute("OCL", parser.ParseObjectCreationListReference)
        };
    }

    public Vector3 Location { get; private set; }
    public LazyAssetReference<ObjectCreationList> OCL { get; private set; }
}

public sealed class TransitionDamageParticleSystem
{
    internal static TransitionDamageParticleSystem Parse(IniParser parser)
    {
        return new TransitionDamageParticleSystem
        {
            Bone = parser.ParseAttribute("Bone", parser.ScanBoneName),
            RandomBone = parser.ParseAttributeBoolean("RandomBone"),
            ParticleSystem = parser.ParseAttribute("PSys", parser.ScanFXParticleSystemTemplateReference)
        };
    }

    public string Bone { get; private set; }
    public bool RandomBone { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> ParticleSystem { get; private set; }
}

[AddedIn(SageGame.Bfme)]
public sealed class RubbleNeighbor
{
    internal static RubbleNeighbor Parse(IniParser parser)
    {
        var result = new RubbleNeighbor();
        result.Offset = parser.ParseAttributeVector3("NeighborOffset");
        result.SubObjects.Add(parser.ParseAttributeIdentifier("SubObject"));
        result.SubObjects.Add(parser.ParseAttributeIdentifier("SubObject"));
        result.OCL = parser.ParseAttributeIdentifier("OCL");
        return result;
    }

    public Vector3 Offset { get; private set; }
    public List<string> SubObjects { get; } = new List<string>();
    public string OCL { get; private set; }
}
