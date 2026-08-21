using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FX;

namespace OpenSage.Logic.Object;

// Ported from the GPL VeterancyCrateCollide (GameEngine/Source/GameLogic/Object/Collide/CrateCollide/VeterancyCrateCollide.cpp):
// grants a level of experience to the collider, or to every same-player unit within
// EffectRange, when a valid unit touches the crate. AddsOwnerVeterancy replaces the flat
// "1 level" grant with the crate's own veterancy rank (set by whatever created the crate, e.g.
// a dying unit's rank carried forward). IsPilot restricts the grant to vehicles controlled by
// the crate's own owner and excludes airborne locomotors, then hands the crate's name to the
// vehicle so designers can keep scripting it. AffectsUpToLevel and ExecuteFX are BFME
// additions with no GPL counterpart; AffectsUpToLevel 0 means "no cap" (consistent with the
// rest of this INI schema's zero-is-unset convention).
//
// Deliberately not ported: the GPL isValidToExecute()'s `ai->getGoalObject() != other` gate,
// which only makes sense for the AI-driven "pilot walking to a vehicle" case and has no
// equivalent goal-object concept on this engine's AIUpdate yet.
//
// IsValidToExecute below inlines a translation of the shared CrateCollide::isValidToExecute
// base gate (CrateCollide.cpp) ahead of this leaf's own checks, mirroring
// VeterancyCrateCollide::isValidToExecute's `if(!CrateCollide::isValidToExecute(other)) return
// false;` call. No sibling CrateCollide has ever occupied the shared CrateCollide.cs base with
// this pipeline (see SabotagePowerPlantCrateCollide's header for the same note), so it is
// translated self-contained here rather than growing the shared base for one module family.
// DEVIATION: RequiredKindOf is a MASK in GPL (isKindOfMulti checks both required and
// forbidden together); the existing ported CrateCollideModuleData.RequiredKindOf field is a
// single ObjectKinds value (a pre-existing simplification predating this port, out of scope to
// change here), so only ForbiddenKindOf is enforced below. PickupScience has no ported field on
// CrateCollideModuleData at all (never parsed), so it is not enforced either. The base gate's
// `other->getAIUpdateInterface() == NULL` / BuildingPickup / crate-isAboveTerrain checks are
// also left unported here: R13 review findings (research/modules-r13/review/
// veterancy-crate-collide.md) scoped the confirmed gap to isNeutralControlled/
// RequiredKindOf/ForbiddenKindOf/ForbidOwnerPlayer/HumanOnly/KindOf(PARACHUTE) only, and this
// engine's test/spawn fixtures for mobile units don't universally carry an AIUpdateInterface
// module the way retail's always does, so enforcing that check needs a broader fixture audit
// this fix doesn't attempt to avoid guessing at scope.
public sealed class VeterancyCrateCollide : CrateCollide
{
    private readonly VeterancyCrateCollideModuleData _moduleData;

    internal VeterancyCrateCollide(GameObject gameObject, IGameEngine gameEngine, VeterancyCrateCollideModuleData moduleData) : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    public override void OnCollide(GameObject other, in Vector3 location, in Vector3 normal)
    {
        if (other == null)
        {
            // The ground never picks up a crate.
            return;
        }

        if (!IsValidToExecute(other))
        {
            return;
        }

        ExecuteCrateBehavior(other);
    }

    private int GetLevelsToGain()
    {
        if (!_moduleData.AddsOwnerVeterancy)
        {
            return 1;
        }

        // Requires that Regular is 0, Veteran is 1, etc.
        return (int)GameObject.Rank;
    }

    private bool IsValidToExecute(GameObject other)
    {
        // ---- CrateCollide::isValidToExecute (shared base gate) ----

        var neutralPlayer = GameEngine.Game.PlayerManager.NeutralPlayer;
        if (other.Owner == neutralPlayer)
        {
            // "Nothing Neutral can pick up any type of crate."
            return false;
        }

        if (_moduleData.ForbiddenKindOf is { AnyBitSet: true } forbidden
            && other.Definition.KindOf.Intersects(forbidden))
        {
            return false;
        }

        if (other.IsEffectivelyDead)
        {
            return false;
        }

        if (_moduleData.ForbidOwnerPlayer && GameObject.Owner == other.Owner)
        {
            // "Design has decreed this to not be picked up by the dead guy's team."
            return false;
        }

        if (_moduleData.HumanOnly && other.Owner is { IsHuman: false })
        {
            // "Human only mission crate."
            return false;
        }

        if (other.IsKindOf(ObjectKinds.Parachute))
        {
            return false;
        }

        // ---- VeterancyCrateCollide::isValidToExecute (leaf-specific checks) ----

        if (other.IsEffectivelyDead)
        {
            return false;
        }

        if (other.IsSignificantlyAboveTerrain)
        {
            return false;
        }

        var levelsToGain = GetLevelsToGain();
        if (levelsToGain <= 0)
        {
            return false;
        }

        var tracker = other.ExperienceTracker;
        if (tracker == null || !tracker.IsTrainable)
        {
            // If the other unit can't gain experience, then we can't help promote it.
            return false;
        }

        if (!tracker.CanGainExpForLevel(levelsToGain))
        {
            return false;
        }

        if (_moduleData.AffectsUpToLevel > 0 && (int)other.Rank >= _moduleData.AffectsUpToLevel)
        {
            return false;
        }

        if (_moduleData.IsPilot)
        {
            if (other.Owner != GameObject.Owner)
            {
                // Pilot entering a vehicle must be on the same side, e.g. not a civilian vehicle.
                return false;
            }

            if (other.IsUsingAirborneLocomotor())
            {
                // Can't upgrade a helicopter or plane.
                return false;
            }
        }

        return true;
    }

    private void ExecuteCrateBehavior(GameObject other)
    {
        var levelsToGain = GetLevelsToGain();
        var canScaleForBonus = !_moduleData.IsPilot;

        if (_moduleData.EffectRange <= 0)
        {
            // Do just the collider.
            other.ExperienceTracker.GainExpForLevel(levelsToGain, canScaleForBonus);
        }
        else
        {
            // The GPL radius query includes its own center object; this engine's
            // Quadtree.FindNearby deliberately excludes the search object, so the collider
            // is granted explicitly and the quadtree only supplies the rest of the crowd.
            other.ExperienceTracker.GainExpForLevel(levelsToGain, canScaleForBonus);

            foreach (var candidate in GameEngine.Quadtree.FindNearby(other, other.Transform, _moduleData.EffectRange))
            {
                if (candidate.Owner != other.Owner)
                {
                    continue;
                }

                // Gives just enough experience for the object to gain a level, if it can.
                candidate.ExperienceTracker.GainExpForLevel(levelsToGain, canScaleForBonus);
            }
        }

        if (_moduleData.IsPilot)
        {
            // Transfer the crate's name to the vehicle, so designers can keep scripting it.
            // Null-tolerant: the headless sim host has no ScriptingSystem.
            GameEngine.Game.Scripting?.TransferObjectName(GameObject.Name, other);
        }

        _moduleData.ExecuteFX?.Value?.Execute(new FXListExecutionContext(
            GameObject.Rotation,
            GameObject.Translation,
            GameEngine));

        GameEngine.GameLogic.DestroyObject(GameObject);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

public sealed class VeterancyCrateCollideModuleData : CrateCollideModuleData
{
    internal static VeterancyCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<VeterancyCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<VeterancyCrateCollideModuleData>
        {
            { "EffectRange", (parser, x) => x.EffectRange = parser.ParseInteger() },
            { "AddsOwnerVeterancy", (parser, x) => x.AddsOwnerVeterancy = parser.ParseBoolean() },
            { "IsPilot", (parser, x) => x.IsPilot = parser.ParseBoolean() },
            { "ExecuteFX", (parser, x) => x.ExecuteFX = parser.ParseFXListReference() },
            { "AffectsUpToLevel", (parser, x) => x.AffectsUpToLevel = parser.ParseInteger() },
        });


    public int EffectRange { get; private set; }
    public bool AddsOwnerVeterancy { get; private set; }
    public bool IsPilot { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public LazyAssetReference<FXList> ExecuteFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int AffectsUpToLevel { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new VeterancyCrateCollide(gameObject, gameEngine, this);
    }
}
