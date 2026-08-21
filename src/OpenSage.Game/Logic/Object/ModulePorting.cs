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
//
// DROPPED-R15 convention (R15 L5-P1; bfme2-workbench/research/skirmish-sufficiency-census.md
// §3.3, "Dead weight — five"): a [ParseOnly] class whose Note begins with "DROPPED-R15" is a
// FORMAL exposure verdict, not an ordinary backlog entry — the census's resolved-precedence
// AotR corpus scan showed the class has zero live module-position uses (every authored
// reference is commented out, or the token is unreachable/never instantiated), so it will
// NOT be picked up by a future porting wave absent new content evidence. The class stays
// [ParseOnly] rather than being deleted outright: retail/community .ini content may still
// author the keyword (harmlessly, per the base ModuleData.CreateModule contract returning
// null — see GameObject's behavior-instantiation loop), so parsing + keyword dispatch in
// BehaviorModuleData's parse table must keep working even though the runtime module never
// will. Grep `DROPPED-R15` to enumerate this list; do not schedule these for porting without
// re-opening the census verdict that dropped them.

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
