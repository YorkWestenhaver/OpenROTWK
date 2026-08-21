// AttributeModifierUpgrade - R11 Track B port. BFME-only (no generals-gpl sibling) and no
// clean-room spec in bfme2-workbench/research/, so this is the minimal behavior the INI
// chain needs (e.g. AotR default/object.ini difficulty bonuses, TriggeredBy campaign/solo
// upgrades): when the upgrade mux fires, register the named ModifierList on the owning
// object through the engine's attribute-modifier registry.
//
// TODO-spec (unverified retail behavior, filed not invented):
//   - modifier EFFECTS are applied by the legacy GameObject.LogicTick modifier loop
//     (Scene3D-driven, float substrate); the headless sim host does not run that loop, so
//     in the harness the grant is observable via GameObject.HasAttributeModifier while the
//     stat effects remain unmodeled until the modifier system ports to the sim vocabulary;
//   - whether the retail module removes the modifier again when the triggering upgrade is
//     removed (modeled: apply-only, like the other one-shot upgrade ports).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AttributeModifierUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly AttributeModifierUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public AttributeModifierUpgrade(GameObject gameObject, ISimContext context, AttributeModifierUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        var list = _data.AttributeModifier?.Value;
        if (list != null)
        {
            GameObject.AddAttributeModifier(list.Name, new Logic.AttributeModifier(list));
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9): the mux flag is the
    // entire per-module inventory; the granted modifier lives in the GameObject registry.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class AttributeModifierUpgradeModuleData : UpgradeModuleData
{
    internal static AttributeModifierUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<AttributeModifierUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<AttributeModifierUpgradeModuleData>
        {
            { "AttributeModifier", (parser, x) => x.AttributeModifier = parser.ParseModifierListReference() }
        });

    public LazyAssetReference<ModifierList> AttributeModifier { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AttributeModifierUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
