#nullable enable

// S9-08 (R15 L3): one AI team - a named set of orderable units and the state machine it moves
// through.
//
// The team is the unit of intent for the attack lane (S9-09): the production manager makes units,
// this makes armies out of them, and the attack manager gives armies somewhere to be. Keeping
// the machine in its own file (rather than as fields on the manager) is what lets a test drive a
// team through Building -> Ready -> Tasked -> Retreating -> Disbanded with no world at all.
//
// MEMBERSHIP INVARIANTS, all enforced here rather than trusted of callers:
//   * members are unique;
//   * members are held in ASCENDING object id, always - so the selection order the emitter sees
//     is a function of the SET of members and nothing else (S9-04 normalizes ids anyway, but a
//     team that reported them in recruitment order would make every trace line depend on
//     enumeration accidents);
//   * a member is never ObjectId.Invalid;
//   * a DISBANDED team accepts no more members - it is terminal, and the manager compacts it
//     away at the end of the frame.
//
// What this class deliberately does NOT do: pick targets, emit orders, or know what a horde is.
// The horde rule (never recruit a horde MEMBER) is applied by AiTeamManager at recruitment time,
// because it is a property of the world snapshot, not of the team.

using System;
using System.Collections.Generic;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// The lifecycle of an <see cref="AiTeam"/>. Forward-only apart from Tasked/Retreating settling
/// back to <see cref="Ready"/>.
/// </summary>
public enum AiTeamState
{
    /// <summary>Recruiting towards its target size. Not yet allowed to be given a mission.</summary>
    Building,

    /// <summary>At target size and idle: the pool the attack manager picks missions out of.</summary>
    Ready,

    /// <summary>Executing a mission (moving, attacking).</summary>
    Tasked,

    /// <summary>Pulling back. Settles to <see cref="Ready"/> once it regroups, or disbands.</summary>
    Retreating,

    /// <summary>Terminal: wiped out or dissolved. The manager removes it at end of frame.</summary>
    Disbanded,
}

/// <summary>
/// A set of orderable units the AI moves as one.
/// </summary>
public sealed class AiTeam
{
    private readonly List<ObjectId> _members = new();

    /// <summary>Match-unique, monotonically increasing team id. Trace text and stable identity.</summary>
    public int Id { get; }

    /// <summary>Current lifecycle state.</summary>
    public AiTeamState State { get; private set; } = AiTeamState.Building;

    /// <summary>Members, always in ascending object id.</summary>
    public IReadOnlyList<ObjectId> Members => _members;

    /// <summary>How many members this team wants before it reports <see cref="AiTeamState.Ready"/>.</summary>
    public int TargetSize { get; }

    /// <summary>Logic frame the team was created on.</summary>
    public uint CreatedFrame { get; }

    /// <summary>Logic frame the team last changed <see cref="State"/>.</summary>
    public uint StateSinceFrame { get; private set; }

    /// <summary>The most members this team ever held. Used to judge how badly it has been mauled.</summary>
    public int PeakSize { get; private set; }

    /// <summary>True once the team holds at least <see cref="TargetSize"/> members.</summary>
    public bool IsFull => _members.Count >= TargetSize;

    /// <summary>True for the terminal state.</summary>
    public bool IsDisbanded => State == AiTeamState.Disbanded;

    /// <summary>True while the team may still take recruits.</summary>
    public bool IsRecruiting => State == AiTeamState.Building && !IsFull;

    public AiTeam(int id, int targetSize, uint createdFrame)
    {
        if (targetSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetSize),
                targetSize,
                "A team must want at least one member; 0 would report Ready while empty and be tasked into nothing.");
        }

        Id = id;
        TargetSize = targetSize;
        CreatedFrame = createdFrame;
        StateSinceFrame = createdFrame;
    }

    /// <summary>
    /// Adds a member. Returns false when the id is invalid, already present, or the team is
    /// disbanded - never throws, because recruitment runs over a snapshot that can disagree with
    /// the team by a frame.
    /// </summary>
    public bool TryAddMember(ObjectId id)
    {
        if (id.IsInvalid || IsDisbanded || Contains(id))
        {
            return false;
        }

        // Insert in ascending id: the list is short (a team is single digits), and keeping it
        // sorted here means no caller ever has to sort it again.
        var index = _members.Count;

        for (var i = 0; i < _members.Count; i++)
        {
            if (_members[i].Index > id.Index)
            {
                index = i;
                break;
            }
        }

        _members.Insert(index, id);

        if (_members.Count > PeakSize)
        {
            PeakSize = _members.Count;
        }

        return true;
    }

    /// <summary>Removes a member (dead, or reassigned). Returns whether it was there.</summary>
    public bool RemoveMember(ObjectId id)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            if (_members[i] == id)
            {
                _members.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Membership test.</summary>
    public bool Contains(ObjectId id)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            if (_members[i] == id)
            {
                return true;
            }
        }

        return false;
    }

    // ---- transitions -------------------------------------------------------------------
    //
    // Each returns whether it actually moved the team, so a caller can count real transitions
    // without re-reading State. Illegal transitions are refused, not thrown: the callers are a
    // manager reacting to a stale snapshot and (later) the attack lane, and a wrong guess should
    // cost nothing.

    /// <summary>Building -> Ready. Refused unless the team is at target size.</summary>
    public bool MarkReady(uint frame)
    {
        if (State == AiTeamState.Building && IsFull)
        {
            return MoveTo(AiTeamState.Ready, frame);
        }

        // Regrouped: a retreating team that still has members becomes available again.
        if (State == AiTeamState.Retreating && _members.Count > 0)
        {
            return MoveTo(AiTeamState.Ready, frame);
        }

        // Mission over.
        return State == AiTeamState.Tasked && MoveTo(AiTeamState.Ready, frame);
    }

    /// <summary>
    /// Ready -> Tasked. Only a Ready team may be given a mission.
    /// </summary>
    /// <remarks>
    /// Taking a mission REBASES <see cref="PeakSize"/> onto the members the team actually marches
    /// out with, because peak means "the most this team held on its current sortie" — the maul
    /// judgement <see cref="AiTeamManager.ShouldRetreat"/> makes is only meaningful against the
    /// strength the team set out with. [INT-R2B] Without the rebase a regrouped survivor team is
    /// re-tasked while its peak still records the pre-maul army, so
    /// <c>AiTeamManager.Update</c> marks it Retreating on the very next tick, the attack wave
    /// musters again, regroups, relaunches — and the wave ping-pongs between Engaging and
    /// Mustering forever instead of fighting. On a first sortie the team is at target size, so
    /// this is a no-op; it only bites on the relaunch arc S9-09 introduced.
    /// </remarks>
    public bool MarkTasked(uint frame)
    {
        if (State != AiTeamState.Ready || !MoveTo(AiTeamState.Tasked, frame))
        {
            return false;
        }

        // Never rebase to zero: PeakSize > 0 is also the manager's "this team once had members"
        // wipe test, and an empty team must still grade as wiped rather than as a fresh slot.
        if (_members.Count > 0)
        {
            PeakSize = _members.Count;
        }

        return true;
    }

    /// <summary>Tasked -> Retreating.</summary>
    public bool MarkRetreating(uint frame)
        => State == AiTeamState.Tasked && MoveTo(AiTeamState.Retreating, frame);

    /// <summary>Anything -> Disbanded. Terminal; drops the members with it.</summary>
    public bool Disband(uint frame)
    {
        if (IsDisbanded)
        {
            return false;
        }

        _members.Clear();
        return MoveTo(AiTeamState.Disbanded, frame);
    }

    private bool MoveTo(AiTeamState state, uint frame)
    {
        if (State == state)
        {
            return false;
        }

        State = state;
        StateSinceFrame = frame;
        return true;
    }

    /// <summary>Short stable tag for trace lines.</summary>
    public static string Tag(AiTeamState state) => state switch
    {
        AiTeamState.Building => "building",
        AiTeamState.Ready => "ready",
        AiTeamState.Tasked => "tasked",
        AiTeamState.Retreating => "retreating",
        _ => "disbanded",
    };
}
