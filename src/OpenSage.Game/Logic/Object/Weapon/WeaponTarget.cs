using System.Numerics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

internal sealed class WeaponTarget
{
    private readonly IGameObjectCollection _gameObjects;

    public readonly WeaponTargetType TargetType;
    public readonly Vector3? TargetGroundPosition;
    public readonly ObjectId TargetObjectId;

    public bool IsDestroyed => TargetType == WeaponTargetType.Object && GetTargetObject() == null;

    public Vector3 TargetPosition => TargetType == WeaponTargetType.Position
        ? TargetGroundPosition.Value
        : GetTargetObject().Translation;

    internal WeaponTarget(in Vector3 targetGroundPosition)
    {
        TargetType = WeaponTargetType.Position;
        TargetGroundPosition = targetGroundPosition;
    }

    internal WeaponTarget(IGameObjectCollection gameObjects, ObjectId targetObjectId)
    {
        _gameObjects = gameObjects;

        TargetType = WeaponTargetType.Object;
        TargetObjectId = targetObjectId;
    }

    public GameObject GetTargetObject() => _gameObjects.GetObjectById(TargetObjectId);

    public void DoDamage(
        DamageType damageType,
        Fix64 amount,
        DeathType deathType,
        GameObject damageDealer,
        WeaponTemplate sourceWeaponTemplate = null)
    {
        if (TargetType == WeaponTargetType.Object)
        {
            // The S1 Fix64 delivery path (position targets need the Fix64 transform
            // port; see research/systems/weapon-damage-armor.md).
            DamagePipeline.DealDirectDamage(GetTargetObject(), new CombatDamageInput
            {
                SourceId = damageDealer?.Id ?? ObjectId.Invalid,
                DamageType = damageType,
                DeathType = deathType,
                Amount = amount,
                SourceWeaponTemplate = sourceWeaponTemplate,
            });
        }
    }
}
