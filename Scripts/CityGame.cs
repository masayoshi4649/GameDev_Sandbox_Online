using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 全地物の設計、道路の接続、信号制御、地形造成と共同編集を統合します。
/// </summary>
public partial class CityGame : Node3D
{
    public CollaborationSession Session { get; private set; } = null!;
    public UrbanCatalog Catalog { get; private set; } = null!;
    public UrbanTerrain Terrain { get; private set; } = null!;
    public UrbanHud Hud { get; private set; } = null!;
    public OrbitCamera CameraRig { get; private set; } = null!;
    public UrbanRenderer Renderer { get; private set; } = null!;
    public UrbanAgentRenderer AgentRenderer { get; private set; } = null!;
    public EditableTerrainRenderer TerrainRenderer { get; private set; } = null!;
    public BayfrontMap Map { get; private set; } = null!;
    public string? SelectedId { get; private set; }
    public string? SelectedAgentId { get; private set; }
    public string CurrentSavePath => _testMode ? ProjectSettings.GlobalizePath(Map.UserSavePath) : _saveId.Length > 0 ? _saves.PathFor(_saveId) : "";
    private UrbanObject _draft = new();
    private readonly List<UrbanPoint> _roadPath = new();
    private readonly MeshInstance3D _preview = new();
    private string _tool = "select", _linkField = "", _selectionJson = "", _editsJson = "";
    private bool _vertexMode, _brushing;
    private int _dragVertex = -1;
    private UrbanObject? _vertexDraft;
    private double _time, _lastBrush = -1, _nextClockRefresh;
    private Vector3 _lastBrushPosition;
    private string? _capture;
    private int _captureFrames;
    private ConfirmationDialog _newCityDialog = null!;
    private bool _newCityWithSource;
    private UrbanObject? _transformSource, _transformDraft;
    private Vector3 _transformStart;
    private Vector2 _transformScreen;
    private bool _followAgent, _testMode, _newTrafficCity;
    private CitySaveStore _saves = null!;
    private string _saveId = "";
    private long _agentTick = -1;
    private bool IsPathTool => _tool is "road" or "rail" or "waterway";

    /// <summary>
    /// 都市の基礎データと編集可能な描画・UI・通信を初期化します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            Map = BayfrontMap.Load();
            var data = Map.RealWorld ?? throw new InvalidOperationException("都市設計には実地形データが必要です。");
            Terrain = new UrbanTerrain(data);
            Catalog = new UrbanCatalog(data);
            var seed = Map.CreateSeedState();
            Terrain.SetEdits(seed.Edits);
            CreateEnvironment();
            CameraRig = new OrbitCamera { Name = "UrbanCamera" }; AddChild(CameraRig); CameraRig.ConfigureTerrain(Terrain.Height, true); CameraRig.FocusOn(Map.Districts[1].Center, 210);
            CameraRig.ManualPanRequested += StopAgentFollowing;
            Hud = new UrbanHud { Name = "UrbanHud" }; AddChild(Hud);
            TerrainRenderer = new EditableTerrainRenderer { Name = "EditableTerrain" }; AddChild(TerrainRenderer);
            TerrainRenderer.ProgressChanged += (done, total) => Hud.ShowLoading("地形", done, total);
            TerrainRenderer.Initialize(data, Terrain);
            Renderer = new UrbanRenderer { Name = "EditableObjects" }; AddChild(Renderer);
            Renderer.ProgressChanged += (done, total) => Hud.ShowLoading("都市の地物", done, total);
            Renderer.Completed += () => Hud.Notify("全地物を編集できます。建物・道路・交差点・信号を選択してください。");
            Renderer.Failed += message => { Hud.Notify(message); if (_capture is not null) GetTree().Quit(1); };
            Renderer.Initialize(Catalog, seed, Terrain.Height);
            AgentRenderer = new UrbanAgentRenderer { Name = "ResidentsAndTraffic" }; AddChild(AgentRenderer); AgentRenderer.Initialize(Terrain.Height); AgentRenderer.Refresh(seed);
            Session = new CollaborationSession { Name = "CollaborationSession" };
            GetParent().AddChild(Session);
            Session.ConfigureUrban(Catalog.SourceExists, Catalog.SourceObject, Catalog.GetRemovalChanges);
            Session.Initialize(seed, Terrain.IsLand);
            Session.ConfigureSimulation(state => Catalog.GetEffective(state));
            _testMode = OS.GetCmdlineUserArgs().Contains("--urban-interaction-test");
            var legacy = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "東京ベイ・サンドボックス", "cities", "real-bayfront.json");
            _saves = new CitySaveStore(ProjectSettings.GlobalizePath("user://cities/saves"), ProjectSettings.GlobalizePath(Map.UserSavePath), legacy);
            Session.StateChanged += OnStateChanged;
            Session.RuntimeChanged += OnRuntimeChanged;
            Session.StatusChanged += message => { Hud.Notify(message); UpdateSummary(); };
            Session.PeerCountChanged += _ => UpdateSummary();
            AddChild(_preview); _preview.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("8aefd0"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true };
            ConnectHud();
            _newCityDialog = new ConfirmationDialog { Title = "新しい都市を開始", DialogText = "現在の編集状態を置き換えます。必要な都市は先に保存してください。", OkButtonText = "新規都市を開始", CancelButtonText = "キャンセル" };
            AddChild(_newCityDialog); _newCityDialog.Confirmed += () => { if (_newTrafficCity) CreateTrafficCity(); else NewCity(_newCityWithSource); };
            OnStateChanged(Session.State);
            SetTool("select");
            GetWindow().Title = "都市づくり｜" + Session.State.CityName;
            var args = OS.GetCmdlineUserArgs();
            var captureIndex = Array.IndexOf(args, "--capture-preview");
            if (captureIndex >= 0 && captureIndex + 1 < args.Length) _capture = args[captureIndex + 1];
            if (_capture is not null) { CameraRig.SetProcess(false); CameraRig.SetProcessInput(false); CameraRig.SetProcessUnhandledInput(false); SetProcessUnhandledInput(false); }
            if (args.Contains("--blank-city")) NewCity(false);
            if (args.Contains("--traffic-city")) { CreateTrafficCity(); for (var step = 0; step < 60; step++) Session.Tick(1); }
            var district = args.FirstOrDefault(a => a.StartsWith("--capture-district=", StringComparison.Ordinal));
            if (district is not null && int.TryParse(district.Split('=')[1], out var index) && index is >= 0 and < 4) CameraRig.FocusOn(Map.Districts[index].Center, 100);
            if (args.Contains("--urban-interaction-test")) UrbanInteractionTest.Run(this);
        }
        catch (Exception error) { GD.PushError($"都市設計の初期化に失敗: {error}"); GetTree().Quit(1); }
    }

    /// <summary>
    /// UIの操作を都市の編集コマンドへ接続します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void ConnectHud()
    {
        Hud.ToolChanged += SetTool; Hud.ApplyRequested += ApplyDesign; Hud.DeleteRequested += DeleteSelected;
        Hud.UndoRequested += () => Report(Session.UndoEdit(), "編集を元に戻しました。"); Hud.RedoRequested += () => Report(Session.RedoEdit(), "編集をやり直しました。");
        Hud.SaveRequested += SaveCity; Hud.LoadRequested += () => { if (_testMode) LoadCity(); else OpenSaves(); };
        Hud.OpenSavesRequested += OpenSaves; Hud.SaveAsRequested += SaveAsCity; Hud.LoadSaveRequested += LoadSavedCity;
        Hud.CityNameRequested += name => Report(Session.SetCityName(name.Trim()), "都市名を変更しました。");
        Hud.DemoRequested += () => { _newTrafficCity = true; _newCityDialog.PopupCentered(); };
        Hud.FocusRequested += FocusSelection; Hud.AgentSelected += id => SelectAgent(id, true);
        Hud.FinishPathRequested += FinishRoad; Hud.EditVerticesRequested += BeginVertexEdit;
        Hud.LinkRequested += field => { if (SelectedId is null) { Hud.Notify("先に接続元の地物を選択してください。"); return; } _linkField = field; Hud.Notify(field == "parent" ? "所属する交差点をクリックしてください。" : "接続先の道路をクリックしてください。"); };
        Hud.SelectRequested += id => { Select(id); var item = Catalog.Find(id, Session.State); if (item is not null) CameraRig.FocusOn(new Vector2(item.X, item.Z), 65); };
        Hud.SearchRequested += text => Hud.ShowSearch(Catalog.GetEffective(Session.State).Where(o => o.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || o.Usage.Contains(text, StringComparison.OrdinalIgnoreCase) || o.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || UrbanHud.KindName(o.Kind).Contains(text, StringComparison.OrdinalIgnoreCase)));
        Hud.DistrictRequested += index => { StopAgentFollowing(); CameraRig.FocusOn(index < 4 ? Map.Districts[index].Center : Vector2.Zero, index < 4 ? 120 : 750); };
        Hud.ReferencePhotoChanged += TerrainRenderer.SetReferencePhoto;
        Hud.NewCityRequested += source => { if (!Session.CanReplaceState) { Hud.Notify("新規都市はホストだけが開始できます。"); return; } _newTrafficCity = false; _newCityWithSource = source; _newCityDialog.PopupCentered(); };
        Hud.JoinRequested += (address, port) => Session.Join(address, port); Hud.HostRequested += port => Session.Host(port); Hud.DisconnectRequested += Session.Disconnect;
        Hud.SpeedChanged += value => { if (!Session.SetTimeScale(value)) Hud.Notify(Session.LastError); };
        Hud.ResetTerrainRequested += () => Submit(new UrbanCommand { Operation = "resetTerrain" });
        Hud.WaterRequested += meters => Submit(new UrbanCommand { Operation = "water", SeaLevelMeters = (Terrain.SeaLevel * 10) + meters });
    }

    /// <summary>
    /// 都市時間、造成の連続入力、設計プレビューと画面保存を更新します。
    /// 引数: delta（double）経過秒。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        if (Session is null) return;
        _time += delta;
        Session.Tick((float)delta);
        Renderer.AdvanceTime(Session.SimulationSeconds);
        AgentRenderer.AdvanceTime(Session.SimulationSeconds);
        if (_followAgent && SelectedAgentId is { } agentId && UrbanSimulation.FindAgent(Session.State, agentId) is { } agent)
        {
            if (AgentRenderer.GetAgentPosition(agentId) is { } displayed) CameraRig.Follow(displayed + Vector3.Up * 0.08f);
            else { _followAgent = false; FocusAgentLocation(agent); ShowAgentInformation(agent); Hud.Notify("建物内または乗車中のため、追跡を終了して所在地を表示しました。"); }
        }
        if (_brushing && _time - _lastBrush > 0.22 && GetViewport().GuiGetHoveredControl() is null) Brush(CameraRig.GroundPosition(GetViewport().GetMousePosition()));
        UpdatePreview();
        if (_capture is not null && Renderer.IsComplete && TerrainRenderer.IsComplete && ++_captureFrames == 90) Capture();
    }

    /// <summary>
    /// UIの上でマウスを離しても造成と頂点のドラッグを終了します。
    /// 引数: input（InputEvent）入力。返り値: void。
    /// </summary>
    public override void _Input(InputEvent input)
    {
        if (input is InputEventMouseMotion motion && _transformSource is not null) { UpdateTransformDrag(motion.Position); GetViewport().SetInputAsHandled(); }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _brushing = false;
            if (_transformDraft is { } transformed) { _transformSource = null; _transformDraft = null; ApplyDesign(transformed); }
            if (_dragVertex >= 0 && _vertexDraft is not null) { var item = _vertexDraft; _dragVertex = -1; _vertexDraft = null; ApplyDesign(item); }
        }
    }

    /// <summary>
    /// ショートカット、地物選択、自由形状の作図と造成を処理します。
    /// 引数: input（InputEvent）入力。返り値: void。
    /// </summary>
    public override void _UnhandledInput(InputEvent input)
    {
        if (Session is null) return;
        var textFocus = GetViewport().GuiGetFocusOwner();
        if (input is InputEventKey && textFocus is LineEdit or TextEdit) return;
        if (input is InputEventMouseButton { Pressed: true } && textFocus is LineEdit or TextEdit) textFocus.ReleaseFocus();
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.CtrlPressed && key.PhysicalKeycode == Key.S) { SaveCity(); return; }
            if (key.CtrlPressed && key.PhysicalKeycode == Key.Z) { Report(Session.UndoEdit(), "編集を元に戻しました。"); return; }
            if (key.CtrlPressed && key.PhysicalKeycode == Key.Y) { Report(Session.RedoEdit(), "編集をやり直しました。"); return; }
            if (key.PhysicalKeycode == Key.Enter) { FinishRoad(); return; }
            if (key.PhysicalKeycode == Key.Delete) { DeleteSelected(); return; }
            if (key.PhysicalKeycode == Key.Escape) { CancelInteraction(); return; }
            if (key.PhysicalKeycode == Key.G) { SetTool("move"); return; }
            if (key.PhysicalKeycode == Key.R) { SetTool("rotate"); return; }
            if (key.PhysicalKeycode == Key.H) { SetTool("height"); return; }
            var tools = new[] { "select", "building", "road", "intersection", "signal", "prop", "surface", "terrain", "delete" };
            var number = (int)key.PhysicalKeycode - (int)Key.Key1;
            if (number >= 0 && number < tools.Length) { SetTool(tools[number]); return; }
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } && (_transformSource is not null || _roadPath.Count > 0 || _vertexMode)) { CancelInteraction(); return; }
        if (input is InputEventMouseMotion motion && _dragVertex >= 0 && _vertexDraft is not null)
        {
            var point = Snap(CameraRig.GroundPosition(motion.Position));
            var points = _vertexDraft.Kind == "road" ? _vertexDraft.Path : _vertexDraft.Footprint;
            points[_dragVertex] = new UrbanPoint(point.X, point.Z);
            NormalizeFootprint(_vertexDraft); return;
        }
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse) return;
        HandleWorldClick(mouse.Position);
    }

    /// <summary>
    /// 地図クリックを現在のツールに応じた操作へ変換します。
    /// 引数: screen（Vector2）画面座標。返り値: void。
    /// </summary>
    public void HandleWorldClick(Vector2 screen)
    {
        var point = Snap(CameraRig.GroundPosition(screen));
        if (_linkField.Length > 0)
        {
            var kind = _linkField == "parent" ? "intersection" : "road";
            var item = FindNear(kind, point);
            if (item is null) Hud.Notify("対応する地物が見つかりません。近づいて選択してください。");
            else { Hud.AddLink(_linkField, item.Id); _linkField = ""; }
            return;
        }
        if (_vertexMode && SelectedId is not null)
        {
            var selected = Catalog.Find(SelectedId, Session.State);
            if (selected is null) return;
            var points = selected.Kind == "road" ? selected.Path : UrbanCatalog.GetFootprint(selected).Select(p => new UrbanPoint(p.X, p.Y)).ToList();
            for (var i = 0; i < points.Count; i++)
            {
                var handle = CameraRig.Camera.UnprojectPosition(new Vector3(points[i].X, Terrain.Height(points[i].X, points[i].Z) + selected.Y + 0.2f, points[i].Z));
                if (handle.DistanceTo(screen) > 16) continue;
                _vertexDraft = selected.DeepClone(); if (selected.Kind != "road") { _vertexDraft.Footprint = points.Select(p => p.DeepClone()).ToList(); _vertexDraft.Rotation = 0; NormalizeFootprint(_vertexDraft); }
                _dragVertex = i; return;
            }
            Hud.Notify("水色の頂点をドラッグしてください。数値入力では頂点の追加・削除もできます。"); return;
        }
        if (_tool == "terrain") { _brushing = true; Brush(point); return; }
        if (_tool is "select" or "delete" or "move" or "rotate" or "height")
        {
            var origin = CameraRig.Camera.ProjectRayOrigin(screen); var direction = CameraRig.Camera.ProjectRayNormal(screen);
            var item = Catalog.Pick(origin, direction, Session.State, Terrain.Height, Hud.SelectionFilter == "agent" ? null : Hud.SelectionFilter);
            if (_tool == "select" && Hud.SelectionFilter is "all" or "agent")
            {
                var agentId = AgentRenderer.Pick(origin, direction, CameraRig.Camera, screen);
                if (agentId is not null && (item is null || AgentRenderer.LastPickDistance < Catalog.LastPickDistance)) { SelectAgent(agentId); return; }
            }
            if (Hud.SelectionFilter == "agent") return;
            if (item is null) { if (_tool == "select") ClearSelection(); return; }
            Select(item.Id); if (_tool == "delete") DeleteSelected();
            else if (_tool is "move" or "rotate" or "height") { _transformSource = item.DeepClone(); _transformDraft = item.DeepClone(); _transformStart = point; _transformScreen = screen; }
            return;
        }
        if (IsPathTool) { _roadPath.Add(new UrbanPoint(point.X, point.Z)); Hud.Notify($"経路の頂点 {_roadPath.Count} 個。Enterで確定、右クリックで中止。"); return; }
        try
        {
            var design = Hud.ReadDesign(); design.Id = Guid.NewGuid().ToString("N"); design.SourceId = ""; design.X = point.X; design.Z = point.Z;
            if (design.Kind == "signal" && design.ParentId.Length == 0)
            {
                var junction = FindNear("intersection", point); if (junction is not null) { design.ParentId = junction.Id; design.JunctionId = junction.Id; }
            }
            Submit(new UrbanCommand { Object = design });
        }
        catch (FormatException error) { Hud.Notify(error.Message); }
    }

    // ----------------------------------------

    /// <summary>
    /// 地物の属性編集を確定します。未配置の設計は次の配置に使います。
    /// 引数: item（UrbanObject）変更案。返り値: void。
    /// </summary>
    public void ApplyDesign(UrbanObject item)
    {
        if (SelectedId is null) { _draft = item.DeepClone(); Hud.ShowObject(_draft, false); Hud.Notify("設計値を更新しました。地図をクリックして配置してください。"); return; }
        if (item.Id != SelectedId) { Hud.Notify("選択が変更されています。地物を選び直してください。"); return; }
        Submit(new UrbanCommand { Operation = "batch", Objects = Catalog.GetObjectChanges(item, Session.State).ToList() });
    }

    /// <summary>
    /// 道路の経路を確定し、同じ立体交差層の接続点を生成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void FinishRoad()
    {
        if (!IsPathTool || _roadPath.Count < 2) { Hud.Notify("経路は地図上で2点以上を指定してください。"); return; }
        try
        {
            var item = Hud.ReadDesign(); item.Kind = "road"; item.Id = Guid.NewGuid().ToString("N"); item.SourceId = ""; item.Path = _roadPath.Select(p => p.DeepClone()).ToList(); item.X = item.Path.Average(p => p.X); item.Z = item.Path.Average(p => p.Z);
            if (Hud.AutoConnect && _tool == "road") item = Catalog.CreateConnectedRoad(item, Session.State);
            var command = Hud.AutoConnect && _tool == "road" ? new UrbanCommand { Operation = "batch", Objects = Catalog.GetRoadConnections(item, Session.State).ToList() } : new UrbanCommand { Object = item };
            if (Submit(command)) _roadPath.Clear();
        }
        catch (FormatException error) { Hud.Notify(error.Message); }
    }

    /// <summary>
    /// 地物を選択し、原典由来かを問わず全設計値を編集欄へ渡します。
    /// 引数: id（string）地物識別子。返り値: void。
    /// </summary>
    public void Select(string id)
    {
        var item = Catalog.Find(id, Session.State); if (item is null) return;
        if (_tool is not ("select" or "delete" or "move" or "rotate" or "height")) { _tool = "select"; Hud.SetTool("select"); _brushing = false; _roadPath.Clear(); }
        SelectedAgentId = null; _followAgent = false; AgentRenderer.Select(null);
        SelectedId = id; _selectionJson = JsonSerializer.Serialize(item); Hud.ShowObject(item); Renderer.Select(id);
    }

    /// <summary>
    /// 選択した原典地物または新設地物を撤去します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void DeleteSelected()
    {
        if (SelectedId is null) { Hud.Notify("撤去する地物を選択してください。"); return; }
        var id = SelectedId; if (Submit(new UrbanCommand { Operation = "delete", ObjectId = id })) { SelectedId = null; Renderer.Select(null); _vertexMode = false; Hud.ClearSelection(); Hud.Notify("地物を撤去しました。Ctrl+Zで復元できます。"); }
    }

    /// <summary>
    /// 作図ツールを切り替え、新しい地物の自由な設計を準備します。
    /// 引数: tool（string）ツール識別子。返り値: void。
    /// </summary>
    public void SetTool(string tool)
    {
        if (tool == "clear") { ClearSelection(); tool = "select"; }
        if (tool is "building" or "road" or "rail" or "waterway" or "intersection" or "signal" or "prop" or "surface" or "terrain")
        {
            _followAgent = false;
            if (SelectedAgentId is not null) { SelectedAgentId = null; AgentRenderer.Select(null); Hud.ClearSelection(); }
        }
        _tool = tool; _roadPath.Clear(); _vertexMode = false; _vertexDraft = null; _dragVertex = -1; _brushing = false; _linkField = "";
        _transformSource = null; _transformDraft = null;
        Hud.SetTool(tool);
        if (tool is "select" or "delete" or "move" or "rotate" or "height" && SelectedId is { } selected && Catalog.Find(selected, Session.State) is { } selection) Hud.ShowObject(selection);
        if (tool is "building" or "road" or "rail" or "waterway" or "intersection" or "signal" or "prop" or "surface")
        {
            SelectedId = null; Renderer.Select(null);
            _draft = new UrbanObject { Kind = tool, Name = "", Floors = tool == "building" ? 1 : 0, HeightMeters = tool == "signal" ? 4 : tool == "surface" ? 0.1f : 3.2f, WidthMeters = tool is "signal" ? 0.6f : tool == "intersection" ? 12 : 20, DepthMeters = tool == "signal" ? 0.6f : tool == "intersection" ? 12 : 20, Control = tool == "signal" ? "signals" : "priority", ColorHex = tool == "road" ? "#424b53" : "#c2ccd2" };
            if (IsPathTool) { _draft.Kind = "road"; _draft.Usage = tool == "rail" ? "rail" : tool == "waterway" ? "waterway" : "road"; _draft.WidthMeters = tool == "rail" ? 6 : tool == "waterway" ? 12 : 10; _draft.SpeedKph = tool == "rail" ? 80 : tool == "waterway" ? 20 : 40; _draft.SidewalkWidthMeters = tool == "road" ? 2 : 0; }
            Hud.ShowObject(_draft, false);
        }
    }

    /// <summary>
    /// 選択形状の頂点を地図上で動かすモードへ入ります。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BeginVertexEdit() { if (SelectedId is null) { Hud.Notify("既存の地物を選択してください。"); return; } _vertexMode = true; Hud.Notify("水色の頂点をドラッグして変形します。Escで通常選択へ戻ります。"); }

    /// <summary>
    /// 指定位置へ現在の造成ブラシを適用します。
    /// 引数: point（Vector3）地表都市座標。返り値: void。
    /// </summary>
    public void Brush(Vector3 point)
    {
        _lastBrush = _time; _lastBrushPosition = point;
        Submit(new UrbanCommand { Operation = "terrain", Terrain = new TerrainEdit { Mode = Hud.TerrainMode, X = point.X, Z = point.Z, RadiusMeters = Hud.BrushRadius, AmountMeters = Hud.BrushAmount, TargetHeightMeters = Hud.TargetHeight + Terrain.SeaLevel * 10 } }, false);
    }

    /// <summary>
    /// 最新版番号を添えた編集要求をホストへ送ります。
    /// 引数: command（UrbanCommand）編集要求、notify（bool）成功通知。返り値: bool 確定または送信成功。
    /// </summary>
    public bool Submit(UrbanCommand command, bool notify = true)
    {
        command.ExpectedRevision = Session.State.Revision;
        var success = Session.SubmitEdit(command);
        if (!success) Hud.Notify(Session.LastError);
        else if (notify) Hud.Notify(Session.IsGuest ? "編集をホストへ要求しました。" : "都市の設計を更新しました。");
        return success;
    }

    /// <summary>
    /// 確定した都市の変更を地形、地物描画、選択欄へ反映します。
    /// 引数: state（CityState）確定都市。返り値: void。
    /// </summary>
    private void OnStateChanged(CityState state)
    {
        var edits = JsonSerializer.Serialize(state.Edits);
        if (_editsJson != edits)
        {
            _editsJson = edits; Terrain.SetEdits(state.Edits); TerrainRenderer.UpdateTerrain(state); Renderer.Refresh(state);
            if (SelectedId is { } id)
            {
                var item = Catalog.Find(id, state);
                if (item is null) { SelectedId = null; Renderer.Select(null); _vertexMode = false; Hud.ClearSelection(); }
                else { var json = JsonSerializer.Serialize(item); if (_selectionJson != json) { _selectionJson = json; Hud.ShowObject(item); } }
            }
        }
        AgentRenderer.Refresh(state); _agentTick = state.Simulation.LastProcessedTick;
        GetWindow().Title = "都市づくり｜" + state.CityName;
        UpdateSummary();
    }

    /// <summary>
    /// 地物の設計床面積と道路延長を更新します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void UpdateSummary()
    {
        if (Session is null || Hud is null) return;
        Hud.UpdateCity(Session, UrbanModel.CalculateStatistics(Catalog.GetEffective(Session.State)));
        Hud.UpdateRuntime(Session);
    }

    /// <summary>
    /// 地物の再集計を行わず、都市時計の表示だけを毎秒2回まで更新します。
    /// 引数: state（CityState）ホストが確定した都市状態。返り値: void。
    /// </summary>
    private void OnRuntimeChanged(CityState state)
    {
        if (_agentTick != state.Simulation.LastProcessedTick) { _agentTick = state.Simulation.LastProcessedTick; AgentRenderer.Refresh(state); }
        if (_time < _nextClockRefresh) return;
        _nextClockRefresh = _time + 0.5;
        Hud.UpdateRuntime(Session);
        Hud.UpdateTraffic(state);
        if (SelectedAgentId is { } id)
        {
            if (UrbanSimulation.FindAgent(state, id) is { } agent) ShowAgentInformation(agent);
            else ClearSelection();
        }
        else if (SelectedId is { } objectId) Hud.ShowBuildingRuntime(state, objectId);
    }

    /// <summary>
    /// 実都市または更地を初期状態とする都市を開始します。
    /// 引数: source（bool）原典の建物と道路を初期配置するか。返り値: void。
    /// </summary>
    public void NewCity(bool source)
    {
        var state = new CityState { MapId = Map.MapId, MapVersion = Map.MapVersion, Edits = new UrbanEdits { IncludeSource = source } };
        if (!Session.ReplaceState(state)) { Hud.Notify(Session.LastError); return; }
        _saveId = ""; ClearSelection(); SetTool("select"); Hud.Notify(source ? "実都市を編集可能な初期状態として開始しました。" : "地形だけを残した更地から開始しました。");
    }

    /// <summary>
    /// 機能: 実地形の海岸に住宅・目的地・道路・鉄道・航路を備えた未保存の街を開始します。
    /// 引数: なし。
    /// 返り値: void。参加者による置換は拒否し、配置失敗時は現在の都市を保持します。
    /// </summary>
    private void CreateTrafficCity()
    {
        if (!Session.CanReplaceState) { Hud.Notify("交通街区はホストだけが開始できます。"); return; }
        try
        {
            var state = UrbanDemo.Create(Map, out var center);
            if (!Session.ReplaceState(state)) { Hud.Notify(Session.LastError); return; }
            _saveId = ""; ClearSelection(); SetTool("select");
            var road = state.Edits.Objects.First(item => item.Id == "demo-road");
            var along = new Vector2(road.Path[^1].X - road.Path[0].X, road.Path[^1].Z - road.Path[0].Z);
            CameraRig.FramePoints(UrbanDemo.GetViewPoints(state, Terrain), along);
            GD.Print($"BAYFRONT_TRAFFIC_CITY_VIEW 中心={center} カメラ={CameraRig.Camera.GlobalPosition}");
            Hud.Notify("住宅から目的地へ通う街を開始しました。時間を進めて住民・車・列車・船を選択してください。全施設と経路は編集できます。");
        }
        catch (Exception error) { Hud.Notify($"交通街区を開始できませんでした: {error.Message}"); }
    }

    // ----------------------------------------

    /// <summary>
    /// 都市の全編集をUTF-8で原子的に保存します。参加者からの保存は拒否します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void SaveCity()
    {
        if (!Session.CanSaveState) { Hud.Notify("共有都市はホストだけが保存できます。"); return; }
        try
        {
            if (_saveId.Length == 0) _saveId = Guid.NewGuid().ToString("N");
            if (_testMode)
            {
                var path = CurrentSavePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path + ".tmp", CityModel.Serialize(Session.State), new UTF8Encoding(false)); if (File.Exists(path)) File.Copy(path, path + ".bak", true); File.Move(path + ".tmp", path, true);
            }
            else _saves.Save(Session.State, _saveId);
            Hud.Notify($"「{Session.State.CityName}」を保存しました。住民・経路・都市時間も保持します。");
        }
        catch (Exception error) { Hud.Notify($"保存できませんでした: {error.Message}"); }
    }

    /// <summary>
    /// 保存都市を検証し、ホストの確定状態として復元します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void LoadCity()
    {
        if (!Session.CanReplaceState) { Hud.Notify("共有都市の読み込みはホストが行います。"); return; }
        try
        {
            var path = ProjectSettings.GlobalizePath(Map.UserSavePath);
            if (!File.Exists(path)) { Hud.Notify("保存した都市がありません。"); return; }
            if (!CityModel.TryDeserialize(File.ReadAllText(path, Encoding.UTF8), Map.IsLand, out var state, out var error, Map.CanPlace)) { Hud.Notify(error); return; }
            CityModel.MigrateLegacyObjects(state);
            Report(Session.ReplaceState(state), "保存した都市の設計と地形を復元しました。");
        }
        catch (Exception error) { Hud.Notify($"読み込めませんでした: {error.Message}"); }
    }

    /// <summary>
    /// 都市名と保存データを選ぶ画面を開きます。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void OpenSaves() => Hud.ShowSaves(_saves.List(), Session.State.CityName);

    /// <summary>
    /// 都市名を検証して新しい保存識別子へ保存します。
    /// 引数: name（string）プレイヤーが付ける都市名。返り値: void。
    /// </summary>
    public void SaveAsCity(string name)
    {
        if (!Session.SetCityName(name.Trim())) { Hud.Notify(Session.LastError); return; }
        _saveId = Guid.NewGuid().ToString("N"); SaveCity();
    }

    /// <summary>
    /// 保存一覧の都市を検証し、名称と稼働状態を含めて復元します。
    /// 引数: path（string）一覧で選んだ保存先。返り値: void。
    /// </summary>
    public void LoadSavedCity(string path)
    {
        if (!Session.CanReplaceState) { Hud.Notify("都市を読み込めるのはホストだけです。"); return; }
        try
        {
            if (!CityModel.TryDeserialize(_saves.Read(path), Map.IsLand, out var state, out var error, Map.CanPlace)) { Hud.Notify(error); return; }
            CityModel.MigrateLegacyObjects(state);
            if (!Session.ReplaceState(state)) { Hud.Notify(Session.LastError); return; }
            _saveId = _saves.IdFor(path) ?? ""; ClearSelection(); SetTool("select"); Hud.Notify($"「{state.CityName}」を読み込みました。");
        }
        catch (Exception error) { Hud.Notify($"読み込めませんでした: {error.Message}"); }
    }

    // ----------------------------------------

    /// <summary>
    /// 地物と移動個体の選択、追跡、未確定ドラッグを解除します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void ClearSelection()
    {
        SelectedId = null; SelectedAgentId = null; _followAgent = false; _transformDraft = null; _transformSource = null;
        Renderer.Select(null); AgentRenderer.Select(null); Hud.ClearSelection();
    }

    /// <summary>
    /// 住民または乗り物を選び、その目的地と経路を表示します。
    /// 引数: id（string）個体識別子、focus（bool）近景へ移動するか。返り値: void。
    /// </summary>
    public void SelectAgent(string id, bool focus = false)
    {
        if (UrbanSimulation.FindAgent(Session.State, id) is not { } agent) return;
        _followAgent = false; SetTool("select");
        SelectedId = null; Renderer.Select(null); SelectedAgentId = id; AgentRenderer.Select(id);
        if (focus)
        {
            if (AgentRenderer.GetAgentPosition(id) is { } displayed) { CameraRig.FocusOn(displayed + Vector3.Up * 0.08f, agent.Kind == "person" ? 6 : 16); _followAgent = true; }
            else FocusAgentLocation(agent);
        }
        ShowAgentInformation(agent);
    }

    /// <summary>
    /// 住居・固定職場・滞在先と今回の起終点を解決し、関連個体と表示操作を渡します。
    /// 引数: agent（UrbanAgent）個体。返り値: void。
    /// </summary>
    private void ShowAgentInformation(UrbanAgent agent)
    {
        /*
         * 機能: 施設識別子を名称へ変換し、未割当と撤去済みを区別します。
         * 引数: id と empty（string）施設識別子と未割当時の表示。
         * 返り値: string 型の表示名です。
         */
        string PlaceName(string id, string empty)
        {
            if (id.Length == 0) return empty;
            var item = Catalog.Find(id, Session.State);
            return item is null ? "撤去された施設" : item.Name.Length > 0 ? item.Name : item.Usage.Length > 0 ? item.Usage : UrbanHud.KindName(item.Kind);
        }
        var resident = agent.Kind == "person" ? Session.State.Simulation.Residents.FirstOrDefault(value => value.Id == agent.Id) : null;
        var links = new List<UrbanAgentLink>();
        var linkedPlaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, label) in new[]
        {
            (resident?.HomeId ?? "", "住居を見る"), (resident?.WorkId ?? "", "職場を見る"),
            (resident?.CurrentPlaceId ?? "", "滞在先を見る"), (agent.DestinationId, "今回の行先を見る")
        })
            if (id.Length > 0 && linkedPlaces.Add(id) && Catalog.Find(id, Session.State) is not null)
                links.Add(new UrbanAgentLink(id, label + "　" + PlaceName(id, "未設定"), false));
        if (agent.Kind == "person")
        {
            var vehicle = Session.State.Simulation.Agents.FirstOrDefault(value => value.Kind != "person" && value.PassengerIds.Contains(agent.Id));
            if (vehicle is not null) links.Add(new UrbanAgentLink(vehicle.Id, "乗車中の乗り物　" + vehicle.Name, true));
        }
        else
            foreach (var id in agent.PassengerIds.Take(32))
                if (UrbanSimulation.FindAgent(Session.State, id) is { } passenger)
                    links.Add(new UrbanAgentLink(id, (agent.Kind == "car" ? "乗員を見る　" : "乗客を見る　") + passenger.Name, true));
        var occupants = agent.Kind == "car" ? "運転者と同乗者の区別は未設定です。" : "";
        if (agent.PassengerIds.Count > 32) occupants += "\n関連する乗員の先頭32人を表示しています。";
        Hud.ShowAgent(agent, PlaceName(agent.OriginId, "未設定"), PlaceName(agent.DestinationId, "未設定"), new UrbanAgentDetails
        {
            Home = PlaceName(resident?.HomeId ?? "", "未設定"), Workplace = PlaceName(resident?.WorkId ?? "", "未割当"),
            CurrentPlace = PlaceName(resident?.CurrentPlaceId ?? "", agent.Status == "乗車中" ? "乗車中" : agent.Status == "移動中" ? "屋外を移動中" : "屋外・接続待ち"),
            OccupantNotice = occupants, Links = links, Following = _followAgent && SelectedAgentId == agent.Id,
            CanFollow = AgentRenderer.GetAgentPosition(agent.Id).HasValue, RouteVisible = AgentRenderer.SelectedRouteVisible,
            RouteChanged = visible => AgentRenderer.SetRouteVisible(visible),
            FollowRequested = FocusSelection
        });
    }

    /// <summary>
    /// 選択地物へ移動するか、選択個体の追跡を切り替えます。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void FocusSelection()
    {
        if (SelectedAgentId is { } id && UrbanSimulation.FindAgent(Session.State, id) is { } agent)
        {
            if (_followAgent) { _followAgent = false; Hud.Notify("追跡を解除しました。"); }
            else if (AgentRenderer.GetAgentPosition(id) is { } displayed)
            { CameraRig.FocusOn(displayed + Vector3.Up * 0.08f, agent.Kind == "person" ? 6 : 16); _followAgent = true; Hud.Notify("選択個体を追跡しています。手動移動または「追う」で解除。"); }
            else { FocusAgentLocation(agent); Hud.Notify("建物内または乗車中のため、個体の所在地を表示しました。"); }
            ShowAgentInformation(agent);
        }
        else if (SelectedId is { } objectId && Catalog.Find(objectId, Session.State) is { } item) CameraRig.FocusOn(new Vector2(item.X, item.Z), Math.Max(8, UrbanCatalog.HeightMetersOf(item) / 10 + 12));
    }

    /// <summary>
    /// 手動で視点を移動した際に追跡を解除し、停止中でも表示を同期します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void StopAgentFollowing()
    {
        if (!_followAgent) return;
        _followAgent = false;
        if (SelectedAgentId is { } id && UrbanSimulation.FindAgent(Session.State, id) is { } agent) ShowAgentInformation(agent);
    }

    /// <summary>
    /// 屋外に表示されていない個体の建物または乗車位置へ、追跡せず視点を合わせます。
    /// 引数: agent（UrbanAgent）在宅・滞在・乗車中などの個体。
    /// 返り値: void。表示中の個体として存在しない場所を追い続けません。
    /// </summary>
    private void FocusAgentLocation(UrbanAgent agent)
    {
        _followAgent = false;
        if (agent.Status == "乗車中")
        {
            var vehicle = Session.State.Simulation.Agents.FirstOrDefault(value => value.Kind != "person" && value.PassengerIds.Contains(agent.Id));
            if (vehicle is not null && AgentRenderer.GetAgentPosition(vehicle.Id) is { } position) { CameraRig.FocusOn(position + Vector3.Up * 0.1f, 16); return; }
        }
        var resident = Session.State.Simulation.Residents.FirstOrDefault(value => value.Id == agent.Id);
        var locationId = resident?.CurrentPlaceId is { Length: > 0 } currentPlace ? currentPlace : agent.DestinationId.Length > 0 ? agent.DestinationId : agent.OriginId;
        var location = Catalog.Find(locationId, Session.State);
        if (location is not null) CameraRig.FocusOn(new Vector2(location.X, location.Z), Math.Max(8, UrbanCatalog.HeightMetersOf(location) / 10 + 12));
        else CameraRig.FocusOn(new Vector2(agent.X, agent.Z), 12);
    }

    /// <summary>
    /// ドラッグの開始位置から移動量・回転・高さを計算して未確定プレビューを更新します。
    /// 引数: screen（Vector2）現在の画面座標。返り値: void。
    /// </summary>
    private void UpdateTransformDrag(Vector2 screen)
    {
        if (_transformSource is null) return;
        var fine = Input.IsPhysicalKeyPressed(Key.Shift);
        var point = CameraRig.GroundPosition(screen); var shift = new Vector2(point.X - _transformStart.X, point.Z - _transformStart.Z);
        shift = shift.Snapped(Vector2.One * (fine ? 0.01f : 0.1f));
        var rotation = Mathf.Snapped((screen.X - _transformScreen.X) * 0.5f, fine ? 1 : 5);
        var height = Mathf.Snapped((_transformScreen.Y - screen.Y) * 0.01f, fine ? 0.01f : 0.1f);
        _transformDraft = UrbanTransform.Apply(_transformSource, _tool == "move" ? shift : Vector2.Zero, _tool == "rotate" ? rotation : 0, _tool == "height" ? height : 0);
    }

    /// <summary>
    /// 作図やドラッグの未確定状態だけを破棄します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void CancelInteraction()
    {
        _transformDraft = null; _transformSource = null; _roadPath.Clear(); _vertexMode = false; _dragVertex = -1; _vertexDraft = null; _linkField = ""; _brushing = false; SetTool("select");
    }

    /// <summary>
    /// 操作結果を表示します。
    /// 引数: success（bool）成功、message（string）成功案内。返り値: void。
    /// </summary>
    private void Report(bool success, string message) => Hud.Notify(success ? message : Session.LastError);

    /// <summary>
    /// 接続対象を地表位置から探します。
    /// 引数: kind（string）種別、point（Vector3）都市位置。返り値: UrbanObject? 近傍地物。
    /// </summary>
    private UrbanObject? FindNear(string kind, Vector3 point)
    {
        var p = new Vector2(point.X, point.Z);
        return Catalog.GetEffective(Session.State).Where(o => o.Kind == kind).Select(o => (Item: o, Distance: kind == "road" && o.Path.Count > 1 ? Enumerable.Range(1, o.Path.Count - 1).Min(i => Geometry2D.GetClosestPointToSegment(p, new Vector2(o.Path[i - 1].X, o.Path[i - 1].Z), new Vector2(o.Path[i].X, o.Path[i].Z)).DistanceTo(p)) : new Vector2(o.X, o.Z).DistanceTo(p))).Where(v => v.Distance < 8).OrderBy(v => v.Distance).Select(v => v.Item).FirstOrDefault();
    }

    /// <summary>
    /// 敷地の中心と幅・奥行を明示的な頂点に合わせます。
    /// 引数: item（UrbanObject）編集案。返り値: void。
    /// </summary>
    private static void NormalizeFootprint(UrbanObject item)
    {
        if (item.Kind == "road" || item.Footprint.Count == 0) return;
        item.X = item.Footprint.Average(p => p.X); item.Z = item.Footprint.Average(p => p.Z);
        item.WidthMeters = (item.Footprint.Max(p => p.X) - item.Footprint.Min(p => p.X)) * 10; item.DepthMeters = (item.Footprint.Max(p => p.Z) - item.Footprint.Min(p => p.Z)) * 10;
    }

    /// <summary>
    /// 位置を0.1m単位へ揃えます。
    /// 引数: point（Vector3）都市座標。返り値: Vector3 補正した位置。
    /// </summary>
    private static Vector3 Snap(Vector3 point) => new(Mathf.Snapped(point.X, 0.01f), point.Y, Mathf.Snapped(point.Z, 0.01f));

    /// <summary>
    /// 作成中の経路・造成範囲・選択頂点と、未確定の移動・回転・高さを立体表示します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void UpdatePreview()
    {
        var transforming = _tool is "move" or "rotate" or "height";
        if (!IsPathTool && _tool != "terrain" && !_vertexMode && !transforming) { if (_preview.Mesh is not null) _preview.Mesh = null; return; }
        var mesh = new ImmediateMesh(); var lines = new List<(Vector3 Position, Color Color)>();
        var mint = new Color("#8aefd0");
        if (_preview.MaterialOverride is StandardMaterial3D material) { material.AlbedoColor = Colors.White; material.VertexColorUseAsAlbedo = true; }
        /*
         * 機能: 色付きの一本の線分をプレビューへ追加します。
         * 引数: a と b（Vector3）端点、color（Color）表示色。
         * 返り値: void。
         */
        void Line(Vector3 a, Vector3 b, Color color) { lines.Add((a, color)); lines.Add((b, color)); }
        /*
         * 機能: 経路の設定に合わせ、地表または海面からの位置を求めます。
         * 引数: item（UrbanObject）経路、p（Vector2）位置、lift（float）表示上げ量。
         * 返り値: Vector3 型の描画位置です。
         */
        Vector3 PathPoint(UrbanObject item, Vector2 p, float lift) => new(p.X,
            (item.FollowTerrain ? item.Usage is "waterway" or "航路" ? Terrain.SeaLevel : Terrain.Height(p.X, p.Y) : 0) + item.Y + lift, p.Y);
        /*
         * 機能: 設計案の側面、天井、屋根、経路外縁を元メッシュとは別に線で描きます。
         * 引数: item（UrbanObject）未確定の設計案、color（Color）線の色。
         * 返り値: void。
         */
        void Wireframe(UrbanObject item, Color color)
        {
            if (item.Kind == "road")
            {
                for (var i = 1; i < item.Path.Count; i++)
                {
                    var a = new Vector2(item.Path[i - 1].X, item.Path[i - 1].Z); var b = new Vector2(item.Path[i].X, item.Path[i].Z);
                    var side = new Vector2(-(b - a).Y, (b - a).X).Normalized() * item.WidthMeters / 20;
                    Line(PathPoint(item, a, 0.08f), PathPoint(item, b, 0.08f), color);
                    foreach (var sign in new[] { -1, 1 }) Line(PathPoint(item, a + side * sign, 0.08f), PathPoint(item, b + side * sign, 0.08f), color);
                    Line(PathPoint(item, a - side, 0.08f), PathPoint(item, a + side, 0.08f), color);
                    Line(PathPoint(item, b - side, 0.08f), PathPoint(item, b + side, 0.08f), color);
                }
                return;
            }
            var footprint = UrbanCatalog.GetFootprint(item);
            var ground = UrbanCatalog.BaseHeight(item, Terrain.Height) + 0.025f;
            var height = Math.Max(0.02f, UrbanCatalog.HeightMetersOf(item) / 10);
            var top = ground + height;
            var basePoints = footprint.Select(p => new Vector3(p.X, ground, p.Y)).ToArray();
            var topPoints = footprint.Select(p => new Vector3(p.X, top, p.Y)).ToArray();
            for (var i = 0; i < footprint.Length; i++)
            {
                var next = (i + 1) % footprint.Length;
                Line(basePoints[i], basePoints[next], color); Line(topPoints[i], topPoints[next], color); Line(basePoints[i], topPoints[i], color);
            }
            if (item.Kind != "building") return;
            if (item.Roof is "hip" or "pyramid")
            {
                var bounds = UrbanCatalog.BoundsOf(footprint);
                var peak = new Vector3(item.X, top + Math.Min(1.5f, Math.Min(bounds.Size.X, bounds.Size.Y) * 0.3f), item.Z);
                foreach (var p in topPoints) Line(p, peak, color);
            }
            else if (item.Roof is "gable" or "gabled")
            {
                var origin = new Vector2(item.X, item.Z); var angle = Mathf.DegToRad(item.Rotation);
                var local = footprint.Select(p => (p - origin).Rotated(-angle)).ToArray(); var bounds = UrbanCatalog.BoundsOf(local);
                var acrossX = bounds.Size.X <= bounds.Size.Y; var middle = bounds.GetCenter();
                var rise = Math.Min(1.5f, (acrossX ? bounds.Size.X : bounds.Size.Y) * 0.275f);
                var a = (acrossX ? new Vector2(middle.X, bounds.Position.Y) : new Vector2(bounds.Position.X, middle.Y)).Rotated(angle) + origin;
                var b = (acrossX ? new Vector2(middle.X, bounds.End.Y) : new Vector2(bounds.End.X, middle.Y)).Rotated(angle) + origin;
                var ridgeA = new Vector3(a.X, top + rise, a.Y); var ridgeB = new Vector3(b.X, top + rise, b.Y);
                Line(ridgeA, ridgeB, color);
                for (var i = 0; i < local.Length; i++)
                {
                    var towardA = acrossX ? local[i].Y < middle.Y : local[i].X < middle.X;
                    Line(topPoints[i], towardA ? ridgeA : ridgeB, color);
                }
            }
        }
        var pointer = CameraRig.GroundPosition(GetViewport().GetMousePosition());
        if (IsPathTool && _roadPath.Count > 0)
        {
            var points = _roadPath.Select(p => PathPoint(_draft, new Vector2(p.X, p.Z), 0.15f)).Append(PathPoint(_draft, new Vector2(pointer.X, pointer.Z), 0.15f)).ToArray();
            for (var i = 1; i < points.Length; i++) Line(points[i - 1], points[i], mint);
        }
        if (_tool == "terrain")
        {
            var radius = Hud.BrushRadius / 10;
            for (var i = 0; i < 64; i++) foreach (var angle in new[] { i * Mathf.Tau / 64, (i + 1) * Mathf.Tau / 64 }) { var x = pointer.X + Mathf.Cos(angle) * radius; var z = pointer.Z + Mathf.Sin(angle) * radius; lines.Add((new Vector3(x, Math.Max(Terrain.Height(x, z), Terrain.SeaLevel) + 0.2f, z), mint)); }
        }
        if (_vertexMode && SelectedId is not null)
        {
            var item = _vertexDraft ?? Catalog.Find(SelectedId, Session.State);
            if (item is not null)
            {
                var points = item.Kind == "road" ? item.Path : UrbanCatalog.GetFootprint(item).Select(p => new UrbanPoint(p.X, p.Y)).ToList();
                var size = Math.Max(0.12f, CameraRig.Camera.GlobalPosition.DistanceTo(pointer) * 0.003f);
                foreach (var p in points) { var center = PathPoint(item, new Vector2(p.X, p.Z), 0.2f); Line(center - Vector3.Right * size, center + Vector3.Right * size, mint); Line(center - Vector3.Back * size, center + Vector3.Back * size, mint); }
                for (var i = 1; i < points.Count; i++) Line(PathPoint(item, new Vector2(points[i - 1].X, points[i - 1].Z), 0.2f), PathPoint(item, new Vector2(points[i].X, points[i].Z), 0.2f), mint);
            }
        }
        if (transforming && SelectedId is not null && (_transformDraft ?? Catalog.Find(SelectedId, Session.State)) is { } selected)
        {
            if (_transformDraft is not null)
            {
                Wireframe(_transformDraft, mint);
                if (_transformSource is not null) Line(new Vector3(_transformSource.X, UrbanCatalog.BaseHeight(_transformSource, Terrain.Height) + 0.1f, _transformSource.Z),
                    new Vector3(_transformDraft.X, UrbanCatalog.BaseHeight(_transformDraft, Terrain.Height) + 0.1f, _transformDraft.Z), new Color("#f1d99b"));
            }
            var center = new Vector3(selected.X, UrbanCatalog.BaseHeight(selected, Terrain.Height) + 0.15f, selected.Z);
            var length = Math.Clamp(CameraRig.Camera.Size * 0.025f, 0.6f, 10);
            foreach (var (axis, side, color) in new[] { (Vector3.Right, Vector3.Back, new Color("#ff887c")), (Vector3.Up, Vector3.Right, new Color("#a7ef8d")), (Vector3.Back, Vector3.Right, new Color("#8dbdff")) })
            {
                var end = center + axis * length; Line(center, end, color);
                Line(end, end - axis * length * 0.18f + side * length * 0.08f, color); Line(end, end - axis * length * 0.18f - side * length * 0.08f, color);
            }
            if (_tool == "rotate")
                for (var i = 0; i < 64; i++) Line(center + new Vector3(Mathf.Cos(i * Mathf.Tau / 64), 0, Mathf.Sin(i * Mathf.Tau / 64)) * length * 0.75f,
                    center + new Vector3(Mathf.Cos((i + 1) * Mathf.Tau / 64), 0, Mathf.Sin((i + 1) * Mathf.Tau / 64)) * length * 0.75f, mint);
        }
        if (lines.Count > 0) { mesh.SurfaceBegin(Mesh.PrimitiveType.Lines); foreach (var line in lines) { mesh.SurfaceSetColor(line.Color); mesh.SurfaceAddVertex(line.Position); } mesh.SurfaceEnd(); }
        _preview.Mesh = mesh;
    }

    /// <summary>
    /// 編集可能な都市の表示用照明と空を作成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void CreateEnvironment()
    {
        var sky = new ProceduralSkyMaterial { SkyTopColor = new Color("7cb4d0"), SkyHorizonColor = new Color("cedfdf"), GroundBottomColor = new Color("536473"), GroundHorizonColor = new Color("c5d6d7") };
        var environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Sky, Sky = new Sky { SkyMaterial = sky }, AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("dbe8ed"), AmbientLightEnergy = 0.65f, TonemapMode = Godot.Environment.ToneMapper.Linear, SsaoEnabled = RenderingServer.GetCurrentRenderingMethod() == "forward_plus", SsaoRadius = 0.8f, SsaoIntensity = 0.65f };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -35, 0), LightColor = new Color("fff1dc"), LightEnergy = 0.7f, ShadowEnabled = true, DirectionalShadowMaxDistance = 800 });
    }

    /// <summary>
    /// 全読み込み完了後の実画面を保存して終了します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private async void Capture()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(_capture!);
        GD.Print($"BAYFRONT_URBAN_PREVIEW {error} 地物={Renderer.EffectiveObjectCount} FPS={Engine.GetFramesPerSecond():F1} process_ms={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F2} physics_ms={Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:F2} draw_calls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} primitives={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)} visible_objects={Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }
}
