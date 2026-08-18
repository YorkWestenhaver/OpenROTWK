// CreateCrateDie - Round-5 Die batch, class 5 of 11 (experiment-round-4 §4).
//
// Behavioral reference: generals-gpl GeneralsMD CreateCrateDie.cpp/.h + CrateSystem.cpp
// (GPL semantics reference only; this is fresh code against the frozen contract).
//
// MUTABLE STATE INVENTORY (written before any code, template v1.1 runbook step 1):
//   NONE. The GPL class has no members at all - every decision is taken inside one onDie
//   call from the module data, the crate template, and three RNG draws. Its xfer is a
//   version byte extending the base, and so is ours. A stateless module is still a full
//   contract citizen: it overrides HasSimXfer, joins the Objects channel walk, and its
//   version byte is what makes a future field addition a detectable format change.
//
// THE DRAW SEQUENCE IS THE BEHAVIOUR (batch note: crate chance draws via
// Context.GameLogicRandom ONLY, never GameLogic.Random; draw count is conformance channel 5).
// Per death, walking the module's crate-template list in declaration order:
//   draw 1  creation chance, taken for EVERY template that resolves, before any other test,
//           so a failing condition below it still consumes the draw (GPL comment: "always
//           test this");
//   draw 2  the one-of-n weighted crate pick, taken only once every condition passed;
//   draw 3  the new crate's facing, taken only once a crate type resolved.
// A template that does not resolve (dangling CrateData name - AotR ships one, see the doc)
// consumes no draws at all. Reordering, adding or skipping any of these is a channel-5
// divergence even when the crates that appear happen to match.
//
// GPL semantics implemented here:
//   - DieModule's DeathTypes/status filter runs first (the base class does it).
//   - An ALLIED killer cancels everything, once, before the template loop: "no crate for
//     killing ally at all". A null killer (script kill, terrain) is not allied and passes.
//   - VeterancyLevel tests the VICTIM's level for EQUALITY (LEVEL_INVALID = do not test).
//   - KilledByType is a KindOf mask and the killer must carry ALL of its bits; a null killer
//     fails the test outright.
//   - KillerScience must be held by the killer's controlling player; a null killer fails.
//   - OwnedByMaker hands the crate to the dead object's player's DEFAULT team.
//
// DELIBERATE DEVIATIONS (facts recorded in research/die/CreateCrateDie.md):
//   - GPL scatters the crate with ThePartitionManager->findPositionAround (5-unit ignore-units
//     scan, 125-unit fallback). No fixed-point analogue exists yet, so the crate is created at
//     the victim's feet and "a spot was found" is treated as always true. This changes WHERE a
//     crate lands, never WHETHER one does, and keeps the draw sequence identical.
//   - AIUpdate::notifyCrate for computer-player killers is not called: the AIUpdate sub-surface
//     is deliberately unfrozen (api-freeze-v1 §7) and no AI module is ported.
//   - The crate drawable's terrain decal is client-side output, outside this contract.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CreateCrateDie : DieModule
{
    private readonly CreateCrateDieModuleData _data;

    public CreateCrateDie(GameObject gameObject, ISimContext context, CreateCrateDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        var killer = Context.GameLogic.GetObjectById(damageInput.SourceID);

        // "Nope, no crate for killing ally at all." - one test, before the loop, so an allied
        // kill consumes no draws either.
        if (killer is not null && KillerIsAlliedWithVictim(killer))
        {
            return;
        }

        foreach (var reference in _data.CrateDatas)
        {
            var crateData = reference?.Value;
            if (crateData is null)
            {
                // GPL findCrateTemplate returned NULL: the whole entry is skipped silently,
                // before the creation-chance draw.
                continue;
            }

            // draw 1 - always taken for a resolved template.
            if (Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.One) >= crateData.CreationChance)
            {
                continue;
            }

            if (crateData.VeterancyLevel is { } required &&
                required != GameObject.ExperienceTracker.VeterancyLevel)
            {
                continue;
            }

            if (crateData.KilledByType is { AnyBitSet: true } killedByType &&
                !KillerHasAllKinds(killer, killedByType))
            {
                continue;
            }

            if (crateData.KillerScience is not null &&
                !KillerHasScience(killer, crateData.KillerScience.Value))
            {
                continue;
            }

            var crate = CreateCrate(crateData);
            if (crate is not null && crateData.OwnedByMaker)
            {
                // "Design needs to set ownership of crates sometimes."
                crate.Team = GameObject.Owner?.DefaultTeam;
            }
        }
    }

    /// <summary>
    /// One-of-n weighted pick, then the spawn. Returns null when the pick resolved to no
    /// object definition (a designer whose chances do not sum to 1 gets exactly that) -
    /// after the draw has already been taken, as in the reference.
    /// </summary>
    private GameObject CreateCrate(CrateData crateData)
    {
        // draw 2 - the contiguous-percentage walk over the template's weighted entries.
        var pick = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.One);
        var runningTotal = Fix64.Zero;
        LazyAssetReference<ObjectDefinition> chosen = null;

        foreach (var candidate in crateData.CrateObjects)
        {
            runningTotal += candidate.Probability;
            if (runningTotal > pick)
            {
                chosen = candidate.Object;
                break;
            }
        }

        var definition = chosen?.Value;
        if (definition is null)
        {
            return null;
        }

        // draw 3 - the crate's facing, in radians. Taken only once a crate type resolved,
        // matching the reference's "spot found" gate (see the deviation note in the header).
        var orientation = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.PiTimes2);

        return Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject, orientation);
    }

    /// <summary>
    /// GPL <c>killer-&gt;getRelationship(me) == ALLIES</c>. <see cref="GameObject.GetRelationship"/>
    /// is the direct analogue, but OpenSAGE only populates the team/player relationship tables
    /// from map and script data, so an in-game object usually reads NEUTRAL against its own
    /// side. The player-level alliance set is therefore consulted too - the same realization
    /// of "allied" the pilot used - which is what makes "you get no salvage from your own
    /// unit" true here rather than only in a scripted map.
    /// </summary>
    private bool KillerIsAlliedWithVictim(GameObject killer)
    {
        if (killer.GetRelationship(GameObject) == RelationshipType.Allies)
        {
            return true;
        }

        var victimOwner = GameObject.Owner;
        var killerOwner = killer.Owner;
        if (victimOwner is null || killerOwner is null)
        {
            return false;
        }

        return killerOwner == victimOwner || killerOwner.Allies.Contains(victimOwner);
    }

    /// <summary>
    /// GPL isKindOfMulti(mustBeSet: the mask, mustBeClear: none) - EVERY bit of the mask has
    /// to be present, not merely one of them.
    /// </summary>
    private static bool KillerHasAllKinds(GameObject killer, BitArray<ObjectKinds> required)
    {
        if (killer is null)
        {
            return false;
        }

        return killer.Definition.KindOf?.CountIntersectionBits(required) == required.NumBitsSet;
    }

    private static bool KillerHasScience(GameObject killer, Science science)
    {
        return killer?.Owner?.HasScience(science) == true;
    }

    // ---- the single walk (§3/§4). No mutable state exists, so the walk is the version
    // byte alone: it still pins the format, and the shadow-copy test still proves the
    // round trip. ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9), kept and unchanged per
    // template v1.1 D-9: this port replaces an existing module, so its .sav layout survives
    // until the save system migrates onto the Xfer walk. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). The quantized vocabulary of
// this class lives in the CrateData asset it points at; the module data itself is a list
// of crate-template names, which is why the parse entry APPENDS (GPL m_crateNameList
// push_back) instead of overwriting: "CrateData = X" twice means two templates, and the
// old single-reference shape silently dropped the first.
// ============================================================================
[SimDataAudited]
public sealed class CreateCrateDieModuleData : DieModuleData
{
    internal static CreateCrateDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CreateCrateDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<CreateCrateDieModuleData>
        {
            { "CrateData", (parser, x) => x.CrateDatas.Add(parser.ParseCrateReference()) }
        });

    /// <summary>
    /// Crate templates tried in declaration order on every death; each one that resolves
    /// costs one logic-RNG draw. Order is load-bearing and never sorted.
    /// </summary>
    public List<LazyAssetReference<CrateData>> CrateDatas { get; } = [];

    internal override CreateCrateDie CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CreateCrateDie(gameObject, gameEngine.SimContext, this);
    }
}
