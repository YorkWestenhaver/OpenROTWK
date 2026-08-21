using System;
using System.Numerics;
using ImGuiNET;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// Animated docking/undocking for a railed transport (a train that "swallows" units at a
/// dock action point and pushes them back out through the same dock, one at a time or all
/// at once). GPL <c>RailedTransportDockUpdate</c>. The base <see cref="DockUpdate"/> here is
/// still the simplified box-dispenser the R10 header notes describe (its approach queue only
/// understands <see cref="SupplyAIUpdate"/>), so this module does not lean on it for the
/// per-object docking state machine - it drives dockers and unloaders through the world
/// directly, exactly as the GPL source does through raw <c>Object*</c> position sets.
/// </summary>
public sealed class RailedTransportDockUpdate : DockUpdate
{
    /// <summary>GPL <c>enum { UNLOAD_ALL = -1 }</c>: sentinel for "keep unloading forever".</summary>
    private const int UnloadAllSentinel = -1;

    /// <summary>
    /// GPL hardcodes this as a local (<c>closeEnoughDistance = 6.0f</c>) inside
    /// <c>doPullInDocking</c>. It is distinct from <see cref="RailedTransportDockUpdateModuleData.ToleranceDistance"/>,
    /// which only gates whether a docker is close enough to be accepted into the pull-in
    /// state at all (in <c>action</c>/<see cref="Dock"/>) - not when the pull-in completes.
    /// </summary>
    private const float PullInCloseEnoughDistance = 6.0f;

    /// <summary>GPL hardcoded local in <c>doPushOutDocking</c> (<c>closeEnoughDistance = 3.0f</c>).</summary>
    private const float PushOutCloseEnoughDistance = 3.0f;

    private const string DockEndBoneName = "DOCKEND";
    private const string DockWaitingClearBoneName = "DOCKWAITING07";

    private readonly RailedTransportDockUpdateModuleData _moduleData;

    /// <summary>GPL <c>m_dockingObjectID</c>: the object currently being pulled inside.</summary>
    private ObjectId _dockingObjectId;

    /// <summary>GPL <c>m_pullInsideDistancePerFrame</c>.</summary>
    private float _pullInsideDistancePerFrame;

    /// <summary>GPL <c>m_unloadingObjectID</c>: the object currently being pushed outside.</summary>
    private ObjectId _unloadingObjectId;

    /// <summary>GPL <c>m_pushOutsideDistancePerFrame</c>.</summary>
    private float _pushOutsideDistancePerFrame;

    /// <summary>
    /// GPL <c>m_unloadCount</c>: governs unloading one or all objects.
    /// <see cref="UnloadAllSentinel"/> unloads forever, 0 unloads nothing further, otherwise
    /// it is decremented once per object that starts unloading.
    /// </summary>
    private int _unloadCount = UnloadAllSentinel;

    internal RailedTransportDockUpdate(GameObject gameObject, IGameEngine gameEngine, RailedTransportDockUpdateModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>GPL <c>isLoadingOrUnloading()</c>.</summary>
    public bool IsLoadingOrUnloading => _unloadingObjectId.IsValid || _dockingObjectId.IsValid;

    /// <summary>
    /// GPL <c>isClearToEnter(Object const *docker)</c>: base-class restrictions (here, the
    /// R10 <see cref="DockUpdate.IsDockCrippled"/> flag) plus "we can't be full".
    /// </summary>
    public bool IsClearToEnter(GameObject docker)
    {
        if (IsDockCrippled)
        {
            return false;
        }

        var contain = FindContainModule();
        return contain != null && contain.CanAddUnit(docker);
    }

    /// <summary>
    /// GPL <c>action(Object *docker, Object *drone)</c>: the dock action callback. Sets the
    /// docker up for pull-in the first time it is seen, provided it is already within
    /// <see cref="RailedTransportDockUpdateModuleData.ToleranceDistance"/> of the dock.
    /// </summary>
    public void Dock(GameObject docker)
    {
        if (docker == null)
        {
            return;
        }

        if (_dockingObjectId == docker.Id)
        {
            return;
        }

        var dockPos = GameObject.Translation;
        var dockerPos = docker.Translation;
        var v = dockPos - dockerPos;

        var mag = v.Length();
        if (mag > _moduleData.ToleranceDistance)
        {
            return;
        }

        _dockingObjectId = docker.Id;

        // don't let the user interact with this object anymore
        docker.Owner?.DeselectUnit(docker);
        docker.SetSelectable(false);

        // hold the object so physics doesn't mess with it anymore
        docker.SetDisabled(DisabledType.Held);

        // now that we know how far we must go, how much distance should we travel every frame
        _pullInsideDistancePerFrame = mag / _moduleData.PullInsideDuration.Value;

        // orient docker so its facing toward the transport
        docker.SetOrientation(MathF.Atan2(dockPos.Y - dockerPos.Y, dockPos.X - dockerPos.X));
    }

    /// <summary>GPL <c>unloadAll()</c>.</summary>
    public void UnloadAll()
    {
        // sanity, if we're already unloading, ignore this command and just allow us to finish
        if (_unloadingObjectId.IsValid)
        {
            return;
        }

        _unloadCount = UnloadAllSentinel;
        UnloadNext();
    }

    /// <summary>
    /// GPL <c>unloadSingleObject(Object *obj)</c>. GPL never reads <paramref name="obj"/> -
    /// it always unloads whatever is first in the container - so the parameter is kept only
    /// for interface fidelity.
    /// </summary>
    public void UnloadSingleObject(GameObject obj)
    {
        _unloadCount = 1;
        UnloadNext();
    }

    public override UpdateSleepTime Update()
    {
        base.Update();

        DoPullInDocking();
        DoPushOutDocking();

        // TODO(Port): Use correct value.
        return UpdateSleepTime.None;
    }

    /// <summary>
    /// GPL <c>doPullInDocking()</c>: if we're pulling an object inside of us, do that pull
    /// now. We need this so that the railed transport can "pull" objects inside it because
    /// typically those objects can only drive on land and have a hard time driving "inside"
    /// the railed transport ... so we fake it!
    /// </summary>
    private void DoPullInDocking()
    {
        if (_dockingObjectId.IsInvalid)
        {
            return;
        }

        var docker = GameEngine.GameLogic.GetObjectById(_dockingObjectId);

        // check for docker gone (a destroyed object is only removed from the id table at the
        // end of the frame, so IsDestroyed is checked too - see GameLogic.DeleteDestroyed)
        if (docker == null || docker.IsDestroyed)
        {
            _dockingObjectId = ObjectId.Invalid;
            return;
        }

        var dockerPos = docker.Translation;
        var dockPos = GameObject.Translation;

        // get the vector from the docker to the dock pos
        var v = dockPos - dockerPos;
        if (v != Vector3.Zero)
        {
            v = Vector3.Normalize(v);
        }

        // apply "movement" to the vector, then the docker's current position
        var newPos = new Vector3(
            dockerPos.X + v.X * _pullInsideDistancePerFrame,
            dockerPos.Y + v.Y * _pullInsideDistancePerFrame,
            dockerPos.Z); // keep Z height the same and just scoot along the ground

        docker.SetTranslation(newPos);

        // set the model condition for the object as "moving" even though it really isn't in
        // the traditional sense, but we don't want them to scoot/slide into the transport
        // and look weird
        docker.ModelConditionFlags.Set(ModelConditionFlag.Moving, true);

        // if we're at the destination then stop and put it inside the dock object
        var dx = newPos.X - dockPos.X;
        var dy = newPos.Y - dockPos.Y;
        var distSq = dx * dx + dy * dy;
        if (distSq <= PullInCloseEnoughDistance * PullInCloseEnoughDistance)
        {
            docker.ModelConditionFlags.Set(ModelConditionFlag.Moving, false);

            // stop the dock action - GPL's cancelDock() also drops the docker from the base
            // DockUpdate's approach queue, but that queue is SupplyAIUpdate-specific here
            // (see the DockUpdate R10 header note) and this docker never entered it.

            // stop the docker from doing anything by going idle
            docker.AIUpdate?.AIIdle(CommandSourceType.FromAI);

            // put object inside us
            FindContainModule()?.Add(docker);

            // no object is docking now
            _dockingObjectId = ObjectId.Invalid;
        }
    }

    /// <summary>GPL <c>doPushOutDocking()</c>: if we have an object being pushed out, do that here.</summary>
    private void DoPushOutDocking()
    {
        if (_unloadingObjectId.IsInvalid)
        {
            return;
        }

        var unloader = GameEngine.GameLogic.GetObjectById(_unloadingObjectId);

        // if unloader is not found (like they got destroyed) unload the next object inside
        if (unloader == null || unloader.IsDestroyed)
        {
            UnloadNext();
            return;
        }

        var unloaderPos = unloader.Translation;

        // get the destination point as the DOCKEND, snapped to the ground
        var destPos = GetExitPosition();
        destPos = new Vector3(destPos.X, destPos.Y, GameEngine.Game.TerrainLogic.GetGroundHeight(destPos.X, destPos.Y));

        // get the vector from the unloader to the destination point
        var v = destPos - unloaderPos;
        if (v != Vector3.Zero)
        {
            v = Vector3.Normalize(v);
        }

        // apply "movement" to that vector, then the unloader's current position
        var newPos = new Vector3(
            unloaderPos.X + v.X * _pushOutsideDistancePerFrame,
            unloaderPos.Y + v.Y * _pushOutsideDistancePerFrame,
            destPos.Z); // keep Z height the same and just scoot along the ground

        unloader.SetTranslation(newPos);

        unloader.ModelConditionFlags.Set(ModelConditionFlag.Moving, true);

        // if we're at the destination then stop and unload the next object if present
        var dx = destPos.X - newPos.X;
        var dy = destPos.Y - newPos.Y;
        var dz = destPos.Z - newPos.Z;
        var distSq = dx * dx + dy * dy + dz * dz;
        if (distSq <= PushOutCloseEnoughDistance * PushOutCloseEnoughDistance)
        {
            unloader.ModelConditionFlags.Set(ModelConditionFlag.Moving, false);

            // set the unloaded object as idle
            unloader.AIUpdate?.AIIdle(CommandSourceType.FromAI);

            // clear the held status from this unloading object
            unloader.ClearDisabled(DisabledType.Held);

            // we can now be selected by the player again
            unloader.SetSelectable(true);

            // tell the unloader to move to one of the dock positions and out of the way
            unloader.AIUpdate?.AddTargetPoint(GetBonePosition(DockWaitingClearBoneName));

            // unload the next object
            UnloadNext();
        }
    }

    /// <summary>
    /// GPL <c>unloadNext()</c>: start the next object contained by us as "unloading and
    /// coming out". Mutates <see cref="OpenContainModule.ContainedObjectIds"/> and the
    /// object's container linkage directly rather than going through
    /// <see cref="OpenContainModule.Remove"/>, which queues an evac and exits the unit
    /// through its own <c>ExitStart</c>/<c>ExitEnd</c> bones - the railed transport instead
    /// pushes the unit out over <see cref="RailedTransportDockUpdateModuleData.PushOutsideDuration"/>
    /// frames under its own control, exactly as GPL's immediate
    /// <c>openContain-&gt;removeFromContain(unloader)</c> does.
    /// </summary>
    private void UnloadNext()
    {
        // by default, setup our unloading process to be done with no objects being considered
        _unloadingObjectId = ObjectId.Invalid;

        // if our unload count is zero we can't unload any more until we receive a command to
        // unload another one or everything we've got
        if (_unloadCount == 0)
        {
            return;
        }

        var contain = FindContainModule();
        if (contain == null || contain.ContainedObjectIds.Count == 0)
        {
            return;
        }

        // get the first contained object
        var unloaderId = contain.ContainedObjectIds[0];
        var unloader = GameEngine.GameLogic.GetObjectById(unloaderId);
        if (unloader == null)
        {
            // shouldn't happen, but don't get stuck on a dangling id
            contain.ContainedObjectIds.RemoveAt(0);
            return;
        }

        // remove us from the container
        contain.ContainedObjectIds.RemoveAt(0);
        unloader.RemoveFromContainer();

        // set position of the unloader to our position
        unloader.SetTranslation(GameObject.Translation);

        // orient unloader to the same angle as us so we can drive out the front
        unloader.SetOrientation(GameObject.Yaw);

        // mark it as HELD so physics or anything else can't mess with our position
        unloader.SetDisabled(DisabledType.Held);

        // get the dock point that we're going to go to ... that is where we came in at the
        // DOCKEND point
        var dockPosition = GetExitPosition();
        var unloaderPos = unloader.Translation;

        // how far is it from our current position to the dock position
        var v = dockPosition - unloaderPos;
        var mag = v.Length();

        // now that we know how far we must go, how much distance should we travel every frame
        _pushOutsideDistancePerFrame = mag / _moduleData.PushOutsideDuration.Value;

        // set this as our current unloader
        _unloadingObjectId = unloaderId;

        // we've now used an unload (if we're keeping count for single exits)
        if (_unloadCount != UnloadAllSentinel)
        {
            _unloadCount--;
        }
    }

    private OpenContainModule FindContainModule() => GameObject.FindBehavior<OpenContainModule>();

    /// <summary>GPL <c>getExitPosition</c>: the DOCKEND bone, falling back to our own position.</summary>
    private Vector3 GetExitPosition() => GetBonePosition(DockEndBoneName);

    private Vector3 GetBonePosition(string boneName)
    {
        var (modelInstance, bone) = GameObject.Drawable.FindBone(boneName);
        if (modelInstance != null && bone != null)
        {
            return modelInstance.AbsoluteBoneTransforms[bone.Index].Translation;
        }
        return GameObject.Translation;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistObjectId(ref _dockingObjectId);
        reader.PersistSingle(ref _pullInsideDistancePerFrame);
        reader.PersistObjectId(ref _unloadingObjectId);
        reader.PersistSingle(ref _pushOutsideDistancePerFrame);
        reader.PersistInt32(ref _unloadCount);
    }

    internal override void DrawInspector()
    {
        base.DrawInspector();
        ImGui.LabelText("Docking object", _dockingObjectId.ToString());
        ImGui.LabelText("Unloading object", _unloadingObjectId.ToString());
        ImGui.LabelText("Unload count", _unloadCount.ToString());
    }
}

public sealed class RailedTransportDockUpdateModuleData : DockUpdateModuleData
{
    internal static RailedTransportDockUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<RailedTransportDockUpdateModuleData> FieldParseTable = DockUpdateModuleData.FieldParseTable
        .Concat(new IniParseTable<RailedTransportDockUpdateModuleData>
        {
            { "PullInsideDuration", (parser, x) => x.PullInsideDuration = parser.ParseTimeMillisecondsToLogicFrames() },
            { "PushOutsideDuration", (parser, x) => x.PushOutsideDuration = parser.ParseTimeMillisecondsToLogicFrames() },
            { "ToleranceDistance", (parser, x) => x.ToleranceDistance = parser.ParseFloat() }
        });

    public LogicFrameSpan PullInsideDuration { get; private set; }
    public LogicFrameSpan PushOutsideDuration { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float ToleranceDistance { get; private set; } = 50.0f;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RailedTransportDockUpdate(gameObject, gameEngine, this);
    }
}
