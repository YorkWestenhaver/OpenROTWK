// FIX2-LOCO: a Locomotor applied to an object whose template declares no PhysicsBehavior
// must DEGRADE (log once per template, skip the locomotor work) rather than kill the sim
// tick. Before this fix, Locomotor.SetPhysicsOptions called DebugUtility.Crash, which
// throws in every build, so AIUpdate.Update propagated a System.Exception out of
// GameLogic.Update and ended the match at logic frame ~1 (observed on WildHillTroll_Slaved,
// and on 4 of 19 maps in the R15 20-map sweep).
//
// Retail guards the same four call sites with DEBUG_CRASH followed by an early return
// (EA GPL reference, GeneralsMD Locomotor.cpp: locoUpdate_moveTowardsAngle,
// setPhysicsOptions, locoUpdate_moveTowardsPosition, locoUpdate_maintainCurrentPosition),
// and DEBUG_CRASH compiles out of a shipping build - so shipping retail no-ops the call
// and keeps simulating. These tests pin that behaviour.

using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Locomotion;

public class LocomotorPhysicsDegradationTests : MockedGameTest
{
    public LocomotorPhysicsDegradationTests()
    {
        // The once-per-template gate is process-wide; keep cases independent.
        LocomotorPhysicsRequirement.ResetForTests();
    }

    private static ObjectDefinition MakeDefinitionWithoutPhysics(string name)
    {
        // No modules at all, so GameObject.Physics stays null - exactly the shape of an
        // object template that is missing (or failed to parse) its PhysicsBehavior block.
        return new ObjectDefinition { Name = name };
    }

    private static LocomotorTemplate MakeLocomotorTemplate(string name)
    {
        return new LocomotorTemplate { Name = name };
    }

    private (Locomotor Locomotor, GameObject Object) MakeSubject(
        string objectTemplateName = "TestTroll_Slaved",
        string locomotorTemplateName = "TestTrollLocomotor")
    {
        var gameObject = new GameObject(
            MakeDefinitionWithoutPhysics(objectTemplateName),
            ZeroHour.GameEngine,
            null);

        var locomotor = new Locomotor(
            ZeroHour.GameEngine,
            MakeLocomotorTemplate(locomotorTemplateName),
            100.0f);

        return (locomotor, gameObject);
    }

    [Fact]
    public void SetPhysicsOptions_ObjectWithoutPhysics_DoesNotThrow()
    {
        var (locomotor, gameObject) = MakeSubject();

        Assert.Null(gameObject.Physics);

        // The regression: this used to throw
        // "You can only apply Locomotors to objects with Physics".
        locomotor.SetPhysicsOptions(gameObject);
    }

    [Fact]
    public void LocoUpdateMoveTowardsAngle_ObjectWithoutPhysics_DoesNotThrow()
    {
        var (locomotor, gameObject) = MakeSubject();

        locomotor.LocoUpdateMoveTowardsAngle(gameObject, 0.0f);
    }

    [Fact]
    public void LocoUpdateMaintainCurrentPosition_ObjectWithoutPhysics_DegradesAndReportsSatisfied()
    {
        var (locomotor, gameObject) = MakeSubject();

        // True == "no need to call me again every frame", which is the correct degraded
        // answer for an object that cannot move at all.
        Assert.True(locomotor.LocoUpdateMaintainCurrentPosition(gameObject));
    }

    [Fact]
    public void SetPhysicsOptions_RepeatedCalls_DoNotThrowAndReportOnlyOncePerTemplate()
    {
        var (locomotor, gameObject) = MakeSubject("WildTroll_Slaved");

        for (var i = 0; i < 5; i++)
        {
            locomotor.SetPhysicsOptions(gameObject);
        }

        // The locomotor consumed the once-per-template gate on its first call, so the
        // gate is now closed for this template: a further caller would not log again.
        Assert.False(LocomotorPhysicsRequirement.ShouldReport("WildTroll_Slaved"));
    }

    [Fact]
    public void ShouldReport_IsTrueOncePerTemplateName()
    {
        Assert.True(LocomotorPhysicsRequirement.ShouldReport("TemplateA"));
        Assert.False(LocomotorPhysicsRequirement.ShouldReport("TemplateA"));

        // A different offending template still gets its own single line.
        Assert.True(LocomotorPhysicsRequirement.ShouldReport("TemplateB"));
        Assert.False(LocomotorPhysicsRequirement.ShouldReport("TemplateB"));
    }

    [Fact]
    public void ShouldReport_TreatsNullAndEmptyNamesAsOneUnknownTemplate()
    {
        Assert.True(LocomotorPhysicsRequirement.ShouldReport(null));
        Assert.False(LocomotorPhysicsRequirement.ShouldReport(string.Empty));
    }

    [Fact]
    public void FormatMessage_NamesObjectTemplateLocomotorAndCallSite()
    {
        var message = LocomotorPhysicsRequirement.FormatMessage(
            "WildHillTroll_Slaved",
            "TrollLocomotor",
            "SetPhysicsOptions");

        Assert.Contains("WildHillTroll_Slaved", message);
        Assert.Contains("TrollLocomotor", message);
        Assert.Contains("SetPhysicsOptions", message);
        Assert.Contains("PhysicsBehavior", message);
    }

    [Fact]
    public void FormatMessage_SubstitutesUnknownForMissingNames()
    {
        var message = LocomotorPhysicsRequirement.FormatMessage(null, null, null);

        Assert.Contains("<unknown>", message);
    }
}
