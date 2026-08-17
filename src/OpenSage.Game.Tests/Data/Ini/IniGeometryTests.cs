using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class IniGeometryTests
{
    [Fact]
    public void GeometryFieldsWithoutAPrecedingGeometryLineEditTheDefaultShape()
    {
        var context = new IniParseTestContext();

        // No 'Geometry = ...' line: the engine's object template always owns a geometry, so these
        // fields simply edit it.
        var parser = context.ParseFileText(
            "Object NoGeometryLine\n" +
            "  GeometryMajorRadius = 12.0\n" +
            "  GeometryMinorRadius = 8.0\n" +
            "  GeometryHeight = 20.0\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("NoGeometryLine");
        Assert.NotNull(definition);
        Assert.Equal(12.0f, definition.Geometry.Shapes[0].MajorRadius);
        Assert.Equal(8.0f, definition.Geometry.Shapes[0].MinorRadius);
        Assert.Equal(20.0f, definition.Geometry.Shapes[0].Height);
    }

    [Fact]
    public void GeometryLineStillSelectsTheShapeSubsequentFieldsEdit()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Object WithGeometryLine\n" +
            "  Geometry = BOX\n" +
            "  GeometryMajorRadius = 5.0\n" +
            "  AdditionalGeometry = CYLINDER\n" +
            "  GeometryMajorRadius = 9.0\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);

        var definition = context.AssetStore.ObjectDefinitions.GetByName("WithGeometryLine");
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Geometry.Shapes.Count);
        Assert.Equal(GeometryType.Box, definition.Geometry.Shapes[0].Type);
        Assert.Equal(5.0f, definition.Geometry.Shapes[0].MajorRadius);
        Assert.Equal(GeometryType.Cylinder, definition.Geometry.Shapes[1].Type);
        Assert.Equal(9.0f, definition.Geometry.Shapes[1].MajorRadius);
    }

    [Fact]
    public void AGeometryFieldFailureDoesNotHideTheRestOfTheFile()
    {
        var context = new IniParseTestContext();

        var parser = context.ParseFileText(
            "Object FirstObject\n" +
            "  GeometryHeight = 20.0\n" +
            "End\n" +
            "Object SecondObject\n" +
            "  VisionRange = 100.0\n" +
            "End\n");

        Assert.Empty(parser.ParseErrors);
        Assert.NotNull(context.AssetStore.ObjectDefinitions.GetByName("FirstObject"));
        Assert.NotNull(context.AssetStore.ObjectDefinitions.GetByName("SecondObject"));
    }
}
