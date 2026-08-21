using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class FireWeaponUpdate : UpdateModule
{
    private byte _unknownByte;
    private string _weaponTemplate;

    private uint _unknownUInt1;
    private uint _unknownUInt2;

    private LogicFrame _unknownFrame1;
    private LogicFrame _unknownFrame2;
    private LogicFrame _unknownFrame3;
    private LogicFrame _unknownFrame4;
    private LogicFrame _unknownFrame5;

    private uint _unknownUInt3;
    private uint _unknownUInt4;
    private uint _unknownUInt5;

    public FireWeaponUpdate(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    public override UpdateSleepTime Update()
    {
        // TODO(Port): Use correct value.
        return UpdateSleepTime.None;
    }

    internal override void Load(StatePersister reader)
    {
        var version = reader.PersistVersion(2);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistByte(ref _unknownByte);
        reader.PersistAsciiString(ref _weaponTemplate);

        reader.SkipUnknownBytes(4);

        reader.PersistUInt32(ref _unknownUInt1);
        if (_unknownUInt1 != 2 && _unknownUInt1 != 3) // was 3 for NukeRadiationFieldWeapon
        {
            throw new InvalidStateException();
        }
        reader.PersistUInt32(ref _unknownUInt2); // something large, was 1 for NukeRadiationFieldWeapon

        reader.PersistLogicFrame(ref _unknownFrame1);
        reader.SkipUnknownBytes(4);
        reader.PersistLogicFrame(ref _unknownFrame2);
        reader.PersistLogicFrame(ref _unknownFrame3);
        reader.PersistLogicFrame(ref _unknownFrame4);

        reader.SkipUnknownBytes(8);

        reader.PersistUInt32(ref _unknownUInt3);

        reader.PersistUInt32(ref _unknownUInt4);
        if (_unknownUInt4 != 1)
        {
            throw new InvalidStateException();
        }
        reader.PersistUInt32(ref _unknownUInt5);
        if (_unknownUInt5 != 1)
        {
            throw new InvalidStateException();
        }

        reader.SkipUnknownBytes(4);

        if (version >= 2)
        {
            reader.PersistLogicFrame(ref _unknownFrame5);
        }
    }
}

public sealed class FireWeaponUpdateModuleData : UpdateModuleData
{
    internal static FireWeaponUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FireWeaponUpdateModuleData> FieldParseTable = new IniParseTable<FireWeaponUpdateModuleData>
    {
        { "Weapon", (parser, x) => x.Weapon = parser.ParseWeaponTemplateReference() },
        { "ExclusiveWeaponDelay", (parser, x) => x.ExclusiveWeaponDelay = parser.ParseTimeMillisecondsToLogicFrames() },
        { "InitialDelay", (parser, x) => x.InitialDelay = parser.ParseTimeMillisecondsToLogicFrames() },
        { "ChargingModeTrigger", (parser, x) => x.ChargingModeTrigger = parser.ParseBoolean() },
        { "AliveOnly", (parser, x) => x.AliveOnly = parser.ParseBoolean() },
        { "HeroModeTrigger", (parser, x) => x.HeroModeTrigger = parser.ParseBoolean() },
        { "FireWeaponNugget", (parser, x) => x.FireWeaponNugget = WeaponNugget.Parse(parser) }
    };

    public LazyAssetReference<WeaponTemplate> Weapon { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public LogicFrameSpan ExclusiveWeaponDelay { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public LogicFrameSpan InitialDelay { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool ChargingModeTrigger { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool AliveOnly { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool HeroModeTrigger { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public WeaponNugget FireWeaponNugget { get; private set; }

    internal override FireWeaponUpdate CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FireWeaponUpdate(gameObject, gameEngine);
    }
}

[AddedIn(SageGame.Bfme2)]
public sealed class WeaponNugget
{
    internal static WeaponNugget Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);
    private static readonly IniParseTable<WeaponNugget> FieldParseTable = new IniParseTable<WeaponNugget>
    {
        // -> LazyAssetReference<WeaponTemplate> (S5; DamageFieldUpdateModuleData port spec §2.5/§3):
        // the same resolved-once-at-construction shape PointDefenseLaserUpdateModuleData.WeaponTemplate
        // and FireWeaponUpdateModuleData.Weapon already use. No caller reads WeaponName as a bare
        // string (verified by grep), so this is not a breaking change.
        { "WeaponName", (parser, x) => x.WeaponName = parser.ParseWeaponTemplateReference() },
        // held: FireDelay / OneShot / Offset - parsed, not modeled. No GPL correspondence
        // (GeneralsMD's same-named FireWeaponNugget is an unrelated ObjectCreationList nugget),
        // no landed consumer, and no data-derivation: every DamageFieldUpdate call site in
        // unmodified AotR authors FireDelay = 0, OneShot = No, and no Offset. See
        // DamageFieldUpdate.cs's file header for the full gap note.
        { "FireDelay", (parser, x) => x.FireDelay = parser.ParseInteger() },
        { "OneShot", (parser, x) => x.OneShot = parser.ParseBoolean() },
        { "Offset", (parser, x) => x.Offset = parser.ParseVector3() }
    };
    public LazyAssetReference<WeaponTemplate> WeaponName { get; private set; }
    // held: parsed, not modeled - see DamageFieldUpdate.cs file header.
    public int FireDelay { get; private set; }
    // held: parsed, not modeled - see DamageFieldUpdate.cs file header.
    public bool OneShot { get; private set; }
    // held: parsed, not modeled - see DamageFieldUpdate.cs file header.
    public Vector3 Offset { get; private set; }
}
