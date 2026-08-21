// A headless, graphics-free IGame host for the Round-4 module pipeline: mocked-game unit
// tests and the harness scenario driver both stand real GameObjects with real behavior
// modules on it and tick real GameLogic frames - no renderer, no audio, no files on disk.
//
// This is deliberately the same shape as OpenSage.Game.Tests' TestGame (the established
// mocked-game pattern), plus the three pieces module PORTS need that TestGame lacks:
// a live quadtree (the ISimContext partition seam queries it), a PartitionCellManager,
// and a Scene3D null-object so GameLogic.CreateObject's registration path runs. INI
// content is parsed from in-memory text through the real IniParser, so a test's object
// definitions exercise the real quantizing parse functions (S5).
//
// Not a shipped code path: nothing in the product constructs this type.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSage.Audio;
using OpenSage.Client;
using OpenSage.Content;
using OpenSage.Content.Loaders;
using OpenSage.Data.Ini;
using OpenSage.Data.Map;
using OpenSage.Data.Sav;
using OpenSage.DataStructures;
using OpenSage.Graphics;
using OpenSage.Graphics.Cameras;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Graphics.Rendering;
using OpenSage.Graphics.Rendering.Shadows;
using OpenSage.Graphics.Rendering.Water;
using OpenSage.Gui;
using OpenSage.Gui.Apt;
using OpenSage.Gui.DebugUI;
using OpenSage.Gui.Wnd.Controls;
using OpenSage.Input;
using OpenSage.Input.Cursors;
using OpenSage.IO;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Mathematics;
using OpenSage.Network;
using OpenSage.Rendering;
using OpenSage.Scripting;
using OpenSage.Settings;
using OpenSage.Terrain;
using OpenSage.Terrain.Roads;
using OpenSage.Utilities;
using Veldrid;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Logic.Sim;

internal sealed class HeadlessSimGame : IGame
{
    private static readonly Encoding IniEncoding;

    static HeadlessSimGame()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        IniEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }

    private readonly HeadlessFileSystem _fileSystem = new();
    private readonly IniDataContext _iniDataContext = new();
    private int _iniFileCounter;

    public IQuadtree<GameObject> Quadtree { get; }

    public HeadlessSimGame(SageGame sageGame, uint matchSeed = 0)
    {
        SageGame = sageGame;

        AssetStore = new AssetStore(sageGame, null, null, null, null, null, null, OnDemandAssetLoadStrategy.None);

        Quadtree = new Quadtree<GameObject>(new RectangleF(-10000, -10000, 20000, 20000));

        // A real ObjectCreationListManager: the Die batch's spawning classes (CreateObjectDie,
        // CreateCrateDie, EjectPilotDie) reach it through ISimContext, and it is pure logic -
        // it creates GameObjects through GameLogic, touching no graphics.
        // A real Radar (not null): sabotage/infiltration-style collide handlers report
        // through IGameEngine.Radar, and a port that only compiles against a null Radar
        // would never notice a missing null-guard until it ran in a real game.
        GameEngine = new GameEngine(
            AssetStore.LoadContext, null, null, new ObjectCreationListManager(), null, null, new Radar(),
            Quadtree, new HeadlessScene3D(this), this);

        AssetStore.PushScope();
        AssetStore.Ranks.Add(new RankTemplate { InternalId = 1 });

        PlayerManager = new PlayerManager(this);
        PlayerManager.OnNewGame(
            [OpenSage.Data.Map.Player.CreateNeutralPlayer(), OpenSage.Data.Map.Player.CreateCivilianPlayer()],
            GameType.Skirmish);

        TerrainLogic = new TerrainLogic();
        TerrainLogic.SetHeightMapData(HeightMapData.Create(0, new ushort[2, 2] { { 0, 0 }, { 0, 0 } }));

        PartitionCellManager = new PartitionCellManager(this);

        // A real GameClient, because the LOGIC reads through it: GameObject.Drawable is
        // created by GameClient.CreateDrawable, and GameObject.ModelConditionFlags - which
        // GameObject.OnDie sets on every death (IsBeingConstructed, the Damaged flags) -
        // dereferences it. Without this a headless object cannot die. Drawables here carry
        // no draw modules (the headless INI declares none) and no graphics device is
        // touched; the client RNG stream stays separate from the logic stream (F5).
        GameClient = new GameClient(this);

        GameLogic = new GameLogic(this);
        GameLogic.Random.Initialize(matchSeed);

        Scene3D = GameEngine.Scene3D;
    }

    /// <summary>Parses INI text (Object/Upgrade/... blocks) into this game's asset scope.</summary>
    public void LoadIniText(string source)
    {
        var filePath = $@"Data\INI\headless{_iniFileCounter++}.ini";
        var parser = new IniParser(
            source,
            filePath,
            Path.GetDirectoryName(filePath),
            _fileSystem,
            AssetStore,
            SageGame,
            _iniDataContext,
            IniEncoding);
        parser.ParseFile();
        if (parser.ParseErrors.Count > 0)
        {
            throw new InvalidOperationException($"INI parse errors: {string.Join("; ", parser.ParseErrors.Select(e => $"{e.Position}: {e.Message}"))}");
        }
    }

    /// <summary>Creates a real object (modules, sleepy-update registration, quadtree) at a position.</summary>
    public GameObject SpawnObject(string definitionName, Player owner, in Vector3 position)
    {
        var definition = AssetStore.ObjectDefinitions.GetByName(definitionName)
            ?? throw new ArgumentException($"No object definition named {definitionName}");
        var gameObject = GameLogic.CreateObject(definition, owner);
        gameObject.UpdateTransform(position);
        gameObject.UpdateColliders();   // refresh collider bounds so spatial queries see the position
        return gameObject;
    }

    /// <summary>
    /// Runs one 5 Hz logic frame: sleepy module updates, the frame advances, then the
    /// destroy list is reaped - the same two halves a real frame runs (GameLogic.Update
    /// followed by Scene3D.LogicTick's DeleteDestroyed). Without the reap a killed object
    /// stays in the object list and keeps ticking, which is exactly what a Die test must
    /// not see.
    /// </summary>
    public void Step()
    {
        GameLogic.Update();
        GameLogic.DeleteDestroyed();
    }

    public Player CivilianPlayer => PlayerManager.GetCivilianPlayer();

    /// <summary>
    /// The "local player" a legacy module reads through <c>IGameEngine.Scene3D.LocalPlayer</c>
    /// (e.g. an EVA/UI feedback gate on "is this happening to ME"). Null by default, matching
    /// a host with no seated local player; tests that exercise a local-player branch set it.
    /// </summary>
    public Player LocalPlayer
    {
        get => ((HeadlessScene3D)Scene3D).LocalPlayer;
        set => ((HeadlessScene3D)Scene3D).LocalPlayer = value;
    }

    // ---- the mocked-game IGame surface (TestGame pattern) ----

    public IGameDefinition Definition { get; }
    public SageGame SageGame { get; }
    public Configuration Configuration { get; }
    public string UserDataLeafName { get; }
    public string UserDataFolder { get; }
    public string UserAppDataFolder { get; }
    public GamePanel Panel { get; }
    public Viewport Viewport { get; }
    public CursorManager Cursors { get; }
    public GraphicsLoadContext GraphicsLoadContext { get; }
    public AssetStore AssetStore { get; }
    public ContentManager ContentManager { get; }
    public GraphicsDevice GraphicsDevice { get; }
    public InputMessageBuffer InputMessageBuffer { get; }
    public SkirmishManager SkirmishManager { get; set; }
    public LobbyManager LobbyManager { get; }
    public List<GameSystem> GameSystems { get; }
    public GraphicsSystem Graphics { get; }
    public ScriptingSystem Scripting { get; }
    public LuaScriptEngine Lua { get; set; }
    public GameState GameState { get; }
    public GameStateMap GameStateMap { get; }
    public CampaignManager CampaignManager { get; }
    public TerrainLogic TerrainLogic { get; }
    public TerrainVisual TerrainVisual { get; }
    public GhostObjectManager GhostObjectManager { get; }
    public bool IsRunning { get; }
    public Action Restart { get; set; }
    public Scene2D Scene2D { get; }
    public IScene3D Scene3D { get; set; }
    public NetworkMessageBuffer NetworkMessageBuffer { get; set; }
    public Texture LauncherImage { get; }
    public GameLogic GameLogic { get; }
    public GameClient GameClient { get; }  // set in the constructor above
    public PlayerManager PlayerManager { get; }
    public TeamFactory TeamFactory { get; }
    public PartitionCellManager PartitionCellManager { get; }
    public bool InGame { get; }
    public float LogicUpdateScaleFactor { get; set; }
    public bool IsLogicRunning { get; set; }
    public TimeInterval MapTime { get; }
    public TimeInterval CurrentGameTime { get; }
    public TimeInterval RenderTime { get; }
    public TimeSpan CumulativeLogicUpdateError { get; }
    public OrderGeneratorSystem OrderGenerator { get; }
    public AudioSystem Audio { get; }
    public SelectionSystem Selection { get; }
    public IGameEngine GameEngine { get; }

#pragma warning disable CS0067
    public event EventHandler<GameUpdatingEventArgs> Updating;
    public event EventHandler RenderCompleted;
#pragma warning restore CS0067

    public void LoadSaveFile(FileSystemEntry entry) => throw new NotSupportedException();
    public void LoadReplayFile(FileSystemEntry replayFileEntry) => throw new NotSupportedException();
    public void ShowMainMenu() => throw new NotSupportedException();
    public Window LoadWindow(string wndFileName) => throw new NotSupportedException();
    public AptWindow LoadAptWindow(string aptFileName) => throw new NotSupportedException();
    public void StartCampaign(string side) => throw new NotSupportedException();
    public void StartCampaign(string campaignName, string missionName) => throw new NotSupportedException();
    public void StartSkirmishOrMultiPlayerGame(string mapFileName, IConnection connection, PlayerSetting[] playerSettings, int seed, bool isMultiPlayer) => throw new NotSupportedException();
    public void StartSinglePlayerGame(string mapFileName) => throw new NotSupportedException();
    public void EndGame() => throw new NotSupportedException();
    public void StartRun() => throw new NotSupportedException();
    public void Update(IEnumerable<InputMessage> messages) => throw new NotSupportedException();
    void IGame.Step() => Step();
    public void Render() => throw new NotSupportedException();
    public void Dispose() { }
    public Vector2 GetTopLeftUV() => throw new NotSupportedException();
    public Vector2 GetBottomRightUV() => throw new NotSupportedException();
    public IEnumerable<PlayerTemplate> GetPlayableSides() => throw new NotSupportedException();
    public MappedImage GetMappedImage(string name) => throw new NotSupportedException();
    public void Exit() => throw new NotSupportedException();

    /// <summary>In-memory file system: the host takes no files from disk.</summary>
    private sealed class HeadlessFileSystem : FileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public override FileSystemEntry GetFile(string filePath) =>
            _files.TryGetValue(NormalizeFilePath(filePath), out var contents)
                ? new FileSystemEntry(this, NormalizeFilePath(filePath), (uint)contents.Length,
                    () => new MemoryStream(Encoding.ASCII.GetBytes(contents)))
                : null;

        public override IEnumerable<FileSystemEntry> GetFilesInDirectory(
            string directoryPath, string searchPattern = "*",
            SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            yield break;
        }
    }

    /// <summary>Null-object scene: just enough for GameLogic.CreateObject's registration.</summary>
    private sealed class HeadlessScene3D : IScene3D
    {
        private readonly HeadlessSimGame _game;

        public HeadlessScene3D(HeadlessSimGame game) => _game = game;

        public IEditorCameraController EditorCameraController => null;
        public IGameEngine GameEngine => _game.GameEngine;
        public SelectionGui SelectionGui => null;
        public DebugOverlay DebugOverlay => null;
        public ParticleSystemManager ParticleSystemManager => null;
        public Camera Camera => null;
        public TacticalView TacticalView => null;
        public MapFile MapFile => null;
        public Terrain.Terrain Terrain => null;
        public IQuadtree<GameObject> Quadtree => _game.Quadtree;
        public bool ShowTerrain { get; set; }
        public WaterAreaCollection WaterAreas => null;
        public bool ShowWater { get; set; }
        public RoadCollection Roads => null;
        public bool ShowRoads { get; set; }
        public Bridge[] Bridges => null;
        public bool ShowBridges { get; set; }
        public bool FrustumCulling { get; set; }
        public PlayerScriptsList PlayerScripts => null;
        public IGameObjectCollection GameObjects => null;
        public bool ShowObjects { get; set; }
        public CameraCollection Cameras => null;
        public WaypointCollection Waypoints => null;
        public WorldLighting Lighting => null;
        public ShadowSettings Shadows => null;
        public WaterSettings Waters => null;
        public IReadOnlyList<Player> Players => null;
        public Player LocalPlayer { get; set; }
        public Navigation.Navigation Navigation => null;
        public AudioSystem Audio => null;
        public AssetLoadContext AssetLoadContext => _game.AssetStore.LoadContext;
        public Radar Radar => null;
        public IGame Game => _game;
        public GameObject BuildPreviewObject { get; set; }
        public RenderScene RenderScene => null;
        public RadarDrawUtil RadarDrawUtil => null;
        public int GetPlayerIndex(Player player) => 0;
        public void LogicTick(in TimeInterval time) { }
        public void LocalLogicTick(in TimeInterval gameTime, float tickT) { }
        public void BuildRenderList(RenderList renderList, Camera camera, in TimeInterval gameTime) { }
        public void Render(DrawingContext2D drawingContext) { }
        public GameObject CreateSkirmishPlayerStartingBuilding(in PlayerSetting playerSetting, Player player) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
