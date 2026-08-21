// RemoveUpgradeUpgrade - R13 port (research/modules-r13/specs/RemoveUpgradeUpgradeModuleData.md).
// BFME2-only OnUpgrade-triggered side-effect module: unlike the two pure-marker Upgrade
// exemplars used for idiom (AllowBannerSpawnUpgrade, UpgradeDieModule's category), this one
// removes a set of named/grouped upgrades from the triggering object (and optionally the
// whole owning-player roster) when its shared TriggeredBy mux fires.
//
// No GPL file named RemoveUpgradeUpgrade.cpp exists (genuinely BFME2-only). Grounded instead
// against three GPL siblings (spec §0, full citations there):
//   - Die/UpgradeDie.cpp (GeneralsMD) - the lookup-and-remove idiom.
//   - Common/RTS/Player.cpp:3118 Player::removeUpgrade - Player-scope removal: find-guarded,
//     unlinks from the upgrade list, clears both in-progress/completed mask bits, calls
//     onUpgradeRemoved() only on a completed upgrade. onUpgradeRemoved() is an EMPTY STUB in
//     this GPL snapshot (Player.h:326) - no EVA-loss hook exists there to call (F-RUU-1).
//   - Object.cpp:4515 Object::removeUpgrade - Object-scope removal: unconditional mask-clear,
//     then resetUpgrade(mask) on every UpgradeModuleInterface behavior. Comment in the GPL
//     source is explicit: this does NOT undo already-applied effects, it only resets the
//     target upgrade's own trigger modules so they may re-fire (F-RUU-2: OpenSage's landed
//     GameObject.RemoveUpgrade does not yet implement that re-arm half - TODO at its call
//     site, not this file's job to close).
// Both cited GPL removal functions are themselves safe no-ops on a template/object that
// doesn't hold the upgrade, so this module's own call site needs no HasUpgrade guard (unlike
// UpgradeDie, whose GPL source guards only to fire a debug-only assert).
//
// Real AotR corpus (28 files, spec §0) confirms field shapes and the RemoveFromAllPlayerObjects
// use case: the Ring-Hero-transition family (saruman.ini/sauron.ini/etc.) sets it true because
// the Player-type Ring Hero upgrade may need cleanup on whichever specific object last carried
// the visible ring-hero state. Every other usage (dain.ini's mutual-exclusion pair, crate.ini)
// leaves it default false: self-object-only removal.
//
// SuppressEvaEventForRemoval is parsed and stored but not wired to any effect (F-RUU-1):
// AotR's own "Hack" comment on the Ring-Hero blocks independently confirms an EVA-loss event
// is expected to exist and be worth suppressing, but neither GPL (empty onUpgradeRemoved stub)
// nor OpenSage's frozen ISimEvents (FX/sound/particle members only) names or provides the
// mechanism - there is nothing to call yet. Parsed and preserved, matching the exact idiom
// UpgradeDie.cs established for its own unconsumed BFME2 ModuleTag token.
//
// UpgradeGroupsToRemove-driven removal (match every currently-held upgrade whose GroupName
// equals the field) has no GPL text describing it - filed as a data-derivation (F-RUU-3), not
// a citation: the natural generalization of the named-removal case over UpgradeTemplate's own
// already-landed GroupName field, corroborated by the CreateAHero_Weapon corpus usage (one
// group-tagged removal per mutually-exclusive weapon choice).
//
// MUTABLE SIM STATE INVENTORY: only the inherited upgrade-mux Triggered flag (UpgradeLogic).
// This module has no fields of its own that persist across ticks - the removal is a one-shot
// effect on OTHER state (GameObject/Player upgrade sets) that those owners' own Xfer walks
// already cover, same shape as UpgradeDieModule and AllowBannerSpawnUpgrade.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RemoveUpgradeUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly RemoveUpgradeUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public RemoveUpgradeUpgrade(GameObject gameObject, ISimContext context, RemoveUpgradeUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Whether this module's removal side effect has fired.</summary>
    public bool Triggered => _upgradeLogic.Triggered;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // Spec §1.1: assemble the target-template set. GameObject.CompletedUpgradesIncludingPlayer
        // is an internal shared scratch buffer overwritten by the next call - materialize the
        // group match before touching it again or calling RemoveUpgrade.
        var targets = new List<UpgradeTemplate>();

        foreach (var reference in _data.UpgradeToRemove)
        {
            var template = reference?.Value;
            if (template != null)
            {
                targets.Add(template);
            }
        }

        if (_data.UpgradeGroupsToRemove != null)
        {
            foreach (var held in GameObject.CompletedUpgradesIncludingPlayer)
            {
                if (held.GroupName == _data.UpgradeGroupsToRemove)
                {
                    targets.Add(held);
                }
            }
        }

        // Spec §1.2: removal scope. Type-dispatching call (UpgradeTemplate.RemoveUpgrade)
        // reaches Player.RemoveUpgrade for a Player-type template and GameObject.RemoveUpgrade
        // for an Object-type template - the one call that matches both cited GPL functions.
        foreach (var template in targets)
        {
            template.RemoveUpgrade(GameObject);

            if (_data.RemoveFromAllPlayerObjects)
            {
                foreach (var obj in Context.GameLogic.ObjectsAscendingId)
                {
                    if (obj.Owner == GameObject.Owner)
                    {
                        template.RemoveUpgrade(obj);
                    }
                }
            }
        }

        // Spec §1.3, F-RUU-1: _data.SuppressEvaEventForRemoval is intentionally not read here -
        // no EVA event exists in ISimEvents to gate.
    }

    // Field order = declaration order = OUR choice (F9). The only mutable sim field is the
    // upgrade mux triggered flag; the removal effect lands on other objects' own state, which
    // those owners' own Xfer walks already cover.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class RemoveUpgradeUpgradeModuleData : UpgradeModuleData
{
    internal static RemoveUpgradeUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<RemoveUpgradeUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<RemoveUpgradeUpgradeModuleData>
        {
            // UpgradeGroupsToRemove names a GROUP, not an upgrade template - there is no
            // LazyAssetReference<T> for a bare group-name string (groups are not an asset
            // type; UpgradeTemplate.GroupName is a plain string field, parsed the same way
            // via parser.ParseString()). ParseAssetReference() would resolve against an asset
            // table that has no group-name entries - parse-correctness fix, spec §2.
            { "UpgradeGroupsToRemove", (parser, x) => x.UpgradeGroupsToRemove = parser.ParseString() },
            { "UpgradeToRemove", (parser, x) => x.UpgradeToRemove = parser.ParseUpgradeReferenceArray() },
            { "RemoveFromAllPlayerObjects", (parser, x) => x.RemoveFromAllPlayerObjects = parser.ParseBoolean() },
            { "SuppressEvaEventForRemoval", (parser, x) => x.SuppressEvaEventForRemoval = parser.ParseBoolean() },
        });

    public string UpgradeGroupsToRemove { get; private set; }
    public LazyAssetReference<UpgradeTemplate>[] UpgradeToRemove { get; private set; } = System.Array.Empty<LazyAssetReference<UpgradeTemplate>>();
    public bool RemoveFromAllPlayerObjects { get; private set; }
    public bool SuppressEvaEventForRemoval { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RemoveUpgradeUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
