// mod/commandpointsupgrademoduledata (R13): the Player-side sink CommandPointsUpgrade writes
// to. Additive partial-class member only - no change to any existing Player member (merge
// hygiene). CommandPointsBank itself has no GPL parallel (see Economy/ResourceBank.cs header);
// this file only gives Player an instance of the already-implemented, already-tested type.
//
// Unlike ProductionCostModifiers (KindOfProductionCostRegistry, deliberately excluded from
// Player.Persist/CRC because it is fully re-derivable from triggered-upgrade state on load),
// CommandPointsBank owns genuine accumulating state (Used, via Use/Release from unit
// spawn/death) that is NOT cheaply re-derivable from upgrade flags alone. Wiring this field into
// Player.Persist and the Players CRC channel (api-freeze-v1 F8) is therefore explicitly OUT OF
// SCOPE for this module port - it is a cross-cutting change to shared Player persist/CRC code
// that a population-cap-consuming system (unit spawn/production) should land together with, not
// a single upgrade module. Filed as F-CPU-1 (see modules-r13/specs/
// CommandPointsUpgradeModuleData.md). This module's OWN Xfer walk (the upgrade mux triggered
// flag only, same shape as CostModifierUpgrade) is sufficient to make ITS OWN state
// save/load/CRC-correct; only the Player-side running total is unwired.

using OpenSage.Logic.Economy;

namespace OpenSage.Logic;

public partial class Player
{
    /// <summary>
    /// This player's command-point (population) pool, mutated by triggered
    /// <c>CommandPointsUpgrade</c> modules (<see cref="CommandPointsBank.SetLimit"/>) and,
    /// eventually, by unit production/death (<see cref="CommandPointsBank.Use"/>/
    /// <see cref="CommandPointsBank.Release"/> - not wired by this port, see file header
    /// finding F-CPU-1). Not yet in <c>Player.Persist</c> or the Players CRC channel.
    /// </summary>
    public CommandPointsBank CommandPoints { get; } = new();
}
