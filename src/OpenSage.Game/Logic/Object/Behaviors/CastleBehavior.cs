// CastleBehavior - the castle/camp anchor (R9 castles system; build-roadmap pillar castles).
//
// Behavioral reference: bfme2-workbench/research/spec-castles.md - a clean-room
// behavioral spec (this system has NO GPL reference; behavioral facts only, no
// decompiled code transplanted). What is implemented here, spec section by section:
//   §3.1  the FULL recovered retail parse table, including the three
//         fields missing from the old prototype (BuildTime, DecalName,
//         TransferFoundationHealthToCastleUponUnpack), CrewPrepareTime split from
//         CrewPrepareInterval, and ScanDistance as a REAL with default 0 (Q2: 0 disables
//         the capture scan; the old guessed int/100 is retired).
//   §4    the runtime state machine with retail's own state numbering (CastleState).
//   §5.1  canUnpack(checkTimer): packed, no pending instant build, FadeTime countdown expired.
//   §5.2  initiateUnpack: instant branch unpacks immediately (state 4); the deferred branch
//         waits UnpackDelayTime (state 1) - whether UnpackDelayTime or BuildTime drives the
//         delay is open question Q1 (default: UnpackDelayTime; finding F-CAS-2).
//   §5.3  unpack: stamp the .bse members around the anchor, hand members passing
//         FilterValidOwnedEntries to the unpacking player and the rest to the civilian
//         player, write the castle-id/native-player back-refs into every member's
//         CastleMemberBehavior, record the unpack frame, transfer foundation health.
//   §5.4  the ownership-capture scan (packed, ScanDistance > 0, not InstantUnpack): the
//         per-player tally lives in CastleCaptureScan (pure, tested); nobody-in-range past
//         frame 5 reverts ownership to the CIVILIAN player (Q3 - retail's PlyrCivilian,
//         not the spawn owner as the old prototype guessed).
//   §5.5  isPlayerAllowedToCapture / isPlayerAllowedToPackOrUnpack.
//   §5.7  initiatePack ("aka DIE"): state 5, members killed, m_timer = FadeTime; Packed:
//         residual members destroyed, foundation restored, ownership to civilian.
//         KeepDeathKillsEverything routes keep death into initiatePack; camp members with
//         VITAL_FOR_BASE_SURVIVAL do the same.
//   §5.8  critter trigger: pure geometry in CastleMath; pathing hookup deferred (F-CAS-6).
//
// Float-substrate crossings (.bse stamping, placement, health transfer, plot occupancy) live
// in Logic/Object/Castle/CastleUnpackStamper.cs (NOT [SimState]), never here.

using System;
using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Castle;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
internal sealed class CastleBehavior : FoundationAIUpdate
{
    private readonly CastleBehaviorModuleData _moduleData;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Retail +0x34.</summary>
    private CastleState _state;

    /// <summary>FadeTime countdown, frame-quantized (retail +0x40, float seconds).</summary>
    private LogicFrame _repackTimerExpiry;

    /// <summary>Frame a deferred (state 1) unpack completes.</summary>
    private LogicFrame _unpackCompleteFrame;

    /// <summary>Frame stamp of the last completed unpack (retail +0x48).</summary>
    private LogicFrame _unpackFrame;

    /// <summary>Retail +0x3c m_needInstantBuild: blocks canUnpack while set.</summary>
    private bool _needInstantBuild;

    /// <summary>Chosen CastleToUnpackForFaction entry (retail +0x98 stores the camp name; we store the index; -1 = resolve by faction at unpack).</summary>
    private int _castleEntryIndex = -1;

    /// <summary>Roster index of the player a deferred unpack completes for.</summary>
    private int _unpackPlayerIndex = -1;

    /// <summary>Native player index written into members' CastleMemberBehavior back-refs.</summary>
    private int _nativePlayerIndex;

    /// <summary>Stamped castle anchor member (keep/castle-center), retail +0x38.</summary>
    private ObjectId _castleAnchorId = ObjectId.Invalid;

    /// <summary>Owned members (retail vectors A/C merged; declaration order = our walk order, F9).</summary>
    private readonly List<ObjectId> _memberIds = new();

    /// <summary>Members handed to the civilian player (retail vector B).</summary>
    private readonly List<ObjectId> _neutralMemberIds = new();

    // ---- non-xfered plumbing ----

    private ICastleTemplateProvider _templateProvider;

    internal CastleBehavior(GameObject gameObject, IGameEngine gameEngine, CastleBehaviorModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
        _nativePlayerIndex = PlayerIndexOf(gameObject.Owner);
    }

    /// <summary>The module-facing sim context (legacy ctor path, so the property bridge is used).</summary>
    private ISimContext Ctx => GameEngine.SimContext;

    /// <summary>Test seam: the headless host has no .bse files, so tests inject placements.</summary>
    internal ICastleTemplateProvider TemplateProvider
    {
        get => _templateProvider ??= new BseCastleTemplateProvider(GameEngine);
        set => _templateProvider = value;
    }

    public CastleState State => _state;

    internal IReadOnlyList<ObjectId> MemberIds => _memberIds;

    internal ObjectId CastleAnchorId => _castleAnchorId;

    // ------------------------------------------------------------------
    // Legacy client surface (Scene3D start-castle path, GUI callbacks).
    // ------------------------------------------------------------------

    public bool IsUnpacked
    {
        get => _state == CastleState.Unpacked;
        // CommandButtonCallback's foundation-construct hack sets this; keep the write
        // harmless and idempotent.
        set
        {
            if (value && _state == CastleState.Packed)
            {
                _state = CastleState.Unpacked;
            }
        }
    }

    public int GetUnpackCost(Player player)
    {
        var index = FindEntryIndexForPlayer(player);
        return index >= 0 ? _moduleData.CastleToUnpackForFactions[index].UnpackCost : 0;
    }

    /// <summary>Legacy entry point (Scene3D CreateSkirmishPlayerStartingBuilding): unpack now.</summary>
    public void Unpack(Player player, bool instant = false)
    {
        if (_state != CastleState.Packed)
        {
            return;
        }

        InitiateUnpack(player, explicitEntryIndex: -1, instant);
    }

    // ------------------------------------------------------------------
    // §5.1 canUnpack
    // ------------------------------------------------------------------

    /// <summary>
    /// True iff still packed, no pending instant build, and (when <paramref name="checkTimer"/>)
    /// the post-pack FadeTime countdown has expired (threshold 0.0, spec-castles.md).
    /// </summary>
    public bool CanUnpack(bool checkTimer)
    {
        return _state == CastleState.Packed
            && !_needInstantBuild
            && (!checkTimer || Ctx.CurrentFrame >= _repackTimerExpiry);
    }

    // ------------------------------------------------------------------
    // §5.5 capture / pack permission
    // ------------------------------------------------------------------

    /// <summary>Faction gate: the player's faction has a CastleToUnpackForFaction entry.</summary>
    public bool PlayerAllowedToCapture(Player player) => FindEntryIndexForPlayer(player) >= 0;

    public bool IsPlayerAllowedToCapture(Player player)
        => GameObject.Owner != player && PlayerAllowedToCapture(player);

    public bool IsPlayerAllowedToPackOrUnpack(Player player)
        => GameObject.Owner == player || PlayerAllowedToCapture(player);

    // ------------------------------------------------------------------
    // §5.2 initiateUnpack
    // ------------------------------------------------------------------

    public void InitiateUnpack(Player player, int explicitEntryIndex, bool instant)
    {
        _castleEntryIndex = explicitEntryIndex >= 0 ? explicitEntryIndex : FindEntryIndexForPlayer(player);
        _unpackPlayerIndex = PlayerIndexOf(player);

        if (instant || _moduleData.InstantUnpack)
        {
            // Retail instant branch: unpack() immediately, state 4, status bit 0x4000000
            // (the status bit is client bookkeeping and not modeled; finding F-CAS-9).
            DoUnpack(player);
            return;
        }

        // Deferred branch: state 1; UnpackDelayTime drives the wait (Q1 / F-CAS-2).
        _state = CastleState.UnpackInitiated;
        _unpackCompleteFrame = Ctx.CurrentFrame + _moduleData.UnpackDelayTimeFrames;
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ------------------------------------------------------------------
    // §5.3 unpack
    // ------------------------------------------------------------------

    private void DoUnpack(Player player)
    {
        var entry = _castleEntryIndex >= 0 && _castleEntryIndex < _moduleData.CastleToUnpackForFactions.Count
            ? _moduleData.CastleToUnpackForFactions[_castleEntryIndex]
            : null;

        var placements = entry != null ? TemplateProvider.GetPlacements(entry.Camp) : null;

        var members = CastleUnpackStamper.StampMembers(
            GameObject,
            GameEngine,
            placements,
            instant: true,
            _moduleData.DisableStructureRotation);

        var civilian = GameEngine.Game.PlayerManager.GetCivilianPlayer();

        _memberIds.Clear();
        _neutralMemberIds.Clear();
        _castleAnchorId = ObjectId.Invalid;

        foreach (var member in members)
        {
            // Members failing FilterValidOwnedEntries are given to the civilian player
            // (spec §3.1 FilterValidOwnedEntries; PlyrCivilian).
            var owned = _moduleData.FilterValidOwnedEntries == null
                || _moduleData.FilterValidOwnedEntries.Matches(member);

            member.Owner = owned ? player : civilian;

            if (owned)
            {
                _memberIds.Add(member.Id);

                // Back-refs (spec-castles.md): castle object id + native
                // player index, the Eva-routing/pack-cascade key.
                member.FindBehavior<CastleMemberBehavior>()
                    ?.SetCastleBackReference(GameObject.Id, _nativePlayerIndex);

                if (_castleAnchorId.IsInvalid &&
                    (member.Definition.KindOf.Get(ObjectKinds.CastleKeep) ||
                     member.Definition.KindOf.Get(ObjectKinds.CastleCenter)))
                {
                    _castleAnchorId = member.Id;
                }
            }
            else
            {
                _neutralMemberIds.Add(member.Id);
            }
        }

        if (_moduleData.TransferFoundationHealthToCastleUponUnpack && _castleAnchorId.IsValid)
        {
            CastleUnpackStamper.TransferFoundationHealth(
                GameObject, Ctx.GameLogic.GetObjectById(_castleAnchorId));
        }

        // The foundation is handed to the unpacking player and hidden behind the castle.
        GameObject.Owner = player;
        GameObject.Hidden = true;
        GameObject.IsSelectable = false;

        _unpackFrame = Ctx.CurrentFrame;
        _state = CastleState.Unpacked;
    }

    // ------------------------------------------------------------------
    // §5.7 initiatePack / Packed
    // ------------------------------------------------------------------

    /// <summary>"aka DIE": kill every member, start the FadeTime countdown (state 5).</summary>
    public void InitiatePack()
    {
        if (_state != CastleState.Unpacked && _state != CastleState.UnpackInitiated)
        {
            return;
        }

        _state = CastleState.Packing;
        _repackTimerExpiry = Ctx.CurrentFrame + _moduleData.FadeTimeFrames;

        // Kill (not destroy): members run their normal death pipeline (spec: "team-reset +
        // a kill call" per member). Snapshot first: a member's OnDie pushes OnMemberDied,
        // which edits the live list.
        foreach (var id in _memberIds.ToArray())
        {
            var member = Ctx.GameLogic.GetObjectById(id);
            if (member != null && !member.IsDestroyed)
            {
                member.Kill();
            }
        }

        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Final teardown: destroy residual members, restore the packed foundation.</summary>
    private void FinishPack()
    {
        foreach (var id in _memberIds.ToArray())
        {
            DestroyIfAlive(id);
        }
        foreach (var id in _neutralMemberIds.ToArray())
        {
            DestroyIfAlive(id);
        }

        _memberIds.Clear();
        _neutralMemberIds.Clear();

        if (_castleAnchorId.IsValid)
        {
            DestroyIfAlive(_castleAnchorId);
            _castleAnchorId = ObjectId.Invalid;
        }

        // Residual ownership goes to the civilian player; the foundation reappears and is
        // capturable again (spec §5.7 Packed).
        GameObject.Owner = GameEngine.Game.PlayerManager.GetCivilianPlayer();
        GameObject.Hidden = false;
        GameObject.IsSelectable = true;

        _state = CastleState.Packed;
    }

    private void DestroyIfAlive(ObjectId id)
    {
        var obj = Ctx.GameLogic.GetObjectById(id);
        if (obj != null && !obj.IsDestroyed)
        {
            Ctx.GameLogic.DestroyObject(obj);
        }
    }

    /// <summary>
    /// Member-death notification (pushed by CastleMemberBehavior.OnDie). The keep's death
    /// with KeepDeathKillsEverything, or any VITAL_FOR_BASE_SURVIVAL member's death,
    /// cascades into initiatePack (spec §5.7).
    /// </summary>
    internal void OnMemberDied(GameObject member)
    {
        // While packing, the dying members STAY in the list: retail's Packed destroys
        // every remaining member object, corpses included (spec §5.7).
        if (_state != CastleState.Packing)
        {
            _memberIds.Remove(member.Id);
            _neutralMemberIds.Remove(member.Id);

            if (member.Id == _castleAnchorId)
            {
                _castleAnchorId = ObjectId.Invalid;
            }
        }

        if (_state != CastleState.Unpacked)
        {
            return;
        }

        var keepDeath = member.Definition.KindOf.Get(ObjectKinds.CastleKeep)
            && _moduleData.KeepDeathKillsEverything;
        var vitalDeath = member.Definition.KindOf.Get(ObjectKinds.VitalForBaseSurvival);

        if (keepDeath || vitalDeath)
        {
            InitiatePack();
        }
    }

    // ------------------------------------------------------------------
    // Update: state machine + §5.4 capture scan
    // ------------------------------------------------------------------

    public override UpdateSleepTime Update()
    {
        switch (_state)
        {
            case CastleState.Packed:
                RunCaptureScan();
                return UpdateSleepTime.None;

            case CastleState.UnpackInitiated:
                if (Ctx.CurrentFrame >= _unpackCompleteFrame)
                {
                    var player = PlayerAt(_unpackPlayerIndex) ?? GameObject.Owner;
                    DoUnpack(player);
                }
                return UpdateSleepTime.None;

            case CastleState.Packing:
                if (Ctx.CurrentFrame >= _repackTimerExpiry)
                {
                    FinishPack();
                }
                return UpdateSleepTime.None;

            case CastleState.Unpacked:
            default:
                // Member deaths are pushed at us; nothing to poll.
                return UpdateSleepTime.Forever;
        }
    }

    private void RunCaptureScan()
    {
        // Spec §5.4 gate: still packed (canUnpack(0)), ScanDistance > 0, not InstantUnpack.
        if (!CanUnpack(checkTimer: false)
            || _moduleData.ScanDistance <= Fix64.Zero
            || _moduleData.InstantUnpack)
        {
            return;
        }

        var owner = GameObject.Owner;
        var civilian = GameEngine.Game.PlayerManager.GetCivilianPlayer();
        var neutral = Ctx.Players.NeutralPlayer;

        var candidates = new List<CaptureCandidate>();
        foreach (var candidate in Ctx.Partition.QueryObjectsInRadius(GameObject, _moduleData.ScanDistance))
        {
            if (candidate == GameObject || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }

            // Neutral/civilian bystanders (map props, critters) never tally and never
            // contest; the current owner's own units DO tally (they hold the camp).
            // Qualification predicate pinned here, not recovered - finding F-CAS-4.
            if ((candidate.Owner == civilian || candidate.Owner == neutral) && candidate.Owner != owner)
            {
                continue;
            }

            var isEnemy = candidate.Owner != owner
                && (candidate.Owner.Enemies.Contains(owner) || owner.Enemies.Contains(candidate.Owner));

            var isRealUnit = !candidate.Definition.KindOf.Get(ObjectKinds.Structure);

            candidates.Add(new CaptureCandidate(
                PlayerIndexOf(candidate.Owner),
                isRealUnit,
                templateCaptureBonus: 0, // Q6: template+0x628 feed unrecovered
                isEnemy));
        }

        if (candidates.Count == 0)
        {
            // Nobody nearby: after the 5-frame grace, ownership reverts to the civilian
            // player (retail PlyrCivilian - Q3), never to the spawn owner.
            if (Ctx.CurrentFrame.Value > CastleCaptureScan.CivilianRevertGraceFrames && owner != civilian)
            {
                GameObject.Owner = civilian;
            }
            return;
        }

        var result = CastleCaptureScan.Tally(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));

        if (!result.AnyCandidates || result.EnemyContest)
        {
            return;
        }

        var winner = PlayerAt(result.WinnerPlayerIndex);
        if (winner == null || winner == owner)
        {
            return;
        }

        if (IsPlayerAllowedToCapture(winner))
        {
            GameObject.Owner = winner;
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Faction match for CastleToUnpackForFaction: the entry's faction name against the
    /// player's template side, raw side string, or side with the "Faction" prefix dropped
    /// (map players carry "Faction&lt;side&gt;").
    /// </summary>
    internal int FindEntryIndexForPlayer(Player player)
    {
        if (player == null)
        {
            return -1;
        }

        for (var i = 0; i < _moduleData.CastleToUnpackForFactions.Count; i++)
        {
            var faction = _moduleData.CastleToUnpackForFactions[i].FactionName;
            if (SideMatches(faction, player.Template?.Side) || SideMatches(faction, player.Side))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Entry lookup by camp name (the explicit-object unpack form, 1087).</summary>
    internal int FindEntryIndexForCamp(string campName)
    {
        if (string.IsNullOrEmpty(campName))
        {
            return -1;
        }

        for (var i = 0; i < _moduleData.CastleToUnpackForFactions.Count; i++)
        {
            if (string.Equals(_moduleData.CastleToUnpackForFactions[i].Camp, campName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool SideMatches(string factionName, string side)
    {
        if (string.IsNullOrEmpty(side))
        {
            return false;
        }

        if (string.Equals(factionName, side, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string prefix = "Faction";
        return side.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(factionName, side.Substring(prefix.Length), StringComparison.OrdinalIgnoreCase);
    }

    private int PlayerIndexOf(Player player)
        => player == null ? -1 : GameEngine.Game.PlayerManager.GetPlayerIndex(player);

    private Player PlayerAt(int index)
    {
        if (index < 0)
        {
            return null;
        }

        var players = GameEngine.Game.PlayerManager.Players;
        return index < players.Count ? players[index] : null;
    }

    // ---- the single walk (declaration order = OUR order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("State", ref _state);
        xfer.XferFrame("RepackTimerExpiry", ref _repackTimerExpiry, Tolerance.Quantum);
        xfer.XferFrame("UnpackCompleteFrame", ref _unpackCompleteFrame, Tolerance.Quantum);
        xfer.XferFrame("UnpackFrame", ref _unpackFrame, Tolerance.Quantum);
        xfer.XferBool("NeedInstantBuild", ref _needInstantBuild);
        xfer.XferInt("CastleEntryIndex", ref _castleEntryIndex);
        xfer.XferInt("UnpackPlayerIndex", ref _unpackPlayerIndex);
        xfer.XferInt("NativePlayerIndex", ref _nativePlayerIndex);
        xfer.XferObjectId("CastleAnchorId", ref _castleAnchorId);
        xfer.XferList("MemberIds", _memberIds, static (IXfer x, ref ObjectId id) => x.XferObjectId("Id", ref id));
        xfer.XferList("NeutralMemberIds", _neutralMemberIds, static (IXfer x, ref ObjectId id) => x.XferObjectId("Id", ref id));
    }
}

// ============================================================================
// PARSE SIDE - the full recovered retail parse table (spec-castles §3.1).
// BFME1-era fields not in the BFME2 table (SidesAllowed,
// UseTheNewCastleSystemInsteadOfTheClunkyBuildList, UseSecondaryBuildList) stay
// parse-only vocabulary: the BFME2 binary does not consume them.
// ============================================================================

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public class CastleBehaviorModuleData : FoundationAIUpdateModuleData
{
    internal new static CastleBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal new static readonly IniParseTable<CastleBehaviorModuleData> FieldParseTable = FoundationAIUpdateModuleData.FieldParseTable
        .Concat(new IniParseTable<CastleBehaviorModuleData>
        {
            { "SidesAllowed", (parser, x) => x.SidesAllowed.Add(Side.Parse(parser)) },
            { "UseTheNewCastleSystemInsteadOfTheClunkyBuildList", (parser, x) => x.UseTheNewCastleSystemInsteadOfTheClunkyBuildList = parser.ParseBoolean() },
            { "FilterValidOwnedEntries", (parser, x) => x.FilterValidOwnedEntries = ObjectFilter.Parse(parser) },
            { "UseSecondaryBuildList", (parser, x) => x.UseSecondaryBuildList = parser.ParseBoolean() },
            { "CastleToUnpackForFaction", (parser, x) => x.CastleToUnpackForFactions.Add(CastleEntry.Parse(parser)) },
            { "MaxCastleRadius", (parser, x) => x.MaxCastleRadius = parser.ParseFix64() },
            { "FadeTime", (parser, x) => { x.FadeTime = parser.ParseFix64(); x.FadeTimeFrames = SecondsToFrames(x.FadeTime, parser); } },
            { "ScanDistance", (parser, x) => x.ScanDistance = parser.ParseFix64() },
            { "PreBuiltList", (parser, x) => x.PreBuiltList = PreBuildObject.Parse(parser) },
            { "PreBuiltPlyr", (parser, x) => x.PreBuiltPlayer = parser.ParseString() },
            { "DecalName", (parser, x) => x.DecalName = parser.ParseAssetReference() },
            { "DecalSize", (parser, x) => x.DecalSize = parser.ParseFix64() },
            { "FilterCrew", (parser, x) => x.FilterCrew = ObjectFilter.Parse(parser) },
            { "CrewReleaseFX", (parser, x) => x.CrewReleaseFX = parser.ParseAssetReference() },
            { "CrewPrepareFX", (parser, x) => x.CrewPrepareFX = parser.ParseAssetReference() },
            { "CrewPrepareTime", (parser, x) => x.CrewPrepareTime = parser.ParseInteger() },
            { "CrewPrepareInterval", (parser, x) => x.CrewPrepareInterval = parser.ParseInteger() },
            { "DisableStructureRotation", (parser, x) => x.DisableStructureRotation = parser.ParseBoolean() },
            { "FactionDecal", (parser, x) => x.FactionDecals.Add(CastleEntry.Parse(parser)) },
            { "InstantUnpack", (parser, x) => x.InstantUnpack = parser.ParseBoolean() },
            { "KeepDeathKillsEverything", (parser, x) => x.KeepDeathKillsEverything = parser.ParseBoolean() },
            { "EvaEnemyCastleSightedEvent", (parser, x) => x.EvaEnemyCastleSightedEvent = parser.ParseAssetReference() },
            { "UnpackDelayTime", (parser, x) => { x.UnpackDelayTime = parser.ParseFix64(); x.UnpackDelayTimeFrames = SecondsToFrames(x.UnpackDelayTime, parser); } },
            { "BuildTime", (parser, x) => { x.BuildTime = parser.ParseFix64(); x.BuildTimeFrames = SecondsToFrames(x.BuildTime, parser); } },
            { "TransferFoundationHealthToCastleUponUnpack", (parser, x) => x.TransferFoundationHealthToCastleUponUnpack = parser.ParseBoolean() },
            { "Summoned", (parser, x) => x.Summoned = parser.ParseBoolean() }
        });

    /// <summary>Seconds (Fix64, blessed literal path) to logic frames, ceil (S5 default pin).</summary>
    private static LogicFrameSpan SecondsToFrames(Fix64 seconds, IniParser parser)
    {
        var rate = new Fix64(parser.SageGame.LogicFramesPerSecond());
        var frames = (long)Fix64.Ceiling(seconds * rate);
        return new LogicFrameSpan(frames < 0 ? 0u : (uint)frames);
    }

    // ---- BFME1-era, not consumed by the BFME2 binary (parse-only vocabulary) ----
    public List<Side> SidesAllowed { get; } = new List<Side>();
    public bool UseTheNewCastleSystemInsteadOfTheClunkyBuildList { get; private set; }
    public bool UseSecondaryBuildList { get; private set; }

    // ---- the consumed table ----
    public ObjectFilter FilterValidOwnedEntries { get; private set; }
    public List<CastleEntry> CastleToUnpackForFactions { get; } = new List<CastleEntry>();

    /// <summary>Decal/perimeter radius (UI + AI base radius), quantized Q31.32.</summary>
    public Fix64 MaxCastleRadius { get; private set; }

    /// <summary>Pack fade, seconds; also the initial value of the repack timer on pack.</summary>
    public Fix64 FadeTime { get; private set; }

    /// <summary>FadeTime frame-quantized at parse (ceil, 5 Hz).</summary>
    public LogicFrameSpan FadeTimeFrames { get; private set; }

    /// <summary>
    /// Ownership-capture scan radius; 0 disables the scan. Retail parses a REAL
    /// and the data-absent default is the ctor's 0 (spec Q2) - the old guessed int/100 is
    /// retired.
    /// </summary>
    public Fix64 ScanDistance { get; private set; } = Fix64.Zero;

    public PreBuildObject PreBuiltList { get; private set; }
    public string PreBuiltPlayer { get; private set; }

    /// <summary>Obsolete decal pair (retail warns "use W3DFloorDraw instead").</summary>
    public string DecalName { get; private set; }
    public Fix64 DecalSize { get; private set; }

    public ObjectFilter FilterCrew { get; private set; }
    public string CrewReleaseFX { get; private set; }
    public string CrewPrepareFX { get; private set; }

    /// <summary>Milliseconds (time-as-int, F3); crew flow is BFME1-era and unconsumed.</summary>
    public int CrewPrepareTime { get; private set; }

    /// <summary>Milliseconds (time-as-int, F3). Distinct field from CrewPrepareTime (offset 0x40 vs 0x38).</summary>
    public int CrewPrepareInterval { get; private set; }

    public bool DisableStructureRotation { get; private set; }
    public List<CastleEntry> FactionDecals { get; } = new List<CastleEntry>();

    [AddedIn(SageGame.Bfme2)]
    public bool InstantUnpack { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool KeepDeathKillsEverything { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string EvaEnemyCastleSightedEvent { get; private set; }

    /// <summary>Delay before a deferred unpack completes, seconds.</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 UnpackDelayTime { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public LogicFrameSpan UnpackDelayTimeFrames { get; private set; }

    /// <summary>Castle build duration, seconds (was missing from the old parse table).</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 BuildTime { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public LogicFrameSpan BuildTimeFrames { get; private set; }

    /// <summary>BFME2-only; was missing from the old parse table (moduleData +0x75).</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool TransferFoundationHealthToCastleUponUnpack { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool Summoned { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CastleBehavior(gameObject, gameEngine, this);
    }
}

public sealed class CastleEntry
{
    internal static CastleEntry Parse(IniParser parser)
    {
        var result = new CastleEntry
        {
            FactionName = parser.ParseString(),
            Camp = parser.ParseAssetReference(),
            // Money is int, never Fix64 (F3); the third token is optional.
            UnpackCost = parser.GetIntegerOptional()
        };
        return result;
    }

    public string FactionName { get; private set; }
    public string Camp { get; private set; }
    public int UnpackCost { get; private set; }
}

public sealed class Side
{
    internal static Side Parse(IniParser parser)
    {
        return new Side()
        {
            SideName = parser.ParseString(),
            CommandSourceTypes = parser.ParseEnumBitArray<CommandSourceType>()
        };
    }

    public string SideName { get; private set; }
    public BitArray<CommandSourceType> CommandSourceTypes { get; private set; } = new BitArray<CommandSourceType>();
}

public sealed class PreBuildObject
{
    internal static PreBuildObject Parse(IniParser parser)
    {
        return new PreBuildObject()
        {
            ObjectName = parser.ParseAssetReference(),
            Count = parser.ParseInteger()
        };
    }

    public string ObjectName { get; private set; }
    public int Count { get; private set; }
}
