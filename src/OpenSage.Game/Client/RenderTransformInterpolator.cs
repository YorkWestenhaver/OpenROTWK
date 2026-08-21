// R14 packet 6 (workbench research/design-sim-presentation-bridge.md, §1.6 and §2 packet 6):
// render interpolation - the presentation-side consumer of the tickT fraction that Game.cs
// has been computing and throwing away.
//
// THE DEFECT. Logic runs at 5 Hz for the whole BFME family (SageGame.MsPerLogicFrame = 200,
// which is also SimLoop.LogicFramesPerSecond), but the renderer draws at display rate. W3D
// animations already advance at render rate - Drawable.BuildRenderList calls
// drawModule.Update(gameTime) with render-rate time - while object transforms only ever
// changed 5x/second. The result is animated models teleporting on a 200 ms grid.
// Game.LocalLogicTick already computes tickT (the 0..1 fraction of the way from the last
// logic frame to the next) and threads it through IScene3D.LocalLogicTick and
// Scene3D.LocalLogicTick down to GameObject.LocalLogicTick, which discarded it. Repo-wide it
// had no consumer at all. This type is that consumer.
//
// PRESENTATION ONLY. Floats are legal here by construction: this is not a [SimState] type, it
// lives outside every SimCoreScopedDirs entry, it never writes sim state, and it holds only
// copies of a transform the sim already owns. The crossing is read-side, which is the
// sanctioned direction (design §1.5: "the render side is already read-only over sim state").
// Nothing here may ever feed back into the sim - an interpolated pose is a lie about where
// the object is, true only for the eye. Feeding one to a collider, a weapon range check or a
// CRC channel would desync a network game.
//
// INTERPOLATE, DO NOT EXTRAPOLATE. Lerping from the PREVIOUS sample to the CURRENT one means
// the picture lags the sim by exactly one logic frame (200 ms). That is inherent to
// interpolation and it is the right trade for an RTS: extrapolating forward from a 5 Hz
// sample overshoots every stop, collision and turn, and the correction reads as rubber-
// banding. Uniform latency is invisible; rubber-banding is not.
//
// WHAT THIS DOES NOT FIX. Only the object transform is interpolated. Anything else the sim
// steps at 5 Hz (animation state changes, model-condition flips, particle emitter spawns)
// still lands on the frame boundary, which is correct - those are discrete events, not
// continuous motion.

// LogicFrame comes from the project-wide global using alias (GlobalUsings.cs) onto
// OpenSage.SimCore.Ticking.LogicFrame.
using System;
using System.Numerics;
using OpenSage.Terrain;

namespace OpenSage.Client;

/// <summary>
/// Holds the previous and current sim transform of one renderable and produces the
/// interpolated pose the renderer should draw this render frame.
/// </summary>
/// <remarks>
/// Presentation-only state. Deliberately NOT persisted: a save/load restores the sim transform
/// and re-zeros the logic clock, which <see cref="Observe"/> reads as a discontinuity and snaps
/// across, so the interpolator re-primes itself with no seam of its own.
/// </remarks>
public sealed class RenderTransformInterpolator
{
    /// <summary>
    /// Translation delta, in world units, beyond which one logic frame is treated as a
    /// teleport and snapped instead of interpolated.
    /// </summary>
    /// <remarks>
    /// Ten terrain tiles (<see cref="HeightMap.HorizontalScale"/> world units each) inside a
    /// single 200 ms frame is 500 world units/second, which no locomotor produces. Anything
    /// at or above it is a discontinuity - a respawn, a map-edge wrap, a script-driven
    /// reposition - and lerping across it would smear the model over half the map for a fifth
    /// of a second. Below it, motion is interpolated.
    /// </remarks>
    public const float SnapDistance = 10 * HeightMap.HorizontalScale;

    private const float SnapDistanceSquared = SnapDistance * SnapDistance;

    private Vector3 _previousTranslation;
    private Quaternion _previousRotation;
    private float _previousScale;

    private Vector3 _currentTranslation;
    private Quaternion _currentRotation;
    private float _currentScale;

    private LogicFrame _sampledFrame;
    private float _tickT;

    private Matrix4x4 _matrix;
    private bool _isMatrixDirty = true;

    /// <summary>
    /// False until the first <see cref="Observe"/> call. Callers must fall back to the raw
    /// sim transform while this is false: an object created inside a logic frame can be
    /// rendered before the presentation path has ever seen it.
    /// </summary>
    public bool HasSample { get; private set; }

    /// <summary>The logic frame the current sample was taken on.</summary>
    public LogicFrame SampledFrame => _sampledFrame;

    /// <summary>The clamped 0..1 fraction last handed to <see cref="Observe"/>.</summary>
    public float TickT => _tickT;

    /// <summary>Interpolated translation. Meaningless while <see cref="HasSample"/> is false.</summary>
    public Vector3 Translation => Vector3.Lerp(_previousTranslation, _currentTranslation, _tickT);

    /// <summary>Interpolated rotation. Meaningless while <see cref="HasSample"/> is false.</summary>
    public Quaternion Rotation => Quaternion.Slerp(_previousRotation, _currentRotation, _tickT);

    /// <summary>Interpolated scale. Meaningless while <see cref="HasSample"/> is false.</summary>
    public float Scale => _previousScale + ((_currentScale - _previousScale) * _tickT);

    /// <summary>
    /// The interpolated local-to-world matrix, composed the same way
    /// <see cref="Transform.Matrix"/> composes it (scale, then rotation, then translation) so
    /// it is a drop-in replacement for it on the render path.
    /// </summary>
    public Matrix4x4 Matrix
    {
        get
        {
            if (_isMatrixDirty)
            {
                _matrix =
                    Matrix4x4.CreateScale(Scale) *
                    Matrix4x4.CreateFromQuaternion(Rotation) *
                    Matrix4x4.CreateTranslation(Translation);
                _isMatrixDirty = false;
            }
            return _matrix;
        }
    }

    /// <summary>
    /// Called once per RENDER frame from the presentation path, with the sim transform as it
    /// stands right now and the render-frame's <paramref name="tickT"/>. Advances the
    /// previous/current pair only when the logic frame has actually changed, so calling it
    /// many times inside one logic frame is correct and cheap.
    /// </summary>
    /// <param name="logicFrame">The logic frame <paramref name="transform"/> was produced by.</param>
    /// <param name="transform">The object's sim transform. Read only; never written.</param>
    /// <param name="tickT">
    /// Fraction of the way from the last logic frame to the next, clamped to 0..1 here because
    /// the producer's clamp only guards one side (Game.LocalLogicTick clamps the overdue case
    /// to 1 but not a paused or rewound clock).
    /// </param>
    public void Observe(LogicFrame logicFrame, Transform transform, float tickT)
    {
        ArgumentNullException.ThrowIfNull(transform);

        if (!HasSample)
        {
            // First sight of this object: prime both poses so it does not lerp in from the
            // world origin on its first render frame.
            Snap(transform);
            _sampledFrame = logicFrame;
            HasSample = true;
        }
        else if (logicFrame.Value != _sampledFrame.Value)
        {
            // Contiguity matters as much as distance. A gap means the object was not observed
            // last frame (it was created, or the presentation path did not run), and a
            // backwards step means the logic clock was re-zeroed by GameLogic.Reset(); in both
            // cases the retained "previous" pose is stale and lerping from it is a smear.
            var contiguous = logicFrame.Value == _sampledFrame.Value + 1;
            var teleported =
                Vector3.DistanceSquared(_currentTranslation, transform.Translation) >= SnapDistanceSquared;

            if (contiguous && !teleported)
            {
                _previousTranslation = _currentTranslation;
                _previousRotation = _currentRotation;
                _previousScale = _currentScale;

                _currentTranslation = transform.Translation;
                _currentRotation = transform.Rotation;
                _currentScale = transform.Scale;
            }
            else
            {
                Snap(transform);
            }

            _sampledFrame = logicFrame;
            _isMatrixDirty = true;
        }

        // A non-finite fraction degrades to "show the newest pose" - i.e. exactly the 5 Hz snap
        // this class removes - rather than to NaN, which would propagate into the matrix and
        // make the object vanish. LogicUpdateScaleFactor is developer-settable and divides into
        // LogicUpdateInterval, so the degenerate input is reachable, and an invisible army is a
        // far worse failure than a stuttering one.
        var clamped = float.IsFinite(tickT) ? Math.Clamp(tickT, 0f, 1f) : 1f;
        if (clamped != _tickT)
        {
            _tickT = clamped;
            _isMatrixDirty = true;
        }
    }

    // No public Reset(): every discontinuity a caller could want to reset across is already
    // caught by Observe's own guards - a re-zeroed logic clock (GameLogic.Reset) reads as a
    // backwards frame step, a re-created object starts with HasSample false, and an in-place
    // reposition trips SnapDistance. An explicit reset seam with no caller would just be a
    // second, untested way to say the same thing.
    private void Snap(Transform transform)
    {
        _previousTranslation = _currentTranslation = transform.Translation;
        _previousRotation = _currentRotation = transform.Rotation;
        _previousScale = _currentScale = transform.Scale;
        _isMatrixDirty = true;
    }
}
