using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Data.Ini;

public class ModuleParseTableTests
{
    /// <summary>
    /// Module parse tables are built in static initialisers, and a table built by concatenating a
    /// base table with a derived one throws on a duplicate field name. Because the failure happens
    /// in a static constructor it is invisible at build time and only surfaces as a
    /// TypeInitializationException the first time real data uses that module — this test forces
    /// every one of them up front.
    /// </summary>
    [Fact]
    public void EveryModuleDataTypeInitialisesItsParseTable()
    {
        var moduleDataTypes = typeof(ModuleData).Assembly
            .GetTypes()
            .Where(x => !x.IsAbstract && !x.IsGenericTypeDefinition && typeof(ModuleData).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(moduleDataTypes);

        var failures = new List<string>();

        foreach (var type in moduleDataTypes)
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (TypeInitializationException ex)
            {
                failures.Add($"{type.FullName}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        Assert.Empty(failures);
    }
}
