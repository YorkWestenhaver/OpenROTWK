// IReviveLifecycleModule - the dead-object-lifecycle capability interface the respawn seam
// introduces (design-respawn-seam.md §3.3, as amended by the wave-2a adversarial review and
// ratified by dr-0033).
//
// Behavioral reference: the CONTROL-FLOW idiom is translated from generals-gpl GeneralsMD
// Object.cpp's onDie, which consults a condition on the dying object and, when it holds,
// hands the death to another module's named interface instead of running the ordinary corpse
// path (its RebuildHoleBehaviorInterface handoff). BFME2's revive lifecycle itself has NO GPL
// ancestor - grep over generals-gpl/generals-community finds no RespawnUpdate at all - so the
// BFME-only semantics below come from the shipped INI vocabulary and the written seam design,
// never from the retail binary.
//
// This interface sits alongside the existing IDieModule / IDamageModule / ICollideModule /
// IUpgradeableModule capability family (api-freeze-v1 §3.4 blesses multi-category composition
// through such interfaces, dispatched in ModuleIndex order).

namespace OpenSage.Logic.Object;

/// <summary>
/// A module that owns what happens to its object after a NON-PERMANENT death: the object is
/// not turned into a corpse and is not reaped, and this module drives it back to life in
/// place.
/// </summary>
/// <remarks>
/// Dispatched from <see cref="GameObject.OnDie"/> in ascending <see cref="BehaviorModule.ModuleIndex"/>;
/// the first module whose <see cref="ClaimDeath"/> returns true owns the death, and
/// <c>OnDie</c> returns immediately (no slow death, no <c>IDieModule.OnDie</c>, no die sound,
/// no no-die-module <c>Destroy()</c> fallback). That all-or-nothing suppression is the
/// conservative rule: a claim that left some part of the corpse path running could strand the
/// object in a state the claiming module cannot undo (OQ-2, filed unresolved).
/// </remarks>
public interface IReviveLifecycleModule
{
    /// <summary>
    /// Offered the killing blow BEFORE any death effect runs. Returning true means "this death
    /// is killed-to-respawn, not a corpse": the caller suppresses the whole corpse path and
    /// this module becomes responsible for the object until it is alive again (or until it
    /// dies a death it does not claim). Returning false leaves the ordinary death path
    /// untouched.
    /// </summary>
    /// <param name="damageInput">
    /// The killing blow, passed through from <see cref="GameObject.OnDie"/>. It is the
    /// implementation's ONLY input for deciding permanence, and that is deliberate: the
    /// permanence verdict a <c>RespawnBody</c> latches is resolved from this same damage, and
    /// at <c>OnDie</c> time - which the body's own <c>AttemptDamage</c> reaches from INSIDE
    /// its base call - no already-latched verdict exists yet to read. An implementation that
    /// consults a latch instead of this parameter would see "not permanent" for every death,
    /// including permanent ones (the wave-2a review's H1 finding).
    /// </param>
    bool ClaimDeath(in DamageInfoInput damageInput);
}
