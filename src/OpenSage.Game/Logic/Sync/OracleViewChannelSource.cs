// The harness's oracle view: one uniform record group per game object - Position / Angle /
// Health / MaxHealth - the exact per-object state the workbench extracts from a retail
// memory dump, so a Target-B diff lines up field-for-field without knowing module layouts.
//
// Walk order matches GameObjectsChannelSource: objects ascending ObjectId. Authoritative
// state, not the display mirror: a locomotor-driven object reads its SimPhysics Fix64
// transform; anything else quantizes the float transform through the same wire-float
// boundary spawn positions entered by (SimTransformBridge, F4). Health is the Fix64
// BodyDamageCore; a bodiless object reports zeros.
//
// Rides the Taint channel ordinal: a diagnostic-only channel the ported systems do not
// populate, so map-v1 gets a distinct slot in the checkpoint vector without disturbing the
// Objects/LogicRandom semantics other scenarios pin.

using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Sync;

internal sealed class OracleViewChannelSource : ICrcChannelSource
{
    private readonly GameLogic _gameLogic;

    internal OracleViewChannelSource(GameLogic gameLogic)
    {
        _gameLogic = gameLogic;
    }

    public CrcChannel Channel => CrcChannel.Taint;

    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        foreach (var gameObject in _gameLogic.Objects)
        {
            xfer.BeginModule(new XferModuleId(
                gameObject.Id.Index,
                0,
                "OracleView",
                gameObject.Definition.Name));

            var physics = gameObject.FindBehavior<SimLocomotorUpdate>()?.Physics;
            var position = physics?.Position ?? SimTransformBridge.PullPosition(gameObject);
            var angle = physics?.Yaw ?? SimTransformBridge.PullYaw(gameObject);

            var health = Fix64.Zero;
            var maxHealth = Fix64.Zero;
            if (gameObject.BodyModule is ActiveBody activeBody)
            {
                health = activeBody.DamageCore.CurrentHealth;
                maxHealth = activeBody.DamageCore.MaxHealth;
            }

            xfer.XferFixVector3("Position", ref position, Tolerance.Band);
            xfer.XferFix64("Angle", ref angle, Tolerance.Band);
            xfer.XferFix64("Health", ref health, Tolerance.Quantum);
            xfer.XferFix64("MaxHealth", ref maxHealth, Tolerance.Quantum);
            xfer.EndModule();
        }
    }
}
