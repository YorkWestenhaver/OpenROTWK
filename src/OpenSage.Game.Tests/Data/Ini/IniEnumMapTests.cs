using OpenSage.Data.Ini;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniEnumMapTests
{
    [Fact]
    public void EnumMapKeepsIniNamesOfMembersSharingAValue()
    {
        // ObjectStatus.Masked and ObjectStatus.InsideGarrison share the underlying value 21.
        // The map must contain the [IniEnum] names of both members.
        var map = IniParser.GetEnumMap<ObjectStatus>();

        Assert.True(map.ContainsKey("MASKED"));
        Assert.True(map.ContainsKey("INSIDE_GARRISON"));
        Assert.Equal(map["MASKED"], map["INSIDE_GARRISON"]);
    }

    [Fact]
    public void CanParseInsideGarrison()
    {
        // Used by unmodded ROTWK largegroupaudio.ini (e.g. 'Key = INSIDE_GARRISON').
        var value = IniParser.ParseEnum<ObjectStatus>("INSIDE_GARRISON");

        Assert.Equal(ObjectStatus.InsideGarrison, value);
        Assert.Equal(ObjectStatus.Masked, value);
    }

    [Fact]
    public void EnumMapIsCaseInsensitive()
    {
        var map = IniParser.GetEnumMap<ObjectStatus>();

        Assert.True(map.ContainsKey("inside_garrison"));
    }

    [Fact]
    public void ReverseEnumMapContainsSharedValueOnce()
    {
        var reverse = IniParser.GetEnumMapReverse<ObjectStatus>();

        // First declared member's first INI name wins for the shared value.
        Assert.Equal("MASKED", reverse[ObjectStatus.Masked]);
    }
}
