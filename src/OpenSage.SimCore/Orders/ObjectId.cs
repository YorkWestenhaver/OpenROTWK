
// Moved from OpenSage.Game (OpenSage.Logic.Object) in scaffolding step 5: the frozen IXfer
// surface (api-freeze-v1 S4) takes `ref ObjectId`, so the identity type is sim substrate and
// lives below the module framework. Orders already speak object ids (SimOrder); the type joins
// them here. Consumers keep compiling via `global using ObjectId = ...` bridge aliases, the
// step-4 LogicFrame pattern.

namespace OpenSage.SimCore.Orders;

public readonly record struct ObjectId(uint Index)
{
    /// <summary>
    /// An object ID which is guaranteed to be invalid (0).
    /// This is commonly used as a sentinel value to indicate that a reference to a game object is non-existent or invalid.
    /// </summary>
    public static readonly ObjectId Invalid = new(0);

    /// <summary>
    /// Indicates whether this object ID is potentially valid (non-zero).
    /// Whether or not an object ID is actually valid (i.e. corresponds to an existing object) depends on the current state of the game.
    /// </summary>
    public readonly bool IsValid => Index != 0;

    /// <summary>
    /// Indicates whether this object ID is invalid (0).
    /// </summary>
    public readonly bool IsInvalid => Index == 0;
}
