// R15 packet HUD-TEST (L1 lane) - the grading half of HUD-WIRE.
//
// WHY THIS FILE LOOKS LIKE THIS
// -----------------------------
// AptControlBar (src/OpenSage.Mods.Bfme/Gui/AptControlBarSource.cs) cannot be tested the
// ordinary way, for three independent reasons, each of which this harness works around
// explicitly rather than by pretending it does not exist:
//
//   1. The class is INTERNAL to OpenSage.Mods.Bfme and that assembly grants no
//      InternalsVisibleTo. Only the factory, AptControlBarSource, is public. So the type is
//      reached through Assembly.GetType and the two command-button methods through private
//      MethodInfo. Nothing here reaches into OpenSage.Mods.Bfme by source reference except
//      the public factory, which is what anchors the assembly.
//
//   2. Its constructor immediately dereferences _game.AssetStore.InGameUI.Current (font,
//      point size, bold) - a real InGameUI asset no headless test has. So the instance is
//      allocated with RuntimeHelpers.GetUninitializedObject and the two fields the command
//      button code actually reads, _game and _root, are planted directly. Skipping the ctor
//      is safe here precisely because neither ClearCommandbuttons nor the pre-AVM part of
//      UpdateCommandbuttons touches _font/_fontSize/_fontColor.
//
//   3. Past the CreateContent call, UpdateCommandbuttons needs a live ActionScript VM
//      (_window.Context.Avm) that no headless test can stand up. That is the wall the packet
//      was written around: every Update assertion below stops at a guard that returns BEFORE
//      the CreateContent call, and the CreateContent/Items[1] path proper is graded through
//      the pure policy seam in AptControlBarPolicyTests instead.
//
// WHAT IS BEING GRADED
// --------------------
// The two defects HUD-WIRE fixes:
//
//   * SKIP ASYMMETRY. ClearCommandbuttons skips its whole loop body for
//     Bfme/Bfme2/Bfme2Rotwk ("we do not know how bfme handles this yet"), while
//     UpdateCommandbuttons skips only for Bfme. Under Bfme2Rotwk that means buttons are
//     drawn and never erased: deselect a unit and the previous unit's command buttons stay
//     on screen with live RenderCallbacks. Bfme2RotwkClear_HidesEveryPlaceholder is the
//     assertion for the fixed behaviour, and CncGeneralsClear_HidesEveryPlaceholder is its
//     control - it proves the harness itself drives the loop, so the Bfme2Rotwk test failing
//     is an engine fact and not broken scaffolding.
//
//   * CONSTANTS GUARD. ClearCommandbuttons returns early when the CommandButtons object has
//     no Constants; UpdateCommandbuttons has no such guard at all. Both guard directions are
//     asserted here.
//
// The selection poke is real: a Player is constructed, a GameObject is planted into its
// private _selectedUnits set, and the scene handed to the control bar returns that Player as
// LocalPlayer - so UpdateCommandbuttons takes the Count > 0 branch for real rather than by
// stipulation.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenSage.Audio;
using OpenSage.Content.Loaders;
using OpenSage.Data.Map;
using OpenSage.DataStructures;
using OpenSage.Graphics.Cameras;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Graphics.Rendering;
using OpenSage.Graphics.Rendering.Shadows;
using OpenSage.Graphics.Rendering.Water;
using OpenSage.Gui;
using OpenSage.Gui.Apt;
using OpenSage.Gui.Apt.ActionScript;
using OpenSage.Gui.DebugUI;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Mathematics;
using OpenSage.Mods.Bfme;
using OpenSage.Rendering;
using OpenSage.Scripting;
using OpenSage.Settings;
using OpenSage.Terrain;
using OpenSage.Terrain.Roads;
using Xunit;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Tests.Mods.Bfme;

public class AptControlBarSelectionPathTests : MockedGameTest
{
    /// <summary>
    /// The six Palantir command-button slots the control bar walks (i = 1..6, addressed as
    /// members "0".."5"). Mirrors the literal loop bound in both command-button methods.
    /// </summary>
    private const int SlotCount = 6;

    // ---------------------------------------------------------------------------------
    // ClearCommandbuttons - the Constants guard
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Constants.Count == 0 means the Apt CommandButtons object was never populated, so its
    /// numbered members do not exist and walking them would fault. The guard must win even
    /// for a SageGame whose loop body is otherwise live (CncGenerals), which is why this test
    /// does NOT use a Bfme game: under the current code Bfme* skips the body anyway and the
    /// test would pass for the wrong reason.
    /// </summary>
    [Fact]
    public void ClearWithNoConstants_IsANoOp_EvenForAGameWhoseLoopBodyIsLive()
    {
        var bar = Harness.Create(NewGame(SageGame.CncGenerals), constantsCount: 0, out var slots);

        bar.InvokeClear();

        AssertAllSlotsUntouched(slots);
    }

    /// <summary>
    /// Control for the test above: same game, same harness, Constants populated. If this one
    /// ever goes red the harness has stopped driving the loop and every other assertion in
    /// this file is meaningless.
    /// </summary>
    [Fact]
    public void CncGeneralsClear_HidesEveryPlaceholder_AndDropsEveryRenderCallback()
    {
        var bar = Harness.Create(NewGame(SageGame.CncGenerals), constantsCount: SlotCount, out var slots);

        bar.InvokeClear();

        AssertAllSlotsCleared(slots);
    }

    // ---------------------------------------------------------------------------------
    // ClearCommandbuttons - the skip asymmetry (the HUD-WIRE fix)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// THE defect. Bfme2Rotwk is the game AotR runs as, and UpdateCommandbuttons happily
    /// paints its six slots (it skips only for SageGame.Bfme), so ClearCommandbuttons must
    /// erase them again when the selection empties. Red until HUD-WIRE makes the two skip
    /// predicates the same predicate.
    /// </summary>
    [Fact]
    public void Bfme2RotwkClear_HidesEveryPlaceholder_AndDropsEveryRenderCallback()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme2Rotwk), constantsCount: SlotCount, out var slots);

        bar.InvokeClear();

        AssertAllSlotsCleared(slots);
    }

    /// <summary>
    /// Same defect, the Bfme2 half. Bfme2 is skipped by Clear and driven by Update for
    /// exactly the same reason, so it strands buttons in exactly the same way.
    /// </summary>
    [Fact]
    public void Bfme2Clear_HidesEveryPlaceholder_AndDropsEveryRenderCallback()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme2), constantsCount: SlotCount, out var slots);

        bar.InvokeClear();

        AssertAllSlotsCleared(slots);
    }

    /// <summary>
    /// The one game that must KEEP skipping: Bfme1's Palantir layout is genuinely unknown and
    /// UpdateCommandbuttons skips it too, so Clear and Update stay symmetric by both doing
    /// nothing. This test pins that the asymmetry fix is not applied by simply deleting the
    /// SageGame check outright.
    /// </summary>
    [Fact]
    public void BfmeClear_LeavesEverySlotUntouched_BecauseUpdateNeverPaintedThem()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme), constantsCount: SlotCount, out var slots);

        bar.InvokeClear();

        AssertAllSlotsUntouched(slots);
    }

    // ---------------------------------------------------------------------------------
    // UpdateCommandbuttons - the selection path
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Selection empty: the very first guard (LocalPlayer.SelectedUnits.Count == 0) returns
    /// before anything is read off the Apt tree. Update is not the method that clears - that
    /// is the whole point of the Update/Clear split at the Update(Player) dispatch site.
    /// </summary>
    [Fact]
    public void UpdateWithEmptySelection_ReturnsBeforeTouchingAnySlot()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme2Rotwk), constantsCount: SlotCount, out var slots);
        bar.SetSelection(selectedUnitCount: 0);

        bar.InvokeUpdate();

        AssertAllSlotsUntouched(slots);
    }

    /// <summary>
    /// A real poked selection whose unit carries no CommandSet. This reaches the third guard
    /// (Definition.CommandSet == null) and returns - which is what lets it run headlessly: it
    /// stops short of the CreateContent call and therefore never needs the ActionScript VM.
    /// It is the deepest point of the Update path a headless test can reach today.
    /// </summary>
    [Fact]
    public void UpdateWithSelectedUnitLackingACommandSet_ReturnsBeforeTouchingAnySlot()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme2Rotwk), constantsCount: SlotCount, out var slots);
        bar.SetSelection(selectedUnitCount: 1);

        bar.InvokeUpdate();

        AssertAllSlotsUntouched(slots);
    }

    /// <summary>
    /// The Constants guard, Update side. Today ClearCommandbuttons has it and
    /// UpdateCommandbuttons does not; HUD-WIRE applies it symmetrically. With no Constants
    /// and a real selection, Update must return rather than address members that do not
    /// exist. Red until the guard is added.
    /// </summary>
    [Fact]
    public void UpdateWithNoConstants_ReturnsInsteadOfAddressingAbsentMembers()
    {
        var bar = Harness.Create(NewGame(SageGame.Bfme2Rotwk), constantsCount: 0, out var slots);
        bar.SetSelection(selectedUnitCount: 1);

        var thrown = Record.Exception(() => bar.InvokeUpdate());

        Assert.Null(thrown);
        AssertAllSlotsUntouched(slots);
    }

    // ---------------------------------------------------------------------------------
    // Assertions
    // ---------------------------------------------------------------------------------

    private static void AssertAllSlotsCleared(IReadOnlyList<Slot> slots)
    {
        Assert.Equal(SlotCount, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.False(
                slots[i].Placeholder.Visible,
                $"slot {i}: placeholder should have been hidden by ClearCommandbuttons");
            Assert.Null(slots[i].Shape.RenderCallback);
        }
    }

    private static void AssertAllSlotsUntouched(IReadOnlyList<Slot> slots)
    {
        Assert.Equal(SlotCount, slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            Assert.True(slots[i].Placeholder.Visible, $"slot {i}: placeholder should still be visible");
            Assert.NotNull(slots[i].Shape.RenderCallback);
        }
    }

    // ---------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------

    /// <summary>One Palantir button slot's two observable outputs.</summary>
    private sealed record Slot(SpriteItem Placeholder, RenderItem Shape);

    /// <summary>
    /// The mocked host. Built here in the derived class rather than inside <see cref="Harness"/>
    /// because MockedGameTest.TestGame is private protected, and handed on as IGame - which is
    /// the field type AptControlBar actually declares and which already exposes a settable
    /// Scene3D.
    /// </summary>
    private static IGame NewGame(SageGame sageGame) => new TestGame(sageGame);

    /// <summary>
    /// Builds a minimal but genuine Apt object graph shaped exactly like the one the two
    /// command-button methods walk:
    ///
    ///   _root.ScriptObject
    ///     -> "CommandButtons" (ObjectContext, N Constants)
    ///          -> "0".."5" (ObjectContext)
    ///               -> "placeholder" (ObjectContext whose Item is a SpriteItem)
    ///                    -> Content.Items[1] (RenderItem with a non-null RenderCallback)
    ///
    /// Nothing is mocked: these are the engine's own ObjectContext / SpriteItem /
    /// DisplayList / RenderItem types, allocated without their constructors where those
    /// constructors demand an AptContext the test has no way to build.
    /// </summary>
    private sealed class Harness
    {
        private readonly object _bar;
        private readonly IGame _game;

        private Harness(object bar, IGame game)
        {
            _bar = bar;
            _game = game;
        }

        public static Harness Create(IGame game, int constantsCount, out IReadOnlyList<Slot> slots)
        {
            var root = BuildRoot(constantsCount, out slots);

            var barType = ControlBarType;
            var bar = RuntimeHelpers.GetUninitializedObject(barType);
            GC.SuppressFinalize(bar);
            SetField(bar, "_game", game);
            SetField(bar, "_root", root);

            return new Harness(bar, game);
        }

        /// <summary>
        /// Pokes a selection for real: a Player owned by the mocked game, its private
        /// selection set filled with <paramref name="selectedUnitCount"/> GameObjects that
        /// carry a bare ObjectDefinition (hence a null CommandSet), and a scene that hands
        /// that Player back as LocalPlayer.
        /// </summary>
        public void SetSelection(int selectedUnitCount)
        {
            var player = new Player(0, null, new ColorRgb(255, 255, 255), _game);

            if (selectedUnitCount > 0)
            {
                var selected = (HashSet<GameObject>)GetField(player, "_selectedUnits");
                for (var i = 0; i < selectedUnitCount; i++)
                {
                    var unit = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
                    GC.SuppressFinalize(unit);
                    SetField(unit, nameof(GameObject.Definition), new ObjectDefinition());
                    selected.Add(unit);
                }
            }

            _game.Scene3D = new SelectionOnlyScene3D(player);
        }

        public void InvokeClear() => Invoke("ClearCommandbuttons");

        public void InvokeUpdate() => Invoke("UpdateCommandbuttons");

        private void Invoke(string methodName)
        {
            var method = ControlBarType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(method is not null, $"AptControlBar.{methodName} not found - has HUD-WIRE renamed it?");

            try
            {
                method.Invoke(_bar, Array.Empty<object>());
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }

        /// <summary>
        /// AptControlBar is internal, so it is resolved off the public factory's assembly
        /// rather than by source reference.
        /// </summary>
        private static Type ControlBarType
        {
            get
            {
                var assembly = typeof(AptControlBarSource).Assembly;
                var type = assembly.GetType("OpenSage.Mods.Bfme.AptControlBar", throwOnError: false);
                Assert.True(
                    type is not null,
                    "OpenSage.Mods.Bfme.AptControlBar not found in " + assembly.GetName().Name);
                return type;
            }
        }

        private static SpriteItem BuildRoot(int constantsCount, out IReadOnlyList<Slot> slots)
        {
            var rootScript = new ObjectContext();
            var root = NewSprite(rootScript);

            var commandButtons = new ObjectContext();
            for (var i = 0; i < constantsCount; i++)
            {
                commandButtons.Constants.Add(Value.FromInteger(i));
            }

            rootScript.Variables["CommandButtons"] = Value.FromObject(commandButtons);

            var built = new List<Slot>(SlotCount);
            for (var i = 0; i < SlotCount; i++)
            {
                var shape = (RenderItem)RuntimeHelpers.GetUninitializedObject(typeof(RenderItem));
                GC.SuppressFinalize(shape);
                shape.RenderCallback = (_, _, _) => { };

                var placeholderScript = new ObjectContext();
                var placeholder = NewSprite(placeholderScript);
                placeholder.Visible = true;
                ItemsOf(placeholder.Content).Add(1, shape);

                var button = new ObjectContext();
                button.Variables["placeholder"] = Value.FromObject(placeholderScript);
                // Present so a fixed UpdateCommandbuttons can look it up; it is never reached
                // by these tests, all of which stop at a guard before the CreateContent call.
                button.Variables["CreateContent"] = Value.Undefined();

                commandButtons.Variables[i.ToString()] = Value.FromObject(button);
                built.Add(new Slot(placeholder, shape));
            }

            slots = built;
            return root;
        }

        /// <summary>
        /// A SpriteItem with an empty DisplayList and a two-way link to its ObjectContext.
        /// SpriteItem.Create needs a Character and an AptContext (i.e. a loaded .apt), so the
        /// object is allocated directly and the three members the control bar reads -
        /// ScriptObject, Content, Visible - are planted.
        /// </summary>
        private static SpriteItem NewSprite(ObjectContext script)
        {
            var sprite = (SpriteItem)RuntimeHelpers.GetUninitializedObject(typeof(SpriteItem));
            GC.SuppressFinalize(sprite);

            var displayList = (DisplayList)RuntimeHelpers.GetUninitializedObject(typeof(DisplayList));
            GC.SuppressFinalize(displayList);
            SetField(displayList, "_items", new SortedDictionary<int, DisplayItem>());
            SetField(displayList, "_reverseItems", new SortedDictionary<int, DisplayItem>());

            SetField(sprite, "<Content>k__BackingField", displayList);
            SetField(sprite, "<ScriptObject>k__BackingField", script);
            SetField(script, "<Item>k__BackingField", sprite);

            return sprite;
        }

        private static SortedDictionary<int, DisplayItem> ItemsOf(DisplayList list) =>
            (SortedDictionary<int, DisplayItem>)GetField(list, "_items");

        private static FieldInfo FieldOf(object target, string name)
        {
            for (var type = target.GetType(); type is not null; type = type.BaseType)
            {
                var field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field is not null)
                {
                    return field;
                }
            }

            Assert.Fail($"field '{name}' not found on {target.GetType().FullName} or any base type");
            return null;
        }

        private static void SetField(object target, string name, object value) =>
            FieldOf(target, name).SetValue(target, value);

        private static object GetField(object target, string name) =>
            FieldOf(target, name).GetValue(target);
    }

    /// <summary>
    /// An <see cref="IScene3D"/> that answers exactly one question - who is the local player -
    /// and refuses everything else. UpdateCommandbuttons reads only
    /// Scene3D.LocalPlayer.SelectedUnits, so anything this stub is asked beyond that is a
    /// signal that the method under test grew a dependency these tests do not model.
    /// </summary>
    private sealed class SelectionOnlyScene3D : IScene3D
    {
        public SelectionOnlyScene3D(Player localPlayer)
        {
            LocalPlayer = localPlayer;
        }

        public Player LocalPlayer { get; }

        private static T Unused<T>([CallerMemberName] string member = null) =>
            throw new NotSupportedException(
                $"AptControlBar asked SelectionOnlyScene3D for {member}; the command-button path is " +
                "supposed to read LocalPlayer only.");

        public IEditorCameraController EditorCameraController => Unused<IEditorCameraController>();
        public IGameEngine GameEngine => Unused<IGameEngine>();
        public SelectionGui SelectionGui => Unused<SelectionGui>();
        public DebugOverlay DebugOverlay => Unused<DebugOverlay>();
        ParticleSystemManager IScene3D.ParticleSystemManager => Unused<ParticleSystemManager>();
        public Camera Camera => Unused<Camera>();
        public TacticalView TacticalView => Unused<TacticalView>();
        public MapFile MapFile => Unused<MapFile>();
        public OpenSage.Terrain.Terrain Terrain => Unused<OpenSage.Terrain.Terrain>();
        public IQuadtree<GameObject> Quadtree => Unused<IQuadtree<GameObject>>();
        public bool ShowTerrain { get; set; }
        public WaterAreaCollection WaterAreas => Unused<WaterAreaCollection>();
        public bool ShowWater { get; set; }
        public RoadCollection Roads => Unused<RoadCollection>();
        public bool ShowRoads { get; set; }
        public Bridge[] Bridges => Unused<Bridge[]>();
        public bool ShowBridges { get; set; }
        public bool FrustumCulling { get; set; }
        public PlayerScriptsList PlayerScripts => Unused<PlayerScriptsList>();
        public IGameObjectCollection GameObjects => Unused<IGameObjectCollection>();
        public bool ShowObjects { get; set; }
        public CameraCollection Cameras => Unused<CameraCollection>();
        public WaypointCollection Waypoints => Unused<WaypointCollection>();
        public WorldLighting Lighting => Unused<WorldLighting>();
        public ShadowSettings Shadows => Unused<ShadowSettings>();
        public WaterSettings Waters => Unused<WaterSettings>();
        public IReadOnlyList<Player> Players => new[] { LocalPlayer };
        public OpenSage.Navigation.Navigation Navigation => Unused<OpenSage.Navigation.Navigation>();
        public AudioSystem Audio => Unused<AudioSystem>();
        AssetLoadContext IScene3D.AssetLoadContext => Unused<AssetLoadContext>();
        public Radar Radar => Unused<Radar>();
        public IGame Game => Unused<IGame>();
        public GameObject BuildPreviewObject { get; set; }
        public RenderScene RenderScene => Unused<RenderScene>();
        public RadarDrawUtil RadarDrawUtil => Unused<RadarDrawUtil>();
        public int GetPlayerIndex(Player player) => 0;
        public void SimObjectTick(in TimeInterval time) { }
        public void ReapDestroyed() { }
        public void LocalLogicTick(in TimeInterval gameTime, float tickT) { }
        public void BuildRenderList(RenderList renderList, Camera camera, in TimeInterval gameTime) { }
        public void Render(DrawingContext2D drawingContext) { }
        public GameObject CreateSkirmishPlayerStartingBuilding(in PlayerSetting playerSetting, Player player) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
