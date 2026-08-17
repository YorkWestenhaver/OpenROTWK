using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("OpenSage.Game.Tests")]
[assembly: InternalsVisibleTo("OpenSage.Viewer")]
// Harness scenario driver (api-freeze-v1 §6 step 6/7 glue): hosts HeadlessSimGame and the
// real channel sources to drive ported modules through the SimLoop for Target-A self-diffs.
[assembly: InternalsVisibleTo("OpenSage.SimCore.ScenarioDriver")]
