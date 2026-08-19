// BloodthirstyUpdate - R11 Track B port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/, so this is the minimal behavior the INI
// chain needs (AotR MordorFighterHorde ModuleTag_Bloodthirsty: SacrificeFilter = ALL,
// ExperienceModifier = 1.00; driven in retail by the BloodThirstyFerocity special power,
// unported): a deterministic sacrifice entry point - consume a friendly victim that both
// sides' filters allow, kill it, and bank its experience value scaled by ExperienceModifier.
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - the retail trigger path (special power / AI) and its victim SEARCH (radius, count via
//     NumToSacrifice, nearest-first order) - modeled as an explicit per-victim entry point
//     the special-power port will drive;
//   - InitiateVoice/InitiateVoice2 are EVA/client audio (S8: audio is deliberately absent
//     from ISimContext) - not modeled;
//   - the exact experience amount banked per victim (modeled: the victim's own
//     ExperienceValue for its current level, scaled by ExperienceModifier, floor).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class BloodthirstyUpdate : UpdateModule
{
    private readonly BloodthirstyUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>How many victims this module has consumed (retail caps at NumToSacrifice).</summary>
    private int _numSacrificed;

    public BloodthirstyUpdate(GameObject gameObject, ISimContext context, BloodthirstyUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No periodic behavior: the module acts only through the sacrifice entry point.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public override UpdateSleepTime Update() => UpdateSleepTime.Forever;

    public int NumSacrificed => _numSacrificed;

    /// <summary>Whether another sacrifice is allowed (NumToSacrifice 0 = unlimited).</summary>
    public bool CanSacrifice => _data.NumToSacrifice <= 0 || _numSacrificed < _data.NumToSacrifice;

    /// <summary>
    /// Consume one victim: it must be live, distinct from the consumer, pass this module's
    /// SacrificeFilter, and itself carry a BloodthirstyUpdate ("in order to sacrifice or be
    /// sacrificed, you must have a BloodthirstyUpdate" - the authored data's own comment).
    /// The victim dies and its experience value (scaled by ExperienceModifier) is banked on
    /// the consumer. Returns whether the sacrifice happened.
    /// </summary>
    public bool Sacrifice(GameObject victim)
    {
        if (!CanSacrifice ||
            victim == null || victim == GameObject ||
            victim.IsDestroyed || victim.IsEffectivelyDead)
        {
            return false;
        }
        if (_data.SacrificeFilter != null && !_data.SacrificeFilter.Matches(victim))
        {
            return false;
        }
        if (victim.FindBehavior<BloodthirstyUpdate>() == null)
        {
            return false;
        }

        // The victim's experience worth at its current level, scaled by ExperienceModifier
        // (Fix64 multiply, floor to int - the F4-safe integer crossing).
        var worth = victim.Definition.ExperienceValue?[victim.ExperienceTracker.VeterancyLevel] ?? 0;
        var scaled = (int)((Fix64)worth * _data.ExperienceModifier);

        victim.Kill();
        _numSacrificed++;

        if (scaled > 0)
        {
            // Feedback suppressed: the promotion fanfare is client audio (S8) and the
            // headless host has no audio system.
            var tracker = GameObject.ExperienceTracker;
            tracker.SetExperienceAndLevel(tracker.CurrentExperience + scaled, provideFeedback: false);
        }
        return true;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("NumSacrificed", ref _numSacrificed);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class BloodthirstyUpdateModuleData : UpdateModuleData
{
    internal static BloodthirstyUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BloodthirstyUpdateModuleData> FieldParseTable = new IniParseTable<BloodthirstyUpdateModuleData>
    {
        { "SacrificeFilter", (parser, x) => x.SacrificeFilter = ObjectFilter.Parse(parser) },
        { "NumToSacrifice", (parser, x) => x.NumToSacrifice = parser.ParseInteger() },
        { "InitiateVoice", (parser, x) => x.InitiateVoice = parser.ParseAssetReference() },
        { "InitiateVoice2", (parser, x) => x.InitiateVoice2 = parser.ParseAssetReference() },
        { "ExperienceModifier", (parser, x) => x.ExperienceModifier = parser.ParseFix64() },
    };

    public ObjectFilter SacrificeFilter { get; private set; }

    /// <summary>Sacrifice budget; 0 (unauthored) = unlimited (TODO-spec).</summary>
    public int NumToSacrifice { get; private set; }

    /// <summary>Client audio key (unmodeled, S8).</summary>
    public string InitiateVoice { get; private set; }

    /// <summary>Client audio key (unmodeled, S8).</summary>
    public string InitiateVoice2 { get; private set; }

    /// <summary>Scale on the experience banked per victim (exact-decimal Fix64).</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 ExperienceModifier { get; private set; } = Fix64.One;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BloodthirstyUpdate(gameObject, gameEngine.SimContext, this);
    }
}
