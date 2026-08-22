using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using NLog;
using NLog.Targets;
using OpenSage.Data;
using OpenSage.Diagnostics;
using OpenSage.Graphics;
using OpenSage.Input;
using OpenSage.Logic;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Mathematics;
using OpenSage.Mods.BuiltIn;
using OpenSage.Network;
using Veldrid;

namespace OpenSage.Launcher;

public static class Program
{
    public sealed class Options
    {
        [Option('r', "renderer", Default = null, Required = false, HelpText = "Set the renderer backend (Direct3D11,Vulkan,OpenGL,Metal,OpenGLES).")]
        public GraphicsBackend? Renderer { get; set; }

        [Option("noshellmap", Default = false, Required = false, HelpText = "Disables loading the shell map, speeding up startup time.")]
        public bool NoShellmap { get; set; }

        [Option('g', "game", Default = SageGame.CncGenerals, Required = false, HelpText = "Chooses which game to start.")]
        public SageGame Game { get; set; }

        [Option('m', "map", Required = false, HelpText = "Immediately starts a new skirmish with default settings in the specified map. The map is looked up by its MapCache key (the map's registered name), not by file path.")]
        public string? Map { get; set; }

        [Option("faction", Default = null, Required = false, HelpText = "PlayerTemplate side name (e.g. FactionMen) for player 1 in a --map skirmish. Defaults to the first playable side reported by the current game/mod's asset store.")]
        public string? Faction { get; set; }

        [Option("faction2", Default = null, Required = false, HelpText = "PlayerTemplate side name (e.g. FactionMordor) for player 2 (the AI opponent) in a --map skirmish. Defaults to the second playable side reported by the current game/mod's asset store.")]
        public string? Faction2 { get; set; }

        [Option("ai-difficulty", Default = "Easy", Required = false, HelpText = "AI difficulty for player 2 in a --map skirmish: Easy, Medium, or Hard.")]
        public string AiDifficulty { get; set; } = "Easy";

        [Option("ai-vs-ai", Default = false, Required = false, HelpText = "In a --map skirmish, also set player 1 to AI-controlled (same --ai-difficulty as player 2) instead of human, for an unattended AI-vs-AI match.")]
        public bool AiVsAi { get; set; }

        [Option("ai-report", Default = null, Required = false, HelpText = "Write a bfme2-ai-match/report/v1 JSON match report (AiMatchReport) to this path when the process exits. Captures each skirmish-AI player's AiTrace heartbeats/counters from match start to match end.")]
        public string? AiReport { get; set; }

        [Option("novsync", Default = false, Required = false, HelpText = "Disable vsync.")]
        public bool DisableVsync { get; set; }

        [Option('f', "fullscreen", Default = false, Required = false, HelpText = "Enable fullscreen mode.")]
        public bool Fullscreen { get; set; }

        [Option('d', "renderdoc", Default = false, Required = false, HelpText = "Enable renderdoc debugging.")]
        public bool RenderDoc { get; set; }

        [Option("developermode", Default = false, Required = false, HelpText = "Enable developer mode.")]
        public bool DeveloperMode { get; set; }

        [Option("tracefile", Default = null, Required = false, HelpText = "Generate trace output to the specified path, for example `--tracefile trace.json`. Trace files can be loaded into Chrome's tracing GUI at chrome://tracing")]
        public string? TraceFile { get; set; }

        [Option("trace-frames", Default = 50, Required = false, HelpText = "Logic-frame interval (5 Hz) between periodic sim heartbeat emissions: a log line, plus a GameTrace instant event whenever --tracefile is also set. 0 disables the heartbeat.")]
        public int TraceFrames { get; set; }

        [Option("exit-after-frames", Default = null, Required = false, HelpText = "Exit the process once the logic-frame counter (5 Hz) reaches this many frames, for deterministic unattended termination. Omit to run until the window is closed.")]
        public int? ExitAfterFrames { get; set; }

        [Option("replay", Default = null, Required = false, HelpText = "Specify a replay file to immediately start replaying")]
        public string? ReplayFile { get; set; }

        [Option("save", Default = null, Required = false, HelpText = "Specify a save file to immediately load")]
        public string? SaveFile { get; set; }

        [Option('p', "gamepath", Default = null, Required = false, HelpText = "Force game to use this gamepath")]
        public string? GamePath { get; set; }

        [Option('b', "basegamepath", Default = null, Required = false, HelpText = "Force the game's base game to use this gamepath")]
        public string? BaseGamePath { get; set; }

        [Option("mod", Default = null, Required = false, HelpText = "Load a mod on top of the game, the same way the retail engine's -mod argument does. Accepts either a mod directory (its loose files and its .BIG archives are layered over the game) or a single .big archive.")]
        public string? ModPath { get; set; }

        [Option("headed-crc", Default = 0, Required = false, HelpText = "Logic-frame interval between deep-CRC checkpoints in this headed game, written to --headed-crc-out in the same 'opensage-deepdump v2' format the SimCore ScenarioDriver writes (so the two dumps are comparable with ddiff/DumpDiff). 0 (the default) disables the CRC entirely and the run is byte-identical to one built without this flag. Values above 100 are clamped to 100, as the retail interval is. Requires the developer speed multiplier to stay at 1x.")]
        public int HeadedCrcIntervalInFrames { get; set; }

        [Option("headed-crc-out", Default = null, Required = false, HelpText = "Path the --headed-crc dump is written to. Required whenever --headed-crc is non-zero; ignored otherwise.")]
        public string HeadedCrcOut { get; set; }

        [Option('u', "uniqueports", Default = false, Required = false, HelpText = "Use a unique port for each client in a multiplayer game. Normally, port 8088 is used, but when we want to run multiple game instances on the same machine (for debugging purposes), each client needs a different port.")]
        public bool UseUniquePorts { get; set; }
    }

    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        LogManager.Setup().SetupExtensions(b => b.RegisterTarget<Core.InternalLogger>("OpenSage"));

        InstallCrashHandlers();

        Parser.Default.ParseArguments<Options>(args)
          .WithParsed(opts => Run(opts));
    }

    private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // ---- OBS-2: crash observability -------------------------------------------------
    //
    // Two defects this closes, both proven by the R1 sweep:
    //   1. A managed crash printed a stack and nothing else - not the logic frame, not the
    //      object, not the map object being loaded. CrashContext (OpenSage.Core) carries that
    //      identity ambiently; here we format it into ONE greppable CRASH-CONTEXT-V1 line.
    //   2. THE INSTRUMENT DEFECT: --ai-report was only written after the game loop exited
    //      cleanly, so a crashing run produced zero M-a/M-b signal and the gate could not tell
    //      "the AI did nothing" from "the process died first". The report is now also flushed
    //      on the unhandled-exception path, marked partial=true.

    /// <summary>Set once a match with --ai-report is underway; invoked (once) by the crash path.</summary>
    private static Action? _flushPartialAiReport;

    /// <summary>0/1 latch so a crash emits exactly one CRASH-CONTEXT-V1 line per process.</summary>
    private static int _crashLineEmitted;

    private static void InstallCrashHandlers()
    {
        // Freeze the ambient context at the THROW site. Without this the context is always empty
        // by the time anyone formats it: `using` scopes dispose as the exception unwinds, so a
        // catch block one stack frame up already sees nothing. Verified against the frame-127
        // DozerAndWorkerState NRE, which reported "(no context)" until this hook existed.
        // Cost is an array copy of at most 32 structs per throw, tagged with the exception so a
        // handled throw cannot lend stale context to a later crash.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => CrashContext.CaptureThrowSnapshot(e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            EmitCrashContext(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled-terminating" : "unhandled");
        };

        // An unobserved task fault does not terminate the process on .NET Core, so this path
        // deliberately does NOT flush the AI report or consume the one-line latch: it reports a
        // background fault that would otherwise be invisible, without pre-empting the real crash
        // line for a crash that may still be coming.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Fatal(e.Exception, $"CRASH-CONTEXT (unobserved-task): {CrashContext.DescribeFor(e.Exception)}");
            WriteCrashLineToStderr(CrashContext.FormatCrashLine(e.Exception, "unobserved-task"));
        };
    }

    /// <summary>
    /// Emits the single machine-readable crash record (stderr AND the NLog file, because the
    /// harness reads the wrapper log and triage reads the NLog file), then flushes a partial
    /// AI match report if a match was underway.
    /// </summary>
    internal static void EmitCrashContext(Exception? exception, string phase)
    {
        if (Interlocked.Exchange(ref _crashLineEmitted, 1) != 0)
        {
            return;
        }

        var line = CrashContext.FormatCrashLine(exception, phase);

        WriteCrashLineToStderr(line);

        try
        {
            // Deliberately NOT the CRASH-CONTEXT-V1 line: NLog's default rules send Fatal to the
            // console as well as the file, and the harness wrapper log merges stdout+stderr, so
            // logging the marker here would put two identical V1 records in the wrapper log and
            // break "one record per crash" for OBS-4's dedup. The file target still gets the
            // same information - human-readable context plus the full exception.
            Logger.Fatal(exception, $"CRASH-CONTEXT ({phase}): {CrashContext.DescribeFor(exception)}");
            LogManager.Flush(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Never let the crash reporter throw out of a crash path.
        }

        FlushPartialAiReport();
    }

    private static void WriteCrashLineToStderr(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush();
        }
        catch
        {
            // Never let the crash reporter throw out of a crash path.
        }
    }

    private static void FlushPartialAiReport()
    {
        var flush = Interlocked.Exchange(ref _flushPartialAiReport, null);
        if (flush == null)
        {
            return;
        }

        try
        {
            flush();
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"--ai-report: partial flush failed: {ex}");
            }
            catch
            {
                // Swallowed: we are already unwinding a crash.
            }
        }
    }

    /// <summary>
    /// Captures the end-of-match snapshots and writes the report. Used by BOTH the clean exit
    /// path and the crash flush; <paramref name="partial"/> is the only difference. On the crash
    /// path the live capture may itself throw (that is how we got here), so it degrades to
    /// re-using the start snapshots rather than losing the report entirely - still marked
    /// partial, so a grader never mistakes it for a verdict.
    /// </summary>
    private static void WriteAiReport(
        Game game,
        IReadOnlyList<AiMatchReport.PlayerSnapshot> start,
        string path,
        bool partial)
    {
        IReadOnlyList<AiMatchReport.PlayerSnapshot> CaptureEnd() =>
            AiMatchReport.CaptureAll(AiMatchReport.SkirmishAiBrains(game.PlayerManager.Players));

        var report = partial
            ? AiMatchReport.BuildPartial(
                start,
                CaptureEnd,
                ex => Logger.Error(ex, "--ai-report: end-of-match capture failed on the crash path; falling back to the start snapshots."))
            : AiMatchReport.Build(start, CaptureEnd(), generatedAtUtc: null, partial: false);
        report.WriteToFile(path);
        Logger.Info($"--ai-report: wrote {path} (milestoneA={report.MilestoneA} milestoneB={report.MilestoneB} pass={report.Pass} partial={report.Partial})");
    }

    private static PlayerOwner LogUnknownDifficultyAndDefault(string? requested)
    {
        Logger.Warn($"Unknown --ai-difficulty value '{requested}' (expected Easy, Medium, or Hard); defaulting to Easy.");
        return PlayerOwner.EasyAi;
    }

    private static GameInstallation? GameFromPath(Options opts, SageGame game, string? path)
    {
        var UseLocators = true;

        path ??= Environment.CurrentDirectory;

        foreach (var gameDef in GameDefinition.All)
        {
            if (gameDef.Probe(path))
            {
                game = gameDef.Game;
                UseLocators = false;
            }
        }

        var definition = GameDefinition.FromGame(game);
        if (UseLocators)
        {
            return GameInstallation
                .FindAll(new[] { definition })
                .FirstOrDefault();
        }

        var baseGame = definition.BaseGame != null
            ? GameFromPath(opts, definition.BaseGame.Game, opts.BaseGamePath) // we shouldn't ever have more than one base game
            : null;
        return new GameInstallation(definition, path, baseGame);
    }

    public static void Run(Options opts)
    {
        Logger.Info("Starting...");

        var installation = GameFromPath(opts, opts.Game, opts.GamePath);

        if (installation != null && !string.IsNullOrEmpty(opts.ModPath))
        {
            Logger.Info($"Loading mod from {opts.ModPath}");
            installation = installation.WithMod(opts.ModPath);
        }

        if (installation == null)
        {
            var definition = GameDefinition.FromGame(opts.Game);
            Console.WriteLine($"OpenSAGE was unable to find any installations of {definition.DisplayName}.\n");

            Console.WriteLine("You can manually specify the installation path by setting the following environment variable:");
            Console.WriteLine($"\t{definition.Identifier.ToUpper()}_PATH=<installation path>\n");

            Console.WriteLine("OpenSAGE doesn't yet detect every released version of every game. Please report undetected versions to our GitHub page:");
            Console.WriteLine("\thttps://github.com/OpenSAGE/OpenSAGE/issues");

            Console.WriteLine("\n\n Press any key to exit.");

            Console.ReadLine();

            Environment.Exit(1);
        }

        Logger.Debug($"Have installation of {installation.Game.DisplayName}");

        Platform.Start();

        var traceEnabled = !string.IsNullOrEmpty(opts.TraceFile);
        if (traceEnabled)
        {
            GameTrace.Start(opts.TraceFile);
        }

        // R15 packet 5: --headed-crc is an argument error when it cannot be honoured, not a
        // silently-ignored flag. A run asked for a CRC dump and got none is the worst outcome
        // here: the operator only finds out after the match, when the comparison has nothing
        // to compare.
        if (opts.HeadedCrcIntervalInFrames < 0)
        {
            var message = $"--headed-crc must be 0 (off) or a positive frame interval, not {opts.HeadedCrcIntervalInFrames}. Aborting.";
            Logger.Error(message);
            Console.Error.WriteLine(message);
            Environment.Exit(2);
        }

        if (opts.HeadedCrcIntervalInFrames > 0 && string.IsNullOrEmpty(opts.HeadedCrcOut))
        {
            const string message = "--headed-crc needs --headed-crc-out <path>: there is nowhere to write the dump. Aborting.";
            Logger.Error(message);
            Console.Error.WriteLine(message);
            Environment.Exit(2);
        }

        var headedCrcInterval = (uint)opts.HeadedCrcIntervalInFrames;

        // TODO: Read game version from assembly metadata or .git folder
        // TODO: Set window icon.
        var config = new Configuration()
        {
            UseRenderDoc = opts.RenderDoc,
            LoadShellMap = !opts.NoShellmap,
            UseUniquePorts = opts.UseUniquePorts,
            SimHeartbeatIntervalInFrames = opts.TraceFrames,
            HeadedCrcIntervalInFrames = headedCrcInterval,
            HeadedCrcDumpPath = opts.HeadedCrcOut,
        };

        UPnP.InitializeAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => Logger.Info($"UPnP status: {UPnP.Status}"));

        Logger.Debug($"Have configuration");

        using (var window = new GameWindow($"OpenSAGE - {installation.Game.DisplayName} - master", 100, 100, 1024, 768, opts.Fullscreen))
        using (var game = new Game(installation, opts.Renderer, config, window))
        using (var textureCopier = new TextureCopier(game, window.Swapchain.Framebuffer.OutputDescription))
        using (var developerModeView = new DeveloperModeView(game, window))
        {
            game.GraphicsDevice.SyncToVerticalBlank = !opts.DisableVsync;

            var developerModeEnabled = opts.DeveloperMode;

            if (opts.DeveloperMode)
            {
                window.Maximized = true;
            }

            if (opts.ReplayFile != null)
            {
                var replayFile = game.ContentManager.UserDataFileSystem?.GetFile(Path.Combine("Replays", opts.ReplayFile));
                if (replayFile == null)
                {
                    Logger.Debug("Could not find entry for Replay " + opts.ReplayFile);
                    game.ShowMainMenu();
                }

                game.LoadReplayFile(replayFile);
            }
            else if (opts.SaveFile != null)
            {
                var saveFile = game.ContentManager.UserDataFileSystem?.GetFile(Path.Combine("Save", opts.SaveFile));
                if (saveFile == null)
                {
                    Logger.Debug("Could not find entry for Save " + opts.SaveFile);
                    game.ShowMainMenu();
                }

                game.LoadSaveFile(saveFile);
            }
            else if (opts.Map != null)
            {
                game.Restart = StartMap;
                StartMap();

                void StartMap()
                {
                    var mapCache = game.AssetStore.MapCaches.GetByName(opts.Map);
                    if (mapCache == null)
                    {
                        // A --map key that resolves to no MapCache entry is a fatal argument
                        // error: abort with a clear message instead of falling into the main
                        // menu / game loop, which NREs in Game.GameEngine with no game loaded.
                        var message =
                            $"--map '{opts.Map}' does not match any MapCache entry. " +
                            "Maps are looked up by their MapCache key (the map's registered name, " +
                            "e.g. 'maps\\mymap\\mymap.map'), not by file path. Aborting.";
                        Logger.Error(message);
                        Console.Error.WriteLine(message);
                        Environment.Exit(2);
                    }
                    else if (mapCache.IsMultiplayer)
                    {
                        // Pattern: SkirmishManager.cs:78 (Game.GetPlayableSides().ElementAt(...).Name).
                        var playableSides = game.GetPlayableSides().ToList();

                        var faction1 = opts.Faction ?? playableSides.ElementAtOrDefault(0)?.Name;
                        var faction2 = opts.Faction2 ?? playableSides.ElementAtOrDefault(1)?.Name;

                        if (faction1 == null || faction2 == null)
                        {
                            Logger.Warn(
                                "Could not derive default --faction/--faction2 values: the current game/mod's " +
                                "asset store reports fewer than 2 playable sides. Falling back to the main menu.");
                            game.ShowMainMenu();
                        }
                        else
                        {
                            var aiOwner = opts.AiDifficulty?.Trim().ToLowerInvariant() switch
                            {
                                "easy" => PlayerOwner.EasyAi,
                                "medium" => PlayerOwner.MediumAi,
                                "hard" => PlayerOwner.HardAi,
                                var unknown => LogUnknownDifficultyAndDefault(unknown)
                            };

                            // --ai-vs-ai: player 1 gets the same AI ownership as player 2 instead
                            // of PlayerOwner.Player, so PlayerManager.OnNewGame -> Player.FromMapData
                            // attaches a SkirmishAIBrain (AI.Skirmish.SkirmishAIBrains.AttachTo) to
                            // both slots and no human input is required to reach a verdict.
                            var player1Owner = opts.AiVsAi ? aiOwner : PlayerOwner.Player;

                            var pSettings = new PlayerSetting[]
                            {
                                new(1, faction1, new ColorRgb(255, 0, 0), 0, player1Owner),
                                new(2, faction2, new ColorRgb(0, 255, 0), 0, aiOwner),
                            };

                            Logger.Debug($"Starting multiplayer game with factions '{faction1}' vs '{faction2}' (AI difficulty: {aiOwner}, ai-vs-ai: {opts.AiVsAi})");

                            game.StartSkirmishOrMultiPlayerGame(opts.Map,
                                new EchoConnection(),
                                pSettings,
                                Environment.TickCount,
                                false);
                        }
                    }
                    else
                    {
                        Logger.Debug("Starting singleplayer game");

                        game.StartSinglePlayerGame(opts.Map);
                    }
                }
            }
            else
            {
                Logger.Debug("Showing main menu");
                game.ShowMainMenu();
            }

            // --ai-report: capture every skirmish-AI player's AiTrace state right after the
            // match started (whichever branch above started it), so the report written at exit
            // can tell "money rose" from a real start value rather than assuming 0.
            IReadOnlyList<AiMatchReport.PlayerSnapshot>? aiReportStart = null;
            if (!string.IsNullOrEmpty(opts.AiReport))
            {
                var brains = AiMatchReport.SkirmishAiBrains(game.PlayerManager.Players);
                aiReportStart = AiMatchReport.CaptureAll(brains);
                Logger.Info($"--ai-report: capturing {aiReportStart.Count} skirmish-AI player(s) for {opts.AiReport}");

                // OBS-2: arm the crash-path flush. From here until the clean write below, an
                // unhandled exception still produces an --ai-report file (partial=true) instead
                // of nothing, so a crashing run yields graded M-a/M-b signal for the frames it
                // did manage to run.
                var reportStart = aiReportStart;
                var reportPath = opts.AiReport;
                _flushPartialAiReport = () => WriteAiReport(game, reportStart, reportPath, partial: true);
            }

            game.InputMessageBuffer.Handlers.Add(
                new CallbackMessageHandler(
                    HandlingPriority.Window,
                    message =>
                    {
                        if (message.MessageType != InputMessageType.KeyDown)
                            return InputMessageResult.NotHandled;

                        if (message.Value.Key == Key.Enter && (message.Value.Modifiers & ModifierKeys.Alt) != 0)
                        {
                            window.Fullscreen = !window.Fullscreen;
                            return InputMessageResult.Handled;
                        }

                        if (message.Value.Key == Key.D && (message.Value.Modifiers & ModifierKeys.Alt) != 0)
                        {
                            developerModeEnabled = !developerModeEnabled;
                            return InputMessageResult.Handled;
                        }

                        return InputMessageResult.NotHandled;
                    }));

            Logger.Debug("Starting game");

            game.StartRun();

            // OBS-2: the game loop is the crash surface the sweep actually hits. Catching here
            // (rather than relying only on the AppDomain hook) means the partial report is
            // written while `game` is still alive and undisposed; the exception is rethrown
            // unchanged, so the process exit code and the AppDomain hook's stack are unaffected.
            try
            {
                while (game.IsRunning)
                {
                    if (!window.PumpEvents())
                    {
                        break;
                    }

                    if (developerModeEnabled)
                    {
                        developerModeView.Tick();
                    }
                    else
                    {
                        game.Update(window.MessageQueue);

                        game.Panel.EnsureFrame(window.ClientBounds);

                        game.Render();

                        textureCopier.Execute(
                            game.Panel.Framebuffer.ColorTargets[0].Target,
                            window.Swapchain.Framebuffer);
                    }

                    window.MessageQueue.Clear();

                    game.GraphicsDevice.SwapBuffers(window.Swapchain);

                    if (opts.ExitAfterFrames.HasValue && game.CurrentLogicFrameNumber >= (uint)opts.ExitAfterFrames.Value)
                    {
                        Logger.Info($"Exiting after reaching logic frame {game.CurrentLogicFrameNumber} (--exit-after-frames {opts.ExitAfterFrames.Value})");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                EmitCrashContext(ex, "game-loop");
                throw;
            }

            // --ai-report: written here, still inside the `game` using-block, so the final
            // capture reads live World/Trace state before disposal - never after.
            if (aiReportStart != null && !string.IsNullOrEmpty(opts.AiReport))
            {
                // Clean exit: disarm the crash flush first, so nothing can overwrite this
                // complete report with a partial one during teardown.
                Interlocked.Exchange(ref _flushPartialAiReport, null);

                WriteAiReport(game, aiReportStart, opts.AiReport, partial: false);
            }
        }

        if (traceEnabled)
        {
            GameTrace.Stop();
        }

        Platform.Stop();
    }
}
