// ModelConditionUpgrade - R12 port, translated from generals-gpl GeneralsMD
// ModelConditionUpgrade.cpp/.h (GPL semantics reference for the base ConditionFlag behavior;
// api-freeze-v1 §6 / template v1.1) plus the BFME2 batch-flag extension already parsed by
// this module's [ParseOnly] data (AddConditionFlags / RemoveConditionFlags /
// RemoveConditionFlagsInRange / AddTempConditionFlag / TempConditionTime).
//
// GPL behavior facts translated from ModelConditionUpgrade.cpp (base Generals/ZH; this file
// has no BFME2 extension - the GeneralsMD tree ships the same single-flag module):
//   - upgradeImplementation(): if m_conditionFlag != MODELCONDITION_INVALID, call
//     obj->setModelConditionState(m_conditionFlag). That is the module's entire GPL behavior;
//     ModelConditionUpgrade is otherwise a bare UpgradeModule (no update tick, no xfer state
//     beyond the version byte).
//
// BFME2 EXTENSION (no GPL source exists for these fields - the BFME2 INI schema census
// already parses them, see the packet's plain-language field summary; translated from field
// naming/parse shape only, not invented behavior beyond what each name states):
//   - AddConditionFlags: every named bit is set the same way ConditionFlag is (batch form of
//     the same setModelConditionState call).
//   - RemoveConditionFlags: every named bit is cleared (clearModelConditionState).
//   - RemoveConditionFlagsInRange: parsed with the identical bit-list grammar as
//     RemoveConditionFlags (IniParser.ParseEnumBitArray) - "InRange" names the authored
//     intent (clearing a contiguous state-machine block, e.g. the DAMAGED/REALLYDAMAGED/
//     RUBBLE ladder), but the runtime effect of clearing every named bit is identical to
//     RemoveConditionFlags, so it is applied the same way.
//   - AddTempConditionFlag / TempConditionTime: sets one flag on trigger and schedules its
//     own removal TempConditionTime seconds later. This is the one piece of genuinely new
//     runtime behavior (an update tick this module didn't need before), so it is built on
//     UpdateModule/ISimContext scheduling (Context.CurrentFrame + LogicFrameSpan) rather than
//     any GPL timer shape.
//
// Apply order on trigger (OUR choice, F9 - the GPL source only ever has ConditionFlag, so
// there is no original ordering to preserve across the four BFME2 fields): ConditionFlag,
// then AddConditionFlags, then RemoveConditionFlags, then RemoveConditionFlagsInRange, then
// AddTempConditionFlag last (so a temp flag is never immediately clobbered by a same-frame
// batch remove). All four are independent bit operations on the same BitArray, so this order
// only matters when two fields name the same bit - an authoring conflict, not a modeled case.
//
// Renderer notification: GameObject.ModelConditionFlags is the live BitArray the Drawable
// reads every BuildRenderList call (GameObject.cs), so setting/clearing bits through
// GameObject.SetModelConditionState / ClearModelConditionState is already visible to
// rendering next frame - no separate notification call exists on this contract surface.
//
// Every mutable sim field appears in Xfer exactly once (§3): the shared upgrade mux
// (UpgradeLogic, same shape as StatusBitsUpgrade) plus the temp-flag expiry bookkeeping this
// module owns. The GPL's own xfer() persists nothing beyond the version byte (no BFME2
// source to compare against), so field order here is OUR choice (F9).

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ModelConditionUpgrade : UpdateModule, IUpgradeableModule
{
    private readonly ModelConditionUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>True while the AddTempConditionFlag bit is up and its expiry is pending.</summary>
    private bool _tempFlagActive;

    /// <summary>Frame the temp flag gets cleared on (Context.CurrentFrame + TempConditionTime
    /// at the moment it was applied).</summary>
    private LogicFrame _tempFlagExpiry;

    public ModelConditionUpgrade(GameObject gameObject, ISimContext context, ModelConditionUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No per-frame work until a temp flag is actually pending.
        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL: an
        // initially-active upgrade applies immediately) - same shape as StatusBitsUpgrade.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// GPL upgradeImplementation() plus the BFME2 batch extension - see the file header for
    /// the per-field translation and the apply order.
    /// </summary>
    private void OnUpgradeTriggered()
    {
        // GPL: if (data->m_conditionFlag != MODELCONDITION_INVALID) me->setModelConditionState(...).
        if (_data.ConditionFlag != ModelConditionFlag.None)
        {
            GameObject.SetModelConditionState(_data.ConditionFlag);
        }

        if (_data.AddConditionFlags != null)
        {
            foreach (var flag in _data.AddConditionFlags.GetSetBits())
            {
                GameObject.SetModelConditionState(flag);
            }
        }

        if (_data.RemoveConditionFlags != null)
        {
            foreach (var flag in _data.RemoveConditionFlags.GetSetBits())
            {
                GameObject.ClearModelConditionState(flag);
            }
        }

        if (_data.RemoveConditionFlagsInRange != null)
        {
            foreach (var flag in _data.RemoveConditionFlagsInRange.GetSetBits())
            {
                GameObject.ClearModelConditionState(flag);
            }
        }

        if (_data.AddTempConditionFlag != ModelConditionFlag.None && _data.TempConditionTime > LogicFrameSpan.Zero)
        {
            GameObject.SetModelConditionState(_data.AddTempConditionFlag);
            _tempFlagActive = true;
            _tempFlagExpiry = Context.CurrentFrame + _data.TempConditionTime;
            SetWakeFrame(UpdateSleepTime.Frames(_data.TempConditionTime));
        }
    }

    /// <summary>Clears the temp flag once its scheduled frame arrives; sleeps forever once
    /// there is nothing left pending (this module never otherwise needs to tick).</summary>
    public override UpdateSleepTime Update()
    {
        if (_tempFlagActive && Context.CurrentFrame >= _tempFlagExpiry)
        {
            GameObject.ClearModelConditionState(_data.AddTempConditionFlag);
            _tempFlagActive = false;
        }

        if (!_tempFlagActive)
        {
            return UpdateSleepTime.Forever;
        }

        return UpdateSleepTime.Frames(_tempFlagExpiry - Context.CurrentFrame);
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
        xfer.XferBool("TempFlagActive", ref _tempFlagActive);
        xfer.XferFrame("TempFlagExpiry", ref _tempFlagExpiry);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Switches to a model condition state via upgrades.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class ModelConditionUpgradeModuleData : UpgradeModuleData
{
    internal static ModelConditionUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ModelConditionUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ModelConditionUpgradeModuleData>
        {
            { "ConditionFlag", (parser, x) => x.ConditionFlag = parser.ParseEnum<ModelConditionFlag>() },
            { "AddConditionFlags", (parser, x) => x.AddConditionFlags = parser.ParseEnumBitArray<ModelConditionFlag>() },
            { "RemoveConditionFlags", (parser, x) => x.RemoveConditionFlags = parser.ParseEnumBitArray<ModelConditionFlag>() },
            { "RemoveConditionFlagsInRange", (parser, x) => x.RemoveConditionFlagsInRange = parser.ParseEnumBitArray<ModelConditionFlag>() },
            { "AddTempConditionFlag", (parser, x) => x.AddTempConditionFlag = parser.ParseAttributeEnum<ModelConditionFlag>("ModelConditionState") },
            { "TempConditionTime", (parser, x) => x.TempConditionTime = parser.ParseDurationLogicFramesSeconds() },
        });

    public ModelConditionFlag ConditionFlag { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public BitArray<ModelConditionFlag> AddConditionFlags { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public BitArray<ModelConditionFlag> RemoveConditionFlags { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public BitArray<ModelConditionFlag> RemoveConditionFlagsInRange { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public ModelConditionFlag AddTempConditionFlag { get; private set; }

    /// <summary>Seconds in the INI, ceil-quantized to whole logic frames at parse time (S5).</summary>
    [AddedIn(SageGame.Bfme2)]
    public LogicFrameSpan TempConditionTime { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ModelConditionUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
