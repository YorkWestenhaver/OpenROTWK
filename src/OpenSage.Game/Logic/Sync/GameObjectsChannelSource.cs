// The REAL Objects channel source (api-freeze-v1 F8 channel 0; step-5 handoff: "real
// channel sources implement ICrcChannelSource in OpenSage.Game and must iterate ascending
// (ObjectId, ModuleIndex)").
//
// Walk order is engine-owned (S4): objects ascending ObjectId (GameLogic's backing list is
// ObjectId-indexed), modules ascending ModuleIndex (list order by construction). During
// migration only ported modules (HasSimXfer) enter the walk; when F11 completes, every
// module does and the marker disappears. The engine-owned next-wake frame (S6) is xfered
// by this walk, immediately before the module's own fields, never by the module.

using OpenSage.Logic.Object;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Sync;

internal sealed class GameObjectsChannelSource : ICrcChannelSource
{
    private readonly GameLogic _gameLogic;

    internal GameObjectsChannelSource(GameLogic gameLogic)
    {
        _gameLogic = gameLogic;
    }

    public CrcChannel Channel => CrcChannel.Objects;

    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        foreach (var gameObject in _gameLogic.Objects)
        {
            foreach (var module in gameObject.BehaviorModules)
            {
                if (!module.HasSimXfer)
                {
                    continue;
                }

                xfer.BeginModule(new XferModuleId(
                    gameObject.Id.Index,
                    module.ModuleIndex,
                    module.Tag,
                    module.GetType().Name));

                if (module is UpdateModule updateModule)
                {
                    // Engine-owned scheduling state rides the per-object walk (S6).
                    var wake = updateModule.NextWakeFrameForWalk;
                    xfer.XferFrame("Engine.NextWakeFrame", ref wake);
                    updateModule.NextWakeFrameForWalk = wake;
                }

                module.Xfer(xfer);
                xfer.EndModule();
            }
        }
    }
}
