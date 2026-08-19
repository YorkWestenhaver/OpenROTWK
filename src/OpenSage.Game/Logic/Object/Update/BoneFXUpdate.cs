using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

public sealed class BoneFXUpdate : UpdateModule
{
    private readonly List<uint> _particleSystemIds = new();
    private readonly int[] _unknownInts = new int[96];

    public BoneFXUpdate(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// The body damage state this update last observed. Set by <see cref="ChangeBodyDamageState"/>
    /// (GPL <c>m_curBodyState = newState</c>); it selects which pristine/rubble particle systems
    /// and FX lists the (still-unported) FX subsystem should spawn.
    /// </summary>
    /// <remarks>
    /// This is the landing point for <see cref="BoneFXDamage"/>'s
    /// <c>OnBodyDamageStateChange</c> relay. BoneFXUpdate itself is NOT yet ported (Update is a
    /// stub and the module carries no contract Xfer), so this field does not yet join the Objects
    /// CRC channel — it will when BoneFXUpdate ports. See modules-r7/BoneFXDamage.md finding
    /// F-BFXD-1.
    /// </remarks>
    public BodyDamageType CurrentBodyState { get; private set; }

    public override UpdateSleepTime Update()
    {
        // TODO(Port): Use correct value.
        return UpdateSleepTime.None;
    }

    /// <summary>
    /// Called by the paired <see cref="BoneFXDamage"/> module when the owner's body damage state
    /// transitions. GPL <c>BoneFXUpdate::changeBodyDamageState</c> does three things:
    /// records the new state, kills the running particle systems, and re-initialises the per-bone
    /// spawn timers. Only the first (the sim-visible <see cref="CurrentBodyState"/> assignment) is
    /// wired here; the particle-kill and timer re-init are client-FX subsystem work that belongs
    /// to the BoneFXUpdate port (still a Load-only stub), so they remain a TODO(Port).
    /// </summary>
    public void ChangeBodyDamageState(BodyDamageType oldState, BodyDamageType newState)
    {
        CurrentBodyState = newState;

        // TODO(Port, BoneFXUpdate): killRunningParticleSystems() + initTimes() — client-side FX
        // spawn/kill and per-bone timer re-init. Inert until BoneFXUpdate is ported.
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistList(
            _particleSystemIds,
            static (StatePersister persister, ref uint item) =>
            {
                persister.PersistUInt32Value(ref item);
            });

        reader.PersistArray(
            _unknownInts, static (StatePersister persister, ref int item) =>
            {
                persister.PersistInt32Value(ref item);

                if (persister.Mode == StatePersistMode.Read && item != -1)
                {
                    throw new InvalidStateException();
                }
            });

        reader.SkipUnknownBytes(289 * 4);

        var unknown1 = 1;
        reader.PersistInt32(ref unknown1);
        if (unknown1 != 1)
        {
            throw new InvalidStateException();
        }

        var unknown2 = true;
        reader.PersistBoolean(ref unknown2);
        if (!unknown2)
        {
            throw new InvalidStateException();
        }
    }
}

public sealed class BoneFXUpdateModuleData : UpdateModuleData
{
    internal static BoneFXUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BoneFXUpdateModuleData> FieldParseTable = new IniParseTable<BoneFXUpdateModuleData>
    {
        { "DamageFXTypes", (parser, x) => x.DamageFXTypes = parser.ParseEnumBitArray<DamageType>() },
        { "RubbleFXList1", (parser, x) => x.RubbleFXList1 = BoneFXUpdateFXList.Parse(parser) },

        { "DamageParticleTypes", (parser, x) => x.DamageParticleTypes = parser.ParseEnumBitArray<DamageType>() },
        { "PristineParticleSystem1", (parser, x) => x.PristineParticleSystem1 = BoneFXUpdateParticleSystem.Parse(parser) },
        { "RubbleParticleSystem1", (parser, x) => x.RubbleParticleSystem1 = BoneFXUpdateParticleSystem.Parse(parser) },

        { "PristineParticleSystem2", (parser, x) => x.PristineParticleSystem2 = BoneFXUpdateParticleSystem.Parse(parser) },

        { "PristineParticleSystem3", (parser, x) => x.PristineParticleSystem3 = BoneFXUpdateParticleSystem.Parse(parser) },

        { "PristineParticleSystem4", (parser, x) => x.PristineParticleSystem4 = BoneFXUpdateParticleSystem.Parse(parser) },

        { "PristineParticleSystem5", (parser, x) => x.PristineParticleSystem5 = BoneFXUpdateParticleSystem.Parse(parser) },

        { "PristineParticleSystem6", (parser, x) => x.PristineParticleSystem6 = BoneFXUpdateParticleSystem.Parse(parser) },
    };

    public BitArray<DamageType> DamageFXTypes { get; private set; }
    public BoneFXUpdateFXList RubbleFXList1 { get; private set; }

    public BitArray<DamageType> DamageParticleTypes { get; private set; }
    public BoneFXUpdateParticleSystem PristineParticleSystem1 { get; private set; }
    public BoneFXUpdateParticleSystem RubbleParticleSystem1 { get; private set; }

    public BoneFXUpdateParticleSystem PristineParticleSystem2 { get; private set; }

    public BoneFXUpdateParticleSystem PristineParticleSystem3 { get; private set; }

    public BoneFXUpdateParticleSystem PristineParticleSystem4 { get; private set; }

    public BoneFXUpdateParticleSystem PristineParticleSystem5 { get; private set; }

    public BoneFXUpdateParticleSystem PristineParticleSystem6 { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BoneFXUpdate(gameObject, gameEngine);
    }
}

public sealed class BoneFXUpdateFXList
{
    internal static BoneFXUpdateFXList Parse(IniParser parser)
    {
        return new BoneFXUpdateFXList
        {
            Bone = parser.ParseAttribute("Bone", parser.ScanBoneName),
            OnlyOnce = parser.ParseAttributeBoolean("OnlyOnce"),
            Min = parser.ParseInteger(),
            Max = parser.ParseInteger(),
            FXList = parser.ParseAttribute("FXList", parser.ScanAssetReference)
        };
    }

    public string Bone { get; private set; }
    public bool OnlyOnce { get; private set; }
    public int Min { get; private set; }
    public int Max { get; private set; }
    public string FXList { get; private set; }
}

public sealed class BoneFXUpdateParticleSystem
{
    internal static BoneFXUpdateParticleSystem Parse(IniParser parser)
    {
        return new BoneFXUpdateParticleSystem
        {
            Bone = parser.ParseAttribute("Bone", parser.ScanBoneName),
            OnlyOnce = parser.ParseAttributeBoolean("OnlyOnce"),
            Min = parser.ParseInteger(),
            Max = parser.ParseInteger(),
            ParticleSystem = parser.ParseAttribute("PSys", parser.ScanAssetReference)
        };
    }

    public string Bone { get; private set; }
    public bool OnlyOnce { get; private set; }
    public int Min { get; private set; }
    public int Max { get; private set; }
    public string ParticleSystem { get; private set; }
}
