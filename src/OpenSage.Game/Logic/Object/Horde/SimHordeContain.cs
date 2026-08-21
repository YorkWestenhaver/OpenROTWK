// SimHordeContain - the S6 horde/formation runtime, implemented FRESH from the clean-room
// behavioral spec bfme2-workbench/research/spec-hordes.md (BFME-only system; no GPL
// reference exists - spec facts and addresses only, no decompiled logic transplanted).
//
// The two-layer object model (spec §1): this module lives on the HORDE object (ImmortalBody,
// renders nothing); MEMBERS are full units existing in the world, moved by their own S2
// SimLocomotorUpdate. Per frame this module:
//   1. lazily builds the slot table from RankInfo (spec §5.2) - per-slot jitter drawn from
//      Context.GameLogicRandom, ONE roll per axis per slot per rebuild (CRC-relevant,
//      HordeContain.cpp lines 0x627/0x62b), and spawns the InitialPayload members;
//   2. reaps dead members (slot back to vacancy; banner-death bookkeeping; last member
//      death destroys the horde object - spec §8);
//   3. runs melee bookkeeping: rank release (RanksToReleaseWhenAttacking /
//      RanksToJustFreeWhenAttacking), leash re-tether (MeleeAttackLeashDistance, spec
//      §5.3), and the back-up shuffle (BackUp*/BackupPercentage timers, spec §5.4);
//   4. steers each seated member to its slot's world position (anchor + offset rotated by
//      the horde facing) through the member's SimLocomotorUpdate, snapping facing to the
//      horde facing once in position;
//   5. handles banner respawn timers (DiedRespawnTime / MeleeFreeBannerReSpawnTime).
// The flank test (spec §6, CONFIRMED formula) runs when a member reports damage:
// flanked iff dot(d, f) < cos(FrontAngle / 2), plus the recent-attack ring buffer.
//
// Anchor/facing: the horde object's own S2 locomotor state (SimLocomotorUpdate.Physics) -
// the horde template carries the horde locomotors (spec §1) - so all steering math is
// Fix64 end to end. A horde without a SimLocomotorUpdate does not steer (finding HORDE-F2).
//
// Deliberately deferred with findings (spec §9 "Defer" + §10 open questions): HordeAIUpdate
// (AIUpdate family is deliberately unfrozen, api-freeze-v1 §7), ComboHorde/SplitHorde
// execution, AlternateFormation morph, AODHordeContain noise, porcupine body scaling,
// TheFormationAssistant, FLANKED->attribute-modifier drop, horde XP (S4 seam).

using System.Collections.Generic;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Horde;

[SimState]
public sealed class SimHordeContain : UpdateModule
{
    private readonly SimHordeContainModuleData _data;

    /// <summary>One formation slot (spec §5.2 slot records, our shape).</summary>
    internal struct HordeSlot
    {
        /// <summary>Index into the data's RankInfos; -1 marks the appended banner slot.</summary>
        public int RankInfoIndex;
        public int RankNumber;

        /// <summary>Horde-local offset, jitter already applied (rolled once at build).</summary>
        public Fix64 OffsetX;
        public Fix64 OffsetY;

        /// <summary>Absolute slot index of this slot's leader, -1 when none (spec §5.1 Leader).</summary>
        public int LeaderSlot;

        public ObjectId Occupant;

        /// <summary>Chase-released from the formation while melee attacking (spec §5.3).</summary>
        public bool Released;

        /// <summary>Freed from slot targets but NOT chase-released (RanksToJustFreeWhenAttacking).</summary>
        public bool Freed;

        /// <summary>Back-up shuffle: next roll frame + accumulated local back-up distance (spec §5.4).</summary>
        public LogicFrame NextBackupFrame;
        public Fix64 BackupDistance;
    }

    private struct AttackRecord
    {
        public Fix64 Angle;
        public LogicFrame Expiry;
    }

    /// <summary>Recent-attack ring buffer capacity (binary keeps a small list @ +0x1b0; size unrecovered - finding HORDE-F6).</summary>
    private const int MaxRecentAttacks = 8;

    private static readonly Fix64 MemberSpeedSentinel = Fix64.FromDecimalLiteral("99999");

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private bool _initialized;
    private readonly List<HordeSlot> _slots = new();
    private int _bannerSlotIndex = -1;
    private bool _meleeAttacking;
    private LogicFrame _lastMeleeFrame;
    private LogicFrame _flankedUntil;
    private LogicFrame _nextFlankAllowedFrame;
    private readonly List<AttackRecord> _recentAttacks = new();
    private LogicFrame _bannerDiedFrame;
    private bool _bannerRespawnPending;

    public SimHordeContain(GameObject gameObject, ISimContext context, SimHordeContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (read by tests / future AIUpdate + UI seams) ----

    public bool IsFlanked => Context.CurrentFrame < _flankedUntil;

    public bool IsMeleeAttacking => _meleeAttacking;

    public LogicFrame LastMeleeFrame => _lastMeleeFrame;

    public int MemberCount
    {
        get
        {
            var count = 0;
            foreach (var slot in _slots)
            {
                if (slot.Occupant.IsValid)
                {
                    count++;
                }
            }
            return count;
        }
    }

    public bool IsBelowMinimumSize => MemberCount < _data.MinimumHordeSize;

    public int SlotCount => _slots.Count;

    internal HordeSlot GetSlot(int index) => _slots[index];

    public IEnumerable<ObjectId> MemberIds
    {
        get
        {
            foreach (var slot in _slots)
            {
                if (slot.Occupant.IsValid)
                {
                    yield return slot.Occupant;
                }
            }
        }
    }

    /// <summary>
    /// Slot world position for a seated member (anchor + local offset incl. back-up rotated
    /// by the horde facing). Valid for tests and steering; Fix64 end to end.
    /// </summary>
    public bool TryGetSlotWorldPosition(int slotIndex, out FixVector2 position)
    {
        position = default;
        var mover = HordeMover;
        if (mover == null || !mover.TransformInitialized || slotIndex < 0 || slotIndex >= _slots.Count)
        {
            return false;
        }
        var anchor = mover.Physics.Position;
        var yaw = mover.Physics.Yaw;
        position = SlotWorldPosition(_slots[slotIndex], new FixVector2(anchor.X, anchor.Y), yaw);
        return true;
    }

    /// <summary>
    /// Melee-attack mux (interim public setter: HordeAIUpdate, deliberately unfrozen, is the
    /// eventual caller - finding HORDE-F1). Entering melee arms release/free flags and the
    /// back-up timers; leaving clears them and re-forms.
    /// </summary>
    public void SetMeleeAttacking(bool attacking)
    {
        if (_meleeAttacking == attacking)
        {
            return;
        }
        _meleeAttacking = attacking;
        var now = Context.CurrentFrame;
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (attacking)
            {
                slot.Released = _data.RanksToReleaseWhenAttacking.Contains(slot.RankNumber);
                slot.Freed = _data.RanksToJustFreeWhenAttacking.Contains(slot.RankNumber);
                slot.NextBackupFrame = now + RollBackupDelay();
            }
            else
            {
                slot.Released = false;
                slot.Freed = false;
                slot.BackupDistance = Fix64.Zero;
            }
            _slots[i] = slot;
        }
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// The flank test (spec §6, formula CONFIRMED against the behavioral spec). Called by a member's
    /// SimHordeMember when it takes damage. Direction d = normalize(attacker - horde), horde
    /// forward f from the facing; flanked iff dot(d, f) &lt; cos(FrontAngle / 2); every
    /// unexpired recorded attack angle is retested the same way (multi-directional attacks
    /// flank even a wide arc). FlankedDelay throttles re-triggers; FlankedDuration arms the
    /// expiry frame. FrontAngle &gt;= 360 deg = unflankable.
    /// OPEN QUESTION 1 default: FrontAngle is the TOTAL arc (cos of the half-angle), exactly
    /// as the recovered formula reads; the +-90 deg VM probe stays queued.
    /// </summary>
    public void NotifyMemberDamaged(ObjectId memberId, FixVector2 attackerPosition)
    {
        if (_data.FrontAngleRadians >= Fix64.PiTimes2)
        {
            return;
        }
        var mover = HordeMover;
        if (mover == null || !mover.TransformInitialized)
        {
            return;
        }

        var now = Context.CurrentFrame;
        var anchor = mover.Physics.Position;
        var dx = attackerPosition.X - anchor.X;
        var dy = attackerPosition.Y - anchor.Y;
        var len = Fix64.Sqrt(dx * dx + dy * dy);
        if (len == Fix64.Zero)
        {
            return;
        }
        dx /= len;
        dy /= len;

        var cosHalf = FixTrig.Cos(_data.FrontAngleRadians / Fix64.Two);
        var yaw = mover.Physics.Yaw;
        var fx = FixTrig.Cos(yaw);
        var fy = FixTrig.Sin(yaw);

        var flankedNow = dx * fx + dy * fy < cosHalf;

        // Retest the recorded recent attacks against the current facing (spec §6.3).
        PruneRecentAttacks(now);
        if (!flankedNow)
        {
            foreach (var record in _recentAttacks)
            {
                var ax = FixTrig.Cos(record.Angle);
                var ay = FixTrig.Sin(record.Angle);
                if (ax * fx + ay * fy < cosHalf)
                {
                    flankedNow = true;
                    break;
                }
            }
        }

        // Record this attack in the ring buffer (bounded; oldest dropped).
        if (_recentAttacks.Count >= MaxRecentAttacks)
        {
            _recentAttacks.RemoveAt(0);
        }
        _recentAttacks.Add(new AttackRecord
        {
            Angle = FixTrig.Atan2(dy, dx),
            Expiry = now + _data.FlankedDuration,
        });

        if (flankedNow && now >= _nextFlankAllowedFrame)
        {
            _flankedUntil = now + _data.FlankedDuration;
            _nextFlankAllowedFrame = now + _data.FlankedDelay;
        }
    }

    /// <summary>
    /// Seats an externally produced unit (production/garrison path) into the first vacant
    /// slot matching its template. Returns false when no slot fits.
    /// </summary>
    public bool RegisterMember(GameObject member)
    {
        EnsureInitialized();
        return SeatMember(member);
    }

    /// <summary>
    /// The banner replenish path (spec §7): finds the first vacant non-banner slot, creates
    /// that slot's unit type standing at the banner carrier, seats it, fires UnitSpawnFX.
    /// Called by the banner member's SimBannerCarrierUpdate when its idle timers allow.
    /// OPEN QUESTION 5 default: replenish requires the live banner carrier (the caller).
    /// </summary>
    public bool TryReplenishOneMember(GameObject bannerCarrier, string unitSpawnFXName)
    {
        if (!_initialized)
        {
            return false;
        }
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Occupant.IsValid || slot.RankInfoIndex < 0)
            {
                continue;
            }
            var definition = _data.RankInfos[slot.RankInfoIndex].UnitType?.Value;
            if (definition == null)
            {
                continue;
            }
            var mover = HordeMover;
            var yaw = mover != null ? mover.Physics.Yaw : Fix64.Zero;
            var created = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, bannerCarrier, yaw);
            if (created == null)
            {
                return false;
            }
            SeatIntoSlot(i, created);
            if (!string.IsNullOrEmpty(unitSpawnFXName))
            {
                Context.Events.FireFXAtObject(unitSpawnFXName, created.Id);
            }
            return true;
        }
        return false;
    }

    // ---- per-frame ----

    public override UpdateSleepTime Update()
    {
        EnsureInitialized();

        var now = Context.CurrentFrame;
        if (_meleeAttacking)
        {
            _lastMeleeFrame = now;
        }
        PruneRecentAttacks(now);

        if (!ReapDeadMembers())
        {
            // Last member gone: the horde object dies with its members (spec §8).
            // EvaEventLastMemberDeath is an EVA request - no EVA seam on ISimEvents yet
            // (finding HORDE-F8).
            Context.GameLogic.DestroyObject(GameObject);
            return UpdateSleepTime.Forever;
        }

        TickBannerRespawn(now);

        var mover = HordeMover;
        if (mover != null && mover.TransformInitialized)
        {
            SteerMembers(now, mover);
        }

        // Hordes are cheap always-on modules while members live (steering re-targets every
        // frame per spec §5.3); sleep optimization is a recorded follow-up (HORDE-F9).
        return UpdateSleepTime.None;
    }

    // ---- internals ----

    private SimLocomotorUpdate HordeMover => GameObject.FindBehavior<SimLocomotorUpdate>();

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        BuildSlots();
        SpawnInitialPayload();
    }

    /// <summary>
    /// Slot-list construction (spec §5.2): total slots = sum of Position
    /// counts over RankInfos, in declaration order; per position the offset gets a jitter of
    /// GameLogicRandom in [-RandomOffset .. +RandomOffset] per axis - one X draw then one Y
    /// draw per slot, rolled ONCE at (re)build, never per frame. Leader links resolve to
    /// absolute slot indices. If BannerCarriersAllowed is non-empty a banner slot is
    /// appended at the (default center-rear X:40 Y:0) BannerCarrierPosition.
    /// OPEN QUESTION 2 default: jitter is NOT re-rolled on move orders - only here.
    /// </summary>
    private void BuildSlots()
    {
        _slots.Clear();
        _bannerSlotIndex = -1;

        // First pass: seat offsets with jitter, leaders unresolved.
        var rankStartSlot = new List<int>();
        for (var r = 0; r < _data.RankInfos.Count; r++)
        {
            rankStartSlot.Add(_slots.Count);
            var rank = _data.RankInfos[r];
            foreach (var position in rank.Positions)
            {
                var jitterX = RollJitter(_data.RandomOffset.X);
                var jitterY = RollJitter(_data.RandomOffset.Y);
                _slots.Add(new HordeSlot
                {
                    RankInfoIndex = r,
                    RankNumber = rank.RankNumber,
                    OffsetX = position.X + jitterX,
                    OffsetY = position.Y + jitterY,
                    LeaderSlot = -1,
                    Occupant = ObjectId.Invalid,
                });
            }
        }

        // Second pass: resolve Leader (rank, positionIndex) -> absolute slot index. The
        // original parser guarantees the references are valid; tolerate bad data by leaving
        // the link at -1 rather than crashing on a mod file.
        for (var r = 0; r < _data.RankInfos.Count; r++)
        {
            foreach (var leader in _data.RankInfos[r].Leaders)
            {
                var followerSlot = rankStartSlot[r] + leader.FollowerPositionIndex;
                var leaderRankIndex = FindRankInfoIndexByNumber(leader.LeaderRank);
                if (leaderRankIndex < 0 ||
                    leader.LeaderPositionIndex >= _data.RankInfos[leaderRankIndex].Positions.Count)
                {
                    continue;
                }
                var slot = _slots[followerSlot];
                slot.LeaderSlot = rankStartSlot[leaderRankIndex] + leader.LeaderPositionIndex;
                _slots[followerSlot] = slot;
            }
        }

        // Banner slot appended after the ranks (spec §5.2).
        if (_data.BannerCarriersAllowed.Count > 0)
        {
            var bannerOffset = DefaultBannerOffset();
            _bannerSlotIndex = _slots.Count;
            _slots.Add(new HordeSlot
            {
                RankInfoIndex = -1,
                RankNumber = -1,
                OffsetX = bannerOffset.X,
                OffsetY = bannerOffset.Y,
                LeaderSlot = -1,
                Occupant = ObjectId.Invalid,
            });
        }
    }

    private int FindRankInfoIndexByNumber(int rankNumber)
    {
        for (var i = 0; i < _data.RankInfos.Count; i++)
        {
            if (_data.RankInfos[i].RankNumber == rankNumber)
            {
                return i;
            }
        }
        return -1;
    }

    private FixVector2 DefaultBannerOffset()
    {
        // The matching BannerCarrierPosition entry wins; default center-rear X:40 Y:0
        // (spec §5.2 default entry).
        foreach (var entry in _data.BannerCarrierPositions)
        {
            return entry.Position;
        }
        return new FixVector2(Fix64.FromDecimalLiteral("40"), Fix64.Zero);
    }

    private Fix64 RollJitter(Fix64 halfRange)
    {
        // ONE synchronized draw per axis (CRC-relevant, spec §5.2). A zero range still
        // draws nothing - the original only rolls when the offset is configured.
        if (halfRange == Fix64.Zero)
        {
            return Fix64.Zero;
        }
        return Context.GameLogicRandom.NextFix64(-halfRange, halfRange);
    }

    /// <summary>InitialPayload members auto-created at horde creation (spec §4.1), plus the
    /// level-0-gated banner carrier (spec §7; upgrade purchase path is finding HORDE-F5).</summary>
    private void SpawnInitialPayload()
    {
        var mover = HordeMover;
        var yaw = mover != null ? mover.Physics.Yaw : Fix64.Zero;

        foreach (var payload in _data.InitialPayloads)
        {
            var definition = payload.Object?.Value;
            if (definition == null)
            {
                continue;
            }
            for (var i = 0; i < payload.Count; i++)
            {
                var created = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject, yaw);
                if (created == null || !SeatMember(created))
                {
                    break;
                }
            }
        }

        if (_bannerSlotIndex >= 0 && _data.BannerCarrierMinLevel <= 0 &&
            !_slots[_bannerSlotIndex].Occupant.IsValid)
        {
            SpawnBannerCarrier(yaw);
        }
    }

    private bool SpawnBannerCarrier(Fix64 yaw)
    {
        if (_bannerSlotIndex < 0)
        {
            return false;
        }
        var definition = ResolveBannerDefinition();
        if (definition == null)
        {
            return false;
        }
        var created = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject, yaw);
        if (created == null)
        {
            return false;
        }
        SeatIntoSlot(_bannerSlotIndex, created);
        created.FindBehavior<SimBannerCarrierUpdate>()?.AttachToHorde(GameObject.Id);
        return true;
    }

    private ObjectDefinition ResolveBannerDefinition()
    {
        foreach (var name in _data.BannerCarriersAllowed)
        {
            var definition = Context.Assets.GetObjectDefinition(name);
            if (definition != null)
            {
                return definition;
            }
        }
        return null;
    }

    private bool SeatMember(GameObject member)
    {
        // Banner templates go to the banner slot; everything else to the first vacant slot
        // whose RankInfo UnitType matches (id->slot map + free-slot list semantics: vacated
        // slots refill first-free, members are never reshuffled - spec §5.2).
        if (_bannerSlotIndex >= 0 && !_slots[_bannerSlotIndex].Occupant.IsValid &&
            _data.BannerCarriersAllowed.Contains(member.Definition.Name))
        {
            SeatIntoSlot(_bannerSlotIndex, member);
            member.FindBehavior<SimBannerCarrierUpdate>()?.AttachToHorde(GameObject.Id);
            return true;
        }

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Occupant.IsValid || slot.RankInfoIndex < 0)
            {
                continue;
            }
            var wanted = _data.RankInfos[slot.RankInfoIndex].UnitType?.Value;
            if (wanted == member.Definition ||
                (wanted != null && wanted.ObjectIsMemberOfBuildVariations(member.Definition)))
            {
                SeatIntoSlot(i, member);
                return true;
            }
        }
        return false;
    }

    private void SeatIntoSlot(int slotIndex, GameObject member)
    {
        var slot = _slots[slotIndex];
        slot.Occupant = member.Id;
        slot.Released = false;
        slot.Freed = false;
        slot.BackupDistance = Fix64.Zero;
        if (_meleeAttacking)
        {
            slot.Released = _data.RanksToReleaseWhenAttacking.Contains(slot.RankNumber);
            slot.Freed = _data.RanksToJustFreeWhenAttacking.Contains(slot.RankNumber);
            slot.NextBackupFrame = Context.CurrentFrame + RollBackupDelay();
        }
        _slots[slotIndex] = slot;
        member.FindBehavior<SimHordeMember>()?.AttachToHorde(GameObject.Id);
    }

    /// <summary>Returns false when the horde has no live member left (after initial spawn).</summary>
    private bool ReapDeadMembers()
    {
        var anyAlive = false;
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (!slot.Occupant.IsValid)
            {
                continue;
            }
            var member = Context.GameLogic.GetObjectById(slot.Occupant);
            if (member == null || member.IsDestroyed || member.IsEffectivelyDead)
            {
                slot.Occupant = ObjectId.Invalid;
                slot.Released = false;
                slot.Freed = false;
                slot.BackupDistance = Fix64.Zero;
                _slots[i] = slot;

                if (i == _bannerSlotIndex)
                {
                    OnBannerDied();
                }
                continue;
            }
            anyAlive = true;
        }
        return anyAlive;
    }

    private void OnBannerDied()
    {
        _bannerDiedFrame = Context.CurrentFrame;
        _bannerRespawnPending = true;

        if (_data.BannerCarrierDestroyHordeOnDeath)
        {
            // The whole horde dies with the banner (summons/thrall-master shape, spec §7).
            // Members are killed through the real death path (DeathType from
            // BannerCarrierHordeDeathType degrades to Normal - finding HORDE-F7), the horde
            // object is destroyed next frame by the empty-horde reap.
            foreach (var memberId in MemberIds)
            {
                var member = Context.GameLogic.GetObjectById(memberId);
                member?.Kill();
            }
            _bannerRespawnPending = false;
        }
    }

    private void TickBannerRespawn(LogicFrame now)
    {
        if (!_bannerRespawnPending || _bannerSlotIndex < 0 ||
            _slots[_bannerSlotIndex].Occupant.IsValid)
        {
            return;
        }
        var bannerData = ResolveBannerData();
        if (bannerData == null)
        {
            _bannerRespawnPending = false;
            return;
        }
        // "how much time must pass after Banner Carrier dies before horde can spawn
        // another" + "time since horde has been fighting" (EA comments, spec §4.3).
        if (now < _bannerDiedFrame + bannerData.DiedRespawnTime)
        {
            return;
        }
        if (now < _lastMeleeFrame + bannerData.MeleeFreeBannerReSpawnTime)
        {
            return;
        }
        var mover = HordeMover;
        if (SpawnBannerCarrier(mover != null ? mover.Physics.Yaw : Fix64.Zero))
        {
            _bannerRespawnPending = false;
        }
    }

    private SimBannerCarrierUpdateModuleData ResolveBannerData()
    {
        var definition = ResolveBannerDefinition();
        if (definition == null)
        {
            return null;
        }
        // Explicit ordinal key order (SIMCORE004): the scan wants ONE module data by type,
        // so any total order works; behaviors-dictionary order is implementation-defined.
        foreach (var tag in System.Linq.Enumerable.OrderBy(
                     definition.Behaviors.Keys, static k => k, System.StringComparer.Ordinal))
        {
            if (definition.Behaviors[tag].Data is SimBannerCarrierUpdateModuleData bannerData)
            {
                return bannerData;
            }
        }
        return null;
    }

    private FixVector2 SlotWorldPosition(in HordeSlot slot, in FixVector2 anchor, Fix64 yaw)
    {
        // Back-up shuffle displaces along local +X (rearward: front ranks sit at low X,
        // the default banner slot at X:40 is "center-rear" - spec §5.1/§5.2 orientation).
        var localX = slot.OffsetX + slot.BackupDistance;
        var localY = slot.OffsetY;
        var cos = FixTrig.Cos(yaw);
        var sin = FixTrig.Sin(yaw);
        return new FixVector2(
            anchor.X + localX * cos - localY * sin,
            anchor.Y + localX * sin + localY * cos);
    }

    private LogicFrameSpan RollBackupDelay()
    {
        var min = (int)_data.BackUpMinDelayTime.Value;
        var max = (int)_data.BackUpMaxDelayTime.Value;
        if (max < min)
        {
            max = min;
        }
        return new LogicFrameSpan((uint)Context.GameLogicRandom.Next(min, max));
    }

    private void SteerMembers(LogicFrame now, SimLocomotorUpdate hordeMover)
    {
        var anchor3 = hordeMover.Physics.Position;
        var anchor = new FixVector2(anchor3.X, anchor3.Y);
        var yaw = hordeMover.Physics.Yaw;
        var leash = _data.MeleeAttackLeashDistance;

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (!slot.Occupant.IsValid)
            {
                continue;
            }
            var member = Context.GameLogic.GetObjectById(slot.Occupant);
            var memberMover = member?.FindBehavior<SimLocomotorUpdate>();
            if (memberMover == null)
            {
                continue;
            }

            var memberPos = memberMover.Physics.Position;

            if (_meleeAttacking)
            {
                // Back-up shuffle (spec §5.4): periodic roll against BackupPercentage;
                // success backs the member up a random distance in pathfind cells.
                // Draw order is slot-index order - deterministic.
                if (!slot.Released && now >= slot.NextBackupFrame)
                {
                    if (Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.One) < _data.BackupPercentage)
                    {
                        var cells = Context.GameLogicRandom.NextFix64(
                            _data.BackUpMinDistanceCells, _data.BackUpMaxDistanceCells);
                        slot.BackupDistance += cells * SimHordeContainModuleData.PathfindCellSize;
                    }
                    slot.NextBackupFrame = now + RollBackupDelay();
                    _slots[i] = slot;
                }

                if (slot.Released)
                {
                    // Chase-released: leashless, the member's own combat drives it. But the
                    // leash still snaps it back when it strays too far from the horde
                    // center (spec §5.3: released ranks are exempt; others re-tether).
                    continue;
                }

                if (leash > Fix64.Zero)
                {
                    var ldx = memberPos.X - anchor.X;
                    var ldy = memberPos.Y - anchor.Y;
                    if (ldx * ldx + ldy * ldy > leash * leash)
                    {
                        var target = SlotWorldPosition(slot, anchor, yaw);
                        memberMover.SetTargetPosition(
                            new FixVector3(target.X, target.Y, memberPos.Z), MemberSpeedSentinel);
                        continue;
                    }
                }

                if (slot.Freed)
                {
                    // Freed from slot targets but not chase-released: no formation order.
                    continue;
                }
            }

            // Formation steering (spec §5.3): target = slot world position; in position ->
            // final facing aligned to the horde facing.
            var slotTarget = SlotWorldPosition(slot, anchor, yaw);
            var dx = slotTarget.X - memberPos.X;
            var dy = slotTarget.Y - memberPos.Y;
            var closeEnough = memberMover.CurrentLocomotor?.CloseEnoughDist ?? Fix64.One;
            if (dx * dx + dy * dy <= closeEnough * closeEnough)
            {
                if (memberMover.Mode == SimMoveMode.MoveToPosition)
                {
                    memberMover.Stop();
                }
                memberMover.SetTargetAngle(yaw);
            }
            else
            {
                memberMover.SetTargetPosition(
                    new FixVector3(slotTarget.X, slotTarget.Y, memberPos.Z), MemberSpeedSentinel);
            }
        }
    }

    private void PruneRecentAttacks(LogicFrame now)
    {
        for (var i = _recentAttacks.Count - 1; i >= 0; i--)
        {
            if (now >= _recentAttacks[i].Expiry)
            {
                _recentAttacks.RemoveAt(i);
            }
        }
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Initialized", ref _initialized);
        xfer.XferList("Slots", _slots, XferSlot);
        var bannerSlot = _bannerSlotIndex;
        xfer.XferInt("BannerSlotIndex", ref bannerSlot);
        _bannerSlotIndex = bannerSlot;
        xfer.XferBool("MeleeAttacking", ref _meleeAttacking);
        xfer.XferFrame("LastMeleeFrame", ref _lastMeleeFrame);
        xfer.XferFrame("FlankedUntil", ref _flankedUntil);
        xfer.XferFrame("NextFlankAllowedFrame", ref _nextFlankAllowedFrame);
        xfer.XferList("RecentAttacks", _recentAttacks, XferAttackRecord);
        xfer.XferFrame("BannerDiedFrame", ref _bannerDiedFrame);
        xfer.XferBool("BannerRespawnPending", ref _bannerRespawnPending);
    }

    private static void XferSlot(IXfer xfer, ref HordeSlot slot)
    {
        xfer.XferInt("RankInfoIndex", ref slot.RankInfoIndex);
        xfer.XferInt("RankNumber", ref slot.RankNumber);
        xfer.XferFix64("OffsetX", ref slot.OffsetX);
        xfer.XferFix64("OffsetY", ref slot.OffsetY);
        xfer.XferInt("LeaderSlot", ref slot.LeaderSlot);
        xfer.XferObjectId("Occupant", ref slot.Occupant);
        xfer.XferBool("Released", ref slot.Released);
        xfer.XferBool("Freed", ref slot.Freed);
        xfer.XferFrame("NextBackupFrame", ref slot.NextBackupFrame);
        xfer.XferFix64("BackupDistance", ref slot.BackupDistance);
    }

    private static void XferAttackRecord(IXfer xfer, ref AttackRecord record)
    {
        xfer.XferFix64("Angle", ref record.Angle, Tolerance.Band);
        xfer.XferFrame("Expiry", ref record.Expiry);
    }
}
