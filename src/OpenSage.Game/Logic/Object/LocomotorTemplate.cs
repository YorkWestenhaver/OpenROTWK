using System;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

public sealed class LocomotorTemplate : BaseAsset
{
    private static NLog.Logger Logger => NLog.LogManager.GetCurrentClassLogger();

    internal static LocomotorTemplate Parse(IniParser parser)
    {
        var result = parser.ParseNamedBlock(
             (x, name) => x.SetNameAndInstanceId("LocomotorTemplate", name),
             FieldParseTable);

        result.Validate(parser.SageGame);

        return result;
    }

    private static readonly IniParseTable<LocomotorTemplate> FieldParseTable = new IniParseTable<LocomotorTemplate>
    {
        { "Surfaces", (parser, x) => x.Surfaces = parser.ParseEnumFlags<Surfaces>() },
        // Sim-consumed movement numerics are quantized ONCE at the blessed F4 text boundary
        // (S2 locomotor system); the legacy float mirrors are derived from the quantized
        // value via the display escape so the unmigrated float paths stay consistent with
        // the sim (delta <= 2^-32, invisible to the float pipeline).
        { "Speed", (parser, x) => { x.SimMaxSpeed = parser.ParseFix64VelocityPerLogicFrame(); x.MaxSpeed = x.SimMaxSpeed.ToFloatForDisplay(); } },
        { "SpeedDamaged", (parser, x) => { x.SimMaxSpeedDamaged = parser.ParseFix64VelocityPerLogicFrame(); x.MaxSpeedDamaged = x.SimMaxSpeedDamaged.ToFloatForDisplay(); } },
        { "MinSpeed", (parser, x) => { x.SimMinSpeed = parser.ParseFix64VelocityPerLogicFrame(); x.MinSpeed = x.SimMinSpeed.ToFloatForDisplay(); } },
        { "TurnRate", (parser, x) => { x.SimMaxTurnRate = parser.ParseFix64AngularVelocityPerLogicFrame(); x.MaxTurnRate = x.SimMaxTurnRate.ToFloatForDisplay(); } },
        { "TurnRateDamaged", (parser, x) => { x.SimMaxTurnRateDamaged = parser.ParseFix64AngularVelocityPerLogicFrame(); x.MaxTurnRateDamaged = x.SimMaxTurnRateDamaged.ToFloatForDisplay(); } },
        { "Acceleration", (parser, x) => { x.SimAcceleration = parser.ParseFix64AccelerationPerLogicFrame(); x.Acceleration = x.SimAcceleration.ToFloatForDisplay(); } },
        { "AccelerationDamaged", (parser, x) => { x.SimAccelerationDamaged = parser.ParseFix64AccelerationPerLogicFrame(); x.AccelerationDamaged = x.SimAccelerationDamaged.ToFloatForDisplay(); } },
        { "Lift", (parser, x) => { x.SimLift = parser.ParseFix64AccelerationPerLogicFrame(); x.Lift = x.SimLift.ToFloatForDisplay(); } },
        { "LiftDamaged", (parser, x) => { x.SimLiftDamaged = parser.ParseFix64AccelerationPerLogicFrame(); x.LiftDamaged = x.SimLiftDamaged.ToFloatForDisplay(); } },
        { "Braking", (parser, x) => { x.SimBraking = parser.ParseFix64AccelerationPerLogicFrame(); x.Braking = x.SimBraking.ToFloatForDisplay(); } },
        { "MinTurnSpeed", (parser, x) => { x.SimMinTurnSpeed = parser.ParseFix64VelocityPerLogicFrame(); x.MinTurnSpeed = x.SimMinTurnSpeed.ToFloatForDisplay(); } },
        { "TurnPivotOffset", (parser, x) => { x.SimTurnPivotOffset = parser.ParseFix64(); x.TurnPivotOffset = x.SimTurnPivotOffset.ToFloatForDisplay(); } },
        { "AllowAirborneMotiveForce", (parser, x) => x.AllowAirborneMotiveForce = parser.ParseBoolean() },
        { "PreferredHeight", (parser, x) => { x.SimPreferredHeight = parser.ParseFix64(); x.PreferredHeight = x.SimPreferredHeight.ToFloatForDisplay(); } },
        { "PreferredHeightDamping", (parser, x) => { x.SimPreferredHeightDamping = parser.ParseFix64(); x.PreferredHeightDamping = x.SimPreferredHeightDamping.ToFloatForDisplay(); } },
        { "SpeedLimitZ", (parser, x) => { x.SimSpeedLimitZ = parser.ParseFix64VelocityPerLogicFrame(); x.SpeedLimitZ = x.SimSpeedLimitZ.ToFloatForDisplay(); } },
        { "ZAxisBehavior", (parser, x) => x.BehaviorZ = parser.ParseEnum<LocomotorBehaviorZ>() },
        { "Appearance", (parser, x) => x.Appearance = parser.ParseEnum<LocomotorAppearance>() },
        { "StickToGround", (parser, x) => x.StickToGround = parser.ParseBoolean() },
        { "GroupMovementPriority", (parser, x) => x.MovementPriority = parser.ParseEnum<LocomotorPriority>() },
        { "DownhillOnly", (parser, x) => x.DownhillOnly = parser.ParseBoolean() },

        { "MaxThrustAngle", (parser, x) => x.MaxThrustAngle = parser.ParseAngle() },
        { "ThrustRoll", (parser, x) => x.ThrustRoll = parser.ParseFloat() },
        { "ThrustWobbleRate", (parser, x) => x.ThrustWobbleRate = parser.ParseFloat() },
        { "ThrustMinWobble", (parser, x) => x.ThrustMinWobble = parser.ParseFloat() },
        { "ThrustMaxWobble", (parser, x) => x.ThrustMaxWobble = parser.ParseFloat() },
        { "CloseEnoughDist", (parser, x) => { x.SimCloseEnoughDist = parser.ParseFix64(); x.CloseEnoughDist = x.SimCloseEnoughDist.ToFloatForDisplay(); } },
        { "CloseEnoughDist3D", (parser, x) => x.CloseEnoughDist3D = parser.ParseBoolean() },

        { "WanderWidthFactor", (parser, x) => { x.SimWanderWidthFactor = parser.ParseFix64(); x.WanderWidthFactor = x.SimWanderWidthFactor.ToFloatForDisplay(); } },
        { "WanderLengthFactor", (parser, x) => { x.SimWanderLengthFactor = parser.ParseFix64(); x.WanderLengthFactor = x.SimWanderLengthFactor.ToFloatForDisplay(); } },
        { "WanderAboutPointRadius", (parser, x) => x.WanderAboutPointRadius = parser.ParseFloat() },

        { "AccelerationPitchLimit", (parser, x) => x.AccelerationPitchLimit = parser.ParseAngle() },
        { "DecelerationPitchLimit", (parser, x) => x.DecelerationPitchLimit = parser.ParseAngle() },
        { "BounceAmount", (parser, x) => x.BounceAmount = parser.ParseAngularVelocityToLogicFrames() },
        { "PitchInDirectionOfZVelFactor", (parser, x) => x.PitchInDirectionOfZVelFactor = parser.ParseFloat() },
        { "PitchStiffness", (parser, x) => x.PitchStiffness = parser.ParseFloat() },
        { "RollStiffness", (parser, x) => x.RollStiffness = parser.ParseFloat() },
        { "PitchDamping", (parser, x) => x.PitchDamping = parser.ParseFloat() },
        { "RollDamping", (parser, x) => x.RollDamping = parser.ParseFloat() },
        { "UniformAxialDamping", (parser, x) => x.UniformAxialDamping = parser.ParseFloat() },

        { "ForwardAccelerationPitchFactor", (parser, x) => x.ForwardAccelerationPitchFactor = parser.ParseFloat() },
        { "LateralAccelerationRollFactor", (parser, x) => x.LateralAccelerationRollFactor = parser.ParseFloat() },
        { "ForwardVelocityPitchFactor", (parser, x) => x.ForwardVelocityPitchFactor = parser.ParseFloat() },
        { "LateralVelocityRollFactor", (parser, x) => x.LateralVelocityRollFactor = parser.ParseFloat() },

        { "Apply2DFrictionWhenAirborne", (parser, x) => x.Apply2DFrictionWhenAirborne = parser.ParseBoolean() },
        { "Extra2DFriction", (parser, x) => { x.SimExtra2DFriction = parser.ParseFix64FrictionPerLogicFrame(); x.Extra2DFriction = x.SimExtra2DFriction.ToFloatForDisplay(); } },
        { "AirborneTargetingHeight", (parser, x) => x.AirborneTargetingHeight = parser.ParseInteger() },
        { "LocomotorWorksWhenDead", (parser, x) => x.LocomotorWorksWhenDead = parser.ParseBoolean() },
        { "CirclingRadius", (parser, x) => x.CirclingRadius = parser.ParseFloat() },

        { "HasSuspension", (parser, x) => x.HasSuspension = parser.ParseBoolean() },
        { "CanMoveBackwards", (parser, x) => x.CanMoveBackwards = parser.ParseBoolean() },
        { "MaximumWheelExtension", (parser, x) => x.MaximumWheelExtension = parser.ParseFloat() },
        { "MaximumWheelCompression", (parser, x) => x.MaximumWheelCompression = parser.ParseFloat() },
        { "FrontWheelTurnAngle", (parser, x) => x.FrontWheelTurnAngle = parser.ParseAngle() },

        { "SlideIntoPlaceTime", (parser, x) => { x.SimUltraAccurateSlideIntoPlaceFactor = SlideTimeMsToFrames(parser.ParseInteger(), parser.SageGame); x.SlideIntoPlaceTime = x.SimUltraAccurateSlideIntoPlaceFactor.ToFloatForDisplay(); } },

        { "RudderCorrectionDegree", (parser, x) => x.RudderCorrectionDegree = parser.ParseFloat() },
        { "RudderCorrectionRate", (parser, x) => x.RudderCorrectionRate = parser.ParseFloat() },
        { "ElevatorCorrectionDegree", (parser, x) => x.ElevatorCorrectionDegree = parser.ParseFloat() },
        { "ElevatorCorrectionRate", (parser, x) => x.ElevatorCorrectionRate = parser.ParseFloat() },
        //TODO: check if this conversion formula is correct
        //Converts from time for a 360 turn into degrees per second
        { "TurnTime", (parser, x) => { x.SimMaxTurnRate = TurnTimeMsToRadiansPerFrame(parser.ParseInteger(), parser.SageGame); x.MaxTurnRate = x.SimMaxTurnRate.ToFloatForDisplay(); } },
        { "TurnTimeDamaged", (parser, x) => { x.SimMaxTurnRateDamaged = TurnTimeMsToRadiansPerFrame(parser.ParseInteger(), parser.SageGame); x.MaxTurnRateDamaged = x.SimMaxTurnRateDamaged.ToFloatForDisplay(); } },
        { "SlowTurnRadius", (parser, x) => x.SlowTurnRadius = parser.ParseFloat() },
        { "FastTurnRadius", (parser, x) => x.FastTurnRadius = parser.ParseFloat() },
        { "NonDirtyTransform", (parser, x) => x.NonDirtyTransform = parser.ParseBoolean() },
        { "PreferredAttackHeight", (parser, x) => x.PreferredAttackHeight = parser.ParseInteger() },
        { "AeleronCorrectionDegree", (parser, x) => x.AeleronCorrectionDegree = parser.ParseFloat() },
        { "AeleronCorrectionRate", (parser, x) => x.AeleronCorrectionRate = parser.ParseFloat() },
        { "SwoopStandoffRadius", (parser, x) => x.SwoopStandoffRadius = parser.ParseFloat() },
        { "SwoopStandoffHeight", (parser, x) => x.SwoopStandoffHeight = parser.ParseFloat() },
        { "SwoopTerminalVelocity", (parser, x) => x.SwoopTerminalVelocity = parser.ParseFloat() },
        { "SwoopAccelerationRate", (parser, x) => x.SwoopAccelerationRate = parser.ParseFloat() },
        { "SwoopSpeedTuningFactor", (parser, x) => x.SwoopSpeedTuningFactor = parser.ParseFloat() },
        { "FormationPriority", (parser, x) => x.FormationPriority = parser.ParseEnum<FormationPriority>() },
        { "BackingUpSpeed", (parser, x) => x.BackingUpSpeed = parser.ParsePercentage() },
        { "ChargeAvailable", (parser, x) => x.ChargeAvailable = parser.ParseBoolean() },
        { "ChargeSpeed", (parser, x) => x.ChargeSpeed = parser.ParsePercentage() },
        { "TurnThreshold", (parser, x) => x.TurnThreshold = parser.ParseFloat() },
        { "TurnThresholdHS", (parser, x) => x.TurnThresholdHS = parser.ParseInteger() },
        { "EnableHighSpeedTurnModelconditions", (parser, x) => x.EnableHighSpeedTurnModelconditions = parser.ParseBoolean() },
        { "UseTerrainSmoothing", (parser, x) => x.UseTerrainSmoothing = parser.ParseBoolean() },
        { "WaitForFormation", (parser, x) => x.WaitForFormation = parser.ParseBoolean() },
        { "MaxTurnWithoutReform", (parser, x) => x.MaxTurnWithoutReform = parser.ParseInteger() },
        { "AccDecTrigger", (parser, x) => x.AccDecTrigger = parser.ParseFloat() },
        { "WalkDistance", (parser, x) => x.WalkDistance = parser.ParseInteger() },
        { "MaxOverlappedHeight", (parser, x) => x.MaxOverlappedHeight = parser.ParseInteger() },
        { "CrewPowered", (parser, x) => x.CrewPowered = parser.ParseBoolean() },
        { "BackingUpStopWhenTurning", (parser, x) => x.BackingUpStopWhenTurning = parser.ParseBoolean() },
        { "BackingUpDistanceMin", (parser, x) => x.BackingUpDistanceMin = parser.ParseInteger() },
        { "BackingUpDistanceMax", (parser, x) => x.BackingUpDistanceMax = parser.ParseInteger() },
        { "BackingUpAngle", (parser, x) => x.BackingUpAngle = parser.ParseFloat() },
        { "LookAheadMult", (parser, x) => x.LookAheadMult = parser.ParseFloat() },
        { "ScalesWalls", (parser, x) => x.ScalesWalls = parser.ParseBoolean() },
        { "ChargeIgnoresCondition", (parser, x) => x.ChargeIgnoresCondition = parser.ParseBoolean() },
        { "BurningDeathRadius", (parser, x) => x.BurningDeathRadius = parser.ParseInteger() },
        { "BurningDeathIsCavalry", (parser, x) => x.BurningDeathIsCavalry = parser.ParseBoolean() },
        { "TurnWhileMoving", (parser, x) => x.TurnWhileMoving = parser.ParseBoolean() },
        { "RiverModifier", (parser, x) => x.RiverModifier = parser.ParsePercentage() }
    };

    internal const float BigNumber = 99999.0f;

    // ========================================================================
    // Quantized sim-side vocabulary (S2 locomotor system). These are THE values the
    // deterministic movement code reads; each is quantized once at the F4 text boundary
    // in the parse table above, in per-logic-frame units. The float twins above them are
    // legacy mirrors for the unmigrated float paths and are derived FROM these.
    // Sentinels follow GPL LocomotorTemplate::LocomotorTemplate(): -1 = "same as
    // undamaged" (resolved in Validate), BIGNUM = 99999 (fits plain per F3 R1).
    // ========================================================================

    internal static readonly Fix64 SimBigNumber = Fix64.FromDecimalLiteral("99999");
    private static readonly Fix64 SimMinusOne = Fix64.FromDecimalLiteral("-1");
    private static readonly Fix64 SimSmallSpeed = Fix64.FromDecimalLiteral("0.01");

    /// <summary>360-turn milliseconds -&gt; Fix64 radians/frame: 2000*Pi/(fps*ms), exact in Int128.</summary>
    private static Fix64 TurnTimeMsToRadiansPerFrame(int milliseconds, SageGame sageGame)
    {
        if (milliseconds <= 0)
        {
            return Fix64.Zero;
        }
        var fps = sageGame.LogicFramesPerSecond();
        var numerator = (Int128)2000 * Fix64.Pi.RawValue;
        var denominator = (Int128)fps * milliseconds;
        var half = denominator / 2;
        return Fix64.FromRaw((long)((numerator + half) / denominator));
    }

    public Fix64 SimMaxSpeed { get; internal set; }
    public Fix64 SimMaxSpeedDamaged { get; internal set; } = SimMinusOne;
    public Fix64 SimMinSpeed { get; internal set; }
    public Fix64 SimMaxTurnRate { get; internal set; }
    public Fix64 SimMaxTurnRateDamaged { get; internal set; } = SimMinusOne;
    public Fix64 SimAcceleration { get; internal set; }
    public Fix64 SimAccelerationDamaged { get; internal set; } = SimMinusOne;
    public Fix64 SimLift { get; internal set; }
    public Fix64 SimLiftDamaged { get; internal set; } = SimMinusOne;
    public Fix64 SimBraking { get; internal set; } = SimBigNumber;
    public Fix64 SimMinTurnSpeed { get; internal set; } = SimBigNumber;
    public Fix64 SimTurnPivotOffset { get; internal set; }
    public Fix64 SimPreferredHeight { get; internal set; }
    public Fix64 SimPreferredHeightDamping { get; internal set; } = Fix64.One;
    public Fix64 SimSpeedLimitZ { get; internal set; } = Fix64.FromDecimalLiteral("999999");
    public Fix64 SimExtra2DFriction { get; internal set; }
    public Fix64 SimCloseEnoughDist { get; internal set; } = Fix64.One;
    public Fix64 SimWanderWidthFactor { get; internal set; }
    public Fix64 SimWanderLengthFactor { get; internal set; } = Fix64.One;
    public Fix64 SimUltraAccurateSlideIntoPlaceFactor { get; internal set; }

    /// <summary>Milliseconds -&gt; fractional logic frames as Fix64: ms*fps/1000, round-half-up.</summary>
    private static Fix64 SlideTimeMsToFrames(int milliseconds, SageGame sageGame)
    {
        if (milliseconds <= 0)
        {
            return Fix64.Zero;
        }
        var fps = sageGame.LogicFramesPerSecond();
        var numerator = (Int128)milliseconds * fps << 32;
        var rounded = (numerator + 500) / 1000;
        return Fix64.FromRaw((long)rounded);
    }

    // Default damaged values of -1.0f mean "make the same as undamaged if not explicitly specified".

    /// <summary>
    /// Flags indicating the kinds of surfaces we can use.
    /// </summary>
    public Surfaces Surfaces { get; private set; } = Surfaces.None;

    /// <summary>
    /// Max speed.
    /// </summary>
    public float MaxSpeed { get; private set; } = 0.0f;

    /// <summary>
    /// Max speed when "damaged".
    /// </summary>
    public float MaxSpeedDamaged { get; private set; } = -1.0f;

    /// <summary>
    /// We should never brake past this.
    /// </summary>
    public float MinSpeed { get; private set; } = 0.0f;

    /// <summary>
    /// Max rate at which we can turn, in radians per logic frame.
    /// </summary>
    public float MaxTurnRate { get; private set; } = 0.0f;

    /// <summary>
    /// Max turn rate when "damaged", in radians per logic frame.
    /// </summary>
    public float MaxTurnRateDamaged { get; private set; } = -1.0f;

    /// <summary>
    /// Max acceleration.
    /// </summary>
    public float Acceleration { get; private set; } = 0.0f;

    /// <summary>
    /// Max acceleration when damaged.
    /// </summary>
    public float AccelerationDamaged { get; private set; } = -1.0f;

    /// <summary>
    /// Max lifting acceleration (flying objects only).
    /// </summary>
    public float Lift { get; private set; } = 0.0f;

    /// <summary>
    /// Max lift when damaged.
    /// </summary>
    public float LiftDamaged { get; private set; } = -1.0f;

    /// <summary>
    /// Max braking (deceleration).
    /// </summary>
    public float Braking { get; private set; } = BigNumber;

    /// <summary>
    /// We must be doing >= this speed in order to turn.
    /// </summary>
    public float MinTurnSpeed { get; private set; } = BigNumber;

    /// <summary>
    /// Our preferred height (if flying).
    /// </summary>
    public float PreferredHeight { get; private set; } = 0.0f;

    /// <summary>
    /// How aggressively to adjust to preferred height. 1.0 = very much, 0.1 = gradually, etc.
    /// </summary>
    public float PreferredHeightDamping { get; private set; } = 1.0f;

    /// <summary>
    /// For flying things, the radius at which they circle their "maintain" destination.
    /// Positive = clockwise, negative = counterclockwise, 0 = smallest possible.
    /// </summary>
    public float CirclingRadius { get; private set; } = 0.0f;

    /// <summary>
    /// Try to avoid going up or down at more than this speed, if possible.
    /// </summary>
    public float SpeedLimitZ { get; private set; } = 999999.0f;

    /// <summary>
    /// Extra 2D friction to apply (via physics).
    /// </summary>
    public float Extra2DFriction { get; private set; } = 0.0f;

    /// <summary>
    /// <see cref="LocomotorAppearance.Thrust"/> locomotors only: how much we deflect our thrust angle.
    /// </summary>
    public float MaxThrustAngle { get; private set; } = 0.0f;

    /// <summary>
    /// Z-axis behavior.
    /// </summary>
    public LocomotorBehaviorZ BehaviorZ { get; private set; } = LocomotorBehaviorZ.NoZMotiveForce;

    /// <summary>
    /// How we should diddle the Drawable to imitate this motion.
    /// </summary>
    public LocomotorAppearance Appearance { get; private set; } = LocomotorAppearance.Other;

    /// <summary>
    /// Where to move - front, middle, back.
    /// </summary>
    public LocomotorPriority MovementPriority { get; private set; } = LocomotorPriority.MovesMiddle;

    /// <summary>
    /// Maximum amount we will pitch up under acceleration (including recoil).
    /// </summary>
    public float AccelerationPitchLimit { get; private set; } = 0.0f;

    /// <summary>
    /// Maximum amount we will pitch down under deceleration (including recoil).
    /// </summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float DecelerationPitchLimit { get; private set; } = 0.0f;

    /// <summary>
    /// How much simulating rough terrain "bounces" a wheel up.
    /// </summary>
    public float BounceAmount { get; private set; } = 0.0f;

    // The original game originally used 0 as the defaults for stiffness and damping.
    // Then the defaults were changed to 0.1 and 0.9, respectively.
    // For stiffness, stiffness of the "springs" in the suspension: 0 = no stiffness, 1 = totally stiff
    // For damping, 0 = perfect spring, bounces forever, 1 = glued to terrain.

    /// <summary>
    /// How stiff the springs are forward and back.
    /// </summary>
    public float PitchStiffness { get; private set; } = 0.1f;

    /// <summary>
    /// How stiff the springs are side to side.
    /// </summary>
    public float RollStiffness { get; private set; } = 0.1f;

    /// <summary>
    /// How good the shock absorbers are.
    /// </summary>
    public float PitchDamping { get; private set; } = 0.9f;

    /// <summary>
    /// How good the shock absorbers are.
    /// </summary>
    public float RollDamping { get; private set; } = 0.9f;

    /// <summary>
    /// How much we pitch to response to z-speed.
    /// </summary>
    public float PitchInDirectionOfZVelFactor { get; private set; } = 0.0f;

    /// <summary>
    /// Thrust roll around X axis.
    /// </summary>
    public float ThrustRoll { get; private set; } = 0.0f;

    /// <summary>
    /// How fast thrust things "wobble".
    /// </summary>
    public float ThrustWobbleRate { get; private set; } = 0.0f;

    /// <summary>
    /// How much thrust things "wobble".
    /// </summary>
    public float ThrustMinWobble { get; private set; } = 0.0f;

    /// <summary>
    /// How much thrust things "wobble".
    /// </summary>
    public float ThrustMaxWobble { get; private set; } = 0.0f;

    /// <summary>
    /// How much we pitch in response to speed.
    /// </summary>
    public float ForwardVelocityPitchFactor { get; private set; } = 0.0f;

    /// <summary>
    /// How much we roll in response to speed.
    /// </summary>
    public float LateralVelocityRollFactor { get; private set; } = 0.0f;

    /// <summary>
    /// How much we pitch in response to acceleration.
    /// </summary>
    public float ForwardAccelerationPitchFactor { get; private set; } = 0.0f;

    /// <summary>
    /// How much we roll in response to acceleration.
    /// </summary>
    public float LateralAccelerationRollFactor { get; private set; } = 0.0f;

    /// <summary>
    /// For attenuating the pitch and roll rates.
    /// </summary>
    public float UniformAxialDamping { get; private set; } = 1.0f;

    /// <summary>
    /// Should we pivot around non-center? (-1.0 = rear, 0.0 = center, 1.0 = front)
    /// </summary>
    public float TurnPivotOffset { get; private set; } = 0.0f;

    /// <summary>
    /// The height transition at which I should mark myself as an AA target.
    /// </summary>
    public int AirborneTargetingHeight { get; private set; } = int.MaxValue;

    /// <summary>
    /// How close we have to approach the end of a path before stopping.
    /// </summary>
    public float CloseEnoughDist { get; private set; } = 1.0f;

    /// <summary>
    /// Is <see cref="CloseEnoughDist"/> 3D, for very rare cases that need to move straight down?
    /// </summary>
    public bool CloseEnoughDist3D { get; private set; } = false;

    /// <summary>
    /// How much we can fudge turning when ultra-accurate.
    /// </summary>
    public float SlideIntoPlaceTime { get; private set; } = 0.0f;

    /// <summary>
    /// Should locomotor continue working even when object is dead?
    /// </summary>
    public bool LocomotorWorksWhenDead { get; private set; } = false;

    /// <summary>
    /// Can we apply motive force when airborne?
    /// </summary>
    public bool AllowAirborneMotiveForce { get; private set; }

    /// <summary>
    /// Apply "2D friction" even when airborne... useful for realistic-looking movement.
    /// </summary>
    public bool Apply2DFrictionWhenAirborne { get; private set; } = false;

    /// <summary>
    /// Pinewood derby, moves only by gravity pulling downhill.
    /// </summary>
    public bool DownhillOnly { get; private set; }

    /// <summary>
    /// If true, can't leave ground.
    /// </summary>
    public bool StickToGround { get; private set; } = false;

    /// <summary>
    /// If true, can move backwards.
    /// </summary>
    public bool CanMoveBackwards { get; private set; } = false;

    /// <summary>
    /// If true, calculate 4 wheel independent suspension values.
    /// </summary>
    public bool HasSuspension { get; private set; } = false;

    /// <summary>
    /// Maximum distance wheels can move down (negative value).
    /// </summary>
    public float MaximumWheelExtension { get; private set; } = 0.0f;

    /// <summary>
    /// Maximum distance wheels can move up (positive value).
    /// </summary>
    public float MaximumWheelCompression { get; private set; } = 0.0f;

    /// <summary>
    /// How far the front wheels can turn.
    /// </summary>
    public float FrontWheelTurnAngle { get; private set; } = 0.0f;

    // These only apply when Appearance = TwoLegs
    public float WanderWidthFactor { get; private set; } = 0.0f;
    public float WanderLengthFactor { get; private set; } = 1.0f;
    public float WanderAboutPointRadius { get; private set; } = 0.0f;

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float RudderCorrectionDegree { get; private set; } = 0.0f;

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float RudderCorrectionRate { get; private set; } = 0.0f;

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float ElevatorCorrectionDegree { get; private set; } = 0.0f;

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float ElevatorCorrectionRate { get; private set; } = 0.0f;

    [AddedIn(SageGame.Bfme)]
    public float SlowTurnRadius { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float FastTurnRadius { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool NonDirtyTransform { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public int PreferredAttackHeight { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float AeleronCorrectionDegree { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float AeleronCorrectionRate { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float SwoopStandoffRadius { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float SwoopStandoffHeight { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float SwoopTerminalVelocity { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float SwoopAccelerationRate { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public float SwoopSpeedTuningFactor { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public FormationPriority FormationPriority { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public Percentage BackingUpSpeed { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool ChargeAvailable { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public Percentage ChargeSpeed { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float TurnThreshold { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int TurnThresholdHS { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool EnableHighSpeedTurnModelconditions { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool UseTerrainSmoothing { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool WaitForFormation { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int MaxTurnWithoutReform { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float AccDecTrigger { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int WalkDistance { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int MaxOverlappedHeight { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool CrewPowered { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool BackingUpStopWhenTurning { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int BackingUpDistanceMin { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int BackingUpDistanceMax { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float BackingUpAngle { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float LookAheadMult { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool ScalesWalls { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool ChargeIgnoresCondition { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public int BurningDeathRadius { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool BurningDeathIsCavalry { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool TurnWhileMoving { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public Percentage RiverModifier { get; private set; }

    private void Validate(SageGame sageGame)
    {
        // Generals didn't have DecelerationPitchLimit; that was added in Zero Hour.
        // In Generals there was just a single AccelerationPitchLimit value.
        if (sageGame == SageGame.CncGenerals)
        {
            DecelerationPitchLimit = AccelerationPitchLimit;
        }

        // Sim-side fix-ups mirror GPL LocomotorTemplate::validate() exactly, on the
        // quantized values (the float twins below follow the same rules).
        if (SimMaxSpeedDamaged < Fix64.Zero)
        {
            SimMaxSpeedDamaged = SimMaxSpeed;
        }
        if (SimMaxTurnRateDamaged < Fix64.Zero)
        {
            SimMaxTurnRateDamaged = SimMaxTurnRate;
        }
        if (SimAccelerationDamaged < Fix64.Zero)
        {
            SimAccelerationDamaged = SimAcceleration;
        }
        if (SimLiftDamaged < Fix64.Zero)
        {
            SimLiftDamaged = SimLift;
        }
        if (Appearance == LocomotorAppearance.Wings)
        {
            if (SimMinSpeed <= Fix64.Zero)
            {
                SimMinSpeed = SimSmallSpeed;
            }
            if (SimMinTurnSpeed <= Fix64.Zero)
            {
                SimMinTurnSpeed = SimSmallSpeed;
            }
        }
        if (Appearance == LocomotorAppearance.Thrust)
        {
            if (SimMaxSpeed <= Fix64.Zero)
            {
                SimMaxSpeed = SimSmallSpeed;
            }
            if (SimMaxSpeedDamaged <= Fix64.Zero)
            {
                SimMaxSpeedDamaged = SimSmallSpeed;
            }
            if (SimMinSpeed <= Fix64.Zero)
            {
                SimMinSpeed = SimSmallSpeed;
            }
        }

        // For "damaged" stuff that was omitted, set them to be the same as "undamaged".
        if (MaxSpeedDamaged < 0.0f)
        {
            MaxSpeedDamaged = MaxSpeed;
        }
        if (MaxTurnRateDamaged < 0.0f)
        {
            MaxTurnRateDamaged = MaxTurnRate;
        }
        if (AccelerationDamaged < 0.0f)
        {
            AccelerationDamaged = Acceleration;
        }
        if (LiftDamaged < 0.0f)
        {
            LiftDamaged = Lift;
        }

        if (Appearance == LocomotorAppearance.Wings)
        {
            if (MinSpeed <= 0.0f)
            {
                DebugUtility.Crash("WINGS should always have positive MinSpeed (otherwise, they hover)");
                MinSpeed = 0.01f;
            }
            if (MinTurnSpeed <= 0.0f)
            {
                DebugUtility.Crash("WINGS should always have positive MinTurnSpeed");
                MinTurnSpeed = 0.01f;
            }
        }

        if (Appearance == LocomotorAppearance.Thrust)
        {
            if (BehaviorZ != LocomotorBehaviorZ.NoZMotiveForce
                || Lift != 0.0f
                || LiftDamaged != 0.0f)
            {
                throw new InvalidOperationException("THRUST locos may not use ZAxisBehavior or lift!");
            }
            if (MaxSpeed <= 0.0f)
            {
                // If one of these was omitted, it defaults to zero... just quietly heal it here, rather than crashing.
                Logger.Warn("THRUST locos may not have zero MaxSpeed; healing...");
                MaxSpeed = 0.01f;
            }
            if (MaxSpeedDamaged <= 0.0f)
            {
                // If one of these was omitted, it defaults to zero... just quietly heal it here, rather than crashing.
                Logger.Warn("THRUST locos may not have zero MaxSpeedDamaged; healing...");
                MaxSpeedDamaged = 0.01f;
            }
            if (MinSpeed <= 0.0f)
            {
                // If one of these was omitted, it defaults to zero... just quietly heal it here, rather than crashing.
                Logger.Warn("THRUST locos may not have zero MinSpeed; healing...");
                MinSpeed = 0.01f;
            }
        }
    }
}

public enum LocomotorAppearance
{
    [IniEnum("TWO_LEGS")]
    TwoLegs,

    /// <summary>
    /// Human climber - backs down cliffs.
    /// </summary>
    [IniEnum("CLIMBER")]
    Climber,

    [IniEnum("THRUST")]
    Thrust,

    [IniEnum("FOUR_WHEELS")]
    FourWheels,

    [IniEnum("HOVER")]
    Hover,

    [IniEnum("WINGS")]
    Wings,

    [IniEnum("TREADS")]
    Treads,

    [IniEnum("OTHER")]
    Other,

    [IniEnum("MOTORCYCLE"), AddedIn(SageGame.CncGeneralsZeroHour)]
    Motorcycle,

    [IniEnum("GIANT_BIRD"), AddedIn(SageGame.Bfme2)]
    GiantBird,

    [IniEnum("HUGE_TWO_LEGS"), AddedIn(SageGame.Bfme)]
    HugeTwoLegs,

    [IniEnum("HORDE"), AddedIn(SageGame.Bfme)]
    Horde,

    [IniEnum("FOUR_LEGS_HUGE"), AddedIn(SageGame.Bfme)]
    FourLegsHuge,

    [IniEnum("SHIP"), AddedIn(SageGame.Bfme2)]
    Ship,
}

[Flags]
public enum Surfaces
{
    None = 0x0,

    [IniEnum("GROUND")]
    Ground = 0x1,

    [IniEnum("WATER")]
    Water = 0x2,

    [IniEnum("CLIFF")]
    Cliff = 0x4,

    [IniEnum("AIR")]
    Air = 0x8,

    [IniEnum("RUBBLE")]
    Rubble = 0x10,

    [IniEnum("OBSTACLE"), AddedIn(SageGame.Bfme)]
    Obstacle = 0x20,

    [IniEnum("IMPASSABLE"), AddedIn(SageGame.Bfme)]
    Impassable = 0x40,

    [IniEnum("DEEP_WATER"), AddedIn(SageGame.Bfme2)]
    DeepWater = 0x80,
}

public enum LocomotorPriority
{
    /// <summary>
    /// In a group, this one moves toward the back.
    /// </summary>
    [IniEnum("MOVES_BACK")]
    MovesBack = 0,

    /// <summary>
    /// In a group, this one stays in the middle.
    /// </summary>
    [IniEnum("MOVES_MIDDLE")]
    MovesMiddle = 1,

    /// <summary>
    /// In a group, this one moves toward the front of the group.
    /// </summary>
    [IniEnum("MOVES_FRONT")]
    MovesFront = 2,
}

public enum LocomotorBehaviorZ
{
    /// <summary>
    /// Does whatever physics tells it, but has no z-force of its own.
    /// </summary>
    [IniEnum("NO_Z_MOTIVE_FORCE")]
    NoZMotiveForce,

    /// <summary>
    /// Keep as surface-of-water level.
    /// </summary>
    [IniEnum("SEA_LEVEL")]
    SeaLevel,

    /// <summary>
    /// Try to follow a specific height relative to terrain/water height.
    /// </summary>
    [IniEnum("SURFACE_RELATIVE_HEIGHT")]
    SurfaceRelativeHeight,

    /// <summary>
    /// Try to follow a specific height regardless of terrain/water height.
    /// </summary>
    [IniEnum("ABSOLUTE_HEIGHT")]
    AbsoluteHeight,

    /// <summary>
    /// Stays fixed at surface-relative height, regardless of physics.
    /// </summary>
    [IniEnum("FIXED_SURFACE_RELATIVE_HEIGHT")]
    FixedSurfaceRelativeHeight,

    /// <summary>
    /// Stays fixed at absolute height, regardless of physics.
    /// </summary>
    [IniEnum("FIXED_ABSOLUTE_HEIGHT")]
    FixedAbsoluteHeight,

    /// <summary>
    /// Stays fixed at surface-relative height including buildings, regardless of physics.
    /// </summary>
    [IniEnum("FIXED_RELATIVE_TO_GROUND_AND_BUILDINGS")]
    RelativeToGroundAndBuildings,

    /// <summary>
    /// Try to follow a height relative to the highest layer.
    /// </summary>
    [IniEnum("RELATIVE_TO_HIGHEST_LAYER")]
    RelativeToHighestLayer,

    [IniEnum("FLOATING_Z"), AddedIn(SageGame.Bfme)]
    FloatingZ,

    [IniEnum("SCALING_WALLS"), AddedIn(SageGame.Bfme2)]
    ScalingWalls,
}

[AddedIn(SageGame.Bfme)]
public enum FormationPriority
{
    [IniEnum("NO_FORMATION")]
    NoFormation,

    [IniEnum("MELEE1")]
    Melee1,

    [IniEnum("MELEE2")]
    Melee2,
}
