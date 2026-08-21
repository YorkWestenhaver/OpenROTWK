#nullable enable

using System.Numerics;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

/// <summary>
/// A structure body that, for configured damage types, redirects incoming damage onto one
/// of its spawned slaves (or contained riders) instead of taking it itself. If the right
/// damage type comes in and a slave exists, the closest slave to the shooter eats the whole
/// hit; if no slave exists and the type is also marked "swallow", the hit is silently
/// discarded; otherwise the structure takes the damage normally (GPL header: "If there are
/// no slaves, then the structure will take the damage").
///
/// Fresh clean-room port of GPL GeneralsMD <c>HiveStructureBody</c> (behavioral reference
/// only). GPL derives <c>HiveStructureBody : StructureBody : ActiveBody</c>; OpenSAGE's
/// <c>StructureBody</c> is sealed and contributes only an unused <c>_constructorObjectID</c>
/// (its own comment: "isn't actually used anywhere") with no <c>attemptDamage</c> override,
/// so this class derives directly from <see cref="ActiveBody"/> — behaviorally identical for
/// the damage path, which is all HiveStructureBody touches. See research doc finding F-HSB-2.
///
/// Requires the <see cref="SpawnBehaviorModuleData"/> module to redirect to slaves.
/// </summary>
public sealed class HiveStructureBody : ActiveBody
{
    private readonly HiveStructureBodyModuleData _moduleData;

    internal HiveStructureBody(GameObject gameObject, IGameEngine gameEngine, HiveStructureBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// GPL <c>HiveStructureBody::attemptDamage</c>: for a propagate-type hit, hand it to the
    /// closest slave (or rider) to the shooter; swallow it if there are none and the type is
    /// a swallow type; otherwise fall through to the normal structure body damage path.
    /// </summary>
    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        if (_moduleData.PropagateDamageTypesToSlavesWhenExisting.Get(damageInput.DamageType))
        {
            // The right type of damage to propagate is incoming. Do we have slaves?
            var spawnBehavior = GameObject.FindBehavior<SpawnBehavior>();
            if (spawnBehavior != null)
            {
                // We found the spawn behavior, now get some slaves! We redirect based on the
                // shooter's position, so the shooter must exist; if it does not we fall
                // through and take the damage ourselves (GPL: the inner block returns nothing
                // when findObjectByID fails).
                var shooter = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
                if (shooter != null)
                {
                    var slave = spawnBehavior.GetClosestSlave(shooter.Translation);
                    if (slave != null)
                    {
                        // Propagate damage and return!
                        return slave.AttemptDamage(damageInput);
                    }

                    if (_moduleData.SwallowDamageTypesIfSlavesNotExisting.Get(damageInput.DamageType))
                    {
                        // No slave to give it to, so eat it.
                        return SwallowedOutput;
                    }
                }
            }
            else if (GameObject.Contain != null)
            {
                // No spawn behavior, but a container: redirect to the closest rider instead.
                var shooter = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
                if (shooter != null)
                {
                    var rider = GetClosestRider(GameObject.Contain, shooter.Translation);
                    if (rider != null)
                    {
                        // Propagate damage and return!
                        return rider.AttemptDamage(damageInput);
                    }

                    if (_moduleData.SwallowDamageTypesIfSlavesNotExisting.Get(damageInput.DamageType))
                    {
                        // No rider to give it to, so eat it.
                        return SwallowedOutput;
                    }
                }
            }

            // (GPL DEBUG_CRASHes here when a propagate-type hit lands on a hive that has
            // neither a SpawnBehavior nor a Contain module - a data error - and then, like
            // every other unresolved case, falls through to damage the structure. The crash
            // is debug-only; the shipped behavior is the fall-through below.)
        }

        // Nothing to propagate (different damage type, no slaves/riders, or no shooter), so
        // damage me instead.
        return base.AttemptDamage(damageInput);
    }

    /// <summary>
    /// The "swallowed" result: no damage, no clip, marked as having no effect. Matches GPL's
    /// <c>out.m_actualDamageDealt = 0; out.m_actualDamageClipped = 0; out.m_noEffect = true</c>.
    /// </summary>
    private static DamageInfoOutput SwallowedOutput => new()
    {
        ActualDamageDealt = 0.0f,
        ActualDamageClipped = 0.0f,
        NoEffect = true,
    };

    /// <summary>
    /// The contained object nearest (2D, center-to-center) to <paramref name="position"/>,
    /// mirroring GPL <c>OpenContain::getClosestRider</c>. Inlined here (rather than added to
    /// the container interface) per the pilot's "filter inline, GPL shape" precedent; see
    /// finding F-HSB-3. Distance is float (D-7 partition boundary).
    /// </summary>
    private static GameObject? GetClosestRider(IContainModule contain, in Vector3 position)
    {
        GameObject? closest = null;
        var closestDistanceSquared = 0.0f;

        foreach (var rider in contain.ContainedItems)
        {
            if (rider == null)
            {
                continue;
            }

            var dx = rider.Translation.X - position.X;
            var dy = rider.Translation.Y - position.Y;
            var distanceSquared = dx * dx + dy * dy;

            if (closest == null || distanceSquared < closestDistanceSquared)
            {
                closest = rider;
                closestDistanceSquared = distanceSquared;
            }
        }

        return closest; // Could be null!
    }

    // ---- the contract Xfer walk. HiveStructureBody adds no mutable sim state of its own
    // (the propagate/swallow flags are immutable ModuleData); GPL's xfer writes its own
    // version layer then chains the parent (StructureBody -> ActiveBody). We chain ActiveBody
    // directly (F-HSB-2). HasSimXfer is inherited (true) from ActiveBody. ----

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);   // HiveStructureBody's own version layer (GPL xfer version 1)
        base.Xfer(xfer);       // ActiveBody contract walk
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Structure body that propagates specified damage types to slaves when available. If there
/// are no slaves, the structure takes the damage.
/// </summary>
[SimDataAudited]
public sealed class HiveStructureBodyModuleData : ActiveBodyModuleData
{
    internal static new HiveStructureBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-HB-1: the shadowing Parse must keep the base defaulting.
        return result;
    }

    private static new readonly IniParseTable<HiveStructureBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<HiveStructureBodyModuleData>
        {
            { "PropagateDamageTypesToSlavesWhenExisting", (parser, x) => x.PropagateDamageTypesToSlavesWhenExisting = parser.ParseEnumBitArray<DamageType>() },
            { "SwallowDamageTypesIfSlavesNotExisting", (parser, x) => x.SwallowDamageTypesIfSlavesNotExisting = parser.ParseEnumBitArray<DamageType>() }
        });

    /// <summary>Damage types redirected to a slave/rider when one exists.</summary>
    public BitArray<DamageType> PropagateDamageTypesToSlavesWhenExisting { get; private set; } = new();

    /// <summary>
    /// Subset of the propagate types that are silently discarded when no slave/rider exists
    /// (rather than falling through to damage the structure).
    /// </summary>
    public BitArray<DamageType> SwallowDamageTypesIfSlavesNotExisting { get; private set; } = new();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HiveStructureBody(gameObject, gameEngine, this);
    }
}
