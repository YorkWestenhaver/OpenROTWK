#nullable enable

// S9-01 (R15 L3): the AI's view of one game object.
//
// Deliberately a value snapshot rather than a GameObject reference. A manager that held a live
// GameObject could reach the whole engine through it (Owner -> PlayerManager -> Game), which
// would defeat the point of IAiWorldView and make every manager need a running game to test.

// ObjectId comes from the project-wide global using alias (GlobalUsings.cs) onto
// OpenSage.SimCore.Orders.ObjectId.

using System.Numerics;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// An immutable per-frame snapshot of a single object, as the skirmish AI sees it.
/// </summary>
/// <param name="Id">Engine object id. Orders are addressed by this.</param>
/// <param name="TemplateName">The object definition's name (e.g. "MordorSlaughterHouse").</param>
/// <param name="Position">World position at snapshot time.</param>
/// <param name="OwnerIndex">Index of the owning player.</param>
/// <param name="IsStructure">True when the definition is KINDOF STRUCTURE.</param>
/// <param name="IsUnderConstruction">True while the object is still being built.</param>
/// <param name="HealthFraction">Current health as a 0..1 fraction; 1 when the object has no body.</param>
public readonly record struct AiObjectView(
    ObjectId Id,
    string TemplateName,
    Vector3 Position,
    int OwnerIndex,
    bool IsStructure,
    bool IsUnderConstruction,
    float HealthFraction)
{
    /// <summary>True for a finished (not still-building) structure.</summary>
    public bool IsCompletedStructure => IsStructure && !IsUnderConstruction;
}
