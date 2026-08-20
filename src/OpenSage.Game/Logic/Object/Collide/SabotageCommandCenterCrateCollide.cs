// SabotageCommandCenterCrateCollide - R12 port.
//
// Behavioral reference: generals-gpl GeneralsMD SabotageCommandCenterCrateCollide.cpp/.h and
// its base, CrateCollide.cpp/.h (GPL semantics reference only; this is fresh code against the
// frozen contract). Behavior facts used:
//   - isValidToExecute(other) extends the base CrateCollide gate with three checks of its
//     own: other must not be effectively dead, must be KINDOF_COMMANDCENTER, and must be an
//     ENEMIES relationship to the saboteur. All three are ANDed; any failure rejects.
//   - executeCrateBehavior(other): first re-checks that `other` is still the AI unit's goal
//     object (ai->getGoalObject() != other fails the call) - this exists specifically to
//     stop an unintentional trigger from the saboteur merely walking near the building
//     without the game actually having sent it there. On success: raise a radar
//     infiltration event, play the sabotage feedback FX/audio, play the local-victim EVA
//     notification, then walk every behavior module on the target and call
//     startPowerRecharge() on each one that exposes a SpecialPowerModuleInterface - i.e.
//     every special power on the building is reset to full cooldown, unconditionally.
//   - the class carries no fields of its own and no additional Xfer state beyond the base
//     walk (its crc()/xfer()/loadPostProcess() all just extend the base).
//
// Deviations from the reference, deliberate and recorded:
//   - The base CrateCollide::onCollide dispatch loop (isValidToExecute -> executeCrateBehavior
//     -> destroy-crate-on-success -> world animation) is not ported in OpenSAGE yet: the whole
//     Collide category (CollideModule.OnCollide, CrateCollide) is still the pre-existing
//     "TODO: Make this abstract" stub with no collision-trigger call site (see
//     Logic/Object/Collide/CollideModule.cs and every sibling CrateCollide in this folder -
//     none of them implement OnCollide). Reaching in to build that shared dispatcher is out of
//     scope for a single-module port, so this class exposes its two real decision points as
//     public methods - IsValidToExecute and ExecuteCrateBehavior - exactly like the GPL split,
//     ready for that dispatcher to call once it exists.
//   - the AI goal-object gate (ai->getGoalObject() != other) has no OpenSAGE analogue yet:
//     AIUpdate does not expose a "current goal object" accessor anywhere in this codebase.
//     Rather than invent one on the shared AIUpdate class (out of scope, and not in
//     reservedNames for this port), ExecuteCrateBehavior takes the AI's goal object as an
//     explicit parameter. A null goal object fails the gate exactly like GPL's own
//     `ai && ai->getGoalObject() != other` check does when there is no AI at all.
//   - the ENEMIES relationship check: OpenSAGE's team/player relationship tables
//     (Player/Team GetRelationship) are populated only from saved-game data today, so a live
//     skirmish object usually reads NEUTRAL against every other side through that path alone
//     - the same gap CreateCrateDie.KillerIsAlliedWithVictim documents and works around. This
//     class applies the identical workaround: GameObject.GetRelationship is consulted first,
//     and the player-level Enemies set is consulted as a fallback.
//   - TheRadar->tryInfiltrationEvent and TheEva->setShouldPlay(EVA_BuildingSabotaged) are not
//     portable today: Radar.AddRadarEvent exists but nothing in OpenSAGE computes the map-tile
//     coordinates it requires (zero existing call sites to model), and there is no runtime EVA
//     event dispatcher anywhere in the codebase (EvaEvent/ScoredKillEvaAnnouncer/
//     EvaEventFXNugget are all parse-only data with no player). Inventing either would be
//     guessing at behavior with no reference to check it against, so both are left as TODO-spec
//     here rather than faked. What GPL's doSabotageFeedbackFX plays IS portable - it is a plain
//     MiscAudio sound event, the same AudioSystem.PlayAudioEvent call SpecialPowerModule.
//     Activate already uses for InitiateSound - so that part is real.
//   - the deterministic core - resetting every SpecialPowerModule's recharge timer via
//     SpecialPowerModule.ResetCountdown() (the OpenSAGE analogue of startPowerRecharge()) - is
//     fully ported and is what test coverage for this module exercises.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// Hardcoded to play the SabotageBuilding sound definition when triggered.
/// </summary>
public sealed class SabotageCommandCenterCrateCollide : CrateCollide
{
    internal SabotageCommandCenterCrateCollide(GameObject gameObject, IGameEngine gameEngine)
        : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// GPL SabotageCommandCenterCrateCollide::isValidToExecute. The base CrateCollide gate
    /// (kindof mask, neutral-controlled, above-terrain, etc.) is not ported yet (see the file
    /// header), so this is this class's own extension only: not dead, a command center, and
    /// an enemy of the saboteur.
    /// </summary>
    public bool IsValidToExecute(GameObject other)
    {
        if (other == null)
        {
            return false;
        }

        if (other.IsEffectivelyDead)
        {
            // Can't sabotage dead structures.
            return false;
        }

        if (!other.IsKindOf(ObjectKinds.CommandCenter))
        {
            // We can only sabotage command center structures.
            return false;
        }

        // Can only sabotage enemy buildings.
        return IsEnemy(other);
    }

    /// <summary>
    /// See the file header's ENEMIES-check deviation note.
    /// </summary>
    private bool IsEnemy(GameObject other)
    {
        if (GameObject.GetRelationship(other) == RelationshipType.Enemies)
        {
            return true;
        }

        var myOwner = GameObject.Owner;
        var otherOwner = other.Owner;
        if (myOwner is null || otherOwner is null || myOwner == otherOwner)
        {
            return false;
        }

        return myOwner.Enemies.Contains(otherOwner);
    }

    /// <summary>
    /// GPL SabotageCommandCenterCrateCollide::executeCrateBehavior. Returns false (crate not
    /// consumed, nothing mutated) when the goal-object gate fails; otherwise resets every
    /// special power on <paramref name="other"/> and returns true.
    /// </summary>
    /// <param name="other">The command center being sabotaged. Caller must already have
    /// confirmed <see cref="IsValidToExecute"/>.</param>
    /// <param name="aiGoalObject">The saboteur's AI current goal object, or null if it has no
    /// AI or no current goal. See the file header's goal-object deviation note.</param>
    public bool ExecuteCrateBehavior(GameObject other, GameObject aiGoalObject)
    {
        // Check to make sure that the other object is also the goal object in the AI, in
        // order to prevent an unintentional trigger simply by having the saboteur walk too
        // close to it.
        if (aiGoalObject != other)
        {
            return false;
        }

        // GPL: doSabotageFeedbackFX(other, SAB_VICTIM_COMMAND_CENTER) -> MiscAudio's
        // SabotageResetTimeBuilding sound, positioned on the victim. The radar infiltration
        // event and the EVA_BuildingSabotaged notification are not portable yet - see the
        // file header.
        var sabotageSound = GameEngine.AssetStore.MiscAudio.Current?.SabotageResetTimeBuilding;
        if (!string.IsNullOrEmpty(sabotageSound))
        {
            // Null in a mocked/headless host with no audio backend (MockedGameTest,
            // HeadlessSimGame); a real Game always supplies one.
            GameEngine.AudioSystem?.PlayAudioEvent(sabotageSound);
        }

        // Reset ALL special powers!
        foreach (var specialPowerModule in other.FindBehaviors<SpecialPowerModule>())
        {
            specialPowerModule.ResetCountdown();
        }

        return true;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class SabotageCommandCenterCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageCommandCenterCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageCommandCenterCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageCommandCenterCrateCollideModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SabotageCommandCenterCrateCollide(gameObject, gameEngine);
    }
}
