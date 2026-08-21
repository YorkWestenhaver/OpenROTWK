// L1-03 (A1-G4): SpecialPowerModule used to dereference its SpecialPowerModuleData.SpecialPower
// LazyAssetReference<SpecialPower>.Value with no null check (ctor, ReadyProgress, TryUpgrade,
// Unlock, ResetCountdown, Activate, Matches, the SpecialPowerType property). A SpecialPowerTemplate
// name that fails to resolve - a data set missing/renaming the referenced SpecialPower, e.g. the
// 'map good helms deep' load - therefore NRE'd instead of leaving the object with an inert power,
// mirroring the guard SpawnBehavior already uses for its own unresolved SpawnTemplateName
// reference (SpawnBehavior.cs, one deduped Warn + safe no-op).
//
// These tests exercise the module through real GameObject construction (the actual failure site:
// GameObject's ctor calls BehaviorModuleData.CreateModule for every parsed Behavior block), using
// the real IniParser + AssetStore via IniParseTestContext so the "unresolved name" case is the
// genuine LazyAssetReference<SpecialPower> resolving to null, not a hand-rolled stand-in.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Tests.Data.Ini;
using Xunit;

namespace OpenSage.Tests.Logic.Object;

public class SpecialPowerModuleGuardTests : MockedGameTest
{
    // SpecialPower_DoesNotExist is never defined anywhere in this text, so
    // ParseSpecialPowerReference()'s LazyAssetReference<SpecialPower>.Value resolves to null at
    // construction time - the exact 'map good helms deep' failure mode.
    private const string UnresolvedDefinitions = @"
Object GuardTestObjectUnresolved
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_Power
    SpecialPowerTemplate = SpecialPower_DoesNotExist
  End
End
";

    private const string ResolvedDefinitions = @"
SpecialPower SpecialPower_Real
  Enum = SPECIAL_DAISY_CUTTER
  ReloadTime = 1000
End

Object GuardTestObjectResolved
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_Power
    SpecialPowerTemplate = SpecialPower_Real
  End
End
";

    private const string ModuleTag = "ModuleTag_Power";

    private GameObject SpawnFromContext(IniParseTestContext context, string objectName)
    {
        var objectDefinition = context.AssetStore.ObjectDefinitions.GetByName(objectName);
        return new GameObject(objectDefinition, Generals.GameEngine, Generals.PlayerManager.GetPlayerByIndex(0));
    }

    // ---- unresolved SpecialPowerTemplate: every surface must stay safe ----

    [Fact]
    public void UnresolvedSpecialPowerTemplate_ObjectConstructionDoesNotThrow()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);

        var exception = Record.Exception(() => SpawnFromContext(context, "GuardTestObjectUnresolved"));

        Assert.Null(exception);
    }

    [Fact]
    public void UnresolvedSpecialPowerTemplate_ModuleIsStillCreated()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectUnresolved");

        var module = gameObject.FindBehavior<SpecialPowerModule>();

        // The guard neuters the module rather than dropping it (CreateModule still returns a
        // live SpecialPowerModule) - it just never does anything, exactly like the [ParseOnly]
        // CreateModule-returns-null contract but reached from a different failure (a resolvable
        // module whose data reference failed, not an unported one).
        Assert.NotNull(module);
    }

    [Fact]
    public void UnresolvedSpecialPowerTemplate_SpecialPowerTypeIsDefault()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectUnresolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        Assert.Equal(default, module.SpecialPowerType);
    }

    [Fact]
    public void UnresolvedSpecialPowerTemplate_NeverBecomesReady()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectUnresolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        Assert.False(module.Ready);
        Assert.Equal(0f, module.ReadyProgress());
    }

    [Fact]
    public void UnresolvedSpecialPowerTemplate_PublicSurfaceIsSafeNoOp()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectUnresolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        // None of these may throw, and TryUpgrade must never unlock the power (observed here via
        // Ready staying false/0 through every call - there is no public "unlocked" surface to
        // assert directly).
        var exception = Record.Exception(() =>
        {
            module.TryUpgrade(null);
            module.Unpause();
            module.ResetCountdown();
            module.Activate(Vector3.Zero);
            module.Matches(null);
        });

        Assert.Null(exception);
        Assert.False(module.Ready);
    }

    [Fact]
    public void UnresolvedSpecialPowerTemplate_MatchesAlwaysReturnsFalse()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(UnresolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectUnresolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        Assert.False(module.Matches(null));
    }

    // ---- resolved SpecialPowerTemplate: guard must stay dormant and behavior must be unchanged ----

    [Fact]
    public void ResolvedSpecialPowerTemplate_SpecialPowerTypeMatchesData()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(ResolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectResolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        Assert.Equal(SpecialPowerType.FuelAirBomb, module.SpecialPowerType);
    }

    [Fact]
    public void ResolvedSpecialPowerTemplate_MatchesTheResolvedAssetOnly()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(ResolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectResolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        var resolved = context.AssetStore.SpecialPowers.GetByName("SpecialPower_Real");

        Assert.True(module.Matches(resolved));
        Assert.False(module.Matches(null));
    }

    [Fact]
    public void ResolvedSpecialPowerTemplate_PublicSurfaceStillWorks()
    {
        var context = new IniParseTestContext();
        context.ParseFileText(ResolvedDefinitions);
        var gameObject = SpawnFromContext(context, "GuardTestObjectResolved");
        var module = gameObject.FindBehavior<SpecialPowerModule>();

        // Not disabled, so these must still run their real logic without throwing. Two members are
        // intentionally excluded, both for the same reason and neither having anything to do with
        // this guard: Activate() reaches GameEngine.AudioSystem, and TryUpgrade()'s not-yet-unlocked
        // path reaches GameObject.IsBeingConstructed() -> Drawable.ModelConditionFlags. Neither the
        // audio system nor a Drawable is constructed by the INI-parse test harness, so calling them
        // here would measure the harness, not the module. TryUpgrade's *disabled* path is what this
        // suite actually needs to cover, and it is covered above - the guard returns before
        // GameObject is ever touched, which is precisely why that case does not NRE.
        var exception = Record.Exception(() =>
        {
            module.Unpause();
            module.ResetCountdown();
        });

        Assert.Null(exception);
    }
}
