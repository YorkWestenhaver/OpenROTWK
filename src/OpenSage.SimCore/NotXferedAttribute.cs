using System;

namespace OpenSage.SimCore;

/// <summary>
/// Opts a mutable module field out of the SIMCORE011 Xfer-completeness rule. Use it only for
/// state that is deterministically rebuilt after load (a cache, a memoised lookup, a
/// frame-local scratch value) and therefore does <i>not</i> belong in the per-object Xfer walk
/// (api-freeze-v1 S4). Anything that actually drives the simulation must be persisted instead
/// of suppressed - suppressing genuine sim state is exactly the save-load/lockstep desync the
/// rule exists to catch.
/// </summary>
/// <remarks>
/// The analyzer matches this attribute <i>by name</i> (like <see cref="SimStateAttribute"/>), so
/// a migrating project can annotate a field without referencing SimCore.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class NotXferedAttribute : Attribute
{
}
