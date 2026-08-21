using System;
using System.Linq;
using System.Reflection;
using OpenSage.Network.Wire;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

/// <summary>
/// Design-netcode.md R-N5 / D13's "no decode path produces a System.Single that reaches a
/// sim-facing type" as an automated, structural check rather than a narrow example-based one:
/// scans every type declared in the <see cref="OpenSage.Network.Wire"/> namespace (this whole
/// codec) for any field, property, method parameter, or return type that is <c>float</c> /
/// <c>System.Single</c> (in any shape: bare, array, or by-ref). There are none - the two places
/// a <c>float</c> value genuinely exists (<c>Fix64.ToFloatForDisplay()</c>'s return value on
/// encode, and the <c>float</c> argument implicit in <c>BitConverter.SingleToUInt32Bits</c> on
/// encode) are transient locals inside a method body, never a stored field, a parameter, or a
/// return type anywhere on this namespace's surface - so nothing float-shaped can be held,
/// passed around, or handed back to a caller.
/// </summary>
public class NoFloatSurfaceTests
{
    [Fact]
    public void WireNamespace_HasNoFloatOrSingleInAnyMemberSignature()
    {
        var wireTypes = typeof(WireProtocolVersion).Assembly.GetTypes()
            .Where(t => t.Namespace == "OpenSage.Network.Wire")
            .ToList();

        Assert.NotEmpty(wireTypes); // guard against a namespace typo making this test vacuous

        var offenders = new System.Collections.Generic.List<string>();

        const BindingFlags allMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        foreach (var type in wireTypes)
        {
            foreach (var field in type.GetFields(allMembers))
            {
                if (IsFloatShaped(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} (field)");
                }
            }

            foreach (var property in type.GetProperties(allMembers))
            {
                if (IsFloatShaped(property.PropertyType))
                {
                    offenders.Add($"{type.FullName}.{property.Name} (property)");
                }
            }

            foreach (var method in type.GetMethods(allMembers).Cast<MethodBase>()
                         .Concat(type.GetConstructors(allMembers)))
            {
                if (method is MethodInfo methodInfo && IsFloatShaped(methodInfo.ReturnType))
                {
                    offenders.Add($"{type.FullName}.{method.Name} (return type)");
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (IsFloatShaped(parameter.ParameterType))
                    {
                        offenders.Add($"{type.FullName}.{method.Name}({parameter.Name}) (parameter)");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "float/System.Single found on the Wire codec's surface: " + string.Join(", ", offenders));
    }

    private static bool IsFloatShaped(Type type)
    {
        var effective = type;
        if (effective.IsByRef || effective.IsPointer)
        {
            effective = effective.GetElementType()!;
        }

        while (effective.IsArray)
        {
            effective = effective.GetElementType()!;
        }

        return effective == typeof(float);
    }
}
