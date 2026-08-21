// Mocked-game unit tests for the FlightDeckBehavior port (R12), one test per packet
// testCase: parked-aircraft healing, the takeoff ramp/catapult/launch-wave sequence, landing
// runway reservation plus the ApproachHeight-derived descent target, R1/R2 space
// interleaving, and the replacement-payload spawn timer.
//
// The headless host builds no Drawable model, so every RunwayNSpaces/Takeoff/Landing bone
// lookup misses and falls back to the carrier's own transform (see
// FlightDeckBehavior.ResolveBonePoseAndTransform) - the same accommodation
// RailedTransportDockUpdateContractTests documents for its own bone lookups. That makes every
// space/runway share one position in these tests; the state machine (reservation semantics,
// heal ticks, launch timing, particle-fire attempts, replacement timing) is still exercised
// exactly as the module drives it.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class FlightDeckBehaviorContractTests
{
    // Bfme2 runs at 5 Hz (200ms/logic-frame, SimLoop.MsPerLogicFrame). Every Duration/Period
    // field below is parsed with IniParser.ParseTimeMillisecondsToLogicFrames() (GPL's
    // INI::parseDurationUnsignedInt -> ConvertDurationFromMsecsToFrames), which ceil-converts
    // the INI's millisecond value to a logic-frame count. The values below are deliberately
    // NOT round multiples of 200 so the tests also exercise the ceiling: e.g.
    // LaunchRampDelay = 350ms -> 350/200 = 1.75 -> ceil -> 2 frames.
    private const string Definitions = @"
Object TestJet
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestCarrier
  KindOf = VEHICLE STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = FlightDeckBehavior ModuleTag_Deck
    NumRunways = 2
    NumSpacesPerRunway = 2
    Runway1Spaces = R1S1 R1S2
    Runway2Spaces = R2S1 R2S2
    Runway1Takeoff = R1START R1END
    Runway2Takeoff = R2START R2END
    Runway1Landing = R1LSTART R1LEND
    Runway2Landing = R2LSTART R2LEND
    HealAmountPerSecond = 50
    ApproachHeight = 40.5
    LandingDeckHeightOffset = 5
    PayloadTemplate = TestJet
    ReplacementDelay = 750
    DockAnimationDelay = 500
    LaunchWaveDelay = 750
    LaunchRampDelay = 350
    LowerRampDelay = 350
    CatapultFireDelay = 150
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xDEC0)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    /// <summary>
    /// Steps the carrier's deck module to its first real Update(). A module constructed at
    /// frame 0 with SetWakeFrame(UpdateSleepTime.None) does not tick on the first Step():
    /// GameLogic.Update() reads CurrentFrame before incrementing it, so the module's first
    /// tick lands on the SECOND Step() (the frame-accounting convention the other R12
    /// contract tests document). buildInfo's initial payload spawn happens on that tick.
    /// </summary>
    private static void Prime(HeadlessSimGame game)
    {
        game.Step();
        game.Step();
    }

    private static FlightDeckBehavior DeckOf(GameObject carrier) =>
        carrier.BehaviorModules.OfType<FlightDeckBehavior>().Single();

    private static void Damage(GameObject target, float amount)
    {
        target.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Explosion,
            DeathType = DeathType.Normal,
            Amount = amount,
        });
    }

    // ---- testCase 1: parked aircraft heals at HealAmountPerSecond until full -------------

    [Fact]
    public void ParkedAircraft_HealsAtConfiguredRate_UntilFull()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);

        // Arming tick (SetWakeFrame(None)'s 1-frame minimum latency): buildInfo's initial
        // payload spawn happens on the module's first real Update().
        Prime(game);

        var jetId = deck.Spaces[0].ObjectInSpace;
        Assert.True(jetId.IsValid);
        var jet = game.GameLogic.GetObjectById(jetId);

        Damage(jet, 60f);
        Assert.Equal(40f, jet.BodyModule.Health);

        deck.ReportParkedIdle(jetId);

        // HealAmountPerSecond=50 / 5 heal-ticks-per-second = 10 hp per tick; heal ticks fire
        // every LogicFramesPerSecond/5 frames (5 Hz logic -> every frame here).
        for (var i = 0; i < 20 && jet.BodyModule.Health < 100f; i++)
        {
            game.Step();
        }

        Assert.Equal(100f, jet.BodyModule.Health);
    }

    [Fact]
    public void ReportNoLongerParked_StopsHealing()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jetId = deck.Spaces[0].ObjectInSpace;
        var jet = game.GameLogic.GetObjectById(jetId);
        Damage(jet, 60f);

        deck.ReportParkedIdle(jetId);
        deck.ReportNoLongerParked(jetId);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.Equal(40f, jet.BodyModule.Health);
        Assert.Empty(deck.Healing);
    }

    // ---- testCase 2: takeoff reserves the runway, plays the ramp/catapult sequence --------

    [Fact]
    public void Takeoff_ReservesRunway_RaisesRamp_FiresCatapult_ThenLowersRamp()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jetId = deck.Spaces[0].ObjectInSpace; // front space, runway 0

        Assert.True(deck.ReserveRunway(jetId, forLanding: false));
        Assert.Equal(jetId, deck.GetRunwayReservation(0, forLanding: false));

        deck.RequestTakeoff(jetId);

        game.Step();
        Assert.True(carrier.ModelConditionFlags.Get(ModelConditionFlag.Door2Opening));

        // LaunchRampDelay=350ms -> ceil(350/200)=2 frames until launch; CatapultFireDelay=150ms
        // -> ceil(150/200)=1 more frame until the particle-fire attempt; LowerRampDelay=350ms
        // -> ceil(350/200)=2 more frames until the ramp comes back down.
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.True(deck.CatapultFireCount > 0);
        Assert.False(carrier.ModelConditionFlags.Get(ModelConditionFlag.Door2Opening));
        Assert.True(carrier.ModelConditionFlags.Get(ModelConditionFlag.Door2Closing));
    }

    [Fact]
    public void Takeoff_SecondRunwayUsesItsOwnDoorPair()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jetId = deck.Spaces[1].ObjectInSpace; // front space, runway 1
        Assert.True(deck.ReserveRunway(jetId, forLanding: false));
        deck.RequestTakeoff(jetId);

        game.Step();
        Assert.True(carrier.ModelConditionFlags.Get(ModelConditionFlag.Door3Opening));
        Assert.False(carrier.ModelConditionFlags.Get(ModelConditionFlag.Door2Opening));
    }

    // ---- testCase 3: landing reserves a runway; the approach target uses ApproachHeight ---

    [Fact]
    public void Landing_ReservesRunway_AndApproachTargetUsesApproachHeightAndDeckOffset()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jetId = deck.Spaces[0].ObjectInSpace;

        Assert.True(deck.ReserveRunway(jetId, forLanding: true));
        Assert.Equal(jetId, deck.GetRunwayReservation(0, forLanding: true));

        var info = deck.CalcPPInfoFor(jetId);
        Assert.NotNull(info);
        // ApproachHeight=40.5 (GPL's m_approachHeight is Real, not Int - a fractional INI
        // value here exercises that) + LandingDeckHeightOffset=5, on top of the
        // (headless-fallback) landing-start bone's own Z of 0.
        Assert.Equal(45.5f, info.Value.RunwayApproach.Z, 3);

        deck.ReleaseRunway(jetId);
        Assert.Equal(ObjectId.Invalid, deck.GetRunwayReservation(0, forLanding: true));
    }

    [Fact]
    public void ReserveRunway_DoesNotDoubleBookAnAlreadyReservedRunway()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jet0 = deck.Spaces[0].ObjectInSpace; // runway 0
        var jet2 = deck.Spaces[2].ObjectInSpace; // runway 0 (R1S2)

        Assert.True(deck.ReserveRunway(jet0, forLanding: true));
        Assert.False(deck.ReserveRunway(jet2, forLanding: true));
        Assert.Equal(jet0, deck.GetRunwayReservation(0, forLanding: true));
    }

    // ---- testCase 4: parking spaces come from Runway1/2Spaces, interleaved across runways -

    [Fact]
    public void ParkingSpaces_InterleaveAcrossRunways_InDeclarationOrder()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        // R1S1, R2S1, R1S2, R2S2 - GPL's own "sort the spaces" comment in buildInfo().
        Assert.Equal(4, deck.Spaces.Count);
        Assert.Equal(0, deck.Spaces[0].Runway);
        Assert.Equal(1, deck.Spaces[1].Runway);
        Assert.Equal(0, deck.Spaces[2].Runway);
        Assert.Equal(1, deck.Spaces[3].Runway);

        var occupants = deck.Spaces.Select(s => s.ObjectInSpace).ToArray();
        Assert.All(occupants, id => Assert.True(id.IsValid));
        Assert.Equal(4, occupants.Distinct().Count());
    }

    // ---- testCase 5: replacement payload spawns ReplacementDelay+DockAnimationDelay frames
    // after a space clears ---------------------------------------------------------------

    [Fact]
    public void ReplacementPayload_SpawnsAfterReplacementAndDockDelay_WhenASpaceClears()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var jetId = deck.Spaces[0].ObjectInSpace;
        var jet = game.GameLogic.GetObjectById(jetId);
        jet.Kill();

        // Destroyed-but-not-yet-reaped this frame, then purged and noticed empty the next -
        // that second frame is the one that arms the ReplacementDelay+DockAnimationDelay timer:
        // ceil(750/200) + ceil(500/200) = 4 + 3 = 7 frames.
        game.Step();
        game.Step();
        Assert.True(deck.Spaces[0].ObjectInSpace.IsInvalid);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.True(deck.Spaces[0].ObjectInSpace.IsInvalid); // still within the 7-frame window

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.True(deck.Spaces[0].ObjectInSpace.IsValid);
        Assert.NotEqual(jetId, deck.Spaces[0].ObjectInSpace);
    }

    [Fact]
    public void KillAllParkedUnits_KillsGroundedAircraft_SparesAirborneOnes()
    {
        var game = NewGame();
        var carrier = game.SpawnObject("TestCarrier", game.CivilianPlayer, Vector3.Zero);
        var deck = DeckOf(carrier);
        Prime(game);

        var groundedId = deck.Spaces[0].ObjectInSpace;
        var airborneId = deck.Spaces[1].ObjectInSpace;
        var airborne = game.GameLogic.GetObjectById(airborneId);
        airborne.UpdateTransform(new Vector3(0, 0, 500));

        deck.KillAllParkedUnits();

        var grounded = game.GameLogic.GetObjectById(groundedId);
        Assert.True(grounded == null || grounded.IsEffectivelyDead);
        Assert.False(airborne.IsEffectivelyDead);
    }
}
