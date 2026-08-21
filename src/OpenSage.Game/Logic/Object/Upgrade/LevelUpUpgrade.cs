// LevelUpUpgrade - R11 Track B port. BFME-only (no generals-gpl sibling) and no clean-room
// spec in bfme2-workbench/research/, so this is the minimal behavior the job-009 INI chain
// needs (e.g. AotR MordorFighterHorde ModuleTag_BasicTraining: TriggeredBy training upgrade,
// LevelsToGain = 1, LevelCap = 2): when the upgrade mux fires, grant LevelsToGain experience
// levels through the object's ExperienceTracker, never raising the level above LevelCap.
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - BFME object levels are authored 1-based 1..10; this engine still carries the
//     4-slot Generals VeterancyLevel enum, so LevelCap = N caps at enum index N-1 and
//     everything clamps at Heroic. Re-audit when the BFME rank table ports.
//   - a LevelCap of 0 (unauthored) is treated as "no cap" (clamp at the enum top).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class LevelUpUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly LevelUpUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public LevelUpUpgrade(GameObject gameObject, ISimContext context, LevelUpUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// Grant LevelsToGain levels, capped at LevelCap (1-based; TODO-spec above). The grant
    /// goes through ExperienceTracker.SetVeterancyLevel with feedback suppressed: the
    /// promotion fanfare is client audio (S8), and the headless host has no audio system.
    /// </summary>
    private void OnUpgradeTriggered()
    {
        if (_data.LevelsToGain <= 0)
        {
            return;
        }

        var tracker = GameObject.ExperienceTracker;
        var current = (int)tracker.VeterancyLevel;
        var cap = _data.LevelCap > 0 ? _data.LevelCap - 1 : (int)VeterancyLevel.Last;
        if (cap > (int)VeterancyLevel.Last)
        {
            cap = (int)VeterancyLevel.Last;
        }

        var target = current + _data.LevelsToGain;
        if (target > cap)
        {
            target = cap;
        }

        if (target > current)
        {
            tracker.SetVeterancyLevel((VeterancyLevel)target, provideFeedback: false);
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9): the mux flag is
    // the entire per-module inventory; the level itself is ExperienceTracker-owned state.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class LevelUpUpgradeModuleData : UpgradeModuleData
{
    internal static LevelUpUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<LevelUpUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<LevelUpUpgradeModuleData>
        {
            { "LevelsToGain", (parser, x) => x.LevelsToGain = parser.ParseInteger() },
            { "LevelCap", (parser, x) => x.LevelCap = parser.ParseInteger() }
        });

    public int LevelsToGain { get; private set; }
    public int LevelCap { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new LevelUpUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
