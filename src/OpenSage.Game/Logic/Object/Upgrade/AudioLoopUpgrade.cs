// AudioLoopUpgrade - parked runtime port (R12). Audio-only upgrade module: on trigger the
// retail module starts a looping sound named by SoundToPlay, stopping it either when the
// owning object dies (KillOnDeath) or after KillAfterMS milliseconds (0/unset = indefinite,
// runs until the object dies or the upgrade's own lifetime ends).
//
// Audio is deliberately absent from ISimContext forever (see ISimContext.cs S8: "Deliberately
// absent, forever: audio, rendering, UI, ..."), and there is no per-module die-notification
// seam outside the DieModule category that a plain UpgradeModule could hook to learn about
// KillOnDeath. That means every observable effect of this module - starting the loop, the
// KillOnDeath teardown, the KillAfterMS timeout - is client-side and out of reach of
// [SimState] code today, exactly like LargeGroupAudioUpdate (R11 Track B; see that module's
// header for the same shape of argument). This is a permanently-parked module: it exists so
// authored objects carry a live module (module indexing, module counts, upgrade-mux
// participation) instead of a [ParseOnly] hole. The upgrade-trigger/prerequisite/conflict
// bookkeeping (shared UpgradeLogic mux) IS real and IS exercised: only the audio side effect
// is parked.
//
// TODO-spec (unverified, the whole audio behavior): looping-sound start/stop and the
// KillOnDeath/KillAfterMS teardown paths belong behind an audio-capable ISimContext member
// (and, for KillOnDeath, a die-notification seam) when either exists.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AudioLoopUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly AudioLoopUpgradeModuleData _moduleData;
    private readonly UpgradeLogic _upgradeLogic;

    /// <summary>
    /// Whether the upgrade mux has fired (matches <c>UpgradeModule.Triggered</c>'s
    /// accessibility for the same reason: test/inspector visibility only - no other module
    /// reads a sibling's trigger state through this door).
    /// </summary>
    internal bool Triggered => _upgradeLogic.Triggered;

    public AudioLoopUpgrade(GameObject gameObject, ISimContext context, AudioLoopUpgradeModuleData moduleData)
        : base(gameObject, context)
    {
        _moduleData = moduleData;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL: an
        // initially-active upgrade's audio loop starts immediately, same as every other
        // UpgradeLogic-driven module in this codebase).
        _upgradeLogic = new UpgradeLogic(moduleData.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// GPL upgradeImplementation(): start the looping SoundToPlay audio handle, tracked for
    /// later KillOnDeath/KillAfterMS teardown. No-op here: the audio handle, its death-hook,
    /// and its timeout all live client-side (S8) - see the module header. The parsed fields
    /// (<see cref="AudioLoopUpgradeModuleData.SoundToPlay"/>,
    /// <see cref="AudioLoopUpgradeModuleData.KillOnDeath"/>,
    /// <see cref="AudioLoopUpgradeModuleData.KillAfterMS"/>) are preserved on
    /// <see cref="_moduleData"/> for the future audio host to read once one exists.
    /// </summary>
    private void OnUpgradeTriggered()
    {
        // Intentionally empty: see module header.
    }

    // ---- the single walk: the trigger flag is the only mutable sim state. The audio mix
    // itself carries no determinism obligation (S8) even once a host exists, so it will never
    // belong in this Xfer walk. ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class AudioLoopUpgradeModuleData : UpgradeModuleData
{
    internal static AudioLoopUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<AudioLoopUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<AudioLoopUpgradeModuleData>()
        {
            { "SoundToPlay", (parser, x) => x.SoundToPlay = parser.ParseAssetReference() },
            { "KillOnDeath", (parser, x) => x.KillOnDeath = parser.ParseBoolean() },
            { "KillAfterMS", (parser, x) => x.KillAfterMS = parser.ParseInteger() }
        });

    public string SoundToPlay { get; private set; }
    public bool KillOnDeath { get; private set; }
    public int KillAfterMS { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AudioLoopUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
