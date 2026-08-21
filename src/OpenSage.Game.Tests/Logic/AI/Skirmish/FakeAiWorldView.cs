#nullable enable

// S9-01 (R15 L3): the test double every later AI-manager test builds on.
//
// This class existing, and being this trivial, IS the deliverable of the IAiWorldView seam: an
// AI manager can be tested with no Game, no GraphicsDevice, no INI files and no map. If a
// future manager cannot be tested against this fake, the manager is reaching around the seam.

using System.Collections.Generic;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;

namespace OpenSage.Tests.Logic.AI.Skirmish;

/// <summary>A hand-set <see cref="IAiWorldView"/>. Every member is a writable property.</summary>
internal sealed class FakeAiWorldView : IAiWorldView
{
    public uint CurrentFrame { get; set; }

    public int PlayerIndex { get; set; }

    public string PlayerName { get; set; } = "TestAi";

    public string? Side { get; set; } = "FactionMordor";

    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    public int Money { get; set; }

    public List<AiObjectView> Own { get; } = new();

    public List<AiObjectView> Enemy { get; } = new();

    public IReadOnlyList<AiObjectView> OwnObjects => Own;

    public IReadOnlyList<AiObjectView> EnemyObjects => Enemy;

    public SkirmishAIData? SkirmishAIData { get; set; }

    public AIData? AIData { get; set; }

    public DifficultyTuning? DifficultyTuning { get; set; }

    /// <summary>Advances the fake clock by one frame, as a real logic tick would.</summary>
    public void AdvanceFrame() => CurrentFrame++;
}
