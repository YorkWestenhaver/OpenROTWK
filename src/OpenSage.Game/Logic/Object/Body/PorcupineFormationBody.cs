// PorcupineFormationBody - Body-batch port to the frozen module contract (api-freeze-v1
// §3/§5, template v1.1 = pilot-autoheal §3/§6). Builds ON S1 (weapon/damage/armor): the
// thorn-reflect delivery goes through the landed DamagePipeline + CombatDamageInput; it does
// NOT reimplement any damage / armor / health math.
//
// Behavioral reference: BFME/BFME2-ONLY module - it does NOT exist in generals-gpl
// (Generals / GeneralsMD), so there is NO GPL source to read. Behavioral reference is the
// binary-derived spec only, clean-room. Data evidence (extracted AotR 2.02, the live install):
// PorcupineFormationBody is the "pike wall" body worn by spear/pike infantry - Gondor
// Spearmen, Isengard/Uruk Pikemen (map.ini bodies across helms deep / osgiliath / grey
// company / durins tower, weapon.ini:43237 PikemenPorcupineDamage /
// weapon.ini:43248 PikemenPorcupineCrushDamage). The three fields over ActiveBody:
//   DamageWeaponTemplate       = thorn weapon fired back at a melee attacker (AotR:
//                                PikemenPorcupineDamage - Damage 0, radius 5, SPECIALIST;
//                                the normal reflect is an FX / no-HP prick in shipped data);
//   CrushDamageWeaponTemplate  = weapon fired back at a would-be crusher the formation
//                                resists (AotR: PikemenPorcupineCrushDamage - real
//                                URUK_PIKE_PORCUPINE_DAMAGE to ENEMIES; the pikes gut the
//                                cavalry/monster that tried to trample them);
//   CrusherLevelResisted       = the crusher level this formation stops instead of being
//                                crushed (INI inline comment: 1 = infantry, 2 = trees,
//                                3 = vehicles; every shipped porcupine uses 2).
//
// MUTABLE SIM STATE INVENTORY: none of its own. The porcupine rule is a stateless reflex
// over ActiveBody's Fix64 health ledger; no field enters the Xfer walk (only the version
// wrapper over the base, GPL/ImmortalBody shape). The _reflecting reentrancy guard is
// transient WITHIN a single AttemptDamage call and is always false between frames, so it is
// deliberately NOT sim state and NOT xfered (see F-PFB-3).
//
// THE S1 SEAM (the crux of this port): "reflect a weapon back at the aggressor" lands at the
// AttemptDamage override - PorcupineFormationBody chains ActiveBody.AttemptDamage (which does
// all the armor/scalar/health work in the Fix64 core) and THEN, for a genuine attack from a
// valid foreign attacker, delivers the thorn weapon's DamageNugget(s) back at that attacker
// through DamagePipeline.DealDirectDamage - the exact public surface S1 froze for
// weapon/damage modules (weapon-damage-armor.md §1). This adds ZERO edits to the shared
// ActiveBody body (merge-hygiene): the whole mechanic is an additive override in this file.
//
// THE CRUSH HALF (recorded F-PFB-1): the crush-collision system is NOT landed -
// GameObject.CanCrushOrSquish is a TODO(Port) stub returning false, and PhysicsBehavior's
// crush path (CheckForOverlapCollision) therefore never crushes anyone yet. So the
// CrusherLevelResisted comparison and the CrushDamageWeaponTemplate fire have no live seam to
// be CALLED from. They are implemented and unit-tested here as the public entries
// (ResistsCrusherLevel / ReflectCrushAttempt) that the future crush system will invoke; the
// wiring is a one-line call from that system when it lands (finding, no contract change).

#nullable enable

using System.Collections.Generic;
using System.Linq;
using OpenSage.Content;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// The BFME "porcupine formation" (pike wall) body. Behaves like <see cref="ActiveBody"/>,
/// but reflects a configured thorn weapon back at melee attackers, and (once the crush
/// system lands) resists crushers up to <c>CrusherLevelResisted</c> while gutting them with
/// a crush weapon instead of being trampled.
/// </summary>
public sealed class PorcupineFormationBody : ActiveBody
{
    private readonly PorcupineFormationBodyModuleData _moduleData;

    // Transient within-frame guard: a reflected hit must never itself trigger a reflection
    // (two facing porcupines would otherwise recurse until the stack blows). Always false
    // between frames -> not sim state, not xfered (F-PFB-3).
    private bool _reflecting;

    internal PorcupineFormationBody(GameObject gameObject, IGameEngine gameEngine, PorcupineFormationBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// Chain the full ActiveBody damage resolution (armor/scalar/health, Fix64 core), then
    /// reflect the thorn weapon back at the attacker. Reflection happens after the base has
    /// resolved the hit so the porcupine's own damage/death is unaffected by it.
    /// </summary>
    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        var damageOutput = base.AttemptDamage(damageInput);

        if (!_reflecting && ShouldReflect(damageInput))
        {
            var attacker = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
            FireReflectionWeapon(_moduleData.DamageWeaponTemplate?.Value, attacker);
        }

        return damageOutput;
    }

    /// <summary>
    /// The thorn reflex only fires for a real melee-style attack from a valid FOREIGN
    /// attacker (a valid source object that is not this object itself). Healing and subdual
    /// are not attacks and never provoke the pikes. The finer predicate - whether the
    /// original also gates on melee vs. ranged, on a per-hit throttle (the AotR normal
    /// reflect nugget carries DelayTime 10), or on actual damage dealt - is spec-gated
    /// (F-PFB-2); this is the conservative "reacts to being attacked" reading.
    /// </summary>
    private bool ShouldReflect(in DamageInfoInput damageInput)
    {
        if (damageInput.DamageType == DamageType.Healing
            || damageInput.DamageType.IsSubdualDamage())
        {
            return false;
        }

        var sourceId = damageInput.SourceID;
        return sourceId.IsValid && sourceId != GameObject.Id;
    }

    /// <summary>
    /// Public entry for the (not-yet-landed) crush-collision system: does this formation
    /// resist a crusher of the given level rather than be crushed by it? BFME semantics -
    /// the formation stands firm against any crusher whose level is at or below
    /// <c>CrusherLevelResisted</c>.
    /// </summary>
    public bool ResistsCrusherLevel(int crusherLevel) => crusherLevel <= _moduleData.CrusherLevelResisted;

    /// <summary>
    /// Public entry for the crush-collision system (F-PFB-1): when a crusher this formation
    /// resists tries to trample it, the pikes gut the crusher instead. Fires the configured
    /// crush weapon back at the crusher through the S1 pipeline. Not yet CALLED by a landed
    /// seam - GameObject.CanCrushOrSquish is a stub - but fully implemented and tested here.
    /// </summary>
    public void ReflectCrushAttempt(GameObject? crusher)
    {
        if (!_reflecting)
        {
            FireReflectionWeapon(_moduleData.CrushDamageWeaponTemplate?.Value, crusher);
        }
    }

    /// <summary>
    /// Deliver every <see cref="DamageNugget"/> of <paramref name="weaponTemplate"/> back at
    /// <paramref name="target"/> through the landed S1 <see cref="DamagePipeline"/>. The
    /// porcupine is recorded as the damage source, so kills credit it. Guarded against
    /// reentrancy so a reflected hit cannot re-trigger a reflection.
    /// </summary>
    private void FireReflectionWeapon(WeaponTemplate? weaponTemplate, GameObject? target)
    {
        if (weaponTemplate == null || target == null || target.IsDestroyed || target == GameObject)
        {
            return;
        }

        _reflecting = true;
        try
        {
            foreach (var nugget in weaponTemplate.Nuggets.OfType<DamageNugget>())
            {
                var input = new CombatDamageInput
                {
                    SourceId = GameObject.Id,
                    DamageType = nugget.DamageType,
                    DeathType = nugget.DeathType,
                    Amount = nugget.Damage,
                };

                DamagePipeline.DealDirectDamage(target, input);
            }
        }
        finally
        {
            _reflecting = false;
        }
    }

    // ---- the contract Xfer walk: like GPL ImmortalBody/HiveStructureBody, this Body
    // subclass owns no mutable sim state of its own, so the walk is only its own version
    // wrapper (F9: declaration order, ours) over ActiveBody's walk. HasSimXfer inherited
    // (true) from ActiveBody. ----

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout (F9-exempt legacy reader): version wrapper + ActiveBody state,
        // no own field (porcupine adds no persistent state over ActiveBody).
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// The reflect-damage pike-wall body. Adds three fields over
/// <see cref="ActiveBodyModuleData"/>: the two reflect weapon templates and the crusher
/// level it resists.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class PorcupineFormationBodyModuleData : ActiveBodyModuleData
{
    internal static new PorcupineFormationBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-R7-2 / F-HB-1: the shadowing Parse MUST keep
        return result;                         // the base InitialHealth->MaxHealth default, else
    }                                          // a porcupine unit spawns at 0 HP.

    private static new readonly IniParseTable<PorcupineFormationBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<PorcupineFormationBodyModuleData>
        {
            { "DamageWeaponTemplate", (parser, x) => x.DamageWeaponTemplate = parser.ParseWeaponTemplateReference() },
            { "CrushDamageWeaponTemplate", (parser, x) => x.CrushDamageWeaponTemplate = parser.ParseWeaponTemplateReference() },
            { "CrusherLevelResisted", (parser, x) => x.CrusherLevelResisted = parser.ParseInteger() },
        });

    /// <summary>Thorn weapon fired back at a melee attacker.</summary>
    public LazyAssetReference<WeaponTemplate>? DamageWeaponTemplate { get; private set; }

    /// <summary>Weapon fired back at a would-be crusher the formation resists.</summary>
    public LazyAssetReference<WeaponTemplate>? CrushDamageWeaponTemplate { get; private set; }

    /// <summary>Crusher level this formation resists (1 = infantry, 2 = trees, 3 = vehicles).</summary>
    [AddedIn(SageGame.Bfme2)]
    public int CrusherLevelResisted { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PorcupineFormationBody(gameObject, gameEngine, this);
    }
}
