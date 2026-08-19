using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

internal sealed class WeaponBonusUpgrade : UpgradeModule
{
    private readonly WeaponBonusUpgradeModuleData _moduleData;

    internal WeaponBonusUpgrade(GameObject gameObject, IGameEngine gameEngine, WeaponBonusUpgradeModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    // Behavioral reference: generals-gpl GeneralsMD WeaponBonusUpgrade.cpp
    // (upgradeImplementation) - fresh code, GPL used as a fact source only. The GPL body is
    // "as long as the comment": flag the Object with the PLAYER_UPGRADE weapon-bonus condition
    // and let the WeaponSet chooser pick the WeaponBonus= scaled stats from INI.
    //   Object *obj = getObject();
    //   obj->setWeaponBonusCondition( WEAPONBONUSCONDITION_PLAYER_UPGRADE );
    // WEAPONBONUSCONDITION_PLAYER_UPGRADE maps to WeaponBonusType.PlayerUpgrade (GameData.cs,
    // IniEnum "PLAYER_UPGRADE"). The condition is stored on the GameObject's _weaponBonusTypes
    // bit array (the same channel HordeUpdate feeds), which the object persists directly, so
    // this module carries no mutable state of its own - exactly like GPL's stateless xfer
    // (version byte + base). Sibling: WeaponSetUpgrade.OnUpgrade sets the parallel WeaponSet
    // PLAYER_UPGRADE condition.
    protected override void OnUpgrade()
    {
        GameObject.AddWeaponBonusType(WeaponBonusType.PlayerUpgrade);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Triggers use of WeaponBonus = parameter on this object's weapons.
/// </summary>
// [SimDataAudited]: this ModuleData has no fields of its own - the GPL module defines no
// buildFieldParse, so it contributes nothing beyond the shared upgrade-mux table
// (TriggeredBy/ConflictsWith/... on UpgradeModuleData). There are therefore zero numeric fields
// requiring S5 quantized-vocabulary conversion, and the audit is trivially satisfied.
[SimDataAudited]
public sealed class WeaponBonusUpgradeModuleData : UpgradeModuleData
{
    internal static WeaponBonusUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<WeaponBonusUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<WeaponBonusUpgradeModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new WeaponBonusUpgrade(gameObject, gameEngine, this);
    }
}
