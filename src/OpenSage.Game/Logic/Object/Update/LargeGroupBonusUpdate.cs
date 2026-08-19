// LargeGroupBonusUpdate - R11 Track B port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/, so this is the minimal behavior the job-009
// INI chain needs (AotR MordorFighterHorde ModuleTag_LargeGroupBonus: every UpdateRate ms,
// count nearby friendly Mordor infantry; at Count or more within Radius the horde gains the
// authored AttributeModifier, dropping below loses it again).
//
// The count consumes the landed S3 partition seam (QueryObjectsInRadius, ascending
// ObjectId); the modifier grant goes through the engine's attribute-modifier registry
// (same seam as the AttributeModifierUpgrade port, same headless caveat: effects apply in
// the legacy Scene3D modifier loop, the registration itself is the sim-visible output).
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - RubOffRadius (the bonus "rubbing off" onto members near a bonus-holder) - not modeled;
//   - whether the retail scan staggers its first tick with a logic-RNG draw (modeled: plain
//     cadence from the first frame, no draw, so the RNG stream is undisturbed);
//   - whether the counted set includes the scanning object itself (modeled: yes when it
//     passes the filter, matching the plain reading of "units within Radius");
//   - ObjectFilter template-name matching: the engine's ObjectFilter.Matches tests KindOf
//     bits only, while this module's authored filters name member templates
//     (NONE +MordorFighter ...), so the name lists are matched here module-locally.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class LargeGroupBonusUpdate : UpdateModule
{
    private readonly LargeGroupBonusUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether the group bonus is currently granted.</summary>
    private bool _bonusActive;

    public LargeGroupBonusUpdate(GameObject gameObject, ISimContext context, LargeGroupBonusUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // A zero cadence or an unusable bonus parks the module forever.
        if (_data.UpdateRate.Value > 0 && _data.Count > 0 && _data.Radius > Fix64.Zero)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.UpdateRate));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public bool BonusActive => _bonusActive;

    public override UpdateSleepTime Update()
    {
        var count = 0;
        var owner = GameObject.Owner;
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.Radius))
        {
            if (candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }
            if (_data.AlliesOnly && !IsFriendly(owner, candidate.Owner))
            {
                continue;
            }
            if (MatchesMemberFilter(candidate))
            {
                count++;
            }
        }

        var qualifies = count >= _data.Count;
        if (qualifies && !_bonusActive)
        {
            ApplyBonus();
            _bonusActive = true;
        }
        else if (!qualifies && _bonusActive)
        {
            RemoveBonus();
            _bonusActive = false;
        }

        return UpdateSleepTime.Frames(_data.UpdateRate);
    }

    private static bool IsFriendly(Player owner, Player candidateOwner)
    {
        if (owner is null || candidateOwner is null)
        {
            return false;
        }
        return ReferenceEquals(owner, candidateOwner) || owner.Allies.Contains(candidateOwner);
    }

    /// <summary>KindOf bits via ObjectFilter.Matches, template names module-locally
    /// (TODO-spec note above).</summary>
    private bool MatchesMemberFilter(GameObject candidate)
    {
        var filter = _data.HordeMemberFilter;
        if (filter == null)
        {
            return false;
        }
        // The filter's name lists are stored uppercased by ObjectFilter.Parse; template
        // names compare case-insensitively (SAGE INI is case-insensitive throughout).
        if (filter.ExcludeThings != null)
        {
            foreach (var name in filter.ExcludeThings)
            {
                if (string.Equals(name, candidate.Definition.Name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        if (filter.IncludeThings != null)
        {
            foreach (var name in filter.IncludeThings)
            {
                if (string.Equals(name, candidate.Definition.Name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return filter.Matches(candidate);
    }

    private void ApplyBonus()
    {
        var list = _data.AttributeModifier?.Value;
        if (list != null)
        {
            GameObject.AddAttributeModifier(list.Name, new Logic.AttributeModifier(list));
        }
    }

    private void RemoveBonus()
    {
        var list = _data.AttributeModifier?.Value;
        if (list != null && GameObject.HasAttributeModifier(list.Name))
        {
            GameObject.RemoveAttributeModifier(list.Name);
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("BonusActive", ref _bonusActive);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class LargeGroupBonusUpdateModuleData : UpdateModuleData
{
    internal static LargeGroupBonusUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<LargeGroupBonusUpdateModuleData> FieldParseTable = new IniParseTable<LargeGroupBonusUpdateModuleData>
    {
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "UpdateRate", (parser, x) => x.UpdateRate = parser.ParseDurationLogicFrames() },
        { "HordeMemberFilter", (parser, x) => x.HordeMemberFilter = ObjectFilter.Parse(parser) },
        { "Count", (parser, x) => x.Count = parser.ParseInteger() },
        // Deterministic S3-query radii -> Fix64 (never float across the analyzer wall).
        { "Radius", (parser, x) => x.Radius = parser.ParseFix64() },
        { "RubOffRadius", (parser, x) => x.RubOffRadius = parser.ParseFix64() },
        { "AlliesOnly", (parser, x) => x.AlliesOnly = parser.ParseBoolean() },
        { "AttributeModifier", (parser, x) => x.AttributeModifier = parser.ParseModifierListReference() }
    };

    /// <summary>Frames between group scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan UpdateRate { get; private set; }

    public ObjectFilter HordeMemberFilter { get; private set; }

    /// <summary>Members within Radius needed before the bonus applies.</summary>
    public int Count { get; private set; }

    public Fix64 Radius { get; private set; }

    /// <summary>Reach of the bonus rub-off (TODO-spec: not modeled).</summary>
    public Fix64 RubOffRadius { get; private set; }

    public bool AlliesOnly { get; private set; }

    public LazyAssetReference<ModifierList> AttributeModifier { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new LargeGroupBonusUpdate(gameObject, gameEngine.SimContext, this);
    }
}
