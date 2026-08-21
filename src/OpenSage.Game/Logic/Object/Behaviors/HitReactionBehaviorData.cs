// HitReactionBehaviorData - PARSE SIDE, immutable flyweight, quantized at load
// (design-module-api §2.2). Split out of HitReactionBehavior.cs under the D-7 precedent
// (ProductionQueueHordeContainDamage.cs): the three float threshold fields are the module's
// other float-substrate crossing, and this file declares no [SimState] type, so the per-file
// SIMCORE quarantine scope never turns on for it.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class HitReactionBehaviorData : UpdateModuleData
{
    internal static HitReactionBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<HitReactionBehaviorData> FieldParseTable = new IniParseTable<HitReactionBehaviorData>
    {
        { "HitReactionLifeTimer1", (parser, x) => x.HitReactionLifeTimer1 = parser.ParseDurationLogicFrames() },
        { "HitReactionLifeTimer2", (parser, x) => x.HitReactionLifeTimer2 = parser.ParseDurationLogicFrames() },
        { "HitReactionLifeTimer3", (parser, x) => x.HitReactionLifeTimer3 = parser.ParseDurationLogicFrames() },
        { "HitReactionThreshold1", (parser, x) => x.HitReactionThreshold1 = parser.ParseFloat() },
        { "HitReactionThreshold2", (parser, x) => x.HitReactionThreshold2 = parser.ParseFloat() },
        { "HitReactionThreshold3", (parser, x) => x.HitReactionThreshold3 = parser.ParseFloat() },
        { "FastHitsResetReaction", (parser, x) => x.FastHitsResetReaction = parser.ParseBoolean() }
    };

    /// <summary>Level 1 (light damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer1 { get; private set; }

    /// <summary>Level 2 (medium damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer2 { get; private set; }

    /// <summary>Level 3 (heavy damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer3 { get; private set; }

    /// <summary>Thresholds stay float (F3: rate/time-adjacent scalar compared against the
    /// legacy float `DamageInfo.Result.ActualDamageDealt` - see
    /// FireWeaponWhenDamagedBehaviorModuleData.DamageAmount precedent, same field shape, same
    /// reasoning: IDamageModule's callback struct is still float-typed engine-wide, not yet
    /// migrated to Fix64/CombatDamageOutput).</summary>
    public float HitReactionThreshold1 { get; private set; }
    public float HitReactionThreshold2 { get; private set; }
    public float HitReactionThreshold3 { get; private set; }

    /// <summary>True = a qualifying hit during an active reaction restarts the timer to the
    /// new tier's full duration (swapping HitLevelN if the tier changed); false = the new hit
    /// is dropped and the in-flight reaction runs to its own completion.</summary>
    public bool FastHitsResetReaction { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new HitReactionBehavior(gameObject, gameEngine.SimContext, this);
}
