// StancesBehavior - R11 Track B port. BFME2-only (no generals-gpl sibling) and no clean-room
// spec in bfme2-workbench/research/ (searched: spec-hordes.md mentions stances only as horde
// UI), so this is the minimal runtime the job-009 INI chain needs: the module owns which of
// the StanceTemplate's stances is current, switchable through a deterministic sim entry point
// (the retail path arrives via the STANCEBATTLE/STANCEAGGRESSIVE/STANCEHOLDGROUND
// AISpecialPowerUpdate buttons, unported).
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - the retail default stance on spawn (index 0 = the template's first Stance here);
//   - per-stance AttributeModifier application (the parsed StanceAttributeModifier carries
//     only a MeleeBehavior name in this parser; no modifier plumbing is attached);
//   - per-stance MeleeBehavior handoff to the horde melee logic;
//   - whether the upgrade mux (UpgradeModuleData base) gates stance availability or the
//     whole module (modeled: the mux is honored but triggers no effect of its own).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class StancesBehavior : BehaviorModule, IUpgradeableModule
{
    private readonly StancesBehaviorModuleData _data;
    private readonly StanceTemplate _template;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Index into the template's Stances list; 0 on spawn (TODO-spec: retail default).</summary>
    private int _currentStance;

    public StancesBehavior(GameObject gameObject, ISimContext context, StancesBehaviorModuleData data, StanceTemplate template)
        : base(gameObject, context)
    {
        _data = data;
        _template = template;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // The stance set itself has no one-shot trigger effect; the mux state is tracked
        // so authored TriggeredBy/StartsActive data round-trips (TODO-spec).
    }

    public int StanceCount => _template?.Stances.Count ?? 0;

    /// <summary>The current stance's authored name, or null when the template is missing/empty.</summary>
    public string CurrentStanceName
        => _template != null && _currentStance < _template.Stances.Count
            ? _template.Stances[_currentStance].Name
            : null;

    /// <summary>
    /// Deterministic stance switch (the sim entry the stance command buttons will drive).
    /// Unknown names are ignored, matching the defensive posture of the other ported
    /// entry points. Returns whether the stance changed.
    /// </summary>
    public bool SetStance(string stanceName)
    {
        if (_template == null)
        {
            return false;
        }
        for (var i = 0; i < _template.Stances.Count; i++)
        {
            if (_template.Stances[i].Name == stanceName)
            {
                if (_currentStance == i)
                {
                    return false;
                }
                _currentStance = i;
                return true;
            }
        }
        return false;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
        xfer.XferInt("CurrentStance", ref _currentStance);
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class StancesBehaviorModuleData : UpgradeModuleData
{
    internal static StancesBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private new static readonly IniParseTable<StancesBehaviorModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<StancesBehaviorModuleData>
        {
            { "StanceTemplate", (parser, x) => x.StanceTemplate = parser.ParseIdentifier() },
        });

    public string StanceTemplate { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        // Template resolution happens on the engine side of the seam; the module holds the
        // immutable parsed asset (null-tolerant: authored data may name a missing template).
        var template = StanceTemplate != null
            ? gameEngine.AssetStore.StanceTemplates.GetByName(StanceTemplate)
            : null;
        return new StancesBehavior(gameObject, gameEngine.SimContext, this, template);
    }
}
