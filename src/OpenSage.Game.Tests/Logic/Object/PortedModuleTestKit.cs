// The shared base-test kit every Round-4 module port clones (api-freeze-v1 §5): the
// shadow-copy check (Save -> Load -> CRC == live CRC) is what turns Xfer completeness into
// a failing test instead of a review hope. Mirrors SimCore's XferVisitorTests shapes, but
// over REAL BehaviorModules on a real (headless) game.

using System;
using System.IO;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Sync;
using Xunit;

namespace OpenSage.Tests.Logic.Object;

internal static class PortedModuleTestKit
{
    /// <summary>The module's live CRC: the same walk the Objects channel folds (F7/F8).</summary>
    public static uint LiveCrc(BehaviorModule module)
    {
        var visitor = new XferCrcVisitor();
        module.Xfer(visitor);
        return visitor.Value;
    }

    public static byte[] Save(BehaviorModule module)
    {
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            module.Xfer(save);
        }
        return stream.ToArray();
    }

    public static void Load(BehaviorModule module, byte[] state)
    {
        var stream = new MemoryStream(state);
        using var load = new XferLoad(stream, leaveOpen: true);
        module.Xfer(load);
    }

    /// <summary>
    /// THE shadow-copy base test (design-module-api §6): live state saved, loaded into a
    /// shadow instance, and the shadow's CRC must equal the live CRC. A mismatch means
    /// mutable sim state exists outside the Xfer walk.
    /// </summary>
    public static void AssertShadowCopyCrcEqualsLiveCrc(BehaviorModule live, BehaviorModule shadow)
    {
        var liveCrc = LiveCrc(live);
        Load(shadow, Save(live));
        Assert.Equal(liveCrc, LiveCrc(shadow));

        // And the round-trip is byte-stable: saving the shadow reproduces the stream.
        Assert.Equal(Save(live), Save(shadow));
    }

    // ------------------------------------------------------------------------
    // Death triggering (experiment-round-4 §4.1, DoD item 4: "Die modules need a
    // death-trigger helper - build it once for the batch").
    //
    // Every Die task's minimum test is [create -> trigger death -> observable effect].
    // "Trigger death" is not one line of engine API: a Die module only runs when
    // ActiveBody's health crosses from >0 to <=0 (ActiveBody.AttemptDamage), and the
    // DeathType/DamageType carried by that damage is what DieLogicData.IsDieApplicable
    // filters on (DeathTypes / RequiredStatus / ExemptStatus). So the helper takes both
    // types explicitly rather than defaulting them out of sight: a Die class whose INI
    // says "DeathTypes = NONE +BURNED" is only testable if the test controls DeathType.
    //
    // GameObject.Kill() exists but hardcodes DamageType.Unresistable/DeathType.Normal and
    // takes no damage source; ported Die tests need the source object (CreateObjectDie,
    // EjectPilotDie, CrushDie all read it) and the typed death, so this is the seam.
    // ------------------------------------------------------------------------

    /// <summary>
    /// What one death trigger did, in observable terms. <see cref="Died"/> is exactly the
    /// condition ActiveBody uses to call <c>GameObject.OnDie</c> (health crossed >0 -> &lt;=0),
    /// so asserting on it asserts that the Die modules actually ran.
    /// </summary>
    internal readonly record struct DeathTriggerResult(
        float HealthBefore,
        float HealthAfter,
        DamageInfoOutput Output,
        bool Destroyed)
    {
        /// <summary>True when this damage is the one that killed the object.</summary>
        public bool Died => HealthBefore > 0f && HealthAfter <= 0f;
    }

    /// <summary>
    /// Applies damage of a chosen type to an object and reports what happened. Sub-lethal
    /// amounts are legal (that is the point: a Die module must NOT fire on a flesh wound).
    /// </summary>
    /// <param name="kill">
    /// Sets <c>DamageInfoInput.Kill</c>: ActiveBody then replaces the amount with the
    /// object's current health, so the object dies whatever its armor says.
    /// </param>
    public static DeathTriggerResult ApplyDamage(
        GameObject target,
        float amount,
        DamageType damageType = DamageType.Explosion,
        DeathType deathType = DeathType.Normal,
        GameObject source = null,
        bool kill = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        var body = target.BodyModule
            ?? throw new InvalidOperationException(
                $"{target.Definition.Name} has no Body module, so it can never die");

        var healthBefore = body.Health;
        var output = target.AttemptDamage(new DamageInfoInput(source)
        {
            DamageType = damageType,
            DeathType = deathType,
            Amount = amount,
            Kill = kill,
        });

        return new DeathTriggerResult(healthBefore, body.Health, output, target.IsDestroyed);
    }

    /// <summary>
    /// THE death trigger: kills <paramref name="target"/> with a chosen death type and
    /// damage type, running the real OnDie dispatch over its real Die modules, and asserts
    /// the object actually died (a Die test that silently failed to kill anything is the
    /// failure mode this helper exists to make impossible).
    /// </summary>
    public static DeathTriggerResult TriggerDeath(
        GameObject target,
        DeathType deathType = DeathType.Normal,
        DamageType damageType = DamageType.Unresistable,
        GameObject source = null)
    {
        var result = ApplyDamage(target, amount: 0f, damageType, deathType, source, kill: true);
        Assert.True(result.Died,
            $"death trigger did not kill {target.Definition.Name}: health " +
            $"{result.HealthBefore} -> {result.HealthAfter}");
        return result;
    }

    /// <summary>
    /// Spawns a fresh object of <paramref name="definitionName"/> and kills it in one call:
    /// the [create -> trigger death] half of the Die definition-of-done. The dead object is
    /// returned so the test can assert the observable effect on it (and on the world).
    /// </summary>
    public static (GameObject Object, DeathTriggerResult Result) SpawnAndKill(
        HeadlessSimGame game,
        string definitionName,
        OpenSage.Logic.Player owner,
        in System.Numerics.Vector3 position,
        DeathType deathType = DeathType.Normal,
        DamageType damageType = DamageType.Unresistable,
        GameObject source = null)
    {
        var gameObject = game.SpawnObject(definitionName, owner, position);
        return (gameObject, TriggerDeath(gameObject, deathType, damageType, source));
    }
}
