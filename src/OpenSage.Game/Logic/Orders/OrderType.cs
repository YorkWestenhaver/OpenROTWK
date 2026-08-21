namespace OpenSage.Logic.Orders;

public enum OrderType
{
    EndGame = 27,

    // Selection
    SetSelection = 1001, // Boolean:True, ObjectId:658 // first parameter is whether to clear existing selection (false in case of shift+click)
    SelectAcrossScreen = 1002, // Boolean:false, ObjectId:671, ... (more ids if more objects are selected) // occurs when selecting a unit and then pressing 'e'
    ClearSelection = 1003,
    Deselect = 1004, // ObjectId: 5 // occurs when shift-clicking a unit that is currently selected

    // Group management
    CreateGroup0 = 1006,
    CreateGroup1 = 1007,
    CreateGroup2 = 1008,
    CreateGroup3 = 1009,
    CreateGroup4 = 1010,
    CreateGroup5 = 1011,
    CreateGroup6 = 1012,
    CreateGroup7 = 1013,
    CreateGroup8 = 1014,
    CreateGroup9 = 1015,
    SelectGroup0 = 1016,
    SelectGroup1 = 1017,
    SelectGroup2 = 1018,
    SelectGroup3 = 1019,
    SelectGroup4 = 1020,
    SelectGroup5 = 1021,
    SelectGroup6 = 1022,
    SelectGroup7 = 1023,
    SelectGroup8 = 1024,
    SelectGroup9 = 1025,

    SpecialPower = 1040, // Integer:25, Integer:256, ObjectId:0 // SpecialPowerType, SpecialPowerOrderFlags, source command center?
    SpecialPowerAtLocation = 1041, // Integer:35, Position:<1105.9589, 728.7699, 18.75>, ObjectId:2816, Integer:672, ObjectId:657 // SpecialPowerType, location, unknown, SpecialPowerOrderFlags, source command center?
    SpecialPowerAtObject = 1042, // Integer:14, ObjectId:674, Integer:643, ObjectId:657 // SpecialPowerType, target object, SpecialPowerOrderFlags, source command center?
    SetRallyPoint = 1043,
    PurchaseScience = 1044,
    BeginUpgrade = 1045, //encountered while adding landmines to power plant: ObjectId:671,Integer:1604 (mines is Upgrades[13]), also when upgrading usa power plant (ObjectId:673,Integer:1593), (ObjectId:671,Integer:1593), also for flashbangs in the barracks (ObjectId:678,Integer:1594)
    CancelUpgrade = 1046,
    CreateUnit = 1047,
    CancelUnit = 1048,
    BuildObject = 1049,
    CancelBuild = 1051,
    Sell = 1052,
    ExitContainer = 1053, // ObjectId:683 the objectid to remove from the container
    Evacuate = 1054,
    DrawBoxSelection = 1058,
    AttackObject = 1059,
    ForceAttackObject = 1060,
    ForceAttackGround = 1061,
    ResumeBuild = 1065,
    MoveTo = 1068,
    ToggleOvercharge = 1078,
    SetCameraPosition = 1092,
    Checksum = 1095,
    Unknown1097 = 1097,

    //new from Diamond_Extinction_vs_Squaak_Ammo_cc_vs_cu__[GameReplays.org].rep

    Unknown1 = 1,
    Unknown2 = 2,
    Unknown3 = 3,
    Unknown4 = 4,
    Unknown5 = 5,
    Unknown6 = 6,
    Unknown7 = 7,
    Unknown8 = 8,
    Unknown9 = 9,



    Unknown1005 = 1005,
    Unknown1026 = 1026,
    Unknown1027 = 1027,
    Unknown1028 = 1028,
    Unknown1029 = 1029,
    Unknown1030 = 1030,
    Unknown1031 = 1031,
    Unknown1032 = 1032,
    Unknown1033 = 1033,
    Unknown1034 = 1034,
    Unknown1035 = 1035,
    Unknown1036 = 1036,
    Unknown1037 = 1037,
    // dozer clear mines     Integer:0, Position:<1154.5593, 505.26968, 18.75003>, Integer:2147483647, ObjectId:0 // primary weapon is clear mines
    // dragon tank fire wall Integer:1, Position:<530.40765, 607.319, 9.9999695>, Integer:2147483647, ObjectId:0  // secondary weapon is fire wall
    // comanche rocket pods  Integer:2, Position:<373.81445, 256.29944, 10>, Integer:2147483647, ObjectId:0       // tertiary weapon is rocket pods
    UseWeapon = 1038,
    SnipeVehicle = 1039, // Integer:1, ObjectId:6, Integer:2147483647 // first integer argument could be because sniper is secondary weapon (similar to useweapon above)
    Unknown1050 = 1050,

    Unknown1055 = 1055,
    Unknown1056 = 1056,
    CombatDrop = 1057, // ObjectId:2 (target building) // used by USA Chinook

    RepairVehicle = 1062, // ObjectId:3 includes vehicles returning to war factory for repair and helicopters landing and airfields for repair
    Unknown1063 = 1063,
    RepairStructure = 1064, // ObjectId:4 when a dozer is ordered to repair a structure
    Enter = 1066, // used for entering friendly vehicles and for hijacking vehicles
    GatherDumpSupplies = 1067, // used for both gathering from a supply source and dumping supplies

    AttackMove = 1069, // Position:<1343.561, 378.53568, 18.75>
    Unknown1070 = 1070,
    AddWaypoint = 1071, // Position:<1147.202, 214.9476, 18.75>
    GuardMode = 1072, // Position:<1256.29822, 505.26968, 18.75>, Integer:0 // integer 0 is guard ground, 2 is guard air
    Unknown1073 = 1073,
    StopMoving = 1074,
    Scatter = 1075, // no arguments
    HackInternet = 1076, // no arguments
    Cheer = 1077, // no arguments

    SelectWeapon = 1079, // Integer:1 // e.g. USA Ranger, 1 for flashbang 0 for machine gun
    Unknown1080 = 1080,
    Unknown1081 = 1081,
    Unknown1082 = 1082,
    Unknown1083 = 1083,
    Unknown1084 = 1084,
    Unknown1085 = 1085,
    DirectParticleCannon = 1086, // Position:<490.00174, 279.01785, 10>, Integer:0, ObjectId:0 // occurs when moving a particle cannon while it is being fired
    Unknown1087 = 1087, //same as 1068
    Unknown1088 = 1088,
    Unknown1089 = 1089, //place beacon? (Position:<1627,444. 386,3608. 20>)
    Unknown1090 = 1090,
    Unknown1091 = 1091,

    Unknown1093 = 1093,
    ToggleFormationMode = 1094, // no arguments

    SelectClearMines = 1096, // no arguments

    Unknown1098 = 1098,
    Unknown1099 = 1099,

    /// <summary>
    /// Buy a dead hero's revive at a named anchor building.
    /// Arguments: <c>ObjectId</c> dead hero, <c>ObjectId</c> anchor, <c>Integer</c> command-button
    /// slot index.
    /// </summary>
    /// <remarks>
    /// The number 1114 is the RECOVERED BFME2 value (<c>GameMessageType.MSG_REVIVE</c>), not a
    /// ZH one - this enum is otherwise ZH-derived, and mixing the two schemes is a deliberate,
    /// ratified shortcut (OQ-3, dr-0033): the live dispatcher is <c>OrderProcessor</c> and the
    /// live vocabulary has no revive value, while the vocabulary that has the right value has
    /// no dispatcher. Carrying the same integer means the eventual unification is a rename.
    /// dr-0034 tracks migrating this onto the real SimOrder dispatcher when N14b lands.
    /// <para>
    /// There is deliberately NO CancelRevive value here: the recovered vocabulary has no
    /// cancel-revive message (1115 is <c>MSG_TOGGLE_NO_AUTO_ACQUIRE</c>), and inventing a
    /// number would fabricate a retail fact. Cancellation exists as a driven module method
    /// (<c>RespawnUpdate.CancelRevive</c>) until a real value is recovered.
    /// </para>
    /// </remarks>
    Revive = 1114,

    // ================================================================================
    // BFME2/AotR castle + build-plot orders (R15 S9-05).
    //
    // WHY THESE ARE 2xxx AND NOT THEIR RECOVERED VALUES. Revive above could carry its real
    // BFME2 number (1114) because nothing in this ZH-derived enum already occupied 1114.
    // The castle messages have no such luck: their recovered BFME2 values are
    // MSG_FOUNDATION_CONSTRUCT 1049, MSG_CASTLE_UNPACK 1085 and MSG_CASTLE_PACK 1086, and
    // every one of those integers is ALREADY TAKEN here by an unrelated ZH member -
    // BuildObject = 1049, Unknown1085 = 1085, DirectParticleCannon = 1086. Reusing them
    // would either alias two names onto one value (so `case OrderType.CastleUnpack:` and
    // `case OrderType.Unknown1085:` become the same duplicate switch label and the file
    // stops compiling) or force a renumber of the ZH members, which this enum's replay
    // parser forbids.
    //
    // So these four take engine-local values in a band the recovered vocabulary provably
    // does not use: gamemessage-enum-map.md scopes the BFME2 network message range at
    // 1000-1999, so 2000+ cannot be mistaken for a retail number by a later reader. These
    // are NOT wire values and must never be written to a replay or a network packet as-is.
    // Their retail identity lives in exactly one place - OrderIdentityMap's explicit,
    // literal table - which is also what any SimCore/netcode path must go through. Never a
    // cast. INT-R1B correction: the cast is not merely useless here, it is actively unsafe -
    // GameMessageType's own 2xxx band is fully populated by the object-state messages, so
    // (GameMessageType)(int)OrderType.CastleUnpack is a DEFINED message (an object-position
    // one) rather than a hole. A naive cast therefore type-checks and dispatches silently
    // wrong instead of failing; validity checks cannot catch it, only the table can.
    //
    // These are additive: no existing member's value changes.
    // ================================================================================

    /// <summary>
    /// Build a structure on a build-plot / castle foundation.
    /// Arguments: <c>ObjectId</c> plot, <c>Integer</c> object-definition internal id (the same
    /// id form <see cref="BuildObject"/> carries, resolved through
    /// <c>AssetStore.ObjectDefinitions.GetByInternalId</c>).
    /// Retail identity: <c>GameMessageType.MSG_FOUNDATION_CONSTRUCT</c> - see the band note above.
    /// </summary>
    FoundationConstruct = 2001,

    /// <summary>
    /// Cancel an in-flight foundation construction and refund it.
    /// Arguments: <c>ObjectId</c> plot.
    /// Deliberately has NO recorded retail identity: the recovered vocabulary has no
    /// confirmed foundation-construct-cancel value (1050 is MSG_DOZER_CONSTRUCT, so the
    /// "~1050" guess in CastleOrderHandler's header is not a fact), and inventing one would
    /// fabricate a retail number - the same rule that left Revive without a CancelRevive.
    /// It is therefore permanently unmapped in OrderIdentityMap until a value is recovered.
    /// </summary>
    FoundationConstructCancel = 2002,

    /// <summary>
    /// Unpack a castle/camp foundation into its castle. Charges the matched
    /// <c>CastleToUnpackForFaction</c> entry's UnpackCost.
    /// Arguments: <c>ObjectId</c> foundation.
    /// Retail identity: <c>GameMessageType.MSG_CASTLE_UNPACK</c>.
    /// </summary>
    CastleUnpack = 2003,

    /// <summary>
    /// Pack an unpacked castle back down to its foundation.
    /// Arguments: <c>ObjectId</c> foundation.
    /// Retail identity: <c>GameMessageType.MSG_CASTLE_PACK</c>.
    /// </summary>
    CastlePack = 2004,

    // There is deliberately no CastleUnpackExplicitObject member. Its retail counterpart
    // (MSG_CASTLE_UNPACK_EXPLICIT_OBJECT) selects the camp by NAME, and OrderArgumentType has
    // no string member - an Order literally cannot carry a camp name today. Adding the value
    // without a representable payload would be a member no factory can build and no case can
    // dispatch. CastleOrderHandler.HandleCastleUnpackExplicitObject stays a direct-call path
    // (map scripts / triggers) until Order grows a string argument; that is an L3 -> L2
    // escalation, recorded in OrderIdentityMap's "deliberately unmapped" section.

    Zero = 0
}
