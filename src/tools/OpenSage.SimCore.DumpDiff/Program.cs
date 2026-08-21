// dumpdiff -- the engine-side equivalent of the workbench's ddiff.py (bfme2-workbench/tools/
// ddiff/ddiff.py), living in-repo because the engine (YorkWestenhaver/OpenROTWK) is a
// standalone repo whose CI checkout cannot see the private workbench. Lockstep-diffs two
// "opensage-deepdump" dumps and, on divergence, emits the structured report the M3 cross-arch
// CI gate depends on (n14a-gate-wiring writes its operator runbook against this contract).
//
// FORMAT ("opensage-deepdump v2", written by OpenSage.SimCore.Sync.DeepCrcWriter; ASCII, LF
// newlines; v1 is also accepted -- ground truth: DeepCrcWriter.cs, mirrored from ddiff.py):
//
//   # opensage-deepdump v2                                  header line
//   F <frame>                                                begin checkpoint frame
//   C <ordinal> <channelName>                                begin channel
//   R <objectId> <moduleIndex> <tag> <class> <field> <tol> <type> <hexBytes>
//   E <ordinal> <crc8hex>                                    end channel, folded CRC
//   V <frame> <combined8hex> <crc8hex>...                    checkpoint vector
//
// (v1 R records omit the <type> token: 8 fields instead of 9.)
//
// Because both engines emit the same canonical walk, comparison is a lockstep line walk and
// the first differing line IS the diagnosis (api-freeze-v1 F14: no triage step between
// "divergence detected" and "module X, field Y, frame N").
//
// METADATA CONVENTION (this tool's own addition; the emitter side -- ScenarioDriver's
// --arch-stamp/--exclude handling in OpenSage.SimCore.ScenarioDriver.Program -- writes exactly
// this shape; see DumpParser.TryHarvestMetadata): a comment line shaped exactly "# key=value"
// anywhere after the header is harvested as metadata and stripped from the lockstep walk, like
// every other "#" line. Recognized keys: arch, os, rid, exclude. Any dump lacking these lines
// still compares fine -- the report just shows "unspecified" for what it doesn't know.
//
// ============================================================================================
// THE DIVERGENCE REPORT CONTRACT (the load-bearing deliverable -- read this before changing
// DivergenceReport.cs or the CLI's exit-code behavior; n14a-gate-wiring's runbook is written
// against this exact ordering):
//
// On exit 1 (a real divergence between two structurally valid, same-version dumps that both
// carried at least one checkpoint), the report states, in this order:
//   1. the two leg labels
//   2. the LAST frame at which the two streams were still identical
//   3. the frame at which the divergence was detected
//   4. the channel in effect (ordinal + name)
//   5. for an R-record divergence: objectId, moduleIndex, module tag, module class, field
//      name, tolerance token, type token, and both sides' hex bytes
//   6. for a V-line divergence: the FULL per-channel vector from both sides (not just the
//      differing entry), so the reader can see which channels held
//   7. the exclusion set each leg ran with
// A machine-readable single-line JSON form carrying the same facts is always available via
// DivergenceReport.RenderMachineJson() / --report, keyed for programmatic assertions.
//
// On exit 2 (format/setup error: bad header, mismatched deepdump versions between legs, a leg
// with zero checkpoint lines, or an unmet --require-cross-arch requirement) there is no
// lockstep position to report -- the frame/channel/record section of the contract does not
// apply, and the report instead states which leg/check failed and why.
//
// DEGENERATE CASES THAT MUST NOT SILENTLY PASS (these are the ones that turn a red gate green
// if mishandled):
//   - dumps of different lengths where the shorter is a clean prefix of the longer: one leg's
//     run stopped early. This IS a divergence (exit 1), never a pass.
//   - a dump with zero V lines: the run produced no checkpoints at all. Exit 2 (malformed),
//     never exit 0 -- this also covers empty-vs-empty, which must never report success.
//
// Exit codes match ddiff.py exactly (0 identical, 1 divergent, 2 usage/format error) so the
// two tools are interchangeable for an operator.
// ============================================================================================

using System;
using System.IO;

namespace OpenSage.SimCore.DumpDiff;

internal static class Program
{
    private const string Usage =
        "usage: dumpdiff <A.dump> <B.dump> [--label-a NAME] [--label-b NAME] [--report <path>]\n" +
        "                 [--exclude-a SET] [--exclude-b SET] [--require-cross-arch]\n" +
        "\n" +
        "Lockstep-diffs two opensage-deepdump files and prints the first divergence.\n" +
        "Exit 0 = identical, 1 = divergent, 2 = usage/format error.";

    private static int Main(string[] args)
    {
        string? pathA = null, pathB = null;
        string? labelA = null, labelB = null;
        string? excludeA = null, excludeB = null;
        string? reportPath = null;
        var requireCrossArch = false;

        var positionals = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--label-a":
                    labelA = RequireValue(args, ref i, "--label-a");
                    break;
                case "--label-b":
                    labelB = RequireValue(args, ref i, "--label-b");
                    break;
                case "--exclude-a":
                    excludeA = RequireValue(args, ref i, "--exclude-a");
                    break;
                case "--exclude-b":
                    excludeB = RequireValue(args, ref i, "--exclude-b");
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i, "--report");
                    break;
                case "--require-cross-arch":
                    requireCrossArch = true;
                    break;
                case "-h":
                case "--help":
                    Console.Error.WriteLine(Usage);
                    return 2;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"dumpdiff: unrecognized option '{args[i]}'\n\n{Usage}");
                        return 2;
                    }
                    if (positionals == 0) { pathA = args[i]; }
                    else if (positionals == 1) { pathB = args[i]; }
                    else
                    {
                        Console.Error.WriteLine($"dumpdiff: unexpected extra argument '{args[i]}'\n\n{Usage}");
                        return 2;
                    }
                    positionals++;
                    break;
            }
        }

        if (pathA == null || pathB == null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string textA, textB;
        try
        {
            textA = File.ReadAllText(pathA);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"dumpdiff: cannot read {pathA}: {e.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"dumpdiff: cannot read {pathA}: {e.Message}");
            return 2;
        }

        try
        {
            textB = File.ReadAllText(pathB);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"dumpdiff: cannot read {pathB}: {e.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"dumpdiff: cannot read {pathB}: {e.Message}");
            return 2;
        }

        var options = new CompareOptions
        {
            LabelA = labelA ?? pathA,
            LabelB = labelB ?? pathB,
            ExcludeOverrideA = excludeA,
            ExcludeOverrideB = excludeB,
            RequireCrossArch = requireCrossArch,
        };

        var report = DumpComparator.Compare(textA, textB, options);

        Console.Out.WriteLine(report.RenderHuman());

        var json = report.RenderMachineJson();
        if (reportPath != null)
        {
            File.WriteAllText(reportPath, json + "\n");
        }
        else
        {
            Console.Out.WriteLine(json);
        }

        return report.ExitCode;
    }

    private static string RequireValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"dumpdiff: {option} requires a value\n\n{Usage}");
            Environment.Exit(2);
        }
        i++;
        return args[i];
    }
}
