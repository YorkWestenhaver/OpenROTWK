#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.Terrain;
using OpenSage.Utilities;

namespace OpenSage.Logic.Object;

public class ActiveBody : BodyModule
{
    private const float YellowDamagePercent = 0.25f;

    private readonly ActiveBodyModuleData _moduleData;
    private readonly List<uint> _particleSystemIds = [];

    // S1: canonical health state is the Fix64 BodyDamageCore; every float below is a
    // display/legacy view over it (D-7 boundary - this file keeps the float side
    // effects, the core keeps the arithmetic).
    private readonly BodyDamageCore _core = new();
    private LogicFrame _nextDamageFXFrame;
    private DamageType? _lastDamageFXDone;
    private DamageInfo _lastDamageInfo;
    private LogicFrame? _lastDamageFrame;
    private LogicFrame? _lastHealingFrame;
    private bool _frontCrushed;
    private bool _backCrushed;
    private bool _lastDamageCleared;
    private bool _indestructible;

    private BitArray<ArmorSetCondition> _currentArmorSetFlags = new();
    private ArmorTemplateSet? _currentArmorSet;
    private Armor _currentArmor = Armor.NoArmor;
    private DamageFX? _currentDamageFX;

    internal ActiveBody(GameObject gameObject, IGameEngine gameEngine, ActiveBodyModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;

        // R7 Body ModuleData audit: MaxHealth/InitialHealth are now quantized ONCE at parse
        // (ParseFix64, S5 blessed integer text boundary); the ctor consumes Fix64 directly.
        // The two CombatLegacyBridge.QuantizeFloat edges S1 carried here are burned.
        _core.Initialize(
            moduleData.MaxHealth,
            moduleData.InitialHealth,
            Thresholds);

        // Force an initially-valid armor setup.
        ValidateArmorAndDamageFX();

        // Start us in the right state (rubble side effects, effectively-dead flag).
        ApplyDamageStateSideEffects();
    }

    /// <summary>Quantized GameData damage-state thresholds (constant after parse).</summary>
    private DamageStateThresholds Thresholds
    {
        get
        {
            var gameData = GameEngine.AssetStore.GameData.Current;
            return new DamageStateThresholds(
                CombatLegacyBridge.QuantizeFloat(gameData.UnitDamagedThreshold),
                CombatLegacyBridge.QuantizeFloat(gameData.UnitReallyDamagedThreshold));
        }
    }

    /// <summary>Canonical Fix64 health ledger (the S1 system's read surface).</summary>
    public BodyDamageCore DamageCore => _core;

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        ValidateArmorAndDamageFX();

        var damageOutput = new DamageInfoOutput();

        if (_indestructible)
        {
            return damageOutput;
        }

        // We cannot damage again objects that are already dead.
        var obj = GameObject;
        if (obj.IsEffectivelyDead)
        {
            return damageOutput;
        }

        var damager = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);

        var alreadyHandled = false;
        var allowModifier = true;

        // The Fix64 chain: quantize the (still float-typed) request once, then armor,
        // scalar and health all resolve in Q31.32.
        var amount = _currentArmor.AdjustDamage(
            damageInput.DamageType,
            CombatLegacyBridge.QuantizeFloat(damageInput.Amount));

        switch (damageInput.DamageType)
        {
            case DamageType.Healing:
                {
                    if (!damageInput.Kill)
                    {
                        // Healing and Damage are separate, so this shouldn't happen.
                        damageOutput = AttemptHealing(damageInput);
                    }
                    return damageOutput;
                }

            case DamageType.KillPilot:
                {
                    // This type of damage doesn't actually damage the unit, but it
                    // does kill its pilot, in the case of a vehicle.
                    if (obj.IsKindOf(ObjectKinds.Vehicle))
                    {
                        // Handle special case for combat bike. We actually kill
                        // the bike by forcing the rider to leave the bike. That
                        // way the bike will automatically scuttle and be unusable.
                        var contain = obj.Contain;
                        if (contain != null && contain.IsRiderChangeContain)
                        {
                            var ai = obj.AIUpdate;

                            if (ai.IsMoving)
                            {
                                // Bike is moving, so just blow it up instead.
                                damager?.ScoreTheKill(obj);
                                obj.Kill();
                            }
                            else
                            {
                                // Removing the rider will scuttle the bike.
                                var rider = contain.ContainedItems[0];
                                ai.AIEvacuateInstantly(true, CommandSourceType.FromAI);

                                // Kill the rider.
                                damager?.ScoreTheKill(rider);
                                rider.Kill();
                            }
                        }
                        else
                        {
                            // Make it unmanned, so units can easily check the
                            // ability to "take control of it".
                            obj.SetDisabled(DisabledType.Unmanned);
                            GameEngine.GameLogic.DeselectObject(obj, PlayerMaskType.All, true);

                            obj.AIUpdate?.AIIdle(CommandSourceType.FromAI);

                            // Convert it to the neutral team so it renders gray
                            // giving visual representation that it is unmanned.
                            obj.Team = GameEngine.Game.PlayerManager.NeutralPlayer.DefaultTeam;
                        }

                        // We don't care which team sniped the vehicle... we use
                        // this information to flag whether or not we captured a
                        // vehicle.
                        GameEngine.Game.PlayerManager.NeutralPlayer.AcademyStats.RecordVehicleSniped();
                    }

                    alreadyHandled = true;
                    allowModifier = false;
                    break;
                }

            case DamageType.KillGarrisoned:
                {
                    // Original comment (which suggests that this code and the code
                    // mentioned in DumbProjectileBehavior) could be refactored:
                    //
                    // This code is very misleading (but in a good way). One would think this is
                    // an excellent place to add the hook to kill garrisoned troops. And that is
                    // a correct assumption. Unfortunately, the vast majority of garrison slayings
                    // are performed in DumbProjectileBehavior::projectileHandleCollision(), so my
                    // hope is that this message will save you some research time!

                    var killsToMake = MathUtility.FloorToInt(damageInput.Amount);
                    var contain = obj.Contain;
                    if (contain != null
                        && contain.ContainCount > 0
                        && contain.IsGarrisonable
                        && !contain.IsImmuneToClearBuildingAttacks)
                    {
                        var numKilled = 0;

                        // Garrisonable buildings subvert the normal process here.
                        foreach (var thingToKill in contain.ContainedItems)
                        {
                            if (numKilled >= killsToMake)
                            {
                                break;
                            }

                            if (!thingToKill.IsEffectivelyDead)
                            {
                                damager?.ScoreTheKill(thingToKill);
                                thingToKill.Kill();
                                numKilled++;
                                thingToKill.Owner.AcademyStats.RecordClearedGarrisonedBuilding();
                            }
                        }
                    }

                    alreadyHandled = true;
                    allowModifier = false;
                    break;
                }

            case DamageType.Status:
                {
                    // Damage amount is millisecond time we set the status given in
                    // DamageStatusType.
                    var framesToStatusFor = LogicFrameSpan.FromMilliseconds(amount.ToFloatForDisplay(), GameEngine.MsPerLogicFrame);
                    obj.DoStatusDamage(damageInput.DamageStatusType, framesToStatusFor);
                    alreadyHandled = true;
                    allowModifier = false;
                    break;
                }
        }

        if (damageInput.DamageType.IsSubdualDamage())
        {
            if (!CanBeSubdued)
            {
                return damageOutput;
            }

            var wasSubdued = IsSubdued;
            _core.AddSubdualDamage(amount, _moduleData.SubdualDamageCap);
            var nowSubdued = IsSubdued;

            alreadyHandled = true;
            allowModifier = false;

            if (wasSubdued != nowSubdued)
            {
                OnSubdualChange(nowSubdued);
            }

            obj.NotifySubdualDamage(amount.ToFloatForDisplay());
        }

        if (allowModifier && damageInput.DamageType != DamageType.Unresistable)
        {
            // Apply the damage scalar (extra bonuses, like strategy center
            // defensive battle plan). And remember not to adjust unresistable
            // damage, just like the armor code can't.
            amount *= DamageScalarFix64;
        }

        // Sanity check the damage value. We can't apply negative damage.
        if (amount > SimCore.Numerics.Fix64.Zero || damageInput.Kill)
        {
            var oldState = _core.DamageState;

            // Resolve the Kill override to the concrete remaining health up front (the
            // core's kill override is exactly "amount becomes current health"), so the
            // Body-subclass health-floor hook below can floor even a DAMAGE_KILL - a
            // GPL ImmortalBody survives kills too.
            var healthLoss = damageInput.Kill ? _core.CurrentHealth : amount;

            // Body-subclass health-floor seam. Default identity (every existing body).
            // ImmortalBody/HighlanderBody override it to keep a >= 1 health floor, in
            // Fix64 on the canonical core health (never the float display view). Only the
            // health-affecting path is floored: GPL located the floor in
            // internalChangeHealth, which the special "alreadyHandled" damage types
            // (KillPilot/KillGarrisoned/Status/subdual) never route through.
            if (!alreadyHandled)
            {
                healthLoss = ClampCombatHealthLoss(healthLoss);
            }

            // The arithmetic half (clamp, damage-state recompute) lives in the Fix64
            // core; the visual/dead side effects follow here. Kill is already folded into
            // healthLoss, so it is passed to the core as false.
            CombatDamageOutput combatOutput;
            if (!alreadyHandled && healthLoss <= SimCore.Numerics.Fix64.Zero)
            {
                // A health floor consumed the entire hit (e.g. an already-1-HP
                // ImmortalBody). Run a zero-delta change so previousHealth tracks current
                // - GPL internalChangeHealth always does prev = current before applying,
                // and the fear-sound threshold predicate below reads previousHealth, so a
                // stale prev would produce a phantom (desyncing) fear-sound RNG draw. No
                // damage is dealt.
                _core.ChangeHealth(SimCore.Numerics.Fix64.Zero, Thresholds);
                combatOutput = new CombatDamageOutput();
            }
            else
            {
                combatOutput = _core.ApplyDamage(healthLoss, kill: false, !alreadyHandled, Thresholds, out _);
            }

            if (!alreadyHandled)
            {
                ApplyHealthChangeSideEffects(oldState);
            }

            // Record the actual damage done from this, and when it happened.
            LastCombatOutput = combatOutput;
            damageOutput = CombatLegacyBridge.ToLegacyOutput(combatOutput);

            // Then store the whole DamageInfo for easy lookup.
            var currentDamageInfo = new DamageInfo
            {
                Request = damageInput,
                Result = damageOutput,
            };
            if (_lastDamageFrame < GameEngine.GameLogic.CurrentFrame - 1)
            {
                SetLastDamageInfo(currentDamageInfo);
            }
            else
            {
                // Multiple damages applied in the last two frames. We prefer
                // the one that tells us who the attacker is.
                var srcObj1 = GameEngine.GameLogic.GetObjectById(_lastDamageInfo.Request.SourceID);
                var srcObj2 = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
                if (srcObj2 != null)
                {
                    if (srcObj1 != null)
                    {
                        if (srcObj2.IsKindOf(ObjectKinds.Vehicle)
                            || srcObj2.IsKindOf(ObjectKinds.Infantry)
                            || srcObj2.IsFactionStructure)
                        {
                            SetLastDamageInfo(currentDamageInfo);
                        }
                    }
                    else
                    {
                        SetLastDamageInfo(currentDamageInfo);
                    }
                }
            }

            // Notify the player that they have been attacked by this player.
            if (_lastDamageInfo.Request.SourceID.IsValid)
            {
                var srcObj = GameEngine.GameLogic.GetObjectById(_lastDamageInfo.Request.SourceID);
                if (srcObj != null)
                {
                    var srcPlayer = srcObj.Owner;
                    obj.Owner.SetAttackedBy(srcPlayer.Id);
                }
            }

            // If our health has gone down, then run the damage module callback.
            if (_core.CurrentHealth < _core.PreviousHealth)
            {
                foreach (var module in obj.FindBehaviors<IDamageModule>())
                {
                    module.OnDamage(currentDamageInfo);
                }
            }

            if (_core.DamageState != oldState)
            {
                foreach (var module in obj.FindBehaviors<IDamageModule>())
                {
                    module.OnBodyDamageStateChange(
                        currentDamageInfo,
                        oldState,
                        _core.DamageState);
                }

                // Original comment:
                // @todo: This really feels like it should be in the TransitionFX lists.
                // (Null-conditional: headless hosts carry no audio system; audio is a
                // client-bound output, never a sim input.)
                switch (_core.DamageState)
                {
                    case BodyDamageType.Damaged:
                        GameEngine.AudioSystem?.PlayAudioEvent(
                            obj,
                            obj.Definition.SoundOnDamaged?.Value);
                        break;

                    case BodyDamageType.ReallyDamaged:
                        GameEngine.AudioSystem?.PlayAudioEvent(
                            obj,
                            obj.Definition.SoundOnReallyDamaged?.Value);
                        break;
                }
            }

            // Should we play our fear sound? (Client-bound audio decision; float views.)
            if ((PreviousHealth / MaxHealth) > YellowDamagePercent
                && (Health / MaxHealth) < YellowDamagePercent
                && _core.CurrentHealth > SimCore.Numerics.Fix64.Zero)
            {
                // 25% chance to play. (The draw happens unconditionally - it is
                // sim-relevant RNG consumption, GPL shape - only the playback is
                // client-side and headless-null-safe.)
                if (GameEngine.GameLogic.Random.Next(0, 99) < 25)
                {
                    GameEngine.AudioSystem?.PlayAudioEvent(
                        obj.Translation,
                        // TODO(Port): Set PlayerIndex on sound.
                        // fearSound.setPlayerIndex( obj->getControllingPlayer()->getPlayerIndex() );
                        obj.Definition.VoiceFear?.Value);
                }
            }

            // Check to see if we died.
            if (_core.CurrentHealth <= SimCore.Numerics.Fix64.Zero
                && _core.PreviousHealth > SimCore.Numerics.Fix64.Zero)
            {
                // Give our killer credit for killing us, if there is one.
                damager?.ScoreTheKill(obj);
                obj.OnDie(damageInput);
            }
        }

        DoDamageFX(damageInput, damageOutput);

        // Damaged repulsable civilians scare (repulse) other civilians.
        if (GameEngine.AssetStore.AIData.Current.EnableRepulsors
            && obj.IsKindOf(ObjectKinds.CanBeRepulsed))
        {
            obj.SetObjectStatus(ObjectStatus.Repulsor, true);
        }

        // Retaliate, even if I'm dead. We'll still get my nearby friends to
        // get revenge! Also only retaliate if we're controlled by a human
        // player and the thing that attacked me is an enemy.
        var controllingPlayer = obj.Owner;
        if (controllingPlayer != null
            && controllingPlayer.IsLogicalRetaliationModeEnabled
            && controllingPlayer.IsHuman
            && ShouldRetaliateAgainstAggressor(obj, damager))
        {
            // TODO(Port): Do retaliation. Requires some partition filter
            // and AI stuff that we don't have yet.
        }

        return damageOutput;
    }

    public override DamageInfoOutput AttemptHealing(in DamageInfoInput damageInput)
    {
        ValidateArmorAndDamageFX();

        if (damageInput.DamageType != DamageType.Healing)
        {
            // Healing and Damage are separate, so this shouldn't happen.
            return AttemptDamage(damageInput);
        }

        var obj = GameObject;

        var damageOutput = new DamageInfoOutput();

        // Sorry, once yer dead, yer dead.
        // Special case for bridges, cause the system now thinks they're dead.
        // Original comment:
        // @todo we need to figure out what has changed so we don't have to hack this
        if (!obj.IsKindOf(ObjectKinds.Bridge)
            && !obj.IsKindOf(ObjectKinds.BridgeTower)
            && obj.IsEffectivelyDead)
        {
            return damageOutput;
        }

        var amount = _currentArmor.AdjustDamage(
            damageInput.DamageType,
            CombatLegacyBridge.QuantizeFloat(damageInput.Amount));

        // Sanity check the damage value. Can't apply negative healing.
        if (amount > SimCore.Numerics.Fix64.Zero)
        {
            var oldState = _core.DamageState;

            // Do the damage simplistic damage ADDITION (Fix64 core).
            var combatOutput = _core.ApplyHealing(amount, Thresholds, out _);
            ApplyHealthChangeSideEffects(oldState);

            // Record the actual damage done from this, and when it happened.
            LastCombatOutput = combatOutput;
            damageOutput = CombatLegacyBridge.ToLegacyOutput(combatOutput);

            // Then copy the whole DamageInfo struct for easy lookup.
            var currentDamageInfo = new DamageInfo
            {
                Request = damageInput,
                Result = damageOutput
            };
            SetLastDamageInfo(currentDamageInfo);
            _lastHealingFrame = GameEngine.GameLogic.CurrentFrame;

            // If our health has gone UP, then run the damage module callback.
            if (_core.CurrentHealth > _core.PreviousHealth)
            {
                foreach (var module in obj.FindBehaviors<IDamageModule>())
                {
                    module.OnHealing(currentDamageInfo);
                }
            }

            if (_core.DamageState != oldState)
            {
                foreach (var module in obj.FindBehaviors<IDamageModule>())
                {
                    module.OnBodyDamageStateChange(
                        currentDamageInfo,
                        oldState,
                        _core.DamageState);
                }
            }
        }

        DoDamageFX(damageInput, damageOutput);

        return damageOutput;
    }

    public override float EstimateDamage(in DamageInfoInput damageInput)
    {
        ValidateArmorAndDamageFX();

        // Subdual damage can't affect you if you can't be subdued.
        if (damageInput.DamageType.IsSubdualDamage() && !CanBeSubdued)
        {
            return 0.0f;
        }

        switch (damageInput.DamageType)
        {
            case DamageType.KillGarrisoned:
                var contain = GameObject.Contain;

                var canKillGarrisoned = contain != null
                    && contain.ContainCount > 0
                    && contain.IsGarrisonable
                    && !contain.IsImmuneToClearBuildingAttacks;

                return canKillGarrisoned
                    ? 1.0f
                    : 0.0f;

            case DamageType.Sniper:
                if (GameObject.IsKindOf(ObjectKinds.Structure)
                    && GameObject.TestStatus(ObjectStatus.UnderConstruction))
                {
                    // If we're a pathfinder shooting a stinger site under
                    // construction... don't. Special case code.
                    return 0.0f;
                }
                break;
        }

        return _currentArmor.AdjustDamage(
            damageInput.DamageType,
            CombatLegacyBridge.QuantizeFloat(damageInput.Amount)).ToFloatForDisplay();
    }

    public override float Health => _core.CurrentHealth.ToFloatForDisplay();

    public override float MaxHealth => _core.MaxHealth.ToFloatForDisplay();

    /// <summary>
    /// Simple setting of the health value. It does _not_ track any transition
    /// states for the event of "damage" or the event of "death".
    /// </summary>
    public override void SetMaxHealth(float maxHealth, MaxHealthChangeType healthChangeType)
    {
        var oldState = _core.DamageState;
        _core.SetMaxHealth(CombatLegacyBridge.QuantizeFloat(maxHealth), healthChangeType, Thresholds);
        ApplyHealthChangeSideEffects(oldState);
    }

    public override float InitialHealth => _core.InitialHealth.ToFloatForDisplay();

    /// <summary>
    /// Simple setting of the initial health value. It does _not_ track any transition
    /// states for the event of "damage" or the event of "death".
    /// </summary>
    public override void SetInitialHealth(int initialPercent)
    {
        var oldState = _core.DamageState;
        _core.SetInitialHealthPercent(initialPercent, Thresholds);
        ApplyHealthChangeSideEffects(oldState);
    }

    public override float PreviousHealth => _core.PreviousHealth.ToFloatForDisplay();

    public override LogicFrameSpan SubdualDamageHealRate => _moduleData.SubdualDamageHealRate;

    public override bool HasAnySubdualDamage => _core.CurrentSubdualDamage > SimCore.Numerics.Fix64.Zero;

    public override float CurrentSubdualDamageAmount => _core.CurrentSubdualDamage.ToFloatForDisplay();

    public override BodyDamageType DamageState
    {
        get => _core.DamageState;
        set
        {
            var thresholds = Thresholds;
            var ratio = value switch
            {
                BodyDamageType.Pristine => SimCore.Numerics.Fix64.One,
                BodyDamageType.Damaged => thresholds.Damaged,
                BodyDamageType.ReallyDamaged => thresholds.ReallyDamaged,
                BodyDamageType.Rubble => SimCore.Numerics.Fix64.Zero,
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            };

            // GPL setDamageState: desired = max * ratio - 1 (-1 because < not <= in
            // CalculateDamageState), floored at zero.
            var desiredHealth = _core.MaxHealth * ratio - SimCore.Numerics.Fix64.One;
            if (desiredHealth < SimCore.Numerics.Fix64.Zero)
            {
                desiredHealth = SimCore.Numerics.Fix64.Zero;
            }

            InternalChangeHealth(desiredHealth - _core.CurrentHealth);
        }
    }

    public override void SetAflame(bool setting)
    {
        // All this does now is act like a major body state change. It is called
        // after Aflame has been set or cleared as an object status.
        UpdateBodyParticleSystems();
    }

    public override void OnVeterancyLevelChanged(VeterancyLevel oldLevel, VeterancyLevel newLevel, bool provideFeedback = false)
    {
        if (oldLevel == newLevel)
        {
            return;
        }

        if (oldLevel < newLevel)
        {
            if (provideFeedback)
            {
                var veterancyChanged = newLevel switch
                {
                    VeterancyLevel.Veteran => GameObject.Definition.SoundPromotedVeteran?.Value,
                    VeterancyLevel.Elite => GameObject.Definition.SoundPromotedElite?.Value,
                    VeterancyLevel.Heroic => GameObject.Definition.SoundPromotedHero?.Value,
                    _ => throw new ArgumentOutOfRangeException(nameof(newLevel))
                };
                GameEngine.AudioSystem.PlayAudioEvent(GameObject, veterancyChanged);
            }

            // Also mark the UI dirty, in case the object is selected or contained.
            // TODO(Port): Implement this.
            //    var obj = GameObject;
            //    var draw = TheInGameUI->getFirstSelectedDrawable();
            //    if (draw != null)
            //    {
            //        var checkOwner = draw.GameObject;
            //        if (checkOwner == obj)
            //        {
            //            // Our selected object has been promoted!
            //            TheControlBar->markUIDirty();
            //        }
            //        else
            //        {
            //            var containedBy = obj.ContainedBy;
            //            if (containedBy && TheInGameUI->getSelectCount() == 1)
            //            {
            //                var checkOwner = draw.GameObject;
            //                if (checkOwner == containedBy)
            //                {
            //                    //But only if the contained by object is containing me!
            //                    TheControlBar->markUIDirty();
            //                }
            //            }
            //        }
            //    }
        }

        var oldBonus = GameEngine.AssetStore.GameData.Current.HealthBonus[(int)oldLevel];
        var newBonus = GameEngine.AssetStore.GameData.Current.HealthBonus[(int)newLevel];
        var mult = newBonus / oldBonus;

        // change the max
        SetMaxHealth(MaxHealth * mult, MaxHealthChangeType.PreserveRatio);

        switch (newLevel)
        {
            case VeterancyLevel.Regular:
                ClearArmorSetFlag(ArmorSetCondition.Veteran);
                ClearArmorSetFlag(ArmorSetCondition.Elite);
                ClearArmorSetFlag(ArmorSetCondition.Hero);
                break;

            case VeterancyLevel.Veteran:
                SetArmorSetFlag(ArmorSetCondition.Veteran);
                ClearArmorSetFlag(ArmorSetCondition.Elite);
                ClearArmorSetFlag(ArmorSetCondition.Hero);
                break;

            case VeterancyLevel.Elite:
                ClearArmorSetFlag(ArmorSetCondition.Veteran);
                SetArmorSetFlag(ArmorSetCondition.Elite);
                ClearArmorSetFlag(ArmorSetCondition.Hero);
                break;

            case VeterancyLevel.Heroic:
                ClearArmorSetFlag(ArmorSetCondition.Veteran);
                ClearArmorSetFlag(ArmorSetCondition.Elite);
                SetArmorSetFlag(ArmorSetCondition.Hero);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(newLevel));
        }
    }

    public override void SetArmorSetFlag(ArmorSetCondition armorSetType)
    {
        _currentArmorSetFlags.Set(armorSetType, true);
    }

    public override void ClearArmorSetFlag(ArmorSetCondition armorSetType)
    {
        _currentArmorSetFlags.Set(armorSetType, false);
    }

    public override bool TestArmorSetFlag(ArmorSetCondition armorSetType)
    {
        return _currentArmorSetFlags.Get(armorSetType);
    }

    public override DamageInfo? LastDamageInfo => _lastDamageInfo;

    public override LogicFrame LastDamageFrame => _lastDamageFrame ?? LogicFrame.Zero;

    public override LogicFrame LastHealingFrame => _lastHealingFrame ?? LogicFrame.Zero;

    public override ObjectId ClearableLastAttacker => _lastDamageCleared ? ObjectId.Invalid : _lastDamageInfo.Request.SourceID;

    public override void ClearLastAttacker()
    {
        _lastDamageCleared = true;
    }

    public override bool FrontCrushed
    {
        get => _frontCrushed;
        set => _frontCrushed = value;
    }

    public override bool BackCrushed
    {
        get => _backCrushed;
        set => _backCrushed = value;
    }

    public override void InternalChangeHealth(float delta)
    {
        InternalChangeHealth(CombatLegacyBridge.QuantizeFloat(delta));
    }

    /// <summary>
    /// Body-subclass hook: clamp the health loss a single <see cref="AttemptDamage"/>
    /// will inflict, evaluated AFTER armor and the damage scalar and after the Kill
    /// override has been resolved to remaining health. Default: no clamp. ImmortalBody /
    /// HighlanderBody override it to enforce a minimum surviving health, entirely in
    /// Fix64 on <see cref="DamageCore"/> (not the float display view). This is the S1
    /// seam that replaces GPL's per-subclass override of the virtual
    /// <c>internalChangeHealth</c>: the S1 core extraction commits the health mutation
    /// inside <see cref="BodyDamageCore"/>, so the overridable point moves here.
    /// </summary>
    protected virtual SimCore.Numerics.Fix64 ClampCombatHealthLoss(SimCore.Numerics.Fix64 loss) => loss;

    /// <summary>The canonical Fix64 health change (GPL internalChangeHealth).</summary>
    public void InternalChangeHealth(SimCore.Numerics.Fix64 delta)
    {
        var oldState = _core.DamageState;
        _core.ChangeHealth(delta, Thresholds);
        ApplyHealthChangeSideEffects(oldState);
    }

    /// <summary>
    /// The float-substrate side effects the original ran inside
    /// internalChangeHealth/setCorrectDamageState: rubble geometry/pathfind work,
    /// visual-condition re-evaluation on a state change, and the effectively-dead flag.
    /// The arithmetic itself lives in <see cref="BodyDamageCore"/>.
    /// </summary>
    private void ApplyHealthChangeSideEffects(BodyDamageType oldState)
    {
        ApplyDamageStateSideEffects();

        // If our state has changed, show a visual change in the model for the damage
        // state. We do not show visual changes for damage states when things are under
        // construction, because we just don't have all the art states for that during
        // buildup animation.
        if (_core.DamageState != oldState
            && !GameObject.TestStatus(ObjectStatus.UnderConstruction))
        {
            EvaluateVisualCondition();
        }

        // Mark the bit according to our health. If our AI is dead but our
        // health improves, it will still re-flag this bit in the AIDeadState
        // every frame.
        GameObject.IsEffectivelyDead = _core.CurrentHealth <= SimCore.Numerics.Fix64.Zero;
    }

    public override bool IsIndestructible
    {
        get => _indestructible;
        set
        {
            _indestructible = value;

            // For bridges, we mirror this state on its towers.
            if (GameObject.IsKindOf(ObjectKinds.Bridge))
            {
                var bb = GameObject.FindBehavior<BridgeBehavior>();
                if (bb != null)
                {
                    foreach (var bridgeTowerType in Enum.GetValues<BridgeTowerType>())
                    {
                        var towerId = bb.GetTowerId(bridgeTowerType);
                        var tower = GameEngine.GameLogic.GetObjectById(towerId);

                        if (tower?.BodyModule != null)
                        {
                            tower.BodyModule.IsIndestructible = value;
                        }
                    }
                }
            }
        }
    }

    public override void EvaluateVisualCondition()
    {
        GameObject.Drawable?.ReactToBodyDamageStateChange(_core.DamageState);

        // Destroy any particle systems that were attached to our body for the
        // old state and create new particle systems for the new state.
        UpdateBodyParticleSystems();
    }

    public override void UpdateBodyParticleSystems()
    {
        // TODO(Port): Implemement this.
    }

    private bool CanBeSubdued => _moduleData.SubdualDamageCap > Fix64.Zero;

    private bool IsSubdued => _core.IsSubdued;

    /// <summary>
    /// The rubble half of GPL setCorrectDamageState (the state computation itself moved
    /// into <see cref="BodyDamageCore"/>; these side effects run after every change).
    /// </summary>
    private void ApplyDamageStateSideEffects()
    {
        // Original comment:
        // @todo srj -- bleah, this is an icky way to do it. oh well.
        if (_core.DamageState == BodyDamageType.Rubble
            && GameObject.IsKindOf(ObjectKinds.Structure))
        {
            var rubbleHeight = GameObject.Definition.StructureRubbleHeight;

            if (rubbleHeight <= 0.0f)
            {
                rubbleHeight = GameEngine.AssetStore.GameData.Current.DefaultStructureRubbleHeight;
            }

            // There's an original comment that says this was changed to a
            // Z only version, to keep it from disappearing from PartitionManager
            // for a frame (which didn't previously happen).
            GameObject.SetGeometryInfoZ(rubbleHeight);

            // Have to tell pathfind as well, as rubble pathfinds differently.
            GameEngine.AI.Pathfinder.RemoveObjectFromPathfindMap(GameObject);
            GameEngine.AI.Pathfinder.AddObjectToPathfindMap(GameObject);

            // Here we make sure nobody collides with us, ever again.
            // This allows projectiles shot from infantry that are inside
            // rubble to get out of said rubble safely.
            GameObject.SetObjectStatus(ObjectStatus.NoCollisions, true);
        }
    }

    private void DoDamageFX(in DamageInfoInput damageInput, in DamageInfoOutput damageOutput)
    {
        // Just the visual aspect of damage can be overridden in some cases.
        // Unresistable is the default to mean no override, as we are out of bits.
        var damageTypeToUse = damageInput.DamageFXOverride != DamageType.Unresistable
            ? damageInput.DamageFXOverride
            : damageInput.DamageType;

        if (_currentDamageFX == null)
        {
            return;
        }

        var now = GameEngine.GameLogic.CurrentFrame;

        if (damageTypeToUse == _lastDamageFXDone && _nextDamageFXFrame > now)
        {
            return;
        }

        var source = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);

        _lastDamageFXDone = damageTypeToUse;
        _nextDamageFXFrame = now + _currentDamageFX.GetDamageFXThrottleTime(damageTypeToUse, source);

        _currentDamageFX.DoDamageFX(
            damageTypeToUse,
            damageOutput.ActualDamageDealt,
            source,
            GameObject,
            GameEngine);
    }

    private void OnSubdualChange(bool isNowSubdued)
    {
        if (!GameObject.IsKindOf(ObjectKinds.Projectile))
        {
            var me = GameObject;

            if (isNowSubdued)
            {
                me.SetDisabled(DisabledType.Subdued);

                me.Contain?.OrderAllPassengersToIdle(CommandSourceType.FromAI);
            }
            else
            {
                me.ClearDisabled(DisabledType.Subdued);

                if (me.IsKindOf(ObjectKinds.FSInternetCenter))
                {
                    // Any unit inside an internet center is a hacker! Order
                    // them to start hacking again.
                    me.Contain?.OrderAllPassengersToHackInternet(CommandSourceType.FromAI);
                }
            }
        }
        else if (isNowSubdued)
        {
            // There's no coming back from being jammed, and projectiles can't
            // even heal, but this makes it clear.
            GameObject.FindBehavior<IProjectileUpdate>()?.ProjectileNowJammed();
        }
    }

    private bool ShouldRetaliateAgainstAggressor(GameObject obj, GameObject? damager)
    {
        // TODO(Port): Implement this.
        return false;
    }

    private void SetLastDamageInfo(in DamageInfo damageInfo)
    {
        _lastDamageInfo = damageInfo;
        _lastDamageCleared = false;
        _lastDamageFrame = GameEngine.GameLogic.CurrentFrame;
    }

    protected internal override void OnDestroy()
    {
        DeleteAllParticleSystems();
    }

    private void DeleteAllParticleSystems()
    {
        // TODO(Port): Implement this.
    }

    private void ValidateArmorAndDamageFX()
    {
        var set = BitArrayMatchFinder.FindBest(
            CollectionsMarshal.AsSpan(GameObject.Definition.ArmorSets),
            _currentArmorSetFlags);

        if (set != null && set != _currentArmorSet)
        {
            _currentArmor = new Armor(set.Armor.Value);
            _currentDamageFX = set.DamageFX?.Value;
            _currentArmorSet = set;
        }
    }

    // ---- the contract Xfer walk: the Fix64 combat state participates in save/load + CRC +
    // deep-dump. Field order = declaration order, ours (F9). R7 completes the Objects-CRC fold
    // by adding the armor-set condition flags (sim state: they select the active armor), packed
    // into a uint over the 19 ArmorSetCondition values - closing finding F-WDA-5 with the frozen
    // IXfer surface (no framework change). Deliberately EXCLUDED from the CRC channel, and why:
    //   - _nextDamageFXFrame / _lastDamageFXDone: DamageFX throttle. Client-bound and set only
    //     when a DamageFX asset is present, so they are HOST-DEPENDENT (null on headless) and
    //     must not enter the sim checksum (D-5).
    //   - _particleSystemIds: client visual handles.
    //   - _lastDamageInfo (+ its frames): a float-laden legacy lookup struct (DamageInfoInput/
    //     Output carry float Amount) used for display / AI last-attacker queries; it cannot cross
    //     the float ban into the Fix64 channel. It rides the F9-exempt retail persister only.
    //   - _currentArmorSet / _currentArmor / _currentDamageFX: derived, recomputed by
    //     ValidateArmorAndDamageFX from the flags every AttemptDamage (rebuilt on load below).
    // ----

    internal override bool HasSimXfer => true;

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(2);
        XferBodyBase(xfer);                       // DamageScalar (Quantum)
        _core.Xfer(xfer);                         // health ledger + damage state
        xfer.XferBool("FrontCrushed", ref _frontCrushed);
        xfer.XferBool("BackCrushed", ref _backCrushed);
        xfer.XferBool("Indestructible", ref _indestructible);
        XferArmorSetFlags(xfer);
    }

    /// <summary>
    /// Folds <see cref="_currentArmorSetFlags"/> into the contract CRC channel by packing the
    /// 19 <see cref="ArmorSetCondition"/> bits into a uint (they select the active armor, so they
    /// are deterministic sim state). On load the flag set and the derived armor/DamageFX are
    /// rebuilt. Closes finding F-WDA-5 without touching the frozen IXfer contract.
    /// </summary>
    private void XferArmorSetFlags(SimCore.Sync.IXfer xfer)
    {
        uint packed = 0;
        if (xfer.Mode != XferMode.Load)
        {
            foreach (var condition in Enum.GetValues<ArmorSetCondition>())
            {
                if (_currentArmorSetFlags.Get(condition))
                {
                    packed |= 1u << (int)condition;
                }
            }
        }

        xfer.XferUInt("ArmorSetFlags", ref packed);

        if (xfer.Mode == XferMode.Load)
        {
            _currentArmorSetFlags = new BitArray<ArmorSetCondition>();
            foreach (var condition in Enum.GetValues<ArmorSetCondition>())
            {
                _currentArmorSetFlags.Set(condition, (packed & (1u << (int)condition)) != 0);
            }

            // Force the derived armor/DamageFX to re-resolve from the restored flags.
            _currentArmorSet = null;
            ValidateArmorAndDamageFX();
        }
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        // Retail .sav layout is float (F9-exempt legacy reader); quantize into the
        // Fix64 core on read, widen on write.
        var currentHealth = _core.CurrentHealth.ToFloatForDisplay();
        var currentSubdualDamage = _core.CurrentSubdualDamage.ToFloatForDisplay();
        var previousHealth = _core.PreviousHealth.ToFloatForDisplay();
        var maxHealth = _core.MaxHealth.ToFloatForDisplay();
        var initialHealth = _core.InitialHealth.ToFloatForDisplay();
        var currentDamageState = _core.DamageState;

        reader.PersistSingle(ref currentHealth);

        // ZH changed added this field, but didn't bump the version.
        if (reader.SageGame >= SageGame.CncGeneralsZeroHour)
        {
            reader.PersistSingle(ref currentSubdualDamage);
        }

        reader.PersistSingle(ref previousHealth);
        reader.PersistSingle(ref maxHealth);
        reader.PersistSingle(ref initialHealth);
        reader.PersistEnum(ref currentDamageState);

        if (reader.Mode == StatePersistMode.Read)
        {
            _core.LoadState(
                CombatLegacyBridge.QuantizeFloat(currentHealth),
                CombatLegacyBridge.QuantizeFloat(currentSubdualDamage),
                CombatLegacyBridge.QuantizeFloat(previousHealth),
                CombatLegacyBridge.QuantizeFloat(maxHealth),
                CombatLegacyBridge.QuantizeFloat(initialHealth),
                currentDamageState);
        }

        reader.PersistLogicFrame(ref _nextDamageFXFrame);
        reader.PersistEnumOptional(ref _lastDamageFXDone);

        reader.PersistObject(ref _lastDamageInfo);
        reader.PersistLogicFrameOptional(ref _lastDamageFrame);
        reader.PersistLogicFrameOptional(ref _lastHealingFrame);

        reader.PersistBoolean(ref _frontCrushed);
        reader.PersistBoolean(ref _backCrushed);

        reader.PersistBoolean(ref _lastDamageCleared);
        reader.PersistBoolean(ref _indestructible);

        reader.PersistList(
        _particleSystemIds,
        static (StatePersister persister, ref uint item) =>
        {
            persister.PersistUInt32Value(ref item);
        });

        reader.PersistBitArray(ref _currentArmorSetFlags);
    }
}

// R7 Body ModuleData audit (design-module-api §2.2, S5 vocabulary): every health/subdual
// quantity is now a parse-time-quantized Fix64; no float field remains. [SimDataAudited] marks
// the conversion. This class is NOT [ParseOnly] - it has had a runtime module since before the
// migration (pilot delta D-11 pattern). ActiveBody itself is the recorded D-7 float boundary
// (its Health/MaxHealth/EstimateDamage display views are mandated by the abstract BodyModule
// contract), so the file is deliberately not [SimState]; the sim-state arithmetic it owns lives
// in the [SimState] BodyDamageCore.
[SimDataAudited]
public class ActiveBodyModuleData : BodyModuleData
{
    internal static ActiveBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);
        return result;
    }

    /// <summary>
    /// BFME and later allow InitialHealth to be omitted, in which case it defaults to
    /// MaxHealth. This lives here (not in the parse table) so subclasses whose own
    /// <c>Parse</c> shadows the base can re-apply it after their <c>ParseBlock</c> call -
    /// otherwise the subclass body would spawn at 0 health (finding F-HB-1).
    /// </summary>
    private protected void ApplyHealthDefaults(IniParser parser)
    {
        if (parser.SageGame >= SageGame.Bfme && !_initialHealthSet)
        {
            InitialHealth = MaxHealth;
        }
    }

    internal static readonly IniParseTable<ActiveBodyModuleData> FieldParseTable = new()
    {
        { "MaxHealth", (parser, x) => x.MaxHealth = parser.ParseFix64() },
        { "InitialHealth", (parser, x) => { x.InitialHealth = parser.ParseFix64(); x._initialHealthSet = true; } },
        { "MaxHealthDamaged", (parser, x) => x.MaxHealthDamaged = parser.ParseFix64() },
        { "MaxHealthReallyDamaged", (parser, x) => x.MaxHealthReallyDamaged = parser.ParseFix64() },
        { "RecoveryTime", (parser, x) => x.RecoveryTime = parser.ParseFix64() },

        { "SubdualDamageCap", (parser, x) => x.SubdualDamageCap = parser.ParseFix64() },
        { "SubdualDamageHealRate", (parser, x) => x.SubdualDamageHealRate = parser.ParseTimeMillisecondsToLogicFrames() },
        { "SubdualDamageHealAmount", (parser, x) => x.SubdualDamageHealAmount = parser.ParseFix64() },
        { "GrabObject", (parser, x) => x.GrabObject = parser.ParseAssetReference() },
        { "GrabOffset", (parser, x) => x.GrabOffset = parser.ParsePoint() },
        { "DamageCreationList", (parser, x) => x.DamageCreationLists.Add(DamageCreationList.Parse(parser)) },
        { "GrabFX", (parser, x) => x.GrabFX = parser.ParseAssetReference() },
        { "GrabDamage", (parser, x) => x.GrabDamage = parser.ParseInteger() },
        { "CheerRadius", (parser, x) => x.CheerRadius = parser.ParseInteger() },
        { "DodgePercent", (parser, x) => x.DodgePercent = parser.ParseFix64Percentage() },
        { "UseDefaultDamageSettings", (parser, x) => x.UseDefaultDamageSettings = parser.ParseBoolean() },
        { "EnteringDamagedTransitionTime", (parser, x) => x.EnteringDamagedTransitionTime = parser.ParseInteger() },
        { "HealingBuffFx", (parser, x) => x.HealingBuffFx = parser.ParseAssetReference() },
        { "BurningDeathBehavior", (parser, x) => x.BurningDeathBehavior = parser.ParseBoolean() },
        { "BurningDeathFX", (parser, x) => x.BurningDeathFX = parser.ParseAssetReference() },
        { "DamagedAttributeModifier", (parser, x) => x.DamagedAttributeModifier = parser.ParseAssetReference() },
        { "ReallyDamagedAttributeModifier", (parser, x) => x.ReallyDamagedAttributeModifier = parser.ParseAssetReference() }
    };

    private bool _initialHealthSet;

    /// <summary>Max hit points (quantized Q31.32 at parse, S5).</summary>
    public Fix64 MaxHealth { get; internal set; }

    /// <summary>Starting hit points; BFME+ defaults it to <see cref="MaxHealth"/> when omitted.</summary>
    public Fix64 InitialHealth { get; internal set; }

    /// <summary>Subdual damage needed to subdue (quantized Q31.32); zero = cannot be subdued.</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public Fix64 SubdualDamageCap { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public LogicFrameSpan SubdualDamageHealRate { get; private set; }

    /// <summary>Subdual hit points healed per heal tick (quantized Q31.32).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public Fix64 SubdualDamageHealAmount { get; private set; }

    /// <summary>BFME per-damage-state max health (quantized Q31.32; parsed, not yet consumed - finding).</summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 MaxHealthDamaged { get; private set; }

    /// <summary>BFME recovery time; parsed as a bare Fix64, units unpinned and unconsumed (finding).</summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 RecoveryTime { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string? GrabObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public Point2D GrabOffset { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public List<DamageCreationList> DamageCreationLists { get; private set; } = [];

    /// <summary>BFME per-damage-state max health (quantized Q31.32; parsed, not yet consumed - finding).</summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 MaxHealthReallyDamaged { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string? GrabFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int GrabDamage { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int CheerRadius { get; private set; }

    /// <summary>Dodge probability (exact Fix64 percentage; parsed, not yet consumed - finding).</summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 DodgePercent { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool UseDefaultDamageSettings { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int EnteringDamagedTransitionTime { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string? HealingBuffFx { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool BurningDeathBehavior { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string? BurningDeathFX { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string? DamagedAttributeModifier { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string? ReallyDamagedAttributeModifier { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ActiveBody(gameObject, gameEngine, this);
    }
}

[AddedIn(SageGame.Bfme)]
public sealed class DamageCreationList
{
    internal static DamageCreationList Parse(IniParser parser)
    {
        return new DamageCreationList()
        {
            Object = parser.ParseAssetReference(),
            ObjectKind = parser.ParseEnum<ObjectKinds>(),
            Unknown = parser.ParseString()
        };
    }

    public string? Object { get; private set; }
    public ObjectKinds ObjectKind { get; private set; }
    public string? Unknown { get; private set; }
}
