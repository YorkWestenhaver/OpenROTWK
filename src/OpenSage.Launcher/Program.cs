using System;
using System.IO;
using System.Linq;
using System.Text;
using CommandLine;
using NLog;
using NLog.Targets;
using OpenSage.Data;
using OpenSage.Diagnostics;
using OpenSage.Graphics;
using OpenSage.Input;
using OpenSage.Logic;
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

        [Option('u', "uniqueports", Default = false, Required = false, HelpText = "Use a unique port for each client in a multiplayer game. Normally, port 8088 is used, but when we want to run multiple game instances on the same machine (for debugging purposes), each client needs a different port.")]
        public bool UseUniquePorts { get; set; }
    }

    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        LogManager.Setup().SetupExtensions(b => b.RegisterTarget<Core.InternalLogger>("OpenSage"));

        Parser.Default.ParseArguments<Options>(args)
          .WithParsed(opts => Run(opts));
    }

    private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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

        // TODO: Read game version from assembly metadata or .git folder
        // TODO: Set window icon.
        var config = new Configuration()
        {
            UseRenderDoc = opts.RenderDoc,
            LoadShellMap = !opts.NoShellmap,
            UseUniquePorts = opts.UseUniquePorts,
            SimHeartbeatIntervalInFrames = opts.TraceFrames,
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
                        Logger.Warn("Could not find MapCache entry for map " + opts.Map);
                        game.ShowMainMenu();
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

                            var pSettings = new PlayerSetting[]
                            {
                                new(1, faction1, new ColorRgb(255, 0, 0), 0, PlayerOwner.Player),
                                new(2, faction2, new ColorRgb(0, 255, 0), 0, aiOwner),
                            };

                            Logger.Debug($"Starting multiplayer game with factions '{faction1}' vs '{faction2}' (AI difficulty: {aiOwner})");

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

        if (traceEnabled)
        {
            GameTrace.Stop();
        }

        Platform.Stop();
    }
}
