// EjectPilotDie - Die-batch port to the frozen module contract (api-freeze-v1 §3/§5,
// template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: generals-gpl GeneralsMD EjectPilotDie.cpp/.h (GPL semantics only;
// this is fresh code). Behavior facts used:
//   - onDie(): if the die mux does not apply, do nothing. Otherwise resolve the damage
//     dealer from DamageInfo.in.m_sourceID (may be absent), pick AirCreationList when the
//     dying object isSignificantlyAboveTerrain() and GroundCreationList otherwise, and run
//     the static ejectPilot() helper with it.
//   - ejectPilot(ocl, dyingObject, damageDealer): a null list or a null dying object is a
//     silent no-op ("it's OK for damageDealer to be null"); otherwise
//     ObjectCreationList::create(ocl, dyingObject, damageDealer), then two audio events at
//     the DYING object's position - the per-unit "VoiceEject" (stamped with the dying
//     object's controlling player) and the per-unit "SoundEject".
//   - MUTABLE SIM STATE INVENTORY: EMPTY. The GPL class declares no members and its xfer()
//     writes a version and chains to the base - the whole module is a pure reaction to a
//     death event. Xfer below is therefore version-only, and that is the complete walk, not
//     an omission (see EjectPilotDie.md, "state inventory").
//   - InvulnerableTime is parsed by the GPL ModuleData and read by nothing: the field that
//     actually grants invulnerability is the identically-named one on the OCL's
//     GenericObjectCreationNugget. It is parsed here (audited vocabulary) and deliberately
//     unconsumed, exactly as the original leaves it.
//
// VeterancyLevels belongs to the shared die mux (GPL DieMuxData), not to this class; the
// fork happens to carry it on this ModuleData, so it is evaluated here rather than moved -
// moving it is a batch-wide change, filed as a finding.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class EjectPilotDie : DieModule
{
    /// <summary>The per-unit sound keys the original stamps on the dying object.</summary>
    private const string VoiceEjectSound = "VoiceEject";
    private const string SoundEjectSound = "SoundEject";

    private readonly EjectPilotDieModuleData _data;

    // ---- mutable sim state: NONE. See the header note; Xfer is version-only. ----

    public EjectPilotDie(GameObject gameObject, ISimContext context, EjectPilotDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        // The die mux's death-type/status gate already ran in DieModule.OnDie. The veterancy
        // half of that gate lives on this data class in the fork, so it is applied here.
        if (_data.VeterancyLevels?.Get(GameObject.Rank) == false)
        {
            return;
        }

        // GPL: TheGameLogic->findObjectByID(damageInfo->in.m_sourceID). A dealer that has
        // already left the world resolves to null, which the original explicitly allows.
        var damageDealer = damageInput.SourceID.IsValid
            ? Context.GameLogic.GetObjectById(damageInput.SourceID)
            : null;

        var creationList = Context.Terrain.IsSignificantlyAboveTerrain(GameObject)
            ? _data.AirCreationList
            : _data.GroundCreationList;

        EjectPilot(creationList, damageDealer);
    }

    /// <summary>
    /// The original's static <c>EjectPilotDie::ejectPilot</c>, kept as one method because it
    /// is the shared shape a slow-death update also reaches for (GPL HelicopterSlowDeathUpdate
    /// calls it rather than ObjectCreationList::create, precisely to get the two sounds).
    /// </summary>
    private void EjectPilot(LazyAssetReference<ObjectCreationList> list, GameObject damageDealer)
    {
        var creationList = list?.Value;
        if (creationList == null)
        {
            // "if (!ocl || !dyingObject) return" - no list configured for this branch is a
            // silent no-op, not an error. Notably the sounds do NOT play in that case.
            return;
        }

        Context.GameLogic.CreateFromObjectCreationList(creationList, GameObject, damageDealer);

        // Outputs only, never sim inputs (S8): both events are anchored to the DYING object,
        // which is what carries the VoiceEject/SoundEject entries and the controlling player
        // the original stamps on the voice event.
        Context.Events.FireUnitSoundAtObject(VoiceEjectSound, GameObject.Id);
        Context.Events.FireUnitSoundAtObject(SoundEjectSound, GameObject.Id);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // The state inventory is empty, so the walk is the version stamp alone. The module still
    // joins the Objects channel: its presence and its disappearance on death are themselves
    // observable in the walk.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9; template rule D-9: a port
    // that replaces an existing module KEEPS its Load and remaps it). The original's xfer is
    // version + base chain, and this matches it byte for byte. ----
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
/// Ejects a pilot (an ObjectCreationList) when the owner dies, choosing the air or the ground
/// list by altitude. Also the reason SoundEject and VoiceEject are meaningful keys in an
/// object's UnitSpecificSounds section.
/// </summary>
[SimDataAudited]
public sealed class EjectPilotDieModuleData : DieModuleData
{
    internal static EjectPilotDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<EjectPilotDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<EjectPilotDieModuleData>
        {
            { "GroundCreationList", (parser, x) => x.GroundCreationList = parser.ParseObjectCreationListReference() },
            { "AirCreationList", (parser, x) => x.AirCreationList = parser.ParseObjectCreationListReference() },
            { "InvulnerableTime", (parser, x) => x.InvulnerableTime = parser.ParseDurationLogicFrames() },
            { "VeterancyLevels", (parser, x) => x.VeterancyLevels = parser.ParseEnumBitArray<VeterancyLevel>() },
        });

    /// <summary>List run when the owner dies on (or near) the ground; null = eject nothing.</summary>
    public LazyAssetReference<ObjectCreationList> GroundCreationList { get; private set; }

    /// <summary>List run when the owner dies significantly above terrain; null = eject nothing.</summary>
    public LazyAssetReference<ObjectCreationList> AirCreationList { get; private set; }

    /// <summary>
    /// Milliseconds in INI, ceil-quantized to frames at parse (S5). Parsed and deliberately
    /// unconsumed: the original parses this field here and never reads it - the live one is
    /// the OCL nugget's own InvulnerableTime. Kept so the vocabulary audit is honest about
    /// what the data says.
    /// </summary>
    public LogicFrameSpan InvulnerableTime { get; private set; }

    /// <summary>
    /// Veterancy ranks this die module runs for; null = every rank (the original's
    /// VETERANCY_LEVEL_FLAGS_ALL default). Part of the shared die mux upstream.
    /// </summary>
    public BitArray<VeterancyLevel> VeterancyLevels { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new EjectPilotDie(gameObject, gameEngine.SimContext, this);
    }
}
