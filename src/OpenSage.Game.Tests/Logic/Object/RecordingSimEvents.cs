// A recording ISimEvents sink for module ports whose observable effect IS an event.
//
// ISimEvents is fire-and-forget by design (S8: outputs, never sim inputs), which would make
// "the module fired the right FX" untestable if the only sink were the real client one. This
// sink is installed on the headless host's SimContext for the duration of a test and records
// what the sim asked for, in the order it asked - the [create -> trigger death -> observable
// effect] triple of the Die batch's definition of done, for classes whose effect is an event.
//
// It records requests, not renderings: nothing here asserts anything about the client.

using System.Collections.Generic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;

namespace OpenSage.Tests.Logic.Object;

/// <summary>How an FX request was oriented (which ISimEvents overload the module chose).</summary>
internal enum FXOrientation
{
    /// <summary>Oriented to the subject object (the original's doFXObj).</summary>
    ToObject,

    /// <summary>Position only, identity rotation (the original's doFXPos).</summary>
    PositionOnly,
}

internal readonly record struct RecordedFX(
    string FXListName,
    ObjectId ObjectId,
    ObjectId SourceObjectId,
    FXOrientation Orientation);

internal readonly record struct RecordedParticleSystem(
    string ParticleSystemName,
    ObjectId ObjectId,
    string Bone,
    bool RandomBone);

internal sealed class RecordingSimEvents : ISimEvents
{
    public List<RecordedFX> Events { get; } = new();

    public void FireFXAtObject(string fxListName, ObjectId objectId) =>
        Events.Add(new RecordedFX(fxListName, objectId, ObjectId.Invalid, FXOrientation.ToObject));

    public void FireFXAtObject(string fxListName, ObjectId objectId, ObjectId sourceObjectId) =>
        Events.Add(new RecordedFX(fxListName, objectId, sourceObjectId, FXOrientation.ToObject));

    public void FireFXAtObjectPosition(string fxListName, ObjectId objectId) =>
        Events.Add(new RecordedFX(fxListName, objectId, ObjectId.Invalid, FXOrientation.PositionOnly));

    /// <summary>Unit-sound requests, in order (e.g. EjectPilotDie's VoiceEject).</summary>
    public List<(string SoundKey, ObjectId ObjectId)> Sounds { get; } = new();

    public void FireUnitSoundAtObject(string unitSpecificSoundKey, ObjectId objectId) =>
        Sounds.Add((unitSpecificSoundKey, objectId));

    /// <summary>Literal AudioEvent requests, in order (HordeSiegeEngineContain's EnterSound/ExitSound, R12).</summary>
    public List<(string AudioEventName, ObjectId ObjectId)> AudioEvents { get; } = new();

    public void FireAudioEventAtObject(string audioEventName, ObjectId objectId) =>
        AudioEvents.Add((audioEventName, objectId));

    /// <summary>Free-unit crate-pickup sting requests, in order (UnitCrateCollide, R12).</summary>
    public int CrateFreeUnitPickupSoundCount { get; private set; }

    public void FireCrateFreeUnitPickupSound() => CrateFreeUnitPickupSoundCount++;

    /// <summary>Attached-particle-system requests, in order (TransitionDamageFX).</summary>
    public List<RecordedParticleSystem> ParticleSystems { get; } = new();

    public void FireParticleSystemAtObject(string particleSystemName, ObjectId objectId, string bone, bool randomBone) =>
        ParticleSystems.Add(new RecordedParticleSystem(particleSystemName, objectId, bone, randomBone));

    /// <summary>Destroy-attached-particles requests, in order (HeightDieUpdate, R12).</summary>
    public List<ObjectId> DestroyedAttachedParticleSystemsFor { get; } = new();

    public void DestroyAttachedParticleSystems(ObjectId objectId) =>
        DestroyedAttachedParticleSystemsFor.Add(objectId);

    /// <summary>Installs a fresh recorder on the headless host's context and returns it.</summary>
    public static RecordingSimEvents InstallOn(HeadlessSimGame game)
    {
        var recorder = new RecordingSimEvents();
        ((SimContext)game.GameEngine.SimContext).SetEventSink(recorder);
        return recorder;
    }
}
