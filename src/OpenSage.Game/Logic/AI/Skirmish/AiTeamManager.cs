#nullable enable

// S9-08 (R15 L3): AiTeamManager v1 - turns loose units into teams, and runs their lifecycle.
//
// This is the half of the dr-0039 M-c criterion that reads ">= 1 team Ready": if this manager
// does nothing, no team ever reaches Ready, the attack lane (S9-09) has nothing to task, and the
// AI owns an army that stands in its base. <see cref="TeamReadyCounter"/> is the grading key.
//
// THE ONE RULE THAT MATTERS MORE THAN THE REST
//
// A team recruits HORDE OBJECTS and STANDALONE UNITS. It NEVER recruits a horde MEMBER.
//
// A ten-orc horde is eleven objects in the snapshot: the HORDE object plus ten members whose
// GameObject.ParentHorde points at it. AIUpdate.SetTargetPoint (AIUpdate/AIUpdate.cs, the
// ParentHorde early-out at the head of the method) returns immediately for an object with a
// parent horde, so a move order addressed to a member is silently discarded - no error, no log,
// nothing. An AI that recruited members would build full-looking teams, emit correct-looking
// orders, and never move a single unit; the failure presents as "the AI just sits there", which
// is the single most expensive way to be wrong in this lane. The exclusion is expressed once,
// in <see cref="AiObjectView.IsOrderableUnit"/>, and is asserted directly by
// AiTeamManagerTests.HordeMembers_AreNeverRecruited.
//
// ONE TEAM PER UNIT
//
// Enforced structurally rather than with a side index: a candidate is eligible only if no
// existing team already contains it (<see cref="FindTeamOf"/>). There is no assignment map that
// can drift out of sync with the teams it describes.
//
// DETERMINISM
//
// Recruitment walks candidates in ascending object id after sorting them itself - it does not
// trust the snapshot's order, and AiTeamManagerTests.Recruitment_IsIndependentOfSnapshotOrder
// pins that by shuffling the fake's list. Team ids are monotonic, teams are held in creation
// order, and end-of-frame compaction removes disbanded teams while preserving that order, so two
// peers that saw the same objects hold structurally identical team lists.
//
// CLEAN-ROOM: group sizes are seeded from the mod's own AIData (MinInfantryForGroup), and the
// retreat/regroup thresholds are v1 heuristics chosen to make the state machine observable, not
// recovered retail behaviour.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// v1 team manager: recruits orderable units into teams, promotes full teams to
/// <see cref="AiTeamState.Ready"/>, and runs the lifecycle to <see cref="AiTeamState.Disbanded"/>.
/// </summary>
public sealed class AiTeamManager : IAiBrainManager
{
    /// <summary>Trace/report tag. Keep stable - the match report groups evidence on it.</summary>
    public const string ManagerName = "team";

    /// <summary>Counter bumped each time a team reaches Ready. THE M-c team grading key.</summary>
    public const string TeamReadyCounter = "team.ready";

    /// <summary>Counter bumped when a new (Building) team is created.</summary>
    public const string TeamFormedCounter = "team.formed";

    /// <summary>Counter bumped per unit recruited into a team.</summary>
    public const string TeamRecruitedCounter = "team.recruited";

    /// <summary>Counter bumped per member dropped because it no longer exists.</summary>
    public const string TeamMemberLostCounter = "team.member.lost";

    /// <summary>Counter bumped each time a team is disbanded.</summary>
    public const string TeamDisbandedCounter = "team.disbanded";

    /// <summary>Counter bumped each time a mauled team is pulled back.</summary>
    public const string TeamRetreatCounter = "team.retreat";

    /// <summary>
    /// Team size used when the mod ships no <see cref="AIData.MinInfantryForGroup"/>. v1
    /// placeholder tuning, not a recovered retail constant: six hordes is an attack that survives
    /// contact, and it is small enough that the first team forms early in a gate run.
    /// </summary>
    public const int DefaultTeamSize = 6;

    /// <summary>
    /// Upper clamp on the seeded team size. A mod that ships an absurd MinInfantryForGroup would
    /// otherwise produce a team that can never fill, and the AI would hold every unit it ever
    /// built in a Building team that is never tasked.
    /// </summary>
    public const int MaxTeamSize = 30;

    /// <summary>
    /// Teams (of any state) the AI keeps at once. Bounds the recruit loop and the trace volume.
    /// </summary>
    public const int DefaultMaxTeams = 8;

    /// <summary>
    /// Percent of its peak size a Tasked team may fall to before it is pulled back. Int percent,
    /// not a float fraction, matching the int-only arithmetic rule the economy manager set.
    /// </summary>
    public const int RetreatAtPercentOfPeak = 50;

    /// <summary>
    /// Frames a Retreating team spends regrouping before it reports Ready again. Inclusive "not
    /// before" convention: a team that started retreating on frame F becomes Ready when
    /// frame &gt; F + this.
    /// </summary>
    public const uint DefaultRegroupFrames = 150;

    private readonly List<AiTeam> _teams = new();
    private readonly List<AiObjectView> _candidates = new();
    private readonly int _maxTeams;
    private readonly uint _regroupFrames;
    private readonly int _teamSizeOverride;

    private int _nextTeamId = 1;
    private bool _disabledReported;

    /// <inheritdoc />
    public string Name => ManagerName;

    /// <summary>Live teams, in creation order. Disbanded teams are compacted out each frame.</summary>
    public IReadOnlyList<AiTeam> Teams => _teams;

    /// <summary>The team size in force on the most recent tick.</summary>
    public int TeamSize { get; private set; }

    /// <summary>Teams that have reached Ready over this manager's life.</summary>
    public int TeamsReady { get; private set; }

    /// <summary>Teams created over this manager's life.</summary>
    public int TeamsFormed { get; private set; }

    /// <summary>Teams disbanded over this manager's life.</summary>
    public int TeamsDisbanded { get; private set; }

    /// <summary>Units recruited over this manager's life.</summary>
    public int UnitsRecruited { get; private set; }

    /// <param name="maxTeams">Teams held at once. Non-positive means <see cref="DefaultMaxTeams"/>.</param>
    /// <param name="regroupFrames">Frames a retreating team regroups for.</param>
    /// <param name="teamSizeOverride">
    /// Forces the team size, bypassing the <see cref="AIData"/> seed. Non-positive (the default)
    /// means "use the mod's data"; tests use it to form a team in two frames instead of six.
    /// </param>
    public AiTeamManager(
        int maxTeams = DefaultMaxTeams,
        uint regroupFrames = DefaultRegroupFrames,
        int teamSizeOverride = 0)
    {
        _maxTeams = maxTeams > 0 ? maxTeams : DefaultMaxTeams;
        _regroupFrames = regroupFrames;
        _teamSizeOverride = teamSizeOverride;
    }

    /// <summary>
    /// The group size the AI forms teams at, seeded from the mod's own data.
    /// </summary>
    /// <remarks>
    /// <see cref="AIData.MinInfantryForGroup"/> is the shipped "how many of these before it is
    /// worth calling it a group" number and is the closest thing the data has to a squad size;
    /// BFME2's other group fields (MinVehiclesForGroup, MinDistanceForGroup) are about distance
    /// and vehicle mixes this v1 does not model. A missing or non-positive value degrades to
    /// <see cref="DefaultTeamSize"/> rather than to a crash or to a team of zero, matching the
    /// null policy on <see cref="IAiWorldView.AIData"/>. The result is clamped to
    /// <see cref="MaxTeamSize"/>.
    /// </remarks>
    public static int GroupSize(AIData? aiData)
    {
        var seed = aiData is not null && aiData.MinInfantryForGroup > 0
            ? aiData.MinInfantryForGroup
            : DefaultTeamSize;

        return seed > MaxTeamSize ? MaxTeamSize : seed;
    }

    /// <summary>
    /// One frame of team management: drop dead members, run the lifecycle, recruit, promote,
    /// then compact.
    /// </summary>
    public void Update(SkirmishAIBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);

        var world = brain.World;
        var frame = world.CurrentFrame;

        // Mod-level off switch, same policy as AiBaseManager's DisableBaseBuilding.
        if (world.SkirmishAIData is { DisableTeamBuilding: true })
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                Line(brain, string.Create(CultureInfo.InvariantCulture, $"f={frame} disabled=datateambuilding"));
            }

            return;
        }

        TeamSize = _teamSizeOverride > 0 ? _teamSizeOverride : GroupSize(world.AIData);

        CollectCandidates(world.OwnObjects);

        PruneDeadMembers(brain);
        RunLifecycle(brain, frame);
        Recruit(brain, frame);
        PromoteFullTeams(brain, frame);
        Compact();
    }

    // ---- candidate set -------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the frame's recruitable set: orderable units only, ascending object id.
    /// </summary>
    /// <remarks>
    /// The filter is <see cref="AiObjectView.IsOrderableUnit"/> - not a structure, not still
    /// being built, and NOT a horde member. See this file's header for why the horde-member
    /// exclusion is the load-bearing one. The sort is done here rather than assumed of the
    /// snapshot so that recruitment is a function of the SET of units, whatever order the world
    /// view happened to report them in.
    /// </remarks>
    private void CollectCandidates(IReadOnlyList<AiObjectView>? ownObjects)
    {
        _candidates.Clear();

        if (ownObjects == null)
        {
            return;
        }

        for (var i = 0; i < ownObjects.Count; i++)
        {
            var own = ownObjects[i];

            if (own.IsOrderableUnit && own.Id.IsValid)
            {
                _candidates.Add(own);
            }
        }

        _candidates.Sort(static (a, b) => a.Id.Index.CompareTo(b.Id.Index));
    }

    private bool IsAlive(ObjectId id)
    {
        for (var i = 0; i < _candidates.Count; i++)
        {
            if (_candidates[i].Id == id)
            {
                return true;
            }
        }

        return false;
    }

    // ---- lifecycle -----------------------------------------------------------------------

    /// <summary>
    /// Drops members that are no longer orderable units - dead, sold, or absorbed into a horde
    /// after we recruited them standalone.
    /// </summary>
    private void PruneDeadMembers(SkirmishAIBrain brain)
    {
        for (var t = 0; t < _teams.Count; t++)
        {
            var team = _teams[t];

            if (team.IsDisbanded)
            {
                continue;
            }

            // Backwards: RemoveMember mutates the list we are walking.
            for (var m = team.Members.Count - 1; m >= 0; m--)
            {
                var id = team.Members[m];

                if (IsAlive(id))
                {
                    continue;
                }

                team.RemoveMember(id);
                brain.Trace.Count(TeamMemberLostCounter);
            }
        }
    }

    /// <summary>
    /// Disbands emptied teams, pulls back mauled ones, and lets regrouped ones report Ready.
    /// </summary>
    private void RunLifecycle(SkirmishAIBrain brain, uint frame)
    {
        for (var t = 0; t < _teams.Count; t++)
        {
            var team = _teams[t];

            if (team.IsDisbanded)
            {
                continue;
            }

            // An empty team that once had members is a wipe. An empty Building team that never
            // had any is just an empty recruiting slot and is left alone - disbanding it would
            // churn a new team id every frame until the first unit exists.
            if (team.Members.Count == 0)
            {
                if (team.PeakSize > 0)
                {
                    Disband(brain, team, frame, "wiped");
                }

                continue;
            }

            switch (team.State)
            {
                case AiTeamState.Tasked when ShouldRetreat(team):
                    if (team.MarkRetreating(frame))
                    {
                        brain.Trace.Count(TeamRetreatCounter);
                        Line(brain, string.Create(
                            CultureInfo.InvariantCulture,
                            $"f={frame} retreat team={team.Id} size={team.Members.Count} peak={team.PeakSize}"));
                    }

                    break;

                case AiTeamState.Retreating when frame > team.StateSinceFrame + _regroupFrames:
                    if (team.MarkReady(frame))
                    {
                        ReportReady(brain, team, frame, "regrouped");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// True when a tasked team has lost enough of its peak strength to be worth pulling back.
    /// </summary>
    /// <remarks>
    /// Int cross-multiplication rather than a float ratio: <c>size * 100 &lt; peak * percent</c>
    /// is the same comparison with no rounding and no float-equality hazard.
    /// </remarks>
    public static bool ShouldRetreat(AiTeam team, int retreatAtPercentOfPeak = RetreatAtPercentOfPeak)
    {
        ArgumentNullException.ThrowIfNull(team);

        return team.PeakSize > 0
            && team.Members.Count * 100 < team.PeakSize * retreatAtPercentOfPeak;
    }

    /// <summary>
    /// Fills the recruiting team with unassigned units, lowest object id first, creating a new
    /// team when there is none and the team cap allows it.
    /// </summary>
    private void Recruit(SkirmishAIBrain brain, uint frame)
    {
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];

            // One team per unit: a unit that is already somebody's member is not a candidate.
            if (FindTeamOf(candidate.Id) is not null)
            {
                continue;
            }

            var team = FindRecruitingTeam() ?? CreateTeam(brain, frame);

            if (team is null)
            {
                // Team cap reached; the leftovers stay unassigned and are picked up whenever a
                // team disbands. Not traced per unit - it would be one line per idle unit per
                // frame.
                return;
            }

            if (!team.TryAddMember(candidate.Id))
            {
                continue;
            }

            UnitsRecruited++;
            brain.Trace.Count(TeamRecruitedCounter);
        }
    }

    /// <summary>Promotes every full Building team to Ready.</summary>
    private void PromoteFullTeams(SkirmishAIBrain brain, uint frame)
    {
        for (var t = 0; t < _teams.Count; t++)
        {
            var team = _teams[t];

            if (team.State == AiTeamState.Building && team.IsFull && team.MarkReady(frame))
            {
                ReportReady(brain, team, frame, "formed");
            }
        }
    }

    /// <summary>
    /// Removes disbanded teams, preserving the relative order of the survivors.
    /// </summary>
    /// <remarks>
    /// In place and order-preserving on purpose: swap-with-last compaction would reorder the
    /// list, and team order is what the attack lane iterates, so two peers would task different
    /// teams first. Ids are never reused, so a compacted-away team cannot be confused with a
    /// later one.
    /// </remarks>
    private void Compact()
    {
        var write = 0;

        for (var read = 0; read < _teams.Count; read++)
        {
            if (_teams[read].IsDisbanded)
            {
                continue;
            }

            _teams[write++] = _teams[read];
        }

        if (write < _teams.Count)
        {
            _teams.RemoveRange(write, _teams.Count - write);
        }
    }

    // ---- API for the attack lane (S9-09) ---------------------------------------------------

    /// <summary>
    /// The lowest-id Ready team, or null. The attack lane's pick: lowest id means the oldest
    /// surviving team goes first, and it is stable across machines.
    /// </summary>
    public AiTeam? NextReadyTeam()
    {
        AiTeam? best = null;

        for (var t = 0; t < _teams.Count; t++)
        {
            var team = _teams[t];

            if (team.State != AiTeamState.Ready)
            {
                continue;
            }

            if (best is null || team.Id < best.Id)
            {
                best = team;
            }
        }

        return best;
    }

    /// <summary>The team holding <paramref name="id"/>, or null. One team per unit, so at most one.</summary>
    public AiTeam? FindTeamOf(ObjectId id)
    {
        for (var t = 0; t < _teams.Count; t++)
        {
            if (_teams[t].Contains(id))
            {
                return _teams[t];
            }
        }

        return null;
    }

    /// <summary>Marks a Ready team as executing a mission. Returns whether the transition happened.</summary>
    public bool TaskTeam(SkirmishAIBrain brain, AiTeam team, uint frame)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(team);

        if (!team.MarkTasked(frame))
        {
            return false;
        }

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} tasked team={team.Id} size={team.Members.Count}"));

        return true;
    }

    /// <summary>Pulls a Tasked team back. Returns whether the transition happened.</summary>
    public bool RetreatTeam(SkirmishAIBrain brain, AiTeam team, uint frame)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(team);

        if (!team.MarkRetreating(frame))
        {
            return false;
        }

        brain.Trace.Count(TeamRetreatCounter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} retreat team={team.Id} size={team.Members.Count} peak={team.PeakSize}"));

        return true;
    }

    /// <summary>
    /// Dissolves a team and returns its members to the recruitable pool on the next tick.
    /// </summary>
    public bool DisbandTeam(SkirmishAIBrain brain, AiTeam team, uint frame)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(team);

        return Disband(brain, team, frame, "dissolved");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private AiTeam? FindRecruitingTeam()
    {
        for (var t = 0; t < _teams.Count; t++)
        {
            if (_teams[t].IsRecruiting)
            {
                return _teams[t];
            }
        }

        return null;
    }

    private AiTeam? CreateTeam(SkirmishAIBrain brain, uint frame)
    {
        if (_teams.Count >= _maxTeams)
        {
            return null;
        }

        var team = new AiTeam(_nextTeamId++, TeamSize, frame);
        _teams.Add(team);

        TeamsFormed++;
        brain.Trace.Count(TeamFormedCounter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} form team={team.Id} target={team.TargetSize}"));

        return team;
    }

    private void ReportReady(SkirmishAIBrain brain, AiTeam team, uint frame, string why)
    {
        TeamsReady++;
        brain.Trace.Count(TeamReadyCounter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} ready team={team.Id} size={team.Members.Count} why={why}"));
    }

    private bool Disband(SkirmishAIBrain brain, AiTeam team, uint frame, string why)
    {
        if (!team.Disband(frame))
        {
            return false;
        }

        TeamsDisbanded++;
        brain.Trace.Count(TeamDisbandedCounter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} disband team={team.Id} peak={team.PeakSize} why={why}"));

        return true;
    }

    private void Line(SkirmishAIBrain brain, string message) => brain.Trace.Line(Name, message);
}
