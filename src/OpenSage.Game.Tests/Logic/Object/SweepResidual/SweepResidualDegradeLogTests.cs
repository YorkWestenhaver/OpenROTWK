// R15 L1-11 (sweep ratchet): the shared log-once gate behind the guard shape used by every
// residual-crash-class fix in this packet. Each guard sits inside either the per-object map
// load loop or the per-frame update loop, so an ungated warning would emit thousands of
// identical lines per run and drown the log the sweep harness grades from.

using OpenSage.Diagnostics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.SweepResidual;

[Collection("DegradeLog")]
public class SweepResidualDegradeLogTests
{
    public SweepResidualDegradeLogTests()
    {
        // The gate is process-wide; keep cases independent.
        DegradeLog.ResetForTests();
    }

    [Fact]
    public void ShouldReport_IsTrueOnceThenFalse()
    {
        Assert.True(DegradeLog.ShouldReport("Cat", "Subject"));
        Assert.False(DegradeLog.ShouldReport("Cat", "Subject"));
        Assert.False(DegradeLog.ShouldReport("Cat", "Subject"));
    }

    [Fact]
    public void ShouldReport_NamespacesBySite_SoTwoGuardsBothGetToSpeak()
    {
        Assert.True(DegradeLog.ShouldReport("SiteA", "SharedSubject"));
        Assert.True(DegradeLog.ShouldReport("SiteB", "SharedSubject"));
    }

    [Fact]
    public void ShouldReport_CoalescesNullAndEmptySubjects()
    {
        Assert.True(DegradeLog.ShouldReport("Cat", null));
        Assert.False(DegradeLog.ShouldReport("Cat", ""));
        Assert.False(DegradeLog.ShouldReport("Cat", "   "));
    }

    [Fact]
    public void Normalize_RendersAMissingNameReadably()
    {
        Assert.Equal("<unnamed>", DegradeLog.Normalize(null));
        Assert.Equal("<unnamed>", DegradeLog.Normalize(""));
        Assert.Equal("RohanEntOak", DegradeLog.Normalize("RohanEntOak"));
    }
}
