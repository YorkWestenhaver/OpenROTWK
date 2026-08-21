// Castles / build-plots deterministic core (R9 system task, build-roadmap pillar castles).
//
// Behavioral reference: bfme2-workbench/research/spec-castles.md (clean-room behavioral
// spec; this system has NO GPL reference, and no decompiled code was transplanted - facts and
// cited constants only). The pure pieces of CastleBehavior live here so they are testable
// without a game host and analyzer-walled ([SimState]):
//
//   - the recovered state machine values (spec §4: 0=packed, 1=unpack-initiated,
//     4=unpacked-instant, 5=packing/dying; 2/3 unobserved - reserved, open question Q1);
//   - the capture-scan tally (spec §5.4): a per-player integer score over the
//     partition query, 20 player slots, weight w = 2 for "real" units else 1, plus a
//     per-template capture bonus * w (the template feed is unrecovered - Q6, default 0);
//     enemy presence blocks capture; after frame 5 an empty scan reverts ownership to the
//     civilian player (spec Q3: retail reverts to PlyrCivilian, NOT the spawn owner);
//   - the critter-scare geometry (spec §5.8, constant 150.0), implemented as a pure
//     Fix64 function; the pathing hookup is deliberately deferred (finding F-CAS-6).
//
// All math is Fix64/int; timers are frame-quantized at the 5 Hz logic rate (spec §6:
// "m_timer is float seconds in retail; quantize to frames").

using System;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object.Castle;

/// <summary>
/// CastleBehavior runtime state values, numbered exactly as observed in the retail binary
/// (spec-castles §4, instance +0x34). States 2/3 were unobserved in the decompiles
/// (open question Q1) and are reserved here, never entered.
/// </summary>
public enum CastleState
{
    /// <summary>Foundation visible, capturable (retail 0).</summary>
    Packed = 0,

    /// <summary>Unpack initiated, delayed/normal build path (retail 1, spec-castles.md).</summary>
    UnpackInitiated = 1,

    /// <summary>Reserved: likely "under construction" (unobserved, Q1).</summary>
    ReservedUnderConstruction = 2,

    /// <summary>Reserved: likely "constructed" (unobserved, Q1).</summary>
    ReservedConstructed = 3,

    /// <summary>Unpacked via the instant branch (retail 4; status bit 0x4000000 set).</summary>
    Unpacked = 4,

    /// <summary>Packing/dying (retail 5, spec-castles.md).</summary>
    Packing = 5,
}

/// <summary>
/// One nearby object considered by the ownership-capture scan, pre-extracted so the tally
/// itself is a pure function over sim-safe data.
/// </summary>
public readonly struct CaptureCandidate
{
    /// <summary>Match roster index of the candidate's owner.</summary>
    public readonly int PlayerIndex;

    /// <summary>
    /// True for a "real" unit (weight 2), false otherwise (weight 1). Retail keys this off
    /// an internal object flag (spec §5.4); the exact predicate is unrecovered, so our pin is
    /// "mobile, selectable non-structure" - recorded as finding F-CAS-4.
    /// </summary>
    public readonly bool IsRealUnit;

    /// <summary>
    /// Per-template capture-weight bonus (retail per-template field). The INI field feeding it is
    /// unrecovered (Q6); callers pass 0 until a VM memprobe pins it.
    /// </summary>
    public readonly int TemplateCaptureBonus;

    /// <summary>True when the candidate's owner has an enemy relationship with the castle's current owner.</summary>
    public readonly bool IsEnemyOfCurrentOwner;

    public CaptureCandidate(int playerIndex, bool isRealUnit, int templateCaptureBonus, bool isEnemyOfCurrentOwner)
    {
        PlayerIndex = playerIndex;
        IsRealUnit = isRealUnit;
        TemplateCaptureBonus = templateCaptureBonus;
        IsEnemyOfCurrentOwner = isEnemyOfCurrentOwner;
    }
}

/// <summary>Result of one capture-scan tally.</summary>
public readonly struct CaptureScanResult
{
    /// <summary>Winning player index, or -1 when the scan saw nobody.</summary>
    public readonly int WinnerPlayerIndex;

    /// <summary>True when an enemy (of the current owner) was present - blocks capture.</summary>
    public readonly bool EnemyContest;

    public CaptureScanResult(int winnerPlayerIndex, bool enemyContest)
    {
        WinnerPlayerIndex = winnerPlayerIndex;
        EnemyContest = enemyContest;
    }

    public bool AnyCandidates => WinnerPlayerIndex >= 0;
}

public static class CastleCaptureScan
{
    /// <summary>Retail's fixed per-scan player tally array size (spec-castles.md).</summary>
    public const int PlayerSlotCount = 20;

    /// <summary>
    /// Frames of grace before an empty scan reverts ownership to the civilian player
    /// (retail: "frame &gt; 5", spec-castles.md).
    /// </summary>
    public const uint CivilianRevertGraceFrames = 5;

    /// <summary>
    /// The retail tally (spec §5.4): score[p] += w + bonus*w with w = 2 for real units else 1.
    /// Winner is the max score; on a tie the LOWEST player index wins (deterministic pin -
    /// retail's tie order is the scan's slot order, unrecovered; finding F-CAS-5).
    /// </summary>
    public static CaptureScanResult Tally(ReadOnlySpan<CaptureCandidate> candidates)
    {
        Span<long> scores = stackalloc long[PlayerSlotCount];
        var enemyContest = false;
        var any = false;

        foreach (ref readonly var candidate in candidates)
        {
            if (candidate.IsEnemyOfCurrentOwner)
            {
                enemyContest = true;
            }

            if (candidate.PlayerIndex < 0 || candidate.PlayerIndex >= PlayerSlotCount)
            {
                continue;
            }

            var w = candidate.IsRealUnit ? 2 : 1;
            scores[candidate.PlayerIndex] += w + (long)candidate.TemplateCaptureBonus * w;
            any = true;
        }

        if (!any)
        {
            return new CaptureScanResult(-1, enemyContest);
        }

        var winner = -1;
        long best = long.MinValue;
        for (var p = 0; p < PlayerSlotCount; p++)
        {
            if (scores[p] > 0 && scores[p] > best)
            {
                best = scores[p];
                winner = p;
            }
        }

        return new CaptureScanResult(winner, enemyContest);
    }
}

public static class CastleMath
{
    /// <summary>Critter scare offset distance, retail constant 150.0f (spec §5.8).</summary>
    public static readonly Fix64 CritterScareDistance = new(150);

    /// <summary>
    /// The critter-scare target (spec §5.8): scared animals path to
    /// animalPos + normalize(animalPos - keepPos) * 150. Pure Fix64 geometry; the caller owns
    /// wiring it into pathing (deferred, finding F-CAS-6). A zero direction (animal exactly at
    /// the keep) returns the animal position unchanged.
    /// </summary>
    public static FixVector3 ComputeCritterScareTarget(in FixVector3 animalPosition, in FixVector3 keepPosition)
    {
        var direction = (animalPosition - keepPosition).NormalizedOrZero();
        return animalPosition + direction * CritterScareDistance;
    }
}
