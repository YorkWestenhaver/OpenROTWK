using System;
using System.Numerics;
using ImGuiNET;
using OpenSage.Content;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public class SpecialPowerModule : BehaviorModule, IUpgradableScienceModule
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The next frame when this special power can be used
    /// </summary>
    private LogicFrame _availableAtFrame;
    private bool _paused;
    private LogicFrame _countdownEndFrame; // unclear what this is
    private float _unknownFloat;

    private readonly LogicFrameSpan _reloadFrames;

    /// <summary>
    /// Whether the special power is ready to be used.
    /// </summary>
    /// <remarks>
    /// This intentionally doesn't call <see cref="_ready"/>, as that is used to short-circuit and is lazily set by <see cref="ReadyProgress"/>.
    /// </remarks>
    public bool Ready => ReadyProgress() >= 1;
    private bool _ready;

    /// <summary>
    /// The resolved <see cref="SpecialPower"/> asset for <see cref="SpecialPowerModuleData.SpecialPower"/>,
    /// resolved once here (instead of re-dereferencing the lazy reference on every access) so a data set
    /// missing/renaming the referenced SpecialPowerTemplate is diagnosed exactly once. Null when the
    /// reference is absent or fails to resolve; see <see cref="_disabled"/>.
    /// </summary>
    private readonly SpecialPower _specialPower;

    /// <summary>
    /// True when <see cref="_specialPower"/> failed to resolve. Every public/internal surface below
    /// short-circuits to a safe no-op or default value while this is set, mirroring the guard
    /// <see cref="SpawnBehavior"/> uses for its own unresolved SpawnTemplateName reference.
    /// </summary>
    private readonly bool _disabled;

    private bool _unlocked;

    public SpecialPowerType SpecialPowerType => _disabled ? default : _specialPower.Type;

    internal SpecialPowerModule(GameObject gameObject, IGameEngine gameEngine, SpecialPowerModuleData moduleData) : base(gameObject, gameEngine)
    {
        _specialPower = moduleData.SpecialPower?.Value;

        if (_specialPower == null)
        {
            _disabled = true;
            Logger.Warn($"SpecialPowerModule on '{GameObject.Definition.Name}' could not resolve its SpecialPowerTemplate; special power disabled.");
            return;
        }

        _reloadFrames = _specialPower.ReloadTime;
        _paused = moduleData.StartsPaused;
        if (!_specialPower.SharedSyncedTimer)
        {
            // items with a sharedsyncedtimer have their countdown set to 0 when not unlocked
            ResetCountdown();
        }
    }

    /// <summary>
    /// How close the special power is to being ready, from 0 (not ready at all) to 1 (fully ready).
    /// </summary>
    public float ReadyProgress()
    {
        if (_disabled)
        {
            return 0;
        }

        if (_paused)
        {
            return 0;
        }

        if (_reloadFrames == LogicFrameSpan.Zero)
        {
            _ready = true;
        }

        // quick short-circuit
        if (_ready)
        {
            return 1;
        }

        var availableAtFrame = _availableAtFrame;
        if (_specialPower.SharedSyncedTimer)
        {
            if (GameObject.Owner.SyncedSpecialPowerTimers.TryGetValue(_specialPower.Type, out var frame))
            {
                availableAtFrame = frame;
            }
            else
            {
                return 0; // if it's shared and not in our dictionary, then we probably don't have it unlocked and therefore it's not ready
            }
        }

        var progress = 1 - Math.Clamp((availableAtFrame.Value - Math.Min(availableAtFrame.Value, GameEngine.GameLogic.CurrentFrame.Value)) / (float)_reloadFrames.Value, 0, 1);
        _ready = progress >= 1;
        return progress;
    }

    public virtual void TryUpgrade(Science purchasedScience)
    {
        if (_disabled)
        {
            return;
        }

        if (!_unlocked)
        {
            // the GLA cash bounty is actually awarded immediately upon a CC scaffold being placed - I suspect this is due to the reload time being zero
            if (GameObject.IsBeingConstructed() && _specialPower.ReloadTime != LogicFrameSpan.Zero)
            {
                return; // nothing for us to do if we're not even built yet
            }

            foreach (var requiredScience in _specialPower.RequiredSciences)
            {
                if (!GameObject.Owner.HasScience(requiredScience.Value))
                {
                    return;
                }
            }

            Unlock();
        }
    }

    private void Unlock()
    {
        _unlocked = true;

        if (_specialPower.PublicTimer)
        {
            return; // this is handled by SpecialPowerCreate
        }

        _availableAtFrame = GameEngine.GameLogic.CurrentFrame;
        if (_specialPower.SharedSyncedTimer)
        {
            var player = GameObject.Owner;
            player.SyncedSpecialPowerTimers.TryAdd(_specialPower.Type, _availableAtFrame); // it shouldn't already be added, but this way we don't worry about it
        }
    }

    public void Unpause()
    {
        if (_disabled)
        {
            return;
        }

        _paused = false;
        ResetCountdown();
    }

    public void ResetCountdown()
    {
        if (_disabled)
        {
            return;
        }

        _availableAtFrame = GameEngine.GameLogic.CurrentFrame + _specialPower.ReloadTime;
        _ready = false;

        if (_specialPower.SharedSyncedTimer)
        {
            GameObject.Owner.SyncedSpecialPowerTimers[_specialPower.Type] = _availableAtFrame;
        }
    }

    internal virtual void Activate(Vector3 position)
    {
        if (_disabled)
        {
            return;
        }

        GameEngine.AudioSystem.PlayAudioEvent(_specialPower.InitiateSound?.Value);
        GameEngine.AudioSystem.PlayAudioEvent(position, _specialPower.InitiateAtLocationSound?.Value);
        ResetCountdown();
    }

    public bool Matches(SpecialPower specialPower)
    {
        if (_disabled)
        {
            return false;
        }

        return _specialPower == specialPower;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistLogicFrame(ref _availableAtFrame);

        reader.PersistBoolean(ref _paused);
        reader.SkipUnknownBytes(3);

        reader.PersistLogicFrame(ref _countdownEndFrame);
        reader.PersistSingle(ref _unknownFloat);
    }

    internal override void DrawInspector()
    {
        base.DrawInspector();
        ImGui.LabelText("Available at frame", _availableAtFrame.ToString());
        ImGui.LabelText("Progress", $"{ReadyProgress():P2}%");
    }
}

public class SpecialPowerModuleData : BehaviorModuleData
{
    public override ModuleKinds ModuleKinds => ModuleKinds.SpecialPower;

    internal static SpecialPowerModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<SpecialPowerModuleData> FieldParseTable = new IniParseTable<SpecialPowerModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPower = parser.ParseSpecialPowerReference() },
        { "StartsPaused", (parser, x) => x.StartsPaused = parser.ParseBoolean() },
        { "UpdateModuleStartsAttack", (parser, x) => x.UpdateModuleStartsAttack = parser.ParseBoolean() },
        { "InitiateSound", (parser, x) => x.InitiateSound = parser.ParseAssetReference() },
        { "InitiateSound2", (parser, x) => x.InitiateSound2 = parser.ParseAssetReference() },
        { "AttributeModifier", (parser, x) => x.AttributeModifier = parser.ParseAssetReference() },
        { "AttributeModifierAffectsSelf", (parser, x) => x.AttributeModifierAffectsSelf = parser.ParseBoolean() },
        { "InitiateFX", (parser, x) => x.InitiateFX = parser.ParseAssetReference() },
        { "AntiCategory", (parser, x) => x.AntiCategory = parser.ParseEnum<ModifierCategory>() },
        { "AttributeModifierRange", (parser, x) => x.AttributeModifierRange = parser.ParseFloat() },
        { "AttributeModifierFX", (parser, x) => x.AttributeModifierFX = parser.ParseAssetReference() },
        { "TriggerFX", (parser, x) => x.TriggerFX = parser.ParseAssetReference() },
        { "SetModelCondition", (parser, x) => x.SetModelCondition = parser.ParseAttributeEnum<ModelConditionFlag>("ModelConditionState") },
        { "SetModelConditionTime", (parser, x) => x.SetModelConditionTime = parser.ParseFloat() },
        { "AttributeModifierAffects", (parser, x) => x.AttributeModifierAffects = ObjectFilter.Parse(parser) },
        { "AvailableAtStart", (parser, x) => x.AvailableAtStart = parser.ParseBoolean() },
        { "TargetAllSides", (parser, x) => x.TargetAllSides = parser.ParseBoolean() },
        { "AffectAllies", (parser, x) => x.AffectAllies = parser.ParseBoolean() },
        { "AttributeModifierWeatherBased", (parser, x) => x.AttributeModifierWeatherBased = parser.ParseBoolean() },
        { "TargetEnemy", (parser, x) => x.TargetEnemy = parser.ParseBoolean() },
        { "OnTriggerRechargeSpecialPower", (parser, x) => x.OnTriggerRechargeSpecialPower = parser.ParseAssetReference() },
        { "DisableDuringAnimDuration", (parser, x) => x.DisableDuringAnimDuration = parser.ParseBoolean() },
        { "RequirementsFilterMPSkirmish", (parser, x) => x.RequirementsFilterMPSkirmish = ObjectFilter.Parse(parser) },
        { "RequirementsFilterStrategic", (parser, x) => x.RequirementsFilterStrategic = ObjectFilter.Parse(parser) }
    };

    public LazyAssetReference<SpecialPower> SpecialPower { get; private set; }
    public bool StartsPaused { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool UpdateModuleStartsAttack { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string InitiateSound { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string AttributeModifier { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool AttributeModifierAffectsSelf { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string InitiateFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public ModifierCategory AntiCategory { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float AttributeModifierRange { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string AttributeModifierFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string InitiateSound2 { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string TriggerFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public ModelConditionFlag SetModelCondition { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float SetModelConditionTime { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public ObjectFilter AttributeModifierAffects { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool TargetAllSides { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool AvailableAtStart { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool AffectAllies { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool AttributeModifierWeatherBased { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool TargetEnemy { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string OnTriggerRechargeSpecialPower { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool DisableDuringAnimDuration { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter RequirementsFilterMPSkirmish { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter RequirementsFilterStrategic { get; private set; }

    internal override SpecialPowerModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpecialPowerModule(gameObject, gameEngine, this);
    }
}
