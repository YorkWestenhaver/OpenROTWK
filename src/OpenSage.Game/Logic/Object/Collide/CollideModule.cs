using System.Numerics;

namespace OpenSage.Logic.Object;

public abstract class CollideModule : BehaviorModule, ICollideModule
{
    protected CollideModule(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// The frozen contract ctor (api-freeze-v1 §3 item 1), grown for the UnitCrateCollide
    /// port (R12): the first Collide module ported onto the ISimContext seam (see that file's
    /// header, F-UCC-1, for why it is not [SimState]-marked). See BehaviorModule's matching
    /// ctor.
    /// </summary>
    protected CollideModule(GameObject gameObject, ISimContext context) : base(gameObject, context)
    {
    }

    // TODO: Make this abstract.
    public virtual void OnCollide(GameObject other, in Vector3 location, in Vector3 normal) { }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

public abstract class CollideModuleData : BehaviorModuleData
{
    public override ModuleKinds ModuleKinds => ModuleKinds.Collide;
}

public interface ICollideModule
{
    void OnCollide(GameObject other, in Vector3 location, in Vector3 normal);
}
