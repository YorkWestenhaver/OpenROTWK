// SimHordeContainModuleData - the audited (S5 vocabulary) parse side of the S6 horde
// system, implemented FRESH from the clean-room behavioral spec
// bfme2-workbench/research/spec-hordes.md (BFME-only: no GPL reference exists; every
// field below is from the binary parse table + the shared contain
// table, as documented in spec §4.1, with the RankInfo grammar of spec §5.1).
//
// INTERIM VOCABULARY: registered under the module name "SimHordeContain" (the
// SimLocomotorUpdate precedent, LOCO-F1 shape): the legacy "HordeContain" parse path and
// its float runtime stay untouched for merge hygiene; the integrator retires one of the
// two when the horde object pipeline lands end-to-end. Spec fields the legacy table
// lacked (FlankedDuration, CowerRadius, Leader*, EvaEventLastMemberDeath,
// RanksThatStopAdvance as a LIST, ...) are all parsed here.
//
// Quantization decisions (each pinned in research/systems/hordes.md):
//   - durations (BackUp*DelayTime, FlankedDelay, FlankedDuration) ms -> LogicFrameSpan
//     via ParseDurationLogicFrames (ceil, title rate 5 Hz per D-13);
//   - FrontAngle degrees -> Fix64 radians at parse (S2: angles are plain Fix64 radians);
//   - BackUp*Distance stays in PATHFIND CELLS (spec §4.1 note), converted to world
//     distance at use with PathfindCellSize = 10 (the S2 constant);
//   - BackupPercentage / Vision*Override percent -> Fix64 fraction;
//   - RankInfo positions and RandomOffset are Fix64 pairs through the F4 text boundary.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Horde;

[SimDataAudited]
public sealed class SimHordeContainModuleData : UpdateModuleData
{
    internal static SimHordeContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static Fix64 ParseAttrFix64(IniParser parser, string label) =>
        parser.ParseAttribute(label, parser.ParseFix64);

    /// <summary>Parses the two-attribute pair "X:&lt;f&gt; Y:&lt;f&gt;" as Fix64 (spec rndOff).</summary>
    internal static FixVector2 ParseFix64XYPair(IniParser parser) =>
        new(ParseAttrFix64(parser, "X"), ParseAttrFix64(parser, "Y"));

    private static readonly IniParseTable<SimHordeContainModuleData> FieldParseTable =
        new IniParseTable<SimHordeContainModuleData>
        {
            // ---- shared contain table (spec §4.1 header rows) ----
            { "ObjectStatusOfContained", (parser, x) => x.ObjectStatusOfContained = parser.ParseEnumBitArray<ObjectStatus>() },
            { "InitialPayload", (parser, x) => x.InitialPayloads.Add(SimHordePayload.Parse(parser)) },
            { "Slots", (parser, x) => x.Slots = parser.ParseInteger() },
            { "PassengerFilter", (parser, x) => x.PassengerFilter = ObjectFilter.Parse(parser) },
            { "ShowPips", (parser, x) => x.ShowPips = parser.ParseBoolean() },

            // ---- HordeContain-specific table (spec §4.1, offsets 0x18c-0x280) ----
            { "ThisFormationIsTheMainFormation", (parser, x) => x.ThisFormationIsTheMainFormation = parser.ParseBoolean() },
            { "RankInfo", (parser, x) => x.RankInfos.Add(SimHordeRankInfo.Parse(parser)) },
            { "ComboHorde", (parser, x) => x.ComboHordes.Add(SimComboHorde.Parse(parser)) },
            { "SplitHorde", (parser, x) => x.SplitHordes.Add(SimSplitHorde.Parse(parser)) },
            { "AlternateFormation", (parser, x) => x.AlternateFormation = parser.ParseAssetReference() },
            { "RanksThatStopAdvance", (parser, x) => x.RanksThatStopAdvance.AddRange(parser.ParseIntegerArray()) },
            { "RanksToReleaseWhenAttacking", (parser, x) => x.RanksToReleaseWhenAttacking.AddRange(parser.ParseIntegerArray()) },
            { "RanksToJustFreeWhenAttacking", (parser, x) => x.RanksToJustFreeWhenAttacking.AddRange(parser.ParseIntegerArray()) },
            { "RandomOffset", (parser, x) => x.RandomOffset = ParseFix64XYPair(parser) },
            { "BackUpMinDelayTime", (parser, x) => x.BackUpMinDelayTime = parser.ParseDurationLogicFrames() },
            { "BackUpMaxDelayTime", (parser, x) => x.BackUpMaxDelayTime = parser.ParseDurationLogicFrames() },
            { "BackUpMinDistance", (parser, x) => x.BackUpMinDistanceCells = parser.ParseFix64() },
            { "BackUpMaxDistance", (parser, x) => x.BackUpMaxDistanceCells = parser.ParseFix64() },
            { "BackupPercentage", (parser, x) => x.BackupPercentage = parser.ParseFix64Percentage() },
            { "CowerRadius", (parser, x) => x.CowerRadius = parser.ParseFix64() },
            { "LeadersAllowed", (parser, x) => x.LeadersAllowed.AddRange(parser.ParseAssetReferenceArray()) },
            { "LeaderPosition", (parser, x) => x.LeaderPosition = parser.ParseFixVector3() },
            { "LeaderRank", (parser, x) => x.LeaderRank = parser.ParseInteger() },
            { "BannerCarrierPosition", (parser, x) => x.BannerCarrierPositions.Add(SimBannerCarrierPosition.Parse(parser)) },
            { "BannerCarriersAllowed", (parser, x) => x.BannerCarriersAllowed.AddRange(parser.ParseAssetReferenceArray()) },
            { "BannerCarrierDestroyHordeOnDeath", (parser, x) => x.BannerCarrierDestroyHordeOnDeath = parser.ParseBoolean() },
            { "BannerCarrierHordeDeathType", (parser, x) => x.BannerCarrierHordeDeathType = parser.ParseEnumBitArray<DeathType>() },
            { "AttributeModifiers", (parser, x) => x.AttributeModifiers.AddRange(parser.ParseAssetReferenceArray()) },
            { "IsPorcupineFormation", (parser, x) => x.IsPorcupineFormation = parser.ParseBoolean() },
            { "ForcedLocomotorSet", (parser, x) => x.ForcedLocomotorSet = parser.ParseEnum<LocomotorSetType>() },
            { "MachineAllowed", (parser, x) => x.MachineAllowed = parser.ParseBoolean() },
            { "MachineType", (parser, x) => x.MachineType = parser.ParseAssetReference() },
            { "UseSlowHordeMovement", (parser, x) => x.UseSlowHordeMovement = parser.ParseBoolean() },
            { "MeleeAttackLeashDistance", (parser, x) => x.MeleeAttackLeashDistance = parser.ParseFix64() },
            { "EvaEventLastMemberDeath", (parser, x) => x.EvaEventLastMemberDeath = parser.ParseAssetReference() },
            { "RankSplit", (parser, x) => x.RankSplit = parser.ParseBoolean() },
            { "SplitHordeNumber", (parser, x) => x.SplitHordeNumber = parser.ParseInteger() },
            { "NotComboFormation", (parser, x) => x.NotComboFormation = parser.ParseBoolean() },
            { "UseMarchingAnims", (parser, x) => x.UseMarchingAnims = parser.ParseBoolean() },
            { "FrontAngle", (parser, x) => x.FrontAngleRadians = parser.ParseAngleDegrees() },
            { "FlankedDelay", (parser, x) => x.FlankedDelay = parser.ParseDurationLogicFrames() },
            { "FlankedDuration", (parser, x) => x.FlankedDuration = parser.ParseDurationLogicFrames() },
            { "MeleeBehavior", (parser, x) => x.MeleeBehavior = SimMeleeBehaviorBlock.Parse(parser) },
            { "MinimumHordeSize", (parser, x) => x.MinimumHordeSize = parser.ParseInteger() },
            { "VisionRearOverride", (parser, x) => x.VisionRearOverride = parser.ParseFix64Percentage() },
            { "VisionSideOverride", (parser, x) => x.VisionSideOverride = parser.ParseFix64Percentage() },
            { "BannerCarrierMinLevel", (parser, x) => x.BannerCarrierMinLevel = parser.ParseInteger() },
            { "LivingWorldOverloadTemplate", (parser, x) => x.LivingWorldOverloadTemplate = parser.ParseAssetReference() },
        };

    /// <summary>The S2 pathfind cell size: BackUp*Distance is authored in cells (spec §4.1).</summary>
    public static readonly Fix64 PathfindCellSize = Fix64.FromDecimalLiteral("10");

    // Shared contain table.
    public BitArray<ObjectStatus> ObjectStatusOfContained { get; private set; }
    public List<SimHordePayload> InitialPayloads { get; } = new();
    public int Slots { get; private set; }
    public ObjectFilter PassengerFilter { get; private set; }
    public bool ShowPips { get; private set; }

    // Formation identity / pairing.
    public bool ThisFormationIsTheMainFormation { get; private set; }
    public List<SimHordeRankInfo> RankInfos { get; } = new();
    public List<SimComboHorde> ComboHordes { get; } = new();
    public List<SimSplitHorde> SplitHordes { get; } = new();
    public string AlternateFormation { get; private set; }

    // Rank behavior lists.
    public List<int> RanksThatStopAdvance { get; } = new();
    public List<int> RanksToReleaseWhenAttacking { get; } = new();
    public List<int> RanksToJustFreeWhenAttacking { get; } = new();

    /// <summary>Per-slot jitter half-range; rolled once per slot per rebuild (spec §5.2, CRC-relevant).</summary>
    public FixVector2 RandomOffset { get; private set; }

    // Melee back-up shuffle (spec §5.4).
    public LogicFrameSpan BackUpMinDelayTime { get; private set; }
    public LogicFrameSpan BackUpMaxDelayTime { get; private set; }
    public Fix64 BackUpMinDistanceCells { get; private set; }
    public Fix64 BackUpMaxDistanceCells { get; private set; }
    public Fix64 BackupPercentage { get; private set; }
    public Fix64 CowerRadius { get; private set; }

    // Leaders (BFME1-era captain slots; data-complete, behavior-minimal per spec §7).
    public List<string> LeadersAllowed { get; } = new();
    public FixVector3 LeaderPosition { get; private set; }
    public int LeaderRank { get; private set; }

    // Banner carrier (spec §7).
    public List<SimBannerCarrierPosition> BannerCarrierPositions { get; } = new();
    public List<string> BannerCarriersAllowed { get; } = new();
    public bool BannerCarrierDestroyHordeOnDeath { get; private set; }
    public BitArray<DeathType> BannerCarrierHordeDeathType { get; private set; }
    public int BannerCarrierMinLevel { get; private set; }

    // Formation-wide modifiers / locomotion.
    public List<string> AttributeModifiers { get; } = new();
    public bool IsPorcupineFormation { get; private set; }
    public LocomotorSetType ForcedLocomotorSet { get; private set; }
    public bool MachineAllowed { get; private set; }
    public string MachineType { get; private set; }
    public bool UseSlowHordeMovement { get; private set; }
    public bool UseMarchingAnims { get; private set; }

    /// <summary>"How far members may move from horde center when melee attacking" (EA comment).</summary>
    public Fix64 MeleeAttackLeashDistance { get; private set; }

    public string EvaEventLastMemberDeath { get; private set; }
    public bool RankSplit { get; private set; }
    public int SplitHordeNumber { get; private set; }
    public bool NotComboFormation { get; private set; }

    /// <summary>
    /// Frontal arc in RADIANS (parsed from degrees). 2*pi (data: 360) = unflankable.
    /// Flank test (spec §6, confirmed): flanked iff dot(d, f) &lt; cos(FrontAngle / 2).
    /// </summary>
    public Fix64 FrontAngleRadians { get; private set; } = Fix64.PiTimes2;

    /// <summary>Throttle between flank (re)triggers (spec §6.4).</summary>
    public LogicFrameSpan FlankedDelay { get; private set; }

    /// <summary>How long the FLANKED state lasts per triggering attack (spec §6.4).</summary>
    public LogicFrameSpan FlankedDuration { get; private set; }

    /// <summary>Only shipped value: Amoeba (spec §4.1 / open question 7).</summary>
    public SimMeleeBehaviorBlock MeleeBehavior { get; private set; }

    public int MinimumHordeSize { get; private set; }
    public Fix64 VisionRearOverride { get; private set; }
    public Fix64 VisionSideOverride { get; private set; }
    public string LivingWorldOverloadTemplate { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SimHordeContain(gameObject, gameEngine.SimContext, this);
    }
}

/// <summary>InitialPayload: "&lt;template&gt; &lt;count&gt;" - members auto-created at horde creation (spec §4.1).</summary>
public sealed class SimHordePayload
{
    internal static SimHordePayload Parse(IniParser parser)
    {
        var payload = new SimHordePayload { Object = parser.ParseObjectReference() };
        payload.Count = parser.GetIntegerOptional();
        if (payload.Count == 0)
        {
            payload.Count = 1;
        }
        return payload;
    }

    public LazyAssetReference<ObjectDefinition> Object { get; private set; }
    public int Count { get; private set; }
}

/// <summary>
/// RankInfo grammar (spec §5.1): RankNumber first, then UnitType, then
/// one or more Positions; a Leader entry follows a Position ("Only one 'Leader' per
/// 'Position'") naming (rank, positionIndex); Facing is rejected by the original parser and
/// is deliberately not accepted here either. Positions are horde-local Fix64 pairs.
/// </summary>
public sealed class SimHordeRankInfo
{
    internal static SimHordeRankInfo Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    private static readonly IniParseTable<SimHordeRankInfo> FieldParseTable = new IniParseTable<SimHordeRankInfo>
    {
        { "RankNumber", (parser, x) => x.RankNumber = parser.ParseInteger() },
        { "UnitType", (parser, x) => x.UnitType = parser.ParseObjectReference() },
        { "Position", (parser, x) => x.Positions.Add(SimHordeContainModuleData.ParseFix64XYPair(parser)) },
        { "Leader", (parser, x) => x.Leaders.Add(SimHordeLeaderRef.Parse(parser, x.Positions.Count)) },
        { "GrantedWeaponCondition", (parser, x) => x.GrantedWeaponCondition = parser.ParseEnum<WeaponSetConditions>() },
        { "RevokedWeaponCondition", (parser, x) => x.RevokedWeaponCondition = parser.ParseEnum<WeaponSetConditions>() },
    };

    public int RankNumber { get; private set; }
    public LazyAssetReference<ObjectDefinition> UnitType { get; private set; }
    public List<FixVector2> Positions { get; } = new();
    public List<SimHordeLeaderRef> Leaders { get; } = new();
    public WeaponSetConditions GrantedWeaponCondition { get; private set; }
    public WeaponSetConditions RevokedWeaponCondition { get; private set; }
}

/// <summary>A Leader entry: leader = (rank, positionIndex); binds to the preceding Position.</summary>
public sealed class SimHordeLeaderRef
{
    internal static SimHordeLeaderRef Parse(IniParser parser, int positionCount)
    {
        if (positionCount == 0)
        {
            throw new IniParseException(
                "'Leader' must follow a 'Position'", parser.CurrentPosition);
        }
        return new SimHordeLeaderRef
        {
            FollowerPositionIndex = positionCount - 1,
            LeaderRank = parser.ParseInteger(),
            LeaderPositionIndex = parser.ParseInteger(),
        };
    }

    /// <summary>The position (index in the owning rank) this leader entry is attached to.</summary>
    public int FollowerPositionIndex { get; private set; }
    public int LeaderRank { get; private set; }
    public int LeaderPositionIndex { get; private set; }
}

/// <summary>Per-member-type banner slot: "UnitType:&lt;t&gt; Pos:X:.. Y:.." (default X:40 Y:0, spec §5.2).</summary>
public sealed class SimBannerCarrierPosition
{
    internal static SimBannerCarrierPosition Parse(IniParser parser)
    {
        return new SimBannerCarrierPosition
        {
            UnitType = parser.ParseAttributeObjectReference("UnitType"),
            Position = parser.ParseAttribute("Pos", () => SimHordeContainModuleData.ParseFix64XYPair(parser)),
        };
    }

    public LazyAssetReference<ObjectDefinition> UnitType { get; private set; }
    public FixVector2 Position { get; private set; }
}

/// <summary>SplitHorde: "SplitResult:&lt;hordeTemplate&gt; UnitType:&lt;member&gt;" (spec §4.2). Parsed, split deferred.</summary>
public sealed class SimSplitHorde
{
    internal static SimSplitHorde Parse(IniParser parser)
    {
        return new SimSplitHorde
        {
            SplitResult = parser.ParseAttributeIdentifier("SplitResult"),
            UnitType = parser.ParseAttributeIdentifier("UnitType"),
        };
    }

    public string SplitResult { get; private set; }
    public string UnitType { get; private set; }
}

/// <summary>ComboHorde pairing (BFME1-era; parsed, combo behavior deferred per spec §9).</summary>
public sealed class SimComboHorde
{
    internal static SimComboHorde Parse(IniParser parser) => parser.ParseAttributeList(FieldParseTable);

    private static readonly IniParseTable<SimComboHorde> FieldParseTable = new IniParseTable<SimComboHorde>
    {
        { "Target", (parser, x) => x.Target = parser.ParseIdentifier() },
        { "Result", (parser, x) => x.Result = parser.ParseIdentifier() },
        { "InitiateVoice", (parser, x) => x.InitiateVoice = parser.ParseAssetReference() },
    };

    public string Target { get; private set; }
    public string Result { get; private set; }
    public string InitiateVoice { get; private set; }
}

/// <summary>
/// MeleeBehavior named block. The only value in shipped data is "Amoeba" (spec §4.1;
/// unshipped enum values are open question 7). The porcupine-ish tuning fields ride along.
/// </summary>
public sealed class SimMeleeBehaviorBlock
{
    internal static SimMeleeBehaviorBlock Parse(IniParser parser) =>
        parser.ParseNamedBlock((x, name) => x.Name = name, FieldParseTable);

    private static readonly IniParseTable<SimMeleeBehaviorBlock> FieldParseTable = new IniParseTable<SimMeleeBehaviorBlock>
    {
        { "FacingBonus", (parser, x) => x.FacingBonus = parser.ParseFix64() },
        { "AngleLimitCos", (parser, x) => x.AngleLimitCos = parser.ParseFix64() },
        { "InnerRange", (parser, x) => x.InnerRange = parser.ParseFix64() },
        { "OuterRange", (parser, x) => x.OuterRange = parser.ParseFix64() },
        { "OuterRangeBuildings", (parser, x) => x.OuterRangeBuildings = parser.ParseFix64() },
    };

    public string Name { get; private set; }
    public Fix64 FacingBonus { get; private set; }
    public Fix64 AngleLimitCos { get; private set; }
    public Fix64 InnerRange { get; private set; }
    public Fix64 OuterRange { get; private set; }
    public Fix64 OuterRangeBuildings { get; private set; }
}
