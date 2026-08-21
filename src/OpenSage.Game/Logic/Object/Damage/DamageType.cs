using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

// Numbering: retail BFME2/ROTWK DamageType index table, indices 0-27
// (bfme2-workbench/research/spec-aod-crush.md §2.3; corroborated cross-module by
// research/ghidra-lane-r14.md Q-D7). The numeric values are CRC/persist-visible by
// design — they must match the retail binary bit-for-bit.
//
// Names NOT in the retail BFME2 table (Generals/ZH members kept for legacy INI/save
// parsing, plus BFME1-era names) are packed contiguously above the retail range,
// starting at 28. Values must stay contiguous 0..N-1: BitArray<T>/EnumUtility size
// off member count / max value.
public enum DamageType
{
    // --- Retail BFME2 table, 0-27 (spec-aod-crush.md §2.3) ---

    [IniEnum("FORCE"), AddedIn(SageGame.Bfme)]
    Force = 0,

    [IniEnum("CRUSH")]
    Crush = 1,

    [IniEnum("SLASH"), AddedIn(SageGame.Bfme)]
    Slash = 2,

    [IniEnum("PIERCE"), AddedIn(SageGame.Bfme)]
    Pierce = 3,

    [IniEnum("SIEGE"), AddedIn(SageGame.Bfme)]
    Siege = 4,

    [IniEnum("STRUCTURAL"), AddedIn(SageGame.Bfme)]
    Structural = 5,

    [IniEnum("FLAME")]
    Flame = 6,

    [IniEnum("HEALING")]
    Healing = 7,

    /// <summary>
    /// This is for scripting to cause "armorproof" damage.
    /// </summary>
    [IniEnum("UNRESISTABLE")]
    Unresistable = 8,

    [IniEnum("WATER")]
    Water = 9,

    /// <summary>
    /// From game penalty (you won't receive radar warnings).
    /// </summary>
    [IniEnum("PENALTY")]
    Penalty = 10,

    [IniEnum("FALLING")]
    Falling = 11,

    /// <summary>
    /// Damage from getting toppled.
    /// </summary>
    [IniEnum("TOPPLING")]
    Toppling = 12,

    [IniEnum("REFLECTED"), AddedIn(SageGame.Bfme)]
    Reflected = 13,

    [IniEnum("PASSENGER"), AddedIn(SageGame.Bfme2)]
    Passenger = 14,

    [IniEnum("MAGIC"), AddedIn(SageGame.Bfme)]
    Magic = 15,

    [IniEnum("CHOP"), AddedIn(SageGame.Bfme)]
    Chop = 16,

    [IniEnum("HERO"), AddedIn(SageGame.Bfme)]
    Hero = 17,

    [IniEnum("SPECIALIST"), AddedIn(SageGame.Bfme)]
    Specialist = 18,

    [IniEnum("URUK"), AddedIn(SageGame.Bfme)]
    Uruk = 19,

    [IniEnum("HERO_RANGED"), AddedIn(SageGame.Bfme)]
    HeroRanged = 20,

    [IniEnum("FLY_INTO"), AddedIn(SageGame.Bfme)]
    FlyInto = 21,

    [IniEnum("UNDEFINED"), AddedIn(SageGame.Bfme2)]
    Undefined = 22,

    [IniEnum("LOGICAL_FIRE"), AddedIn(SageGame.Bfme2)]
    LogicalFire = 23,

    [IniEnum("CAVALRY"), AddedIn(SageGame.Bfme2)]
    Cavalry = 24,

    [IniEnum("CAVALRY_RANGED"), AddedIn(SageGame.Bfme2)]
    CavalryRanged = 25,

    [IniEnum("POISON")]
    Poison = 26,

    [IniEnum("FROST"), AddedIn(SageGame.Bfme2Rotwk)]
    Frost = 27,

    // --- Legacy names above the retail range (not in the retail BFME2 table;
    // kept so Generals/ZH/BFME1-era INI and save data still parses) ---

    [IniEnum("EXPLOSION")]
    Explosion = 28,

    [IniEnum("ARMOR_PIERCING")]
    ArmorPiercing = 29,

    [IniEnum("SMALL_ARMS")]
    SmallArms = 30,

    [IniEnum("GATTLING")]
    Gattling = 31, // [sic]

    [IniEnum("RADIATION")]
    Radiation = 32,

    [IniEnum("LASER")]
    Laser = 33,

    [IniEnum("SNIPER")]
    Sniper = 34,

    /// <summary>
    /// For transports to deploy units and order them to all attack.
    /// </summary>
    [IniEnum("DEPLOY")]
    Deploy = 35,

    /// <summary>
    /// If something "dies" to surrender damage, they surrender.
    /// </summary>
    [IniEnum("SURRENDER")]
    Surrender = 36,

    [IniEnum("HACK")]
    Hack = 37,

    /// <summary>
    /// Special snipe attack that kills the pilot and renders a vehicle unmanned.
    /// </summary>
    [IniEnum("KILL_PILOT")]
    KillPilot = 38,

    /// <summary>
    /// Blades, clubs, etc.
    /// </summary>
    [IniEnum("MELEE")]
    Melee = 39,

    /// <summary>
    /// "Special" damage type used for disarming mines, bombs, etc.
    /// (_not_ for disarming an opponent!)
    /// </summary>
    [IniEnum("DISARM")]
    Disarm = 40,

    /// <summary>
    /// Special damage type for cleaning up hazards like radiation or bio-poison.
    /// </summary>
    [IniEnum("HAZARD_CLEANUP")]
    HazardCleanup = 41,

    /// <summary>
    /// Incinerates virtually everything (insanely powerful orbital beam).
    /// </summary>
    [IniEnum("PARTICLE_BEAM")]
    ParticleBeam = 42,

    [IniEnum("INFANTRY_MISSILE")]
    InfantryMissile = 43,

    [IniEnum("AURORA_BOMB")]
    AuroraBomb = 44,

    [IniEnum("LAND_MINE")]
    LandMine = 45,

    [IniEnum("JET_MISSILES")]
    JetMissiles = 46,

    [IniEnum("STEALTHJET_MISSILES")]
    StealthjetMissiles = 47,

    [IniEnum("MOLOTOV_COCKTAIL")]
    MolotovCocktail = 48,

    [IniEnum("COMANCHE_VULCAN")]
    ComancheVulcan = 49,

    [IniEnum("FLESHY_SNIPER")]
    FleshySniper = 50,

    /// <summary>
    /// Damage that does not kill you, but produces some special effect based
    /// on your Body Module. Separate HP from normal damage.
    /// </summary>
    [IniEnum("SUBDUAL_MISSILE"), AddedIn(SageGame.CncGeneralsZeroHour)]
    SubdualMissile = 51,

    [IniEnum("SUBDUAL_VEHICLE"), AddedIn(SageGame.CncGeneralsZeroHour)]
    SubdualVehicle = 52,

    [IniEnum("SUBDUAL_BUILDING"), AddedIn(SageGame.CncGeneralsZeroHour)]
    SubdualBuilding = 53,

    [IniEnum("SUBDUAL_UNRESISTABLE"), AddedIn(SageGame.CncGeneralsZeroHour)]
    SubdualUnresistable = 54,

    /// <summary>
    /// Radiation that only affects infantry.
    /// </summary>
    [IniEnum("MICROWAVE"), AddedIn(SageGame.CncGeneralsZeroHour)]
    Microwave = 55,

    /// <summary>
    /// Kills passengers up to the number specified in damage.
    /// </summary>
    [IniEnum("KILL_GARRISONED"), AddedIn(SageGame.CncGeneralsZeroHour)]
    KillGarrisoned = 56,

    /// <summary>
    /// Damage that gives a status condition, not that does hitpoint damage.
    /// </summary>
    [IniEnum("STATUS"), AddedIn(SageGame.CncGeneralsZeroHour)]
    Status = 57,

    [IniEnum("GOOD_ARROW_PIERCE"), AddedIn(SageGame.Bfme)]
    GoodArrowPierce = 58,

    [IniEnum("EVIL_ARROW_PIERCE"), AddedIn(SageGame.Bfme)]
    EvilArrowPierce = 59,

    [IniEnum("SWORD_SLASH"), AddedIn(SageGame.Bfme)]
    SwordSlash = 60,

    [IniEnum("WITCH_KING_MORGUL_BLADE"), AddedIn(SageGame.Bfme)]
    WitchKingMorgulBlade = 61,

    [IniEnum("BALROG_SWORD"), AddedIn(SageGame.Bfme)]
    BalrogSword = 62,

    [IniEnum("BALROG_WHIP"), AddedIn(SageGame.Bfme)]
    BalrogWhip = 63,

    [IniEnum("ELECTRIC"), AddedIn(SageGame.Bfme)]
    Electric = 64,

    [IniEnum("GIMLI_LEAP"), AddedIn(SageGame.Bfme)]
    GimliLeap = 65,

    [IniEnum("BIG_ROCK"), AddedIn(SageGame.Bfme)]
    BigRock = 66,

    [IniEnum("CLUBBING"), AddedIn(SageGame.Bfme)]
    Clubbing = 67,

    [IniEnum("BECOME_UNDEAD"), AddedIn(SageGame.Bfme2)]
    BecomeUndead = 68,

    [IniEnum("BOLT"), AddedIn(SageGame.Bfme2)]
    Bolt = 69,

    [IniEnum("TORNADO"), AddedIn(SageGame.Bfme2)]
    Tornado = 70,

    [IniEnum("FLOOD_HORSE"), AddedIn(SageGame.Bfme2)]
    FloodHorse = 71,

    [IniEnum("FIRE3"), AddedIn(SageGame.Bfme2)]
    Fire3 = 72,

    [IniEnum("BECOME_UNDEAD_ONCE"), AddedIn(SageGame.Bfme2Rotwk)]
    BecomeUndeadOnce = 73,

    [IniEnum("NECRO1"), AddedIn(SageGame.Bfme2Rotwk)]
    Necro1 = 74,

    [IniEnum("NECRO2"), AddedIn(SageGame.Bfme2Rotwk)]
    Necro2 = 75,
}
