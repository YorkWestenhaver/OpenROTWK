// R15 bridge P4a (dr-0039, packet BR-P4A): OrderType <-> GameMessageType identity map.
//
// WHY THIS FILE EXISTS (blackboard L2-plan #2): OpenSage.Logic.Orders.OrderType (ZH numbering,
// carried over from the pre-SimCore engine) and OpenSage.SimCore.Orders.GameMessageType
// (recovered BFME2 numbering, bfme2-workbench/research/gamemessage-enum-map.md) share the same
// integer range but are NOT the same vocabulary. gamemessage-enum-map.md §1/§2.2 documents that
// BFME2 renumbered 40 of the shared ZH message types and deleted 8 outright, so the two enums
// collide at identical integers with DIFFERENT meanings at several values - three confirmed
// examples: 1001 (OrderType.SetSelection vs GameMessageType.MSG_CREATE_SELECTED_GROUP happen to
// agree - see below), 1059 (OrderType.AttackObject vs GameMessageType.MSG_COMBINE_HORDES_WITH_
// OBJECT do NOT agree), 1068 (OrderType.MoveTo vs GameMessageType.MSG_ENTER do NOT agree).
// A cast from one enum to the other is therefore never legal; every pairing below is a literal,
// individually-cited entry, never `(GameMessageType)(int)orderType`.
//
// SOURCE OF EVIDENCE for each pairing: gamemessage-enum-map.md §1/§2 ("ZH ancestry" column,
// which gives each BFME2 value's ZH-numbered ancestor when one exists) cross-checked against
// the argument-shape doc comments already recorded on OrderType members (Logic/Orders/
// OrderType.cs) from real ZH replay captures. A pairing is only added here when both agree; ZH
// messages BFME2 deleted (§2.2) and OrderType members with no confirmed semantics ("UnknownNNN")
// are deliberately left OUT of the table rather than guessed - see "Deliberately unmapped"
// below.
//
// CASTLE-ORDER AMENDMENT (R15 synthesis): S9-05 (R1-W2) adds four castle OrderTypes -
// FoundationConstruct, CastleUnpack, CastlePack, CastleUnpackExplicitObject - that do not exist
// on OrderType as of this packet (BR-P4A, R1-W1), so they cannot be entered into the table below
// yet. Their target GameMessageType values are already known from gamemessage-enum-map.md and
// are recorded here so the W2 integrate lane (or S9-05 itself) can add the four entries as a
// same-shape follow-up once the OrderType members land:
//   FoundationConstruct -> GameMessageType.MSG_FOUNDATION_CONSTRUCT (1049)
//   CastleUnpack         -> GameMessageType.MSG_CASTLE_UNPACK (1085)
//   CastlePack            -> GameMessageType.MSG_CASTLE_PACK (1086)
//   CastleUnpackExplicitObject -> GameMessageType.MSG_CASTLE_UNPACK_EXPLICIT_OBJECT (1087)
// Until that follow-up lands, these (and every other absent OrderType) are UNMAPPED, which is a
// documented, load-bearing state, not an oversight: IOrderSubmitter's contract requires a
// Local-origin order that misses this map to still execute on the legacy local path
// (IOrderSubmitter.cs header) - CastleOrderHandler already dispatches castle orders correctly
// on that path today (Logic/Object/Castle/CastleOrderHandler.cs), so an unmapped castle order
// is inert here but not dropped end-to-end.

using System.Collections.Generic;
using OpenSage.SimCore.Orders;

namespace OpenSage.Logic.Orders;

/// <summary>
/// The explicit, literal, bidirectional OrderType &lt;-&gt; GameMessageType table. Never casts;
/// every entry is individually justified (see file header). Lookups miss silently (bool
/// try-pattern) for any OrderType or GameMessageType not in the table - a miss is the documented
/// "unmapped" state, not an error.
/// </summary>
public static class OrderIdentityMap
{
    private readonly struct Pair
    {
        public readonly OrderType OrderType;
        public readonly GameMessageType MessageType;

        public Pair(OrderType orderType, GameMessageType messageType)
        {
            OrderType = orderType;
            MessageType = messageType;
        }
    }

    // Ordered by OrderType value. Each line cites the gamemessage-enum-map.md ZH-ancestry row
    // (or the "BFME2-only, ratified shortcut" note for Revive) that justifies it.
    private static readonly Pair[] Entries =
    {
        // 1001: ZH-same value AND matching semantics (OrderType.SetSelection's "boolean =
        // clear-existing-selection, then ObjectIds" shape is exactly MSG_CREATE_SELECTED_GROUP;
        // map §2 row "1001 | MSG_CREATE_SELECTED_GROUP | ZH 1001 (same)"). The one collision
        // value where the naive cast would have been right by coincidence - still written
        // explicitly, per this file's no-cast rule.
        new(OrderType.SetSelection, GameMessageType.MSG_CREATE_SELECTED_GROUP),

        // 1004 ZH -> 1005 BFME2 (shifted +1). Map row "1005 | MSG_REMOVE_FROM_SELECTED_GROUP |
        // ZH 1004 (shifted +1)"; OrderType.Deselect's doc comment ("occurs when shift-clicking a
        // unit that is currently selected") is exactly REMOVE_FROM_SELECTED_GROUP.
        new(OrderType.Deselect, GameMessageType.MSG_REMOVE_FROM_SELECTED_GROUP),

        // 1006-1015 / 1016-1025: ZH-same, unrenumbered team-slot messages (map §2 "ZH 1006..1025
        // (same)" for every CREATE_TEAM/SELECT_TEAM row).
        new(OrderType.CreateGroup0, GameMessageType.MSG_CREATE_TEAM0),
        new(OrderType.CreateGroup1, GameMessageType.MSG_CREATE_TEAM1),
        new(OrderType.CreateGroup2, GameMessageType.MSG_CREATE_TEAM2),
        new(OrderType.CreateGroup3, GameMessageType.MSG_CREATE_TEAM3),
        new(OrderType.CreateGroup4, GameMessageType.MSG_CREATE_TEAM4),
        new(OrderType.CreateGroup5, GameMessageType.MSG_CREATE_TEAM5),
        new(OrderType.CreateGroup6, GameMessageType.MSG_CREATE_TEAM6),
        new(OrderType.CreateGroup7, GameMessageType.MSG_CREATE_TEAM7),
        new(OrderType.CreateGroup8, GameMessageType.MSG_CREATE_TEAM8),
        new(OrderType.CreateGroup9, GameMessageType.MSG_CREATE_TEAM9),
        new(OrderType.SelectGroup0, GameMessageType.MSG_SELECT_TEAM0),
        new(OrderType.SelectGroup1, GameMessageType.MSG_SELECT_TEAM1),
        new(OrderType.SelectGroup2, GameMessageType.MSG_SELECT_TEAM2),
        new(OrderType.SelectGroup3, GameMessageType.MSG_SELECT_TEAM3),
        new(OrderType.SelectGroup4, GameMessageType.MSG_SELECT_TEAM4),
        new(OrderType.SelectGroup5, GameMessageType.MSG_SELECT_TEAM5),
        new(OrderType.SelectGroup6, GameMessageType.MSG_SELECT_TEAM6),
        new(OrderType.SelectGroup7, GameMessageType.MSG_SELECT_TEAM7),
        new(OrderType.SelectGroup8, GameMessageType.MSG_SELECT_TEAM8),
        new(OrderType.SelectGroup9, GameMessageType.MSG_SELECT_TEAM9),

        // 1038 ZH-same. OrderType.UseWeapon's doc comment shows a Position argument alongside
        // the weapon-index integer ("dozer clear mines Integer:0, Position:<...>, ..."), which
        // is DO_WEAPON_AT_LOCATION's shape, not the objectless DO_WEAPON (map row "1038 |
        // MSG_DO_WEAPON_AT_LOCATION | ZH 1038 (same)").
        new(OrderType.UseWeapon, GameMessageType.MSG_DO_WEAPON_AT_LOCATION),

        // 1040-1044: ZH-same, unrenumbered (map rows 1040-1044, all "ZH ... (same)").
        new(OrderType.SpecialPower, GameMessageType.MSG_DO_SPECIAL_POWER),
        new(OrderType.SpecialPowerAtLocation, GameMessageType.MSG_DO_SPECIAL_POWER_AT_LOCATION),
        new(OrderType.SpecialPowerAtObject, GameMessageType.MSG_DO_SPECIAL_POWER_AT_OBJECT),
        new(OrderType.SetRallyPoint, GameMessageType.MSG_SET_RALLY_POINT),
        new(OrderType.PurchaseScience, GameMessageType.MSG_PURCHASE_SCIENCE),

        // 1045-1048: ZH-same (map rows 1045-1048). OrderType.BeginUpgrade's doc comment
        // (landmine/flashbang upgrades queued at a structure) matches QUEUE_UPGRADE.
        new(OrderType.BeginUpgrade, GameMessageType.MSG_QUEUE_UPGRADE),
        new(OrderType.CancelUpgrade, GameMessageType.MSG_CANCEL_UPGRADE),
        new(OrderType.CreateUnit, GameMessageType.MSG_QUEUE_UNIT_CREATE),
        new(OrderType.CancelUnit, GameMessageType.MSG_CANCEL_UNIT_CREATE),

        // 1049 ZH -> 1050 BFME2 (shifted +1). Map row "1050 | MSG_DOZER_CONSTRUCT | ZH 1049
        // (shifted +1)"; OrderType.BuildObject's (objectDefinitionId, position, angle) shape is
        // exactly the recovered DOZER_CONSTRUCT wire signature (map §1: "(objectID, Coord3D,
        // angle)").
        new(OrderType.BuildObject, GameMessageType.MSG_DOZER_CONSTRUCT),

        // 1051-1054: ZH-same (map rows 1051-1054).
        new(OrderType.CancelBuild, GameMessageType.MSG_DOZER_CANCEL_CONSTRUCT),
        new(OrderType.Sell, GameMessageType.MSG_SELL),
        new(OrderType.ExitContainer, GameMessageType.MSG_EXIT),
        new(OrderType.Evacuate, GameMessageType.MSG_EVACUATE),

        // 1057 ZH -> 1058 BFME2 (shifted +1). OrderType.CombatDrop carries an ObjectId (target
        // building), which is the _AT_OBJECT form (map row "1058 | MSG_COMBATDROP_AT_OBJECT |
        // ZH 1057 (shifted +1)"), not the _AT_LOCATION form at 1057.
        new(OrderType.CombatDrop, GameMessageType.MSG_COMBATDROP_AT_OBJECT),

        // 1058 ZH -> 1060 BFME2 (shifted +2). OrderType.DrawBoxSelection is a ScreenRectangle
        // argument (Order.AddScreenRectangleArgument), matching map row "1060 | MSG_AREA_
        // SELECTION | yes x1 `08x1` | ZH 1058 (shifted +2)" and its §1 correction that the
        // payload is one IRegion2D drag rectangle.
        new(OrderType.DrawBoxSelection, GameMessageType.MSG_AREA_SELECTION),

        // 1059/1060/1061 ZH -> 1061/1062/1063 BFME2 (shifted +2 each). THE anchor collision
        // case (blackboard L2-plan #2): GameMessageType's own value 1059 is
        // MSG_COMBINE_HORDES_WITH_OBJECT, a BFME2-only horde message with no ZH ancestor at all
        // - a naive cast of OrderType.AttackObject (1059) would silently produce the wrong
        // message. Map rows "1061 | MSG_DO_ATTACK_OBJECT | ZH 1059 (shifted +2)", "1062 |
        // MSG_DO_FORCE_ATTACK_OBJECT | ZH 1060 (shifted +2)", "1063 | MSG_DO_FORCE_ATTACK_GROUND
        // | ZH 1061 (shifted +2)".
        new(OrderType.AttackObject, GameMessageType.MSG_DO_ATTACK_OBJECT),
        new(OrderType.ForceAttackObject, GameMessageType.MSG_DO_FORCE_ATTACK_OBJECT),
        new(OrderType.ForceAttackGround, GameMessageType.MSG_DO_FORCE_ATTACK_GROUND),

        // 1062 ZH -> 1064 BFME2 (shifted +2). OrderType.RepairVehicle's comment ("vehicles
        // returning to war factory for repair and helicopters landing at airfields") is the
        // passive being-repaired-at-a-depot case: GET_REPAIRED (map row "1064 |
        // MSG_GET_REPAIRED | ZH 1062 (shifted +2)").
        new(OrderType.RepairVehicle, GameMessageType.MSG_GET_REPAIRED),

        // 1064 ZH -> 1066 BFME2 (shifted +2). OrderType.RepairStructure's comment ("when a
        // dozer is ordered to repair a structure") is the active-order case: DO_REPAIR (map row
        // "1066 | MSG_DO_REPAIR | ZH 1064 (shifted +2)") - distinct from RepairVehicle above.
        new(OrderType.RepairStructure, GameMessageType.MSG_DO_REPAIR),

        // 1065 ZH -> 1067 BFME2 (shifted +2). Map row "1067 | MSG_RESUME_CONSTRUCTION | ZH 1065
        // (shifted +2)"; name and ObjectId-only shape both match OrderType.ResumeBuild.
        new(OrderType.ResumeBuild, GameMessageType.MSG_RESUME_CONSTRUCTION),

        // 1066 ZH -> 1068 BFME2 (shifted +2). Map row "1068 | MSG_ENTER | ZH 1066 (shifted +2)".
        // This is the OTHER half of the 1068 collision (see MoveTo below): GameMessageType 1068
        // is MSG_ENTER, and its real ZH ancestor is OrderType.Enter (ZH 1066), not
        // OrderType.MoveTo (ZH 1068).
        new(OrderType.Enter, GameMessageType.MSG_ENTER),

        // 1067 ZH -> 1069 BFME2 (shifted +2). OrderType.GatherDumpSupplies's comment ("used for
        // both gathering from a supply source and dumping supplies") is docking at a
        // building to transfer resources: DOCK (map row "1069 | MSG_DOCK | ZH 1067 (shifted
        // +2)").
        new(OrderType.GatherDumpSupplies, GameMessageType.MSG_DOCK),

        // 1068 ZH -> 1071 BFME2 (shifted +3). THE second anchor collision case: GameMessageType
        // value 1068 is MSG_ENTER (see Enter above), not the move order. Map row "1071 |
        // MSG_DO_MOVETO | yes x4 `06x1` | ZH 1068 (shifted +3)" is OrderType.MoveTo's real
        // target.
        new(OrderType.MoveTo, GameMessageType.MSG_DO_MOVETO),

        // 1069 ZH -> 1072 BFME2 (shifted +3). OrderType.AttackMove's Position-only shape matches
        // map row "1072 | MSG_DO_ATTACKMOVETO | ZH 1069 (shifted +3)".
        new(OrderType.AttackMove, GameMessageType.MSG_DO_ATTACKMOVETO),

        // 1071 ZH -> 1074 BFME2 (shifted +3). Map row "1074 | MSG_ADD_WAYPOINT | ZH 1071
        // (shifted +3)"; both carry a bare Position argument.
        new(OrderType.AddWaypoint, GameMessageType.MSG_ADD_WAYPOINT),

        // 1072 ZH -> 1075 BFME2 (shifted +3). OrderType.GuardMode's (Position, Integer
        // ground/air) shape matches the position-anchored guard, not the object-anchored one:
        // DO_GUARD_POSITION (map row "1075 | MSG_DO_GUARD_POSITION | ZH 1072 (shifted +3)").
        new(OrderType.GuardMode, GameMessageType.MSG_DO_GUARD_POSITION),

        // 1074/1075/1077 ZH -> 1077/1078/1080 BFME2 (shifted +3). All three are documented
        // no-argument orders on both sides (map rows 1077/1078/1080).
        new(OrderType.StopMoving, GameMessageType.MSG_DO_STOP),
        new(OrderType.Scatter, GameMessageType.MSG_DO_SCATTER),
        new(OrderType.Cheer, GameMessageType.MSG_DO_CHEER),

        // 1079 ZH -> 1082 BFME2 (shifted +3). OrderType.SelectWeapon's "Integer:1 // 1 for
        // flashbang 0 for machine gun" comment is a weapon-slot switch: SWITCH_WEAPONS (map row
        // "1082 | MSG_SWITCH_WEAPONS | ZH 1079 (shifted +3)").
        new(OrderType.SelectWeapon, GameMessageType.MSG_SWITCH_WEAPONS),

        // 1086 ZH -> 1089 BFME2 (shifted +3). OrderType.DirectParticleCannon's comment
        // ("occurs when moving a particle cannon while it is being fired") is literally
        // overriding an in-flight special power's destination: DO_SPECIAL_POWER_OVERRIDE_
        // DESTINATION (map row "1089 | ... | ZH 1086 (shifted +3)").
        new(OrderType.DirectParticleCannon, GameMessageType.MSG_DO_SPECIAL_POWER_OVERRIDE_DESTINATION),

        // 1094 ZH -> 1097 BFME2 (shifted +3). Both no-argument formation toggles (map row
        // "1097 | MSG_CREATE_FORMATION | ZH 1094 (shifted +3)").
        new(OrderType.ToggleFormationMode, GameMessageType.MSG_CREATE_FORMATION),

        // 1092 ZH -> 1095 BFME2 (shifted +3). Map row "1095 | MSG_SET_REPLAY_CAMERA | ZH 1092
        // (shifted +3)" - OrderType.SetCameraPosition is the same camera-position-stamping
        // order, renamed for its replay use in BFME2.
        new(OrderType.SetCameraPosition, GameMessageType.MSG_SET_REPLAY_CAMERA),

        // 1095 ZH -> 1098 BFME2 (shifted +3). Map row "1098 | MSG_LOGIC_CRC | yes x6 | ZH 1095
        // (shifted +3)", corroborated independently by map §3's construction-site trace.
        // OrderType.Checksum is this exact message.
        new(OrderType.Checksum, GameMessageType.MSG_LOGIC_CRC),

        // 1096 ZH -> 1099 BFME2 (shifted +3). Both no-argument mine-clearing toggles (map row
        // "1099 | MSG_SET_MINE_CLEARING_DETAIL | ZH 1096 (shifted +3)").
        new(OrderType.SelectClearMines, GameMessageType.MSG_SET_MINE_CLEARING_DETAIL),

        // 1114: BFME2-only value, no ZH ancestor - OrderType.Revive already carries the
        // recovered BFME2 number directly, by deliberate ratified construction (dr-0033;
        // OrderType.cs's own doc comment on Revive). Recorded here as a same-value identity
        // entry so callers get one uniform lookup path instead of special-casing Revive.
        new(OrderType.Revive, GameMessageType.MSG_REVIVE),
    };

    // Deliberately unmapped (NOT an omission - each has a documented reason):
    //   OrderType.EndGame (27)         - session-teardown order, outside the 1000-1999 network
    //                                     range gamemessage-enum-map.md scopes; no candidate.
    //   OrderType.ClearSelection (1003) / OrderType.SelectAcrossScreen (1002) - no map §2 row
    //                                     independently corroborates a specific candidate against
    //                                     these two OrderType members' documented argument shapes;
    //                                     left out rather than guessed (map §1's own methodology:
    //                                     "no string is left over and no value is claimed twice"
    //                                     is a completeness bar this table also holds itself to).
    //   OrderType.ToggleOvercharge (1078) - map §2.2: MSG_TOGGLE_OVERCHARGE is one of the 8 ZH
    //                                     messages BFME2 DELETED outright. There is no BFME2
    //                                     equivalent to map to, ever - not a gap to fill later.
    //   OrderType.HackInternet (1076)  - map §2.2: MSG_INTERNET_HACK is likewise a BFME2
    //                                     deletion (USA hacker net-hack has no LOTR analogue).
    //   OrderType.SnipeVehicle (1039)  - its own doc comment ("first integer argument COULD BE
    //                                     ..." - uncertain) does not clear this table's
    //                                     evidence bar.
    //   OrderType.Unknown* / UnknownNNNN members - by construction, no confirmed semantics to
    //                                     match against.
    //   The four castle OrderTypes S9-05 has not yet added - see file header.

    private static readonly Dictionary<OrderType, GameMessageType> ByOrderType = BuildByOrderType();
    private static readonly Dictionary<GameMessageType, OrderType> ByMessageType = BuildByMessageType();

    private static Dictionary<OrderType, GameMessageType> BuildByOrderType()
    {
        var map = new Dictionary<OrderType, GameMessageType>(Entries.Length);
        foreach (var pair in Entries)
        {
            map.Add(pair.OrderType, pair.MessageType);
        }
        return map;
    }

    private static Dictionary<GameMessageType, OrderType> BuildByMessageType()
    {
        var map = new Dictionary<GameMessageType, OrderType>(Entries.Length);
        foreach (var pair in Entries)
        {
            map.Add(pair.MessageType, pair.OrderType);
        }
        return map;
    }

    /// <summary>
    /// The number of explicit pairings in the table. Exposed for tests that want to assert on
    /// coverage without duplicating the literal count.
    /// </summary>
    public static int Count => Entries.Length;

    /// <summary>
    /// Looks up the GameMessageType for a legacy OrderType. Returns false for any OrderType not
    /// in the table (including the four not-yet-existing castle OrderTypes and every
    /// deliberately-unmapped member listed above) - a miss, not an error; see
    /// IOrderSubmitter's fallback contract.
    /// </summary>
    public static bool TryGetGameMessageType(OrderType orderType, out GameMessageType messageType) =>
        ByOrderType.TryGetValue(orderType, out messageType);

    /// <summary>
    /// Looks up the legacy OrderType for a recovered GameMessageType. Returns false for any
    /// GameMessageType not in the table.
    /// </summary>
    public static bool TryGetOrderType(GameMessageType messageType, out OrderType orderType) =>
        ByMessageType.TryGetValue(messageType, out orderType);
}
