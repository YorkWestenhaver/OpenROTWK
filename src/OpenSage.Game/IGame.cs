using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Audio;
using OpenSage.Client;
using OpenSage.Content;
using OpenSage.Data.Sav;
using OpenSage.Graphics;
using OpenSage.Gui;
using OpenSage.Gui.Apt;
using OpenSage.Gui.Wnd.Controls;
using OpenSage.Input;
using OpenSage.Input.Cursors;
using OpenSage.IO;
using OpenSage.Logic;
using OpenSage.Logic.Orders;
using OpenSage.Mathematics;
using OpenSage.Network;
using OpenSage.Scripting;
using OpenSage.SimCore.Orders;
using Veldrid;

namespace OpenSage;

public interface IGame
{
    CursorManager Cursors { get; }
    internal GraphicsLoadContext GraphicsLoadContext { get; }
    AssetStore AssetStore { get; }
    ContentManager ContentManager { get; }
    GraphicsDevice GraphicsDevice { get; }
    InputMessageBuffer InputMessageBuffer { get; }
    SkirmishManager SkirmishManager { get; set; }
    LobbyManager LobbyManager { get; }
    List<GameSystem> GameSystems { get; }

    /// <summary>
    /// Gets the graphics system.
    /// </summary>
    GraphicsSystem Graphics { get; }

    /// <summary>
    /// Gets the scripting system.
    /// </summary>
    ScriptingSystem Scripting { get; }

    /// <summary>
    /// Load lua script engine.
    /// </summary>
    LuaScriptEngine Lua { get; set; }

    /// <summary>
    /// Gets the selection system.
    /// </summary>
    SelectionSystem Selection { get; }

    /// <summary>
    /// Gets the order generator system.
    /// </summary>
    OrderGeneratorSystem OrderGenerator { get; }

    /// <summary>
    /// Gets the audio system
    /// </summary>
    AudioSystem Audio { get; }

    GameState GameState { get; }
    internal GameStateMap GameStateMap { get; }
    CampaignManager CampaignManager { get; }
    Terrain.TerrainLogic TerrainLogic { get; }
    Terrain.TerrainVisual TerrainVisual { get; }
    GhostObjectManager GhostObjectManager { get; }

    /// <summary>
    /// Is the game running?
    /// This is only false when the game is shutting down.
    /// </summary>
    bool IsRunning { get; }

    Action Restart { get; set; }

    /// <summary>
    /// Are we currently in a skirmish game?
    /// </summary>
    bool InGame { get; }

    float LogicUpdateScaleFactor { get; set; }

    /// <summary>
    /// Is the game running logic updates?
    /// Automatically starts and stops the map timer.
    /// </summary>
    bool IsLogicRunning { get; set; }

    /// <summary>
    /// The amount of time the game has been in this map while running logic updates.
    /// </summary>
    TimeInterval MapTime { get; }

    TimeInterval CurrentGameTime { get; }

    /// <summary>
    /// The amount of time the game has been rendering frames.
    /// </summary>
    TimeInterval RenderTime { get; }

    TimeSpan CumulativeLogicUpdateError { get; }

    /// <summary>
    /// The most recently completed logic frame number. Exposed publicly (unlike
    /// <c>GameLogic</c> itself, which is internal) so external callers - the launcher's
    /// <c>--exit-after-frames</c> termination and the periodic sim heartbeat
    /// (<c>HeadedSimSystems.OnPhase</c>) - can read it. Mirrors
    /// <c>GameLogic.CurrentFrame.Value</c>.
    /// </summary>
    uint CurrentLogicFrameNumber { get; }

    /// <summary>
    /// Count of <see cref="Update"/> calls since the game started - i.e. render/message-pump
    /// frames, distinct from the fixed 5 Hz logic frames counted by
    /// <see cref="CurrentLogicFrameNumber"/>. Used by the periodic sim heartbeat to report how
    /// far render has run ahead of logic.
    /// </summary>
    ulong RenderFrameCount { get; }

    IGameDefinition Definition { get; }
    SageGame SageGame { get; }
    Configuration Configuration { get; }
    string UserDataLeafName { get; }
    string UserDataFolder { get; }
    string UserAppDataFolder { get; }
    GamePanel Panel { get; }
    Viewport Viewport { get; }
    Scene2D Scene2D { get; }
    IScene3D Scene3D { get; set; }

    NetworkMessageBuffer NetworkMessageBuffer { get; set; }

    /// <summary>
    /// The scheduled-order buffer between the transport and the tick loop - the same
    /// <see cref="OpenSage.SimCore.Orders.OrderIngest"/> instance the game's
    /// <see cref="OpenSage.SimCore.Ticking.SimLoop"/> drains every frame (R15 packet BR-P4B).
    /// <para>
    /// Exposed on the interface because the buffer that fills it holds an <see cref="IGame"/>,
    /// and because the loop is constructed after the systems that reach it, so nobody can be
    /// handed the pipe at construction time. Null on a host with no loop attached.
    /// </para>
    /// </summary>
    OrderIngest Orders { get; }

    /// <summary>
    /// The legacy order dispatcher - the pre-SimCore path that actually moves units
    /// (A2-uiflow #2). Orders reach it two ways and no others: out of the DispatchOrders phase
    /// at their scheduled frame, and through <see cref="HeadedOrderSubmitter"/>'s fallback for
    /// order types with no verified SimCore translation.
    /// </summary>
    IOrderProcessor OrderProcessor { get; }

    /// <summary>
    /// The one place a locally-issued order enters the pipe (R15 packet BR-P4A's contract,
    /// implemented by <see cref="HeadedOrderSubmitter"/>). Rebuilt whenever
    /// <see cref="NetworkMessageBuffer"/> changes, and null while there is no buffer - i.e.
    /// whenever there is no game to give an order to.
    /// </summary>
    IOrderSubmitter OrderSubmitter { get; }

    Texture LauncherImage { get; }
    internal GameLogic GameLogic { get; }
    internal GameClient GameClient { get; }
    PlayerManager PlayerManager { get; }
    TeamFactory TeamFactory { get; }
    PartitionCellManager PartitionCellManager { get; }
    IGameEngine GameEngine { get; }

    event EventHandler<GameUpdatingEventArgs> Updating;

    /// <summary>
    /// Fired when a <see cref="Game.Render"/> completes, but before
    /// <see cref="Game.Panel"/>'s <see cref="GamePanel.Framebuffer"/>
    /// is copied to <see cref="GraphicsDevice.SwapchainFramebuffer"/>.
    /// Useful for drawing additional overlays.
    /// </summary>
    event EventHandler RenderCompleted;

    void LoadSaveFile(FileSystemEntry entry);
    void LoadReplayFile(FileSystemEntry replayFileEntry);
    void ShowMainMenu();
    Window LoadWindow(string wndFileName);
    AptWindow LoadAptWindow(string aptFileName);
    void StartCampaign(string side);
    void StartCampaign(string campaignName, string missionName);

    void StartSkirmishOrMultiPlayerGame(
        string mapFileName,
        IConnection connection,
        PlayerSetting[] playerSettings,
        int seed,
        bool isMultiPlayer);

    void StartSinglePlayerGame(string mapFileName);
    void EndGame();
    void StartRun();
    void Update(IEnumerable<InputMessage> messages);
    void Step();
    void Render();

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources
    /// </summary>
    void Dispose();

    Vector2 GetTopLeftUV();
    Vector2 GetBottomRightUV();
    IEnumerable<PlayerTemplate> GetPlayableSides();
    MappedImage GetMappedImage(string name);
    void Exit();

    /// <summary>
    /// The engine's one blessed source of randomness. Defaults to the deterministic SAGE
    /// generator (api-freeze-v1 F5, build-order step 3): <c>SystemRandom</c> was seeded from
    /// nothing reproducible, so replays and lockstep peers could not agree.
    /// <para>
    /// Every stream the engine runs comes from here and each gets its own instance, so client and
    /// audio draws can never shift the logic stream. Seeds: the logic stream takes the match seed
    /// (wired with the order pipeline, build-order step 4); client and audio streams derive
    /// locally, since their values are lockstep-irrelevant and never CRC'd.
    /// </para>
    /// </summary>
    IRandom CreateRandom(uint seed = 0) => new SageRandom(seed);
}
