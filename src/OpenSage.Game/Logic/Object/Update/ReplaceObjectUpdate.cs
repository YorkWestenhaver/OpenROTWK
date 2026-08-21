// ReplaceObjectUpdate - R12 port (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD ReplaceObjectUpgrade.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). The GPL sibling is an
// UpgradeModule, not an UpdateModule - BFME2 rebuilt the same core action
// ("replace myself with a fresh instance of another template, in place") behind a
// SpecialPowerTemplate-triggered UpdateModule with timed phases, and generals-gpl carries no
// source for that BFME2-only shell. There is likewise no clean-room spec under
// bfme2-workbench/research for this file specifically, so the BFME2-only additions below are
// implemented from the plain field semantics the INI vocabulary itself states, kept as small
// and literal as the field names allow, with every non-obvious choice called out as a finding
// rather than folded in silently (CLEAN-ROOM RULE: no binary-derived content, here or
// anywhere - these are engineering choices over an admitted spec gap, not derived facts).
//
// CORE (GPL-faithful) behavior, translated: on trigger, capture the object's position/team
// (implicit here: the donor GameObject itself is read after being marked destroyed, which is
// deliberately legal - api-freeze-v1's "destroy is same-frame visible" contract, see
// IGameLogic.DestroyObject's own doc), destroy the original, create the replacement at the
// same transform, and call OnBuildComplete on the replacement's create modules (GPL's own
// comment: "onCreates were called at the constructor... this magically created thing needs to
// be considered as Built for Game specific stuff").
//
// BFME2 additions, implemented:
//   - PreparationTime / UnpackTime: a two-stage timer gate (Preparing -> Unpacking -> replace)
//     driven every tick, LogicFrameSpan-quantized. A zero duration skips straight through its
//     stage (no observable delay), matching the ordinary "zero means immediate" SAGE timer
//     convention used throughout this update-module family (compare DemoTrapUpdate's
//     ScanRate=0 special-case absence: here it is explicit because two stages chain).
//   - Scatter + ReplaceRadius: a uniform random point in the disc of radius ReplaceRadius
//     around the original's position (Context.GameLogicRandom.NextFix64 draws for angle and
//     radius, FixTrig for the sin/cos - the frozen Fix64-only randomness surface, S3). The
//     scattered replacement is placed at world-facing (yaw 0) rather than the donor's own
//     facing: IGameLogic's offset-placement overload takes an explicit orientation and this
//     seam has no "read the donor's own Fix64 facing" accessor to hand it (positions/
//     orientations are still float substrate off the FixVector3-offset overload's one
//     crossing point, D-7) - documented gap, not invented. The non-scatter path uses the
//     donor-matrix overload instead, which copies position AND rotation exactly, matching GPL.
//   - ReplaceObject.TargetObjectFilter / ReplacementObjectName: the filter is tested against
//     the object being replaced (the same "self" shape every landed ObjectFilter.Matches call
//     in this codebase uses - LargeGroupBonusUpdate, PickupStuffUpdate, BloodthirstyUpdate,
//     etc). A non-matching filter means no replacement configured (silent no-op, phase still
//     completes). Only a single ReplaceObject block is supported (matching the field's
//     existing non-array shape); the packet's "multi-object selection" is therefore modeled
//     as ReplacementObjectName[0] - the array's remaining entries are parsed and held but
//     unconsumed (same audited-but-inert posture as CreateObjectDie's DebrisPortionOfSelf).
//     There is no spec naming a selection rule across multiple names, so none is invented.
//   - AwardXPForTriggering: credited to the triggering object's own ExperienceTracker
//     (GameObject.ExperienceTracker.AddExperiencePoints - the landed veterancy surface every
//     other XP-granting path in this codebase already uses) at the moment the replacement
//     actually happens, not at the moment the ability was requested.
//   - StartAbilityRange: gates InitiateIntentToDoSpecialPower on the Fix64-safe deterministic
//     partition query (Context.Partition.QueryObjectsInRadius(GameObject, StartAbilityRange)
//     containing the triggering object) - the same proximity idiom DemoTrapUpdate/EmpUpdate
//     use, chosen because ISimContext carries no direct Fix64 position accessor for
//     [SimState] code (D-7: positions are float substrate until the transform migrates).
//
// BFME2 fields parsed and held, deliberately NOT modeled (audited gaps, not invented):
//   - SkipContinue, UnpackingVariation, PersistentPrepTime, PackTime, MustFinishAbility: no
//     GPL reference and no clean-room spec state their exact effect. PackTime in particular is
//     not exercised by this task's own testCases (which name only Preparation/Unpack in the
//     trigger sequence), so it is not guessed into the phase chain. MustFinishAbility's
//     precise interrupt-vs-reject semantics are unknown; this port's own safe default (a
//     re-trigger while already Preparing/Unpacking/Done is uniformly rejected) is an
//     engineering choice independent of the flag's value, not a model of it.
//   - Player controller notification (GPL's onStructureConstructionComplete callback to the
//     new owner's Player): no landed Player member exists for it anywhere in this codebase
//     (grep confirms) - finding, not invented; when a construction-complete notification
//     surface lands this is the module to wire it through.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using System.Linq;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ReplaceObjectUpdate : UpdateModule
{
    private readonly ReplaceObjectUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private ReplacePhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// The object that initiated the trigger (for AwardXPForTriggering), as reported to
    /// <see cref="InitiateIntentToDoSpecialPower"/>. Invalid when never triggered, or
    /// triggered with no source.
    /// </summary>
    private ObjectId _triggeringObjectId;

    public ReplaceObjectUpdate(GameObject gameObject, ISimContext context, ReplaceObjectUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = ReplacePhase.Idle;

        // Ticks every frame like the rest of this SpecialPowerTemplate-gated family
        // (MissileLauncherBuildingUpdate): the phase machine is cheap and this keeps the
        // wake-scheduling shape identical to that landed exemplar.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// GPL SpecialPowerUpdateInterface::initiateIntentToDoSpecialPower, BFME2-shaped: only
    /// this module's own special power (matched by template name) may fire it, and only while
    /// idle (no interrupting or re-triggering an in-flight replacement - see the
    /// MustFinishAbility note at the top of this file). <paramref name="triggeringObject"/> is
    /// the object that asked for the power (may be null); it gates StartAbilityRange and is
    /// the AwardXPForTriggering recipient. Driven input (no landed special-power/command
    /// system calls this yet), same posture as MissileLauncherBuildingUpdate's own trigger
    /// seam.
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != ReplacePhase.Idle)
        {
            return false;
        }

        if (_data.StartAbilityRange > Fix64.Zero && triggeringObject != null)
        {
            var inRange = Context.Partition
                .QueryObjectsInRadius(GameObject, _data.StartAbilityRange)
                .Contains(triggeringObject);

            if (!inRange)
            {
                return false;
            }
        }

        _triggeringObjectId = triggeringObject?.Id ?? ObjectId.Invalid;

        EnterPreparationOrLater();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case ReplacePhase.Preparing:
                if (now >= _phaseEndFrame)
                {
                    EnterUnpackingOrLater();
                }
                break;

            case ReplacePhase.Unpacking:
                if (now >= _phaseEndFrame)
                {
                    PerformReplace();
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void EnterPreparationOrLater()
    {
        if (_data.PreparationTime.Value > 0)
        {
            _phase = ReplacePhase.Preparing;
            _phaseEndFrame = Context.CurrentFrame + _data.PreparationTime;
        }
        else
        {
            EnterUnpackingOrLater();
        }
    }

    private void EnterUnpackingOrLater()
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = ReplacePhase.Unpacking;
            _phaseEndFrame = Context.CurrentFrame + _data.UnpackTime;
        }
        else
        {
            PerformReplace();
        }
    }

    /// <summary>
    /// GPL upgradeImplementation, BFME2-shaped: select the replacement template, destroy the
    /// original (pathfind-visible same frame, D-7/api-freeze-v1), create the replacement at
    /// the original's transform (or a scattered offset), run the replacement's onBuildComplete
    /// pass, queue it for pathfinding, and award any triggering XP.
    /// </summary>
    private void PerformReplace()
    {
        _phase = ReplacePhase.Done;

        var replacementDefinition = SelectReplacementDefinition();
        if (replacementDefinition == null)
        {
            // Filter didn't match, or no ReplacementObjectName configured: GPL's own
            // findTemplate-returned-NULL guard - a no-op, not a crash.
            return;
        }

        var me = GameObject;
        var owner = me.Owner;
        var team = me.Team;

        if (!string.IsNullOrEmpty(_data.ReplaceFX))
        {
            Context.Events.FireFXAtObjectPosition(_data.ReplaceFX, me.Id);
        }

        // GPL order: remove/destroy the original FIRST, then create the replacement - "if I
        // don't remove, then the new thing will be placed, and then on deletion I will remove
        // 'his' marks". IGameLogic.DestroyObject documents same-frame visibility, so `me`
        // stays a valid position/team donor for the CreateObjectAt call below.
        Context.GameLogic.DestroyObject(me);

        // R13 fix (finding #1): team-aware CreateObjectAt overloads stamp `team` onto the
        // replacement BEFORE its ICreateModule.OnCreate() pass runs, matching GPL's
        // TheThingFactory->newObject(replacementTemplate, myTeam) construction-time team
        // assignment - every onCreate handler now observes the correct team from its first
        // instruction, same as GPL, instead of seeing it only after a post-hoc assignment.
        GameObject replacement;
        if (_data.Scatter && _data.ReplaceRadius > Fix64.Zero)
        {
            var angle = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.PiTimes2);
            var radius = Context.GameLogicRandom.NextFix64(Fix64.Zero, _data.ReplaceRadius);
            var offset = new FixVector3(radius * FixTrig.Cos(angle), radius * FixTrig.Sin(angle), Fix64.Zero);

            replacement = Context.GameLogic.CreateObjectAt(replacementDefinition, owner, team, me, offset, Fix64.Zero);
        }
        else
        {
            // Donor-matrix overload: exact position AND rotation copy, matching GPL's own
            // myMatrix = *me->getTransformMatrix(); replacementObject->setTransformMatrix(...).
            replacement = Context.GameLogic.CreateObjectAt(replacementDefinition, owner, team, me);
        }

        if (replacement == null)
        {
            return;
        }

        // GPL: onCreates already ran in the constructor (with team already set - see the
        // team-aware CreateObjectAt call above); this loop is the "consider it Built"
        // pass every CreateModule needs to see once.
        foreach (var createModule in replacement.FindBehaviors<ICreateModule>())
        {
            createModule.OnBuildComplete();
        }

        // S5 pathfinding integration: queue the replacement for a path so it (and anything
        // routing around it) is grid-visible from here on.
        Context.GameLogic.PathfindQueueForPath(replacement.Id);

        if (_data.AwardXPForTriggering != 0 && _triggeringObjectId.IsValid)
        {
            var triggeringObject = Context.GameLogic.GetObjectById(_triggeringObjectId);
            triggeringObject?.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }
    }

    /// <summary>
    /// GPL findTemplate: resolves the configured ReplaceObject block against the object being
    /// replaced. See the file-header note on the single-block/first-name shape.
    /// </summary>
    private ObjectDefinition SelectReplacementDefinition()
    {
        var replaceObject = _data.ReplaceObject;
        if (replaceObject?.ReplacementObjectName == null || replaceObject.ReplacementObjectName.Length == 0)
        {
            return null;
        }

        if (replaceObject.TargetObjectFilter != null && !replaceObject.TargetObjectFilter.Matches(GameObject))
        {
            return null;
        }

        return replaceObject.ReplacementObjectName[0]?.Value;
    }

    private enum ReplacePhase
    {
        Idle,
        Preparing,
        Unpacking,
        Done,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum and the triggering-object identity are
    // lifecycle/identity facts, so Exact. The phase-end frame is a timer, so Quantum (ch.2),
    // matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferObjectId("TriggeringObjectId", ref _triggeringObjectId);
    }
}

[SimDataAudited]
public sealed class ReplaceObjectUpdateModuleData : UpdateModuleData
{
    internal static ReplaceObjectUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ReplaceObjectUpdateModuleData> FieldParseTable = new IniParseTable<ReplaceObjectUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "SkipContinue", (parser, x) => x.SkipContinue = parser.ParseBoolean() },
        { "UnpackingVariation", (parser, x) => x.UnpackingVariation = parser.ParseInteger() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        { "MustFinishAbility", (parser, x) => x.MustFinishAbility = parser.ParseBoolean() },
        { "ReplaceObject", (parser, x) => x.ReplaceObject = ReplaceObject.Parse(parser) },
        { "ReplaceRadius", (parser, x) => x.ReplaceRadius = parser.ParseFix64() },
        { "ReplaceFX", (parser, x) => x.ReplaceFX = parser.ParseAssetReference() },
        { "Scatter", (parser, x) => x.Scatter = parser.ParseBoolean() },
    };

    public string SpecialPowerTemplate { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool SkipContinue { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public int UnpackingVariation { get; private set; }

    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public LogicFrameSpan PersistentPrepTime { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public LogicFrameSpan PackTime { get; private set; }

    public int AwardXPForTriggering { get; private set; }
    public Fix64 StartAbilityRange { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool MustFinishAbility { get; private set; }

    public ReplaceObject ReplaceObject { get; private set; }
    public Fix64 ReplaceRadius { get; private set; }
    public string ReplaceFX { get; private set; }
    public bool Scatter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ReplaceObjectUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class ReplaceObject
{
    internal static ReplaceObject Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ReplaceObject> FieldParseTable = new IniParseTable<ReplaceObject>
    {
        { "TargetObjectFilter", (parser, x) => x.TargetObjectFilter = ObjectFilter.Parse(parser) },
        { "ReplacementObjectName", (parser, x) => x.ReplacementObjectName = parser.ParseObjectReferenceArray() }
    };

    public ObjectFilter TargetObjectFilter { get; private set; }
    public LazyAssetReference<ObjectDefinition>[] ReplacementObjectName { get; private set; }
}
