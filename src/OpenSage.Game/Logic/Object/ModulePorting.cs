// Round-4 module-porting migration markers (api-freeze-v1 §3 item 3, seam S5;
// design-module-api §1.4 / §2.2).
//
// [ParseOnly]      - the class parses INI but has no runtime module yet. The grep for this
//                    attribute IS the Round-4 porting backlog; the porting task that
//                    implements the class deletes the attribute.
// [SimDataAudited] - the ModuleData's fields have been converted to the §2.2 quantized
//                    vocabulary (Fix64 / LogicFrameSpan / Fix64-percentage); float-typed
//                    fields remaining in an audited sim ModuleData are contract errors.
// ModuleNotPortedException - thrown by CreateModule of a [ParseOnly] class (debug loudness
//                    beats a silent null module).

using System;

namespace OpenSage.Logic.Object;

/// <summary>Marks a ModuleData class that parses but has no ported runtime module yet.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ParseOnlyAttribute : Attribute
{
    /// <summary>Backlog note: census category, GPL reference file, sizing signal.</summary>
    public string Note { get; }

    public ParseOnlyAttribute(string note)
    {
        Note = note;
    }
}

/// <summary>
/// Marks a ModuleData class whose fields have been audited to the quantized sim vocabulary
/// (design-module-api §2.2). The analyzer treats unaudited legacy classes as warnings and
/// audited ones as errors, so the parse tables convert incrementally without a flag-day.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SimDataAuditedAttribute : Attribute
{
}

/// <summary>Thrown when a [ParseOnly] ModuleData is asked to create its runtime module.</summary>
public sealed class ModuleNotPortedException : Exception
{
    public ModuleNotPortedException(Type moduleDataType)
        : base($"{moduleDataType.Name} is [ParseOnly]: its runtime module has not been ported yet (Round-4 backlog).")
    {
    }
}
