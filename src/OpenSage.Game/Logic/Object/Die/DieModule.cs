#nullable enable

using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

public abstract class DieModule : BehaviorModule, IDieModule
{
    private readonly DieModuleData _moduleData;

    protected DieModule(GameObject gameObject, IGameEngine gameEngine, DieModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// The frozen contract ctor for a PORTED Die module (api-freeze-v1 §3 item 1, and item 4's
    /// rule that the category bases carry it): it forwards to <see cref="BehaviorModule"/>'s
    /// ISimContext ctor, so <see cref="BehaviorModule.Context"/> is populated and the port
    /// reaches the sim through that door only — never through
    /// <see cref="ObjectModule.GameEngine"/>.
    /// <para>
    /// The applicability gate in <c>IDieModule.OnDie</c> below is deliberately shared by ported
    /// and legacy subclasses alike: <see cref="DieLogicData"/> is parse-side data, not per-class
    /// behavior, so a port never re-implements DeathTypes / RequiredStatus / ExemptStatus.
    /// </para>
    /// <para>
    /// Accessibility matches <see cref="BehaviorModule"/>'s ISimContext ctor
    /// (<c>private protected</c>): every Die subclass lives in this assembly. The legacy
    /// <see cref="IGameEngine"/> ctor above stays until the last Die class is ported (F11).
    /// </para>
    /// </summary>
    private protected DieModule(GameObject gameObject, ISimContext context, DieModuleData moduleData)
        : base(gameObject, context)
    {
        _moduleData = moduleData;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        if (!_moduleData.DieData.IsDieApplicable(damageInput, GameObject))
        {
            return;
        }

        Die(damageInput);
    }

    // TODO(Port): Make this abstract.
    protected virtual void Die(in DamageInfoInput damageInput) { }
}

public interface IDieModule
{
    void OnDie(in DamageInfoInput damageInput);
}

public abstract class DieModuleData : BehaviorModuleData
{
    public override ModuleKinds ModuleKinds => ModuleKinds.Die;

    internal static readonly IniParseTableChild<DieModuleData, DieLogicData> FieldParseTable = new IniParseTableChild<DieModuleData, DieLogicData>(x => x.DieData, DieLogicData.FieldParseTable);

    public DieLogicData DieData { get; } = new();
}

public sealed class DieLogicData
{
    internal static readonly IniParseTable<DieLogicData> FieldParseTable = new IniParseTable<DieLogicData>
    {
        { "RequiredStatus", (parser, x) => x.RequiredStatus = parser.ParseEnum<ObjectStatus>() },
        { "ExemptStatus", (parser, x) => x.ExemptStatus = parser.ParseEnum<ObjectStatus>() },
        { "DeathTypes", (parser, x) => x.DeathTypes = parser.ParseEnumBitArray<DeathType>() },
    };

    public BitArray<DeathType>? DeathTypes { get; private set; }
    public ObjectStatus? RequiredStatus { get; private set; }
    public ObjectStatus? ExemptStatus { get; internal set; }

    public bool IsDieApplicable(in DamageInfoInput damageInput, GameObject obj) =>
        (DeathTypes?.Get(damageInput.DeathType) ?? true) && IsCorrectStatus(obj);

    private bool IsCorrectStatus(GameObject obj)
    {
        var required = !RequiredStatus.HasValue || // if nothing is required, we pass
            obj.TestStatus(RequiredStatus.Value);  // or if we are the one of the required statuses, we pass
        var notExempt = !ExemptStatus.HasValue ||  // if nothing is exempt, we pass
            !obj.TestStatus(ExemptStatus.Value);   // or if we are not one of the exempt statuses, we pass
        return required && notExempt;
    }
}
