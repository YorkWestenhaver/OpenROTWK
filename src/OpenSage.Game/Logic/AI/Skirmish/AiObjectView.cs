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
/// <param name="IsHorde">
/// (S9-08) True when the definition is KINDOF HORDE - the horde OBJECT itself, the thing that
/// contains members and the thing an order must address.
/// </param>
/// <param name="IsHordeMember">
/// (S9-08) True when this object is contained by a horde (<c>GameObject.ParentHorde != null</c>).
/// A horde member must NEVER be recruited or ordered directly: AIUpdate.SetTargetPoint returns
/// immediately for an object with a parent horde (AIUpdate.cs, the ParentHorde early-out at the
/// head of SetTargetPoint), so every move order addressed to a member is a silent no-op. Order
/// the parent horde instead - <see cref="IsHorde"/> marks it.
/// </param>
public readonly record struct AiObjectView(
    ObjectId Id,
    string TemplateName,
    Vector3 Position,
    int OwnerIndex,
    bool IsStructure,
    bool IsUnderConstruction,
    float HealthFraction,
    bool IsHorde = false,
    bool IsHordeMember = false)
{
    /// <summary>True for a finished (not still-building) structure.</summary>
    public bool IsCompletedStructure => IsStructure && !IsUnderConstruction;

    /// <summary>
    /// (S9-08) True for something the AI may put in a team and give orders to: a finished,
    /// non-structure object that is not a member of a horde.
    /// </summary>
    /// <remarks>
    /// The horde-member exclusion is the single most load-bearing line in the team lane. A horde
    /// of ten orcs is eleven objects in <see cref="IAiWorldView.OwnObjects"/> - one HORDE object
    /// plus ten members - and only the HORDE object can be moved. Recruiting members would build
    /// teams of ten "units" whose every order is discarded by AIUpdate, which looks exactly like
    /// a working AI that never engages.
    /// </remarks>
    public bool IsOrderableUnit => !IsStructure && !IsUnderConstruction && !IsHordeMember;
}
