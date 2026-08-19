namespace OpenSage.Logic.Object;

public abstract class DamageModule : BehaviorModule, IDamageModule
{
    protected DamageModule(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// The frozen contract ctor for a PORTED Damage module (api-freeze-v1 §3 item 1, and item 4's
    /// rule that the category bases carry it): it forwards to <see cref="BehaviorModule"/>'s
    /// ISimContext ctor, so <see cref="BehaviorModule.Context"/> is populated and the port reaches
    /// the sim through that door only — never through <see cref="ObjectModule.GameEngine"/>.
    /// <para>
    /// Accessibility matches <see cref="BehaviorModule"/>'s ISimContext ctor
    /// (<c>private protected</c>): every Damage subclass lives in this assembly. The legacy
    /// <see cref="IGameEngine"/> ctor above stays until the last Damage class is ported (F11).
    /// This mirrors the promotion the Die batch applied to <c>DieModule</c>.
    /// </para>
    /// </summary>
    private protected DamageModule(GameObject gameObject, ISimContext context) : base(gameObject, context)
    {
    }

    public virtual void OnDamage(in DamageInfo damageInfo) { }

    public virtual void OnHealing(in DamageInfo damageInfo) { }

    public virtual void OnBodyDamageStateChange(
        in DamageInfo damageInfo,
        BodyDamageType oldState,
        BodyDamageType newState)
    {

    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

internal interface IDamageModule
{
    void OnDamage(in DamageInfo damageInfo) { }

    void OnHealing(in DamageInfo damageInfo) { }

    void OnBodyDamageStateChange(
        in DamageInfo damageInfo,
        BodyDamageType oldState,
        BodyDamageType newState)
    {

    }
}

public abstract class DamageModuleData : ContainModuleData
{
    public override ModuleKinds ModuleKinds => ModuleKinds.Damage;
}

public static class DamageConstants
{
    public const float HugeDamageAmount = 999999.0f;

    public static bool IsHealthDamagingDamage(this DamageType damageType) => damageType switch
    {
        DamageType.Status => false,
        DamageType.SubdualMissile => false,
        DamageType.SubdualVehicle => false,
        DamageType.SubdualBuilding => false,
        DamageType.SubdualUnresistable => false,
        DamageType.KillPilot => false,
        DamageType.KillGarrisoned => false,
        _ => true,
    };

    public static bool IsSubdualDamage(this DamageType damageType) => damageType switch
    {
        DamageType.SubdualMissile => true,
        DamageType.SubdualVehicle => true,
        DamageType.SubdualBuilding => true,
        DamageType.SubdualUnresistable => true,
        _ => false,
    };
}
