// HordeGarrisonContain - R12 port. BFME-only garrison container for hordes: no GPL sibling
// exists for this module family (generals-gpl has no horde system at all), so this is fresh
// code against the task packet's spec summary/testCases, not a decompiled translation.
//
// The thing this module seats is not the horde GameObject itself but each of the horde's live
// MEMBERS, found through the already-landed GameObject.ParentHorde back-link that
// HordeContainBehavior.Unpack()/Register() sets on every member it creates/accepts. Seating a
// member reuses the same GameObject.AddToContainer/RemoveFromContainer plumbing every other
// landed Contain module uses (Hidden + Held-disabled + Unselectable), so a garrisoned member
// disappears from the world exactly like any other garrison occupant.
//
// The horde GameObject itself is never added to the container - it is never hidden, disabled,
// or deselected - so it stays command-selectable while its members are sequestered (task
// packet item 4). That is the one behavioral difference from a (still-[ParseOnly])
// HordeTransportContain, which would contain the horde object as a single passenger instead.
//
// Formation layout while garrisoned is a flat slot list keyed off EntryPosition/EntryOffset
// (task packet item 1): member i sits at container-local EntryPosition + i * EntryOffset,
// rotated by the container's current facing. On exit, positions instead come from the horde's
// own HordeContainBehavior.GetFormationOffset (its RankInfo-driven layout), applied at the
// container position offset by ExitOffset - so the horde reforms in its usual shape rather than
// the garrison's entry queue (task packet item 3). AlternateFormation morphing and per-member
// weapon fire are explicitly out of scope here (task packet items 3/4): both are HordeAIUpdate's
// job once that module exists; this module only tracks containment and positioning.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public sealed class HordeGarrisonContain : UpdateModule
{
    private readonly HordeGarrisonContainModuleData _moduleData;
    private readonly List<ObjectId> _containedMemberIds = new();

    internal HordeGarrisonContain(GameObject gameObject, IGameEngine gameEngine, HordeGarrisonContainModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
    }

    public IReadOnlyList<ObjectId> ContainedMemberIds => _containedMemberIds;

    public int OccupiedSlots => _containedMemberIds.Count;

    public int TotalSlots => _moduleData.ContainMax;

    /// <summary>
    /// Garrison-entry gateway for a whole horde (task packet items 1/2). Rejects the entire
    /// horde - seating nothing, leaving the horde's own formation untouched - unless every one
    /// of its live members fits: MaxHordeCapacity caps how big a single horde may be to enter at
    /// all, and the container's remaining ContainMax slots cap how many bodies it can hold
    /// regardless of horde size.
    /// </summary>
    public bool TryGarrisonHorde(GameObject hordeObject)
    {
        var members = HordeMembers(hordeObject).ToList();
        if (members.Count == 0)
        {
            return false;
        }

        if (_moduleData.MaxHordeCapacity > 0 && members.Count > _moduleData.MaxHordeCapacity)
        {
            return false;
        }

        if (members.Count > TotalSlots - OccupiedSlots)
        {
            return false;
        }

        var yaw = GameObject.Transform.Yaw;
        var rotation = GameObject.Transform.Rotation;
        for (var i = 0; i < members.Count; i++)
        {
            SeatMember(members[i], i, yaw, rotation);
        }

        GameEngine.AudioSystem?.PlayAudioEvent(_moduleData.EnterSound);
        return true;
    }

    /// <summary>
    /// Late-arrival gateway (e.g. a banner replenish) for a single member into an
    /// already-garrisoned horde. Rejected at the gate - the already-seated members are
    /// unaffected - when the container has no free slot, or seating this member would push its
    /// horde's contained-member count past MaxHordeCapacity.
    /// </summary>
    public bool RegisterMember(GameObject member)
    {
        if (OccupiedSlots >= TotalSlots)
        {
            return false;
        }

        var horde = member.ParentHorde;
        if (horde != null && _moduleData.MaxHordeCapacity > 0)
        {
            var alreadySeated = _containedMemberIds.Count(id => GameObjectForId(id)?.ParentHorde == horde);
            if (alreadySeated + 1 > _moduleData.MaxHordeCapacity)
            {
                return false;
            }
        }

        SeatMember(member, OccupiedSlots, GameObject.Transform.Yaw, GameObject.Transform.Rotation);
        return true;
    }

    /// <summary>
    /// Garrison-exit for every contained member belonging to one horde (task packet item 3).
    /// Positions come from the horde's own formation (HordeContainBehavior.GetFormationOffset)
    /// anchored at the container position plus ExitOffset, so the horde reforms in its normal
    /// shape rather than the garrison's entry-queue layout. The first exiting member issues a
    /// pathfind order to the exit anchor so the reformed horde starts moving clear of the
    /// garrison.
    /// </summary>
    public bool ExitGarrisonHorde(GameObject hordeObject)
    {
        var exiting = _containedMemberIds.Where(id => GameObjectForId(id)?.ParentHorde == hordeObject).ToList();
        if (exiting.Count == 0)
        {
            return false;
        }

        var hordeContain = hordeObject.FindBehavior<HordeContainBehavior>();
        var rotation = GameObject.Transform.Rotation;
        var exitAnchor = GameObject.Transform.Translation +
            Vector3.Transform(_moduleData.ExitOffset, rotation);

        GameObject? firstMember = null;
        foreach (var id in exiting)
        {
            var member = GameObjectForId(id);
            if (member == null)
            {
                _containedMemberIds.Remove(id);
                continue;
            }

            var formationOffset = hordeContain?.GetFormationOffset(member) ?? Vector3.Zero;
            member.UpdateTransform(exitAnchor + formationOffset, rotation);
            member.RemoveFromContainer();
            _containedMemberIds.Remove(id);

            firstMember ??= member;
        }

        firstMember?.AIUpdate?.AddTargetPoint(exitAnchor);

        GameEngine.AudioSystem?.PlayAudioEvent(_moduleData.ExitSound);
        return true;
    }

    public override UpdateSleepTime Update()
    {
        if (GameObject.BodyModule.Health <= 0 || GameObject.IsDestroyed)
        {
            HandleContainerDeath();
        }

        GameObject.ModelConditionFlags.Set(ModelConditionFlag.Garrisoned, _containedMemberIds.Count > 0);

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// Garrison destruction (task packet item 6): EjectPassengersOnDeath (inherited from
    /// HordeTransportContainModuleData) picks between spilling every contained member out at
    /// the container's position or killing them outright, matching the same
    /// eject-vs-kill fork GarrisonContain/TransportContain apply on death.
    /// </summary>
    private void HandleContainerDeath()
    {
        if (_containedMemberIds.Count == 0)
        {
            return;
        }

        var members = _containedMemberIds.ToArray();
        _containedMemberIds.Clear();

        foreach (var id in members)
        {
            var member = GameObjectForId(id);
            if (member == null)
            {
                continue;
            }

            member.RemoveFromContainer();

            if (_moduleData.EjectPassengersOnDeath)
            {
                member.UpdateTransform(GameObject.Transform.Translation, GameObject.Transform.Rotation);
            }
            else
            {
                member.Kill();
            }
        }
    }

    private void SeatMember(GameObject member, int slotIndex, float yaw, Quaternion rotation)
    {
        var localOffset = _moduleData.EntryPosition + _moduleData.EntryOffset * (float)slotIndex;
        var worldOffset = Vector3.Transform(localOffset, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw));

        member.UpdateTransform(GameObject.Transform.Translation + worldOffset, rotation);
        member.AddToContainer(GameObject.Id);
        _containedMemberIds.Add(member.Id);
    }

    private IEnumerable<GameObject> HordeMembers(GameObject hordeObject) =>
        GameEngine.GameLogic.Objects.Where(o =>
            o.ParentHorde == hordeObject && !o.IsDestroyed && !o.IsEffectivelyDead);

    private GameObject? GameObjectForId(ObjectId id) => GameEngine.GameLogic.GetObjectById(id);

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistListWithUInt32Count(
            _containedMemberIds,
            static (StatePersister persister, ref ObjectId item) =>
            {
                persister.PersistObjectIdValue(ref item);
            });
    }
}

[AddedIn(SageGame.Bfme)]
public sealed class HordeGarrisonContainModuleData : HordeTransportContainModuleData
{
    internal static new HordeGarrisonContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static new readonly IniParseTable<HordeGarrisonContainModuleData> FieldParseTable = HordeTransportContainModuleData.FieldParseTable
        .Concat(new IniParseTable<HordeGarrisonContainModuleData>
        {
            { "ContainMax", (parser, x) => x.ContainMax = parser.ParseInteger() },
            { "MaxHordeCapacity", (parser, x) => x.MaxHordeCapacity = parser.ParseInteger() },
            { "EntryPosition", (parser, x) => x.EntryPosition = parser.ParseVector3() },
            { "EntryOffset", (parser, x) => x.EntryOffset = parser.ParseVector3() },
            { "ExitOffset", (parser, x) => x.ExitOffset = parser.ParseVector3() }
        });

    public int ContainMax { get; private set; }
    public int MaxHordeCapacity { get; private set; }
    public Vector3 EntryPosition { get; private set; }
    public Vector3 EntryOffset { get; private set; }
    public Vector3 ExitOffset { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HordeGarrisonContain(gameObject, gameEngine, this);
    }
}
