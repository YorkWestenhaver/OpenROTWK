// CrushDie - Round-5 Die batch, class 6 of 11 (collision-sourced death).
//
// Behavioral reference: generals-gpl GeneralsMD CrushDie.cpp/.h (GPL semantics reference
// only; this is fresh code against the frozen contract). Behavior facts used:
//   - onDie is gated by the shared DieLogicData mux (DeathTypes / RequiredStatus /
//     ExemptStatus) and then by a HARD damage-type gate: CrushDie acts only on
//     DamageType.Crush and returns silently on anything else.
//   - the damage dealer is looked up by DamageInfo source id. If it is gone (dealt the
//     crush and was itself destroyed in the same frame), the crush degenerates to
//     TOTAL_CRUSH rather than being skipped.
//   - crushLocationCheck picks the crush point CLOSEST to the crusher among the victim's
//     three candidate points (centre, front, back), where front/back sit one half
//     major-radius along the victim's own facing. Points already crushed are not
//     re-offered, and hitting the remaining point of a half-crushed victim upgrades the
//     result to TOTAL_CRUSH. PhysicsCollide has already decided that a crush happens; this
//     only decides WHERE.
//   - a per-crush-point sound may play, gated by a per-crush-point percentage rolled on the
//     LOGIC random stream (GPL: "does not need to be synced, but having it so makes
//     searches so much nicer" - so the draw is part of the lockstep stream and is counted
//     by conformance channel 5 whether or not anything is audible).
//   - the result is written to the victim's Body (FrontCrushed / BackCrushed) and mirrored
//     into the FRONTCRUSHED / BACKCRUSHED model conditions, both flags always assigned
//     (GPL clears the two-flag mask and sets the new value in one call).
//
// MUTABLE SIM STATE INVENTORY (written before any code, template v1.1 runbook step 1):
//   *** EMPTY ***. GPL CrushDie declares no members; its xfer is a version tag extending
//   the base. Everything this module computes is written straight into the victim's Body
//   and Drawable. The Xfer walk below is therefore version-only - complete with respect to
//   the inventory - and the crush FLAGS it sets are unwitnessed by the harness until
//   ActiveBody ports (finding C-2 in research/die/CrushDie.md).
//
// BFME2 note: no INI in the AotR 9.3.1 / RotWK / BFME2 corpus instantiates CrushDie
// (1,673 INI files scanned across all three trees; zero hits). The class is retained
// because the fork's PhysicsBehavior crush path and the CrushType enum below depend on it,
// and because ZH-era data does use it. See CrushDie.md §1.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CrushDie : DieModule
{
    private readonly CrushDieModuleData _data;

    // ---- mutable sim state: NONE (see the inventory above) ----

    public CrushDie(GameObject gameObject, ISimContext context, CrushDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        // The Die mux (DeathTypes / status) already passed in DieModule. CrushDie adds a
        // second, non-negotiable gate: this module exists for crush damage only.
        if (damageInput.DamageType != DamageType.Crush)
        {
            return;
        }

        // A crusher that died in the same frame leaves no object to measure against; GPL
        // degenerates to a total crush rather than skipping the effect.
        var damageDealer = damageInput.SourceID.IsValid
            ? Context.GameLogic.GetObjectById(damageInput.SourceID)
            : null;

        var crushType = damageDealer is not null
            ? CrushLocationCheck(damageDealer, GameObject)
            : CrushType.TotalCrush;

        if (crushType == CrushType.NoCrush)
        {
            return;
        }

        PlayCrushSound(crushType);
        ApplyCrushedState(crushType);
    }

    /// <summary>
    /// The percentage roll. The audio itself is an output with no determinism obligation
    /// (S8), but the DRAW is on the logic stream and is therefore load-bearing: it must
    /// happen exactly when GPL's happens (crush point resolved, sound name non-empty) and
    /// never otherwise, or conformance channel 5 diverges on draw count alone.
    /// </summary>
    private void PlayCrushSound(CrushType crushType)
    {
        var soundName = _data.CrushSound(crushType);
        if (string.IsNullOrEmpty(soundName))
        {
            return;
        }

        // GPL: GameLogicRandomValue(0, 99) < percent, so 0 == never and 100 == always.
        if (Context.GameLogicRandom.Next(0, 99) >= _data.CrushSoundPercent(crushType))
        {
            return;
        }

        // The event would be emitted here. ISimEvents has no audio member yet and a porting
        // task may not grow the framework (experiment-round-4 §4.1 standing rules), so the
        // request is filed as finding C-1 instead; the roll above is what the simulation
        // actually owes, and it is paid.
    }

    private void ApplyCrushedState(CrushType crushType)
    {
        var frontCrushed = crushType is CrushType.TotalCrush or CrushType.FrontEndCrush;
        var backCrushed = crushType is CrushType.TotalCrush or CrushType.BackEndCrush;

        // Body carries the two flags the NEXT crush reads back (see CrushLocationCheck):
        // this is the module's only durable effect, and it lives on unported substrate.
        var body = GameObject.BodyModule;
        if (body is not null)
        {
            body.FrontCrushed = frontCrushed;
            body.BackCrushed = backCrushed;
        }

        // Client mirror: GPL clears the two-flag mask and sets the new value in one call,
        // so both flags are always assigned, never OR-ed in.
        GameObject.ModelConditionFlags.Set(ModelConditionFlag.FrontCrushed, frontCrushed);
        GameObject.ModelConditionFlags.Set(ModelConditionFlag.BackCrushed, backCrushed);
    }

    /// <summary>
    /// Which of the victim's crush points the crusher is nearest. Pure function of the two
    /// objects' geometry - no state is read or written - so it is static.
    /// </summary>
    private static CrushType CrushLocationCheck(GameObject crusher, GameObject victim)
    {
        var body = victim.BodyModule;
        if (body is null)
        {
            return CrushType.NoCrush;
        }

        var frontCrushed = body.FrontCrushed;
        var backCrushed = body.BackCrushed;

        // The three geometry reads, quantized once each at the substrate boundary (D-7).
        var victimDirection = CrushGeometry.UnitDirection2D(victim);
        var crusherPosition = CrushGeometry.Position(crusher);
        var victimPosition = CrushGeometry.Position(victim);

        // Crush points sit half a major radius fore and aft along the VICTIM's facing.
        var crushPointOffsetDistance = CrushGeometry.MajorRadius(victim) * Fix64.Half;
        var offsetX = victimDirection.X * crushPointOffsetDistance;
        var offsetY = victimDirection.Y * crushPointOffsetDistance;

        var result = CrushType.NoCrush;

        // GPL's literal sentinel, kept verbatim: it is compared against SQUARED distances,
        // so a crusher more than ~316 units from a half-crushed victim's remaining point
        // yields NO_CRUSH. Reachable only if something calls this outside a physics
        // overlap, which the collide path never does - reproduced, not "fixed" (behavior
        // note B-1 in CrushDie.md).
        var bestDistance = (Fix64)99999L;

        if (!frontCrushed && !backCrushed)
        {
            // The middle crush point, i.e. the victim's own position.
            result = CrushType.TotalCrush;
            bestDistance = DistanceSquared2D(victimPosition.X, victimPosition.Y, crusherPosition);
        }

        if (!frontCrushed)
        {
            var distance = DistanceSquared2D(victimPosition.X + offsetX, victimPosition.Y + offsetY, crusherPosition);
            if (distance < bestDistance)
            {
                // Crushing the front of an already-back-crushed victim finishes it off.
                result = backCrushed ? CrushType.TotalCrush : CrushType.FrontEndCrush;
                bestDistance = distance;
            }
        }

        if (!backCrushed)
        {
            var distance = DistanceSquared2D(victimPosition.X - offsetX, victimPosition.Y - offsetY, crusherPosition);
            if (distance < bestDistance)
            {
                result = frontCrushed ? CrushType.TotalCrush : CrushType.BackEndCrush;
                // GPL updates bestDist here too; it is a dead store (nothing reads it after
                // the last block) and is omitted rather than silently carried.
            }
        }

        return result;
    }

    /// <summary>Squared planar distance - GPL compares squares and never takes a root.</summary>
    private static Fix64 DistanceSquared2D(Fix64 x, Fix64 y, in FixVector3 from)
    {
        var dx = x - from.X;
        var dy = y - from.Y;
        return (dx * dx) + (dy * dy);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);

        // Deliberately nothing else: the state inventory is empty (see the header). The
        // crushed flags this module writes live on the victim's ActiveBody and join the
        // walk when Body ports - finding C-2.
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per
    // template v1.1 D-9. GPL's xfer is version + base, and so is this. ----
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
/// Allows for the use of the FRONTCRUSHED and BACKCRUSHED condition states.
/// </summary>
[SimDataAudited]
public sealed class CrushDieModuleData : DieModuleData
{
    internal static CrushDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CrushDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<CrushDieModuleData>
        {
            { "TotalCrushSound", (parser, x) => x.TotalCrushSound = parser.ParseAssetReference() },
            { "BackEndCrushSound", (parser, x) => x.BackEndCrushSound = parser.ParseAssetReference() },
            { "FrontEndCrushSound", (parser, x) => x.FrontEndCrushSound = parser.ParseAssetReference() },
            { "TotalCrushSoundPercent", (parser, x) => x.TotalCrushSoundPercent = parser.ParseInteger() },
            { "BackEndCrushSoundPercent", (parser, x) => x.BackEndCrushSoundPercent = parser.ParseInteger() },
            { "FrontEndCrushSoundPercent", (parser, x) => x.FrontEndCrushSoundPercent = parser.ParseInteger() }
        });

    // S5 audit: every field here is either an asset-reference NAME (a string id, never a
    // sim quantity) or an integer PERCENTAGE compared against an integer die roll. Neither
    // class needs a quantizing parse function - ParseFix64Percentage would introduce a
    // fractional comparison the original never makes - so the audited vocabulary for this
    // class is "exact integers and names only". No [ParseOnly]: CreateModule is implemented.

    public string TotalCrushSound { get; private set; }
    public string BackEndCrushSound { get; private set; }
    public string FrontEndCrushSound { get; private set; }

    /// <summary>Chance in [0, 100] that the total-crush sound plays; GPL default 100.</summary>
    public int TotalCrushSoundPercent { get; private set; } = DefaultCrushSoundPercent;

    /// <summary>Chance in [0, 100] that the back-end-crush sound plays; GPL default 100.</summary>
    public int BackEndCrushSoundPercent { get; private set; } = DefaultCrushSoundPercent;

    /// <summary>Chance in [0, 100] that the front-end-crush sound plays; GPL default 100.</summary>
    public int FrontEndCrushSoundPercent { get; private set; } = DefaultCrushSoundPercent;

    /// <summary>GPL CrushDieModuleData's ctor seeds every percentage with 100 ("always").</summary>
    private const int DefaultCrushSoundPercent = 100;

    internal string CrushSound(CrushType crushType) => crushType switch
    {
        CrushType.TotalCrush => TotalCrushSound,
        CrushType.BackEndCrush => BackEndCrushSound,
        CrushType.FrontEndCrush => FrontEndCrushSound,
        _ => null,
    };

    internal int CrushSoundPercent(CrushType crushType) => crushType switch
    {
        CrushType.TotalCrush => TotalCrushSoundPercent,
        CrushType.BackEndCrush => BackEndCrushSoundPercent,
        CrushType.FrontEndCrush => FrontEndCrushSoundPercent,
        _ => 0,
    };

    internal override CrushDie CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CrushDie(gameObject, gameEngine.SimContext, this);
    }
}

public enum CrushType
{
    TotalCrush,
    BackEndCrush,
    FrontEndCrush,
    NoCrush,
}
