// SIMCORE-EXEMPT: the F4 display boundary. ToFloatForDisplay() is float-typed by contract
// (api-freeze-v1 F4) and its result never re-enters sim state, so SIMCORE001 cannot apply
// here. Isolated in its own file so the exemption covers exactly one method - the integer-only
// F4 parse boundaries in Fix64.Parse.cs stay fully policed. See design-simcore-scaffolding §1.3
// and the step-2 ruling in scaffolding-log.md.

namespace OpenSage.SimCore.Numerics
{
    public readonly partial struct Fix64
    {
        /// <summary>
        /// The one blessed escape to float, for rendering/UI display only. Never feed the
        /// result back into sim state (api-freeze-v1 F4; the analyzer polices call sites).
        /// </summary>
        public float ToFloatForDisplay()
        {
            return (float)(m_rawValue / 4294967296.0);
        }
    }
}
