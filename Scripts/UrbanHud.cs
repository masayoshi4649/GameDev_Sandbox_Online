using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 固定アセットの選択ではなく、地物の設計値と接続・地形を編集する画面です。
/// </summary>
public partial class UrbanHud : CanvasLayer
{
    public event Action<string>? ToolChanged;
    public event Action<UrbanObject>? ApplyRequested;
    public event Action? DeleteRequested, UndoRequested, RedoRequested, SaveRequested, LoadRequested, FinishPathRequested, EditVerticesRequested;
    public event Action<bool>? NewCityRequested, ReferencePhotoChanged;
    public event Action<int>? DistrictRequested;
    public event Action<string>? LinkRequested, SearchRequested, SelectRequested;
    public event Action<string, int>? JoinRequested;
    public event Action<int>? HostRequested;
    public event Action? DisconnectRequested, ResetTerrainRequested;
    public event Action<float>? WaterRequested, SpeedChanged;
    public event Action? DemoRequested, FocusRequested, OpenSavesRequested;
    public event Action<string>? CityNameRequested, SaveAsRequested, LoadSaveRequested, AgentSelected;

    public string TerrainMode => _terrainMode.GetItemMetadata(_terrainMode.Selected).AsString();
    public float BrushRadius => Value("BrushRadius");
    public float BrushAmount => Value("BrushAmount");
    public float TargetHeight => Value("TargetHeight");
    public bool AutoConnect => _autoConnect.ButtonPressed;
    public string ActiveTool { get; private set; } = "select";
    public string SelectionFilter { get; private set; } = "all";

    private readonly Dictionary<string, SpinBox> _numbers = new();
    private readonly Dictionary<string, (double Original, double Displayed)> _displayedNumbers = new();
    private readonly Dictionary<string, Control> _fieldRows = new();
    private readonly Dictionary<string, LineEdit> _strings = new();
    private readonly Dictionary<string, Control> _groups = new();
    private readonly Dictionary<string, Button> _tools = new();
    private readonly List<Button> _speedButtons = new();
    private readonly Dictionary<string, (int Done, int Total)> _loadingProgress = new();
    private UrbanObject _basis = new();
    private Label _summary = null!, _notice = null!, _selection = null!, _loading = null!, _help = null!, _identity = null!, _clock = null!;
    private VBoxContainer _fields = null!, _searchResults = null!;
    private TextEdit _shape = null!, _links = null!, _phases = null!;
    private OptionButton _terrainMode = null!, _control = null!, _roof = null!;
    private CheckBox _followTerrain = null!, _autoConnect = null!;
    private Button _save = null!, _load = null!, _source = null!, _blank = null!;
    private Window _network = null!;
    private LineEdit _address = null!, _port = null!;
    private double _noticeTime;
    private bool _hasSelection;
    private Control _inspector = null!, _mapPanel = null!, _objectTools = null!;
    private Label _cityTitle = null!, _agentInfo = null!, _population = null!;
    private Label _worldLoading = null!;
    private VBoxContainer _agentList = null!, _saveList = null!;
    private readonly Dictionary<string, Button> _trafficRows = new(StringComparer.Ordinal);
    private CityState? _latestCityState;
    private Label _trafficEmpty = null!, _trafficLimit = null!;
    private Window _saves = null!, _traffic = null!;
    private LineEdit _cityName = null!;
    private Button _demo = null!, _rename = null!, _saveAs = null!;
    private string _panelTab = "design";
    private string? _selectedAgent;
    private ScrollContainer? _agentDetailsScroll;
    private VBoxContainer? _agentRelations, _agentActions;
    private CheckBox? _agentRouteToggle;
    private Button? _agentFollowToggle;
    private Action? _syncAgentDetails;
    private UrbanAgentDetails? _currentAgentDetails;
    private string _agentLinkSignature = "";
    private static readonly Color Ink = new("eef4f1"), Muted = new("a7b9c1"), Mint = new("88e0bf");

    /// <summary>
    /// 設計用画面、地物プロパティ、地形ブラシと共同編集画面を作成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.Theme = new Theme { DefaultFont = GD.Load<Font>("res://Assets/Fonts/NotoSansJP-Regular.otf"), DefaultFontSize = 14 };
        var top = Panel(root, 18, 14, -18, 78, true);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 22); top.AddChild(row);
        _cityTitle = Text("新しい都市", 24); _cityTitle.CustomMinimumSize = new Vector2(230, 0); row.AddChild(_cityTitle);
        _summary = Text("都市を準備しています", 12); _summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; row.AddChild(_summary);
        row.AddChild(Button("地図・検索", () => _mapPanel.Visible = !_mapPanel.Visible));
        row.AddChild(Button("人・交通", ShowTraffic));
        row.AddChild(Button("都市・セーブ", () => OpenSavesRequested?.Invoke()));
        row.AddChild(Button("元に戻す", () => UndoRequested?.Invoke()));
        row.AddChild(Button("やり直す", () => RedoRequested?.Invoke()));
        row.AddChild(Button("共同編集", () => _network.PopupCentered()));

        // ----------------------------------------

        var left = Panel(root, 18, 139, 285, -158, bottomStretch: true); _mapPanel = left; left.Visible = false;
        var side = new VBoxContainer(); side.AddThemeConstantOverride("separation", 10); left.AddChild(side);
        side.AddChild(Text("地図と検索", 20));
        foreach (var district in new[] { "台場", "有明", "東雲", "豊洲", "全域" }.Select((name, i) => (name, i)))
            side.AddChild(Button(district.name, () => DistrictRequested?.Invoke(district.i)));
        _loading = Text("地形と地物を準備中", 12, Mint); side.AddChild(_loading);
        var photo = new CheckBox { Text = "航空写真を参照表示" }; photo.Toggled += value => ReferencePhotoChanged?.Invoke(value); side.AddChild(photo);
        side.AddChild(Text("地物を検索（名称・用途・ID）", 12, Muted));
        var search = new LineEdit { Name = "ObjectSearch", PlaceholderText = "例：住宅、交差点" };
        search.TextSubmitted += value => SearchRequested?.Invoke(value); side.AddChild(search);
        var resultsScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        side.AddChild(resultsScroll); _searchResults = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; resultsScroll.AddChild(_searchResults);

        // ----------------------------------------

        var inspector = Panel(root, -400, 91, -18, -158, right: true, bottomStretch: true); _inspector = inspector;
        var inspectorBox = new VBoxContainer(); inspectorBox.AddThemeConstantOverride("separation", 9); inspector.AddChild(inspectorBox);
        _selection = Text("地物を選択", 21); inspectorBox.AddChild(_selection);
        _identity = Text("原典から取り込んだ地物も編集・撤去できます。", 11, Muted); _identity.AutowrapMode = TextServer.AutowrapMode.WordSmart; inspectorBox.AddChild(_identity);
        var objectTools = new HBoxContainer(); inspectorBox.AddChild(objectTools); _objectTools = objectTools;
        objectTools.AddChild(Button("移動", () => ToolChanged?.Invoke("move")));
        objectTools.AddChild(Button("回転", () => ToolChanged?.Invoke("rotate")));
        objectTools.AddChild(Button("高さ", () => ToolChanged?.Invoke("height")));
        objectTools.AddChild(Button("追う", () => FocusRequested?.Invoke()));
        objectTools.AddChild(Button("閉じる", () => ToolChanged?.Invoke("clear")));
        var tabs = new HBoxContainer(); inspectorBox.AddChild(tabs);
        foreach (var tab in new[] { ("design", "設計"), ("geometry", "形状"), ("operation", "運用") })
            tabs.AddChild(Button(tab.Item2, () => { _panelTab = tab.Item1; SetGroups(_basis.Kind); }));
        _agentInfo = Text("", 13); _agentInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart; inspectorBox.AddChild(_agentInfo);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inspectorBox.AddChild(scroll); _fields = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _fields.AddThemeConstantOverride("separation", 8); scroll.AddChild(_fields);
        BuildObjectFields(); BuildTerrainFields();

        // ----------------------------------------

        var editing = Panel(root, 18, 90, 636, 132);
        var editingRow = new HBoxContainer(); editing.AddChild(editingRow);
        foreach (var entry in new[] { ("select", "選択"), ("move", "移動 G"), ("rotate", "回転 R"), ("height", "高さ H") })
        { var button = Button(entry.Item2, () => ToolChanged?.Invoke(entry.Item1)); editingRow.AddChild(button); _tools.Add(entry.Item1, button); }
        editingRow.AddChild(Button("形状・頂点", () => EditVerticesRequested?.Invoke()));
        var filter = new OptionButton { Name = "PickFilter" };
        foreach (var name in new[] { "全てを選択", "建物", "道路・線路・航路", "交差点", "信号", "人・乗り物" }) filter.AddItem(name);
        filter.ItemSelected += index => SelectionFilter = index switch { 1 => "building", 2 => "road", 3 => "intersection", 4 => "signal", 5 => "agent", _ => "all" };
        editingRow.AddChild(filter);
        _worldLoading = Text("", 13, Mint); _worldLoading.Name = "WorldLoadingProgress";
        root.AddChild(_worldLoading); _worldLoading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _worldLoading.OffsetLeft = -250; _worldLoading.OffsetRight = 250; _worldLoading.OffsetTop = 145; _worldLoading.OffsetBottom = 202;
        _worldLoading.HorizontalAlignment = HorizontalAlignment.Center; _worldLoading.MouseFilter = Control.MouseFilterEnum.Ignore;
        _worldLoading.AddThemeStyleboxOverride("normal", Style(new Color("102630f5"))); _worldLoading.Visible = false;
        var bottom = Panel(root, 18, -146, -18, -16, true, bottom: true);
        var bottomBox = new VBoxContainer(); bottomBox.AddThemeConstantOverride("separation", 7); bottom.AddChild(bottomBox);
        var tools = new HBoxContainer(); tools.AddThemeConstantOverride("separation", 7); bottomBox.AddChild(tools);
        foreach (var entry in new[] { ("building", "2 建築"), ("road", "3 道路"), ("rail", "線路"), ("waterway", "航路"), ("intersection", "4 交差点"), ("signal", "5 信号"), ("prop", "6 立体物"), ("surface", "7 敷地"), ("terrain", "8 地形"), ("delete", "9 撤去") })
        {
            var button = Button(entry.Item2, () => ToolChanged?.Invoke(entry.Item1)); button.CustomMinimumSize = new Vector2(78, 35); tools.AddChild(button); _tools.Add(entry.Item1, button);
        }
        foreach (var speed in new[] { 0, 1, 3 }) { var button = Button(speed == 0 ? "Ⅱ" : $"{speed}倍", () => SpeedChanged?.Invoke(speed)); tools.AddChild(button); _speedButtons.Add(button); }
        _help = Text("左クリックで地物を選択。移動 WASD／回転 右ドラッグ／拡縮 ホイール", 12, Muted); bottomBox.AddChild(_help);
        var status = new HBoxContainer(); status.AddThemeConstantOverride("separation", 28); bottomBox.AddChild(status);
        _clock = Text("都市時間を準備しています", 12, Mint); _clock.Name = "SimulationClock"; status.AddChild(_clock);
        _population = Text("住民 0人", 14); _population.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; status.AddChild(_population);
        _save = Button("都市を保存", () => SaveRequested?.Invoke()); _load = Button("読み込む", () => LoadRequested?.Invoke()); status.AddChild(_save); status.AddChild(_load);
        _notice = Text("", 14, Mint); root.AddChild(_notice); _notice.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _notice.OffsetLeft = -470; _notice.OffsetRight = 470; _notice.OffsetTop = -190; _notice.OffsetBottom = -150; _notice.HorizontalAlignment = HorizontalAlignment.Center;
        _notice.AddThemeStyleboxOverride("normal", Style(new Color("102630f5")));
        var attribution = Text("原典：国土交通省PLATEAU・東京都／国土地理院・Mapterhornを加工。道路接続・階数不明値は編集画面で確認。", 10, Muted);
        root.AddChild(attribution); attribution.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide); attribution.OffsetLeft = 24; attribution.OffsetRight = -390; attribution.OffsetTop = -159; attribution.OffsetBottom = -146;
        BuildNetwork(root.Theme);
        BuildCitySaves(root.Theme); BuildTraffic(root.Theme); ClearSelection(); SetTool("select");
    }

    /// <summary>
    /// 通知の表示期限を進めます。
    /// 引数: delta（double）経過秒。返り値: void。
    /// </summary>
    public override void _Process(double delta) { _noticeTime -= delta; _notice.Visible = _noticeTime > 0; }

    /// <summary>
    /// 建築・道路・接続・信号の全設計入力を作成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BuildObjectFields()
    {
        var common = Group("common", "地物の設計値");
        StringField(common, "Name", "名称"); StringField(common, "Usage", "用途（自由入力）");
        var use = new OptionButton { Name = "BuildingUsage" };
        var uses = new[] { ("", "用途を入力…"), ("住宅:walk", "住宅・徒歩通勤"), ("住宅:car", "住宅・自動車通勤"), ("住宅:train", "住宅・鉄道通勤"), ("住宅:ship", "住宅・船通勤"), ("職場", "職場"), ("商業", "商業"), ("工場", "工場"), ("公園", "公園"), ("駅", "駅"), ("港", "港") };
        foreach (var entry in uses) use.AddItem(entry.Item2);
        use.ItemSelected += index => { if (index > 0) _strings["Usage"].Text = uses[index].Item1; }; common.AddChild(use);
        Number(common, "X", "東西位置 (m)", -4000, 4000, 0.1); Number(common, "Z", "南北位置 (m)", -4000, 4000, 0.1);
        Number(common, "Y", "地表からの高さ (m)", -1000, 3000, 0.1);
        _followTerrain = new CheckBox { Text = "地表の高さに追従する", ButtonPressed = true }; common.AddChild(_followTerrain);
        Number(common, "Rotation", "回転 (度)", -360, 360, 1);
        Number(common, "Width", "幅 (m)", 0.1, 3000, 0.1); Number(common, "Depth", "奥行 (m)", 0.1, 3000, 0.1);
        Number(common, "Height", "高さ (m)", 0.1, 2000, 0.1);
        StringField(common, "Color", "色 (#RRGGBB)"); StringField(common, "Material", "材質名");
        var building = Group("building", "階数と高さ");
        Number(building, "Floors", "階数（0=未設定）", 0, 300, 1); Number(building, "FloorHeight", "階高 (m)", 1.5, 20, 0.1);
        building.AddChild(Text("階数を設定すると、階数×階高が高さになります。", 11, Muted));
        _roof = Options(building, "屋根", new[] { ("flat", "陸屋根"), ("gable", "切妻屋根"), ("pyramid", "四角錐屋根") });
        var road = Group("road", "道路の構造");
        Number(road, "LanesForward", "順方向の車線数", 0, 12, 1); Number(road, "LanesBackward", "逆方向の車線数", 0, 12, 1);
        Number(road, "LaneWidth", "1車線の幅 (m)", 1.5, 8, 0.1); Number(road, "Sidewalk", "片側の歩道幅 (m)", 0, 30, 0.1); Number(road, "Speed", "制限速度 (km/h)", 1, 160, 1); Number(road, "Level", "立体交差の層", -10, 20, 1);
        _autoConnect = new CheckBox { Text = "同じ高さの交差点へ接続", ButtonPressed = true }; road.AddChild(_autoConnect);
        road.AddChild(Button("描画中の道路を確定 (Enter)", () => FinishPathRequested?.Invoke()));
        var junction = Group("junction", "接続と制御");
        _control = Options(junction, "交差点の制御", new[] { ("unconfigured", "未設定（原典に情報なし）"), ("priority", "優先道路"), ("stop", "一時停止"), ("signals", "信号制御"), ("roundabout", "環状交差点") });
        junction.AddChild(Text("接続道路ID（1行に1つ）", 12, Muted)); _links = Editor(junction, "ConnectedRoads", 70);
        junction.AddChild(Button("地図から接続道路を追加", () => LinkRequested?.Invoke("connection")));
        var signal = Group("signal", "信号現示");
        StringField(signal, "Parent", "所属する交差点ID"); signal.AddChild(Button("地図から交差点を指定", () => LinkRequested?.Invoke("parent")));
        StringField(signal, "Approach", "進入道路ID"); signal.AddChild(Button("地図から進入道路を指定", () => LinkRequested?.Invoke("approach")));
        signal.AddChild(Text("名称 | 青時間(秒) | 通行道路ID,ID\n1行が1現示。黄・全赤は各現示の後に挿入。", 11, Muted));
        _phases = Editor(signal, "SignalPhases", 105);
        Number(signal, "Yellow", "黄時間 (秒)", 0, 20, 0.5); Number(signal, "AllRed", "全赤時間 (秒)", 0, 20, 0.5); Number(signal, "Offset", "周期の時差 (秒)", 0, 86400, 1);
        signal.AddChild(Button("接続道路から現示を組む", BuildPhases));
        var geometry = Group("geometry", "敷地・経路の頂点");
        geometry.AddChild(Text("中心からの位置(m)、1行に 東西,南北\n道路は中心線、それ以外は敷地の外周です。", 11, Muted));
        _shape = Editor(geometry, "ShapePoints", 120);
        geometry.AddChild(Button("入力した頂点を反映", () => EmitDesign(true)));
        geometry.AddChild(Button("地図上で頂点を動かす", () => EditVerticesRequested?.Invoke()));
        var actions = Group("actions", "設計の確定");
        actions.AddChild(Button("設計値を反映", () => EmitDesign(false), true)); actions.AddChild(Button("選択した地物を撤去", () => DeleteRequested?.Invoke()));
    }

    /// <summary>
    /// 地形のブラシと水面高の入力を作成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BuildTerrainFields()
    {
        var terrain = Group("terrain", "地形を造成する");
        _terrainMode = Options(terrain, "処理", new[] { ("raise", "盛土"), ("lower", "掘削"), ("flatten", "指定高へ平坦化"), ("smooth", "斜面をなだらかに") });
        Number(terrain, "BrushRadius", "半径 (m)", 1, 1000, 1, 35); Number(terrain, "BrushAmount", "変化量 (m)", 0.1, 100, 0.1, 2); Number(terrain, "TargetHeight", "平坦化の高さ (海面比m)", -500, 3000, 0.5, 3);
        var terrainHelp = Text("左ドラッグで造成。変更は建物・道路の高さと陸海判定に反映されます。", 12, Muted);
        terrainHelp.AutowrapMode = TextServer.AutowrapMode.WordSmart; terrain.AddChild(terrainHelp);
        Number(terrain, "Water", "水面高の変更 (m)", -500, 500, 0.1, 0);
        terrain.AddChild(Button("水面高を変更", () => WaterRequested?.Invoke(Value("Water"))));
        terrain.AddChild(Button("地形を初期状態へ戻す", () => ResetTerrainRequested?.Invoke()));
    }

    /// <summary>
    /// 選択地物を入力欄へ複製し、地物種別ごとの設計項目を表示します。
    /// 引数: item（UrbanObject）編集対象、selected（bool）既存地物かどうか。返り値: void。
    /// </summary>
    public void ShowObject(UrbanObject item, bool selected = true)
    {
        _selectedAgent = null; _inspector.Visible = true; _fields.Visible = true; _agentInfo.Visible = false; _objectTools.Visible = selected;
        foreach (var button in _objectTools.GetChildren().OfType<Button>()) button.Disabled = false;
        _basis = item.DeepClone(); _hasSelection = selected;
        _selection.Text = selected ? string.IsNullOrWhiteSpace(item.Name) ? KindName(item.Kind) : item.Name : $"新しい{KindName(item.Kind)}を設計";
        _identity.Text = selected ? $"ID: {item.Id}\n{(item.SourceId.Length > 0 ? "実都市由来・編集と撤去が可能" : "この都市で設計した地物")}" : "設計値を入力し、地図をクリックして配置します。";
        _strings["Name"].Text = item.Name; _strings["Usage"].Text = item.Usage;
        Set("X", item.X * 10); Set("Z", item.Z * 10); Set("Y", item.Y * 10); Set("Rotation", item.Rotation); Set("Width", item.WidthMeters); Set("Depth", item.DepthMeters);
        Set("Floors", item.Floors); Set("FloorHeight", item.FloorHeightMeters); Set("Height", item.HeightMeters);
        Set("LanesForward", item.LanesForward); Set("LanesBackward", item.LanesBackward); Set("LaneWidth", item.LaneWidthMeters); Set("Sidewalk", item.SidewalkWidthMeters); Set("Speed", item.SpeedKph); Set("Level", item.Level);
        Set("Yellow", item.YellowSeconds); Set("AllRed", item.AllRedSeconds); Set("Offset", item.CycleOffsetSeconds);
        _strings["Color"].Text = item.ColorHex; _strings["Material"].Text = item.Material; _strings["Parent"].Text = item.ParentId; _strings["Approach"].Text = item.ApproachRoadId;
        _followTerrain.ButtonPressed = item.FollowTerrain; SelectOption(_roof, item.Roof); SelectOption(_control, item.Control);
        _links.Text = string.Join("\n", item.ConnectedRoadIds); _phases.Text = string.Join("\n", item.Phases.Select(p => $"{p.Name} | {p.DurationSeconds.ToString(CultureInfo.InvariantCulture)} | {string.Join(',', p.GreenRoadIds)}"));
        var points = item.Kind == "road" ? item.Path : UrbanCatalog.GetFootprint(item).Select(point => new UrbanPoint(point.X, point.Y)).ToList();
        _shape.Text = string.Join("\n", points.Select(p => $"{((p.X - item.X) * 10).ToString("0.###", CultureInfo.InvariantCulture)}, {((p.Z - item.Z) * 10).ToString("0.###", CultureInfo.InvariantCulture)}"));
        SetGroups(item.Kind);
    }

    /// <summary>
    /// 選択を解除し、撤去済み地物を編集対象として残さないようにします。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void ClearSelection()
    {
        _hasSelection = false;
        _selectedAgent = null; _inspector.Visible = false; _agentInfo.Visible = false;
        _selection.Text = "地物を選択";
        _identity.Text = "原典から取り込んだ地物も編集・撤去できます。";
        foreach (var group in _groups.Values) group.Visible = false;
    }

    /// <summary>
    /// 現在の入力から地物の設計定義を作成します。敷地頂点は専用の反映操作で変更します。
    /// 引数: shape（bool）形状入力も反映するか。返り値: UrbanObject 編集案。
    /// </summary>
    public UrbanObject ReadDesign(bool shape = false)
    {
        var item = _basis.DeepClone();
        item.Name = _strings["Name"].Text; item.Usage = _strings["Usage"].Text;
        item.X = Coordinate("X", _basis.X); item.Z = Coordinate("Z", _basis.Z); item.Y = Coordinate("Y", _basis.Y); item.Rotation = Value("Rotation");
        item.WidthMeters = Value("Width"); item.DepthMeters = Value("Depth"); item.Floors = (int)Value("Floors"); item.FloorHeightMeters = Value("FloorHeight"); item.HeightMeters = Value("Height");
        item.FollowTerrain = _followTerrain.ButtonPressed; item.ColorHex = _strings["Color"].Text; item.Material = _strings["Material"].Text; item.Roof = _roof.GetItemMetadata(_roof.Selected).AsString();
        item.LanesForward = (int)Value("LanesForward"); item.LanesBackward = (int)Value("LanesBackward"); item.LaneWidthMeters = Value("LaneWidth"); item.SidewalkWidthMeters = Value("Sidewalk"); item.SpeedKph = Value("Speed"); item.Level = (int)Value("Level");
        if (item.Kind == "road")
        {
            var lanes = item.LanesForward + item.LanesBackward;
            if (item.LanesForward != _basis.LanesForward || item.LanesBackward != _basis.LanesBackward || item.LaneWidthMeters != _basis.LaneWidthMeters || item.SidewalkWidthMeters != _basis.SidewalkWidthMeters)
                item.WidthMeters = lanes * item.LaneWidthMeters + 2 * item.SidewalkWidthMeters;
            else if (item.WidthMeters != _basis.WidthMeters && lanes > 0)
                item.LaneWidthMeters = (item.WidthMeters - 2 * item.SidewalkWidthMeters) / lanes;
        }
        item.Control = _control.GetItemMetadata(_control.Selected).AsString(); item.ConnectedRoadIds = Lines(_links.Text).ToList();
        item.ParentId = _strings["Parent"].Text.Trim(); item.ApproachRoadId = _strings["Approach"].Text.Trim(); item.YellowSeconds = Value("Yellow"); item.AllRedSeconds = Value("AllRed"); item.CycleOffsetSeconds = Value("Offset");
        item.Phases = ParsePhases(_phases.Text);
        if (item.Kind == "road" && item.Path.Count > 0)
        {
            foreach (var point in item.Path) { point.X += item.X - _basis.X; point.Z += item.Z - _basis.Z; }
            var angle = Mathf.DegToRad(item.Rotation - _basis.Rotation);
            if (!Mathf.IsZeroApprox(angle))
                foreach (var point in item.Path) { var rotated = new Vector2(point.X - item.X, point.Z - item.Z).Rotated(angle); point.X = rotated.X + item.X; point.Z = rotated.Y + item.Z; }
            item.Rotation = 0;
        }
        if (shape)
        {
            var points = ParsePoints(_shape.Text, item.X, item.Z);
            if (item.Kind == "road") item.Path = points;
            else
            {
                item.Footprint = points; item.Rotation = 0;
                if (points.Count > 0) { item.X = points.Average(p => p.X); item.Z = points.Average(p => p.Z); item.WidthMeters = (points.Max(p => p.X) - points.Min(p => p.X)) * 10; item.DepthMeters = (points.Max(p => p.Z) - points.Min(p => p.Z)) * 10; }
            }
        }
        return item;
    }

    /// <summary>
    /// ツール状態と案内を変更します。
    /// 引数: tool（string）ツール識別子。返り値: void。
    /// </summary>
    public void SetTool(string tool)
    {
        ActiveTool = tool;
        foreach (var pair in _tools) pair.Value.Modulate = pair.Key == tool ? Mint : Colors.White;
        _help.Text = tool switch { "road" or "rail" or "waterway" => "経路を順にクリック → Enterで確定。右クリックで取消。幅・速度・高さを右で指定。", "move" => "移動：地物を押してドラッグ。離すと確定。Shiftで細かく移動。右クリックで取消。", "rotate" => "回転：地物を押して左右へドラッグ。Shiftで1度ずつ。右クリックで取消。", "height" => "高さ：地物を押して上下へドラッグ。右クリックで取消。", "terrain" => "左ドラッグで造成／Ctrl+Zで元に戻す／半径・強さ・目標高を右で指定。", "delete" => "地物をクリックして撤去。Ctrl+Zで復元。", "select" => "建物の壁・屋根、人や乗り物をクリックして情報を表示。WASD移動／右ドラッグ回転／ホイール拡縮。", _ => "設計値と用途を右で入力し、地図をクリックして配置。住宅と目的地を交通網へ接続すると移動が始まります。" };
        if (tool == "terrain") { _inspector.Visible = true; _fields.Visible = true; _objectTools.Visible = false; _agentInfo.Visible = false; foreach (var group in _groups.Values) group.Visible = false; _groups["terrain"].Visible = true; _selection.Text = "地形と水面"; _identity.Text = "盛土・掘削・平坦化・平滑化を保存・共同編集します。"; }
        else if (tool is "select" or "delete" or "move" or "rotate" or "height") { if (_hasSelection) SetGroups(_basis.Kind); else if (_selectedAgent is null) ClearSelection(); }
    }

    /// <summary>
    /// 確定都市の集計と接続権限を表示します。
    /// 引数: session（CollaborationSession）共有都市、stats（UrbanStatistics）設計集計。返り値: void。
    /// </summary>
    public void UpdateCity(CollaborationSession session, UrbanStatistics stats)
    {
        _latestCityState = session.State;
        _cityTitle.Text = session.State.CityName;
        _summary.Text = $"{stats.BuildingCount:N0} 棟　交通網 {stats.RoadLengthMeters / 1000:N1} km\n{(session.IsGuest ? "共同編集に参加中" : session.IsHost ? "都市のホスト" : "都市を開発中")}";
        _save.Disabled = !session.CanSaveState; _load.Disabled = !session.CanReplaceState; _source.Disabled = !session.CanReplaceState; _blank.Disabled = !session.CanReplaceState;
        foreach (var button in _speedButtons) button.Disabled = !session.CanSetTimeScale;
        _rename.Disabled = !session.CanReplaceState; _saveAs.Disabled = !session.CanSaveState; _demo.Disabled = !session.CanReplaceState;
    }

    /// <summary>
    /// 都市の経過時間と選択中の速度を、地物の集計とは独立して表示します。
    /// 引数: session（CollaborationSession）時計と速度を共有する都市。返り値: void。
    /// </summary>
    public void UpdateRuntime(CollaborationSession session)
    {
        var state = session.State;
        _latestCityState = state;
        _clock.Text = $"都市 {state.Day:N0}日目　{(state.TimeScale == 0 ? "一時停止" : $"{state.TimeScale:g}倍")}\n経過 {Math.Floor(session.SimulationSeconds):N0} 秒";
        _population.Text = $"住民 {state.Simulation.Residents.Count:N0}人　移動中 {state.Simulation.Agents.Count(a => a.Status is "移動中" or "乗車中"):N0}　資金 ¥{state.Funds:N0}";
        var speeds = new[] { 0, 1, 3 };
        for (var i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Modulate = state.TimeScale == speeds[i] ? Mint : Colors.White;
    }

    /// <summary>
    /// 地物と地形の読み込み進捗を、地図パネルを閉じている間も表示します。
    /// 引数: label（string）対象、done/total（int）件数。返り値: void。
    /// </summary>
    public void ShowLoading(string label, int done, int total)
    {
        _loadingProgress[label] = (done, total);
        _loading.Text = $"{label}  {done:N0} / {total:N0}";
        var pending = _loadingProgress.Where(entry => entry.Value.Done < entry.Value.Total).ToArray();
        _worldLoading.Text = string.Join('\n', pending.Select(entry => $"{entry.Key}を準備中　{entry.Value.Done:N0} / {entry.Value.Total:N0}"));
        _worldLoading.Visible = pending.Length > 0;
    }

    /// <summary>
    /// 操作の結果を一定時間表示します。
    /// 引数: message（string）案内。返り値: void。
    /// </summary>
    public void Notify(string message) { _notice.Text = message; _noticeTime = 7; _notice.Visible = true; }

    /// <summary>
    /// 地物の検索結果を選択ボタンとして表示します。
    /// 引数: items（IEnumerable&lt;UrbanObject&gt;）検索結果。返り値: void。
    /// </summary>
    public void ShowSearch(IEnumerable<UrbanObject> items)
    {
        foreach (var child in _searchResults.GetChildren()) { _searchResults.RemoveChild(child); child.QueueFree(); }
        foreach (var item in items.Take(25)) { var id = item.Id; _searchResults.AddChild(Button($"{KindName(item.Kind)}｜{(item.Name.Length > 0 ? item.Name : item.Id[..Math.Min(15, item.Id.Length)])}", () => SelectRequested?.Invoke(id))); }
    }

    /// <summary>
    /// 地図で選んだ道路または交差点を設計入力へ追加します。
    /// 引数: field（string）接続欄、id（string）地物識別子。返り値: void。
    /// </summary>
    public void AddLink(string field, string id)
    {
        if (field == "parent") _strings["Parent"].Text = id;
        else if (field == "approach") _strings["Approach"].Text = id;
        else _links.Text = string.Join('\n', Lines(_links.Text).Append(id).Distinct());
        Notify("接続先を入力しました。「設計値を反映」で確定します。");
    }

    // ----------------------------------------

    /// <summary>
    /// 設計入力を読み、検証可能なイベントとして渡します。
    /// 引数: shape（bool）頂点も反映するか。返り値: void。
    /// </summary>
    private void EmitDesign(bool shape) { try { ApplyRequested?.Invoke(ReadDesign(shape)); } catch (FormatException error) { Notify(error.Message); } }

    /// <summary>
    /// 道路ごとに編集可能な信号現示の入力行を作成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BuildPhases() { _phases.Text = string.Join('\n', Lines(_links.Text).Select((id, i) => $"現示{i + 1} | 30 | {id}")); if (_phases.Text.Length == 0 && _strings["Approach"].Text.Length > 0) _phases.Text = $"現示1 | 30 | {_strings["Approach"].Text}"; }

    /// <summary>
    /// 都市名、新規都市、複数の保存データを扱う画面を生成します。
    /// 引数: theme（Theme）共通テーマ。返り値: void。
    /// </summary>
    private void BuildCitySaves(Theme theme)
    {
        _saves = new Window { Title = "都市とセーブデータ", Size = new Vector2I(660, 700), Visible = false, Transient = true, Exclusive = true, Theme = theme };
        AddChild(_saves); _saves.CloseRequested += () => _saves.Hide();
        var panel = new PanelContainer(); _saves.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); panel.AddThemeStyleboxOverride("panel", Style(new Color("122b38")));
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 12); panel.AddChild(box);
        box.AddChild(Text("都市の名前", 22));
        _cityName = new LineEdit { Name = "CityName", MaxLength = 80, PlaceholderText = "この都市の名前" }; box.AddChild(_cityName);
        var row = new HBoxContainer(); box.AddChild(row);
        _rename = Button("名前を変更", () => CityNameRequested?.Invoke(_cityName.Text)); row.AddChild(_rename);
        _saveAs = Button("別のセーブとして保存", () => { SaveAsRequested?.Invoke(_cityName.Text); _saves.Hide(); }, true); row.AddChild(_saveAs);
        box.AddChild(Text("新しい都市", 18));
        _source = Button("実都市から新規", () => { _saves.Hide(); NewCityRequested?.Invoke(true); }); box.AddChild(_source);
        _blank = Button("更地から新規", () => { _saves.Hide(); NewCityRequested?.Invoke(false); }); box.AddChild(_blank);
        _demo = Button("交通の試験街区を新規作成", () => { _saves.Hide(); DemoRequested?.Invoke(); }); box.AddChild(_demo);
        box.AddChild(Text("保存した都市", 18));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; box.AddChild(scroll);
        _saveList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_saveList);
        box.AddChild(Text("都市名は自由に付けられます。保存データごとに都市と住民の状態を保持します。", 12, Muted));
    }

    /// <summary>
    /// 保存済み都市の一覧を表示します。選択した都市はゲーム側で検証して読み込みます。
    /// 引数: entries（IReadOnlyList&lt;CitySaveEntry&gt;）保存、cityName（string）現在の都市名。返り値: void。
    /// </summary>
    public void ShowSaves(IReadOnlyList<CitySaveEntry> entries, string cityName)
    {
        _cityName.Text = cityName;
        foreach (var child in _saveList.GetChildren()) { _saveList.RemoveChild(child); child.QueueFree(); }
        if (entries.Count == 0) _saveList.AddChild(Text("保存した都市はまだありません。", 14, Muted));
        foreach (var entry in entries)
            _saveList.AddChild(Button($"{entry.Name}　{entry.UpdatedUtc.ToLocalTime():yyyy/MM/dd HH:mm}", () => { _saves.Hide(); LoadSaveRequested?.Invoke(entry.Path); }));
        _saves.PopupCentered();
    }

    /// <summary>
    /// 目的地を持つ人と乗り物を一覧からも選択できる画面を生成します。
    /// 引数: theme（Theme）共通テーマ。返り値: void。
    /// </summary>
    private void BuildTraffic(Theme theme)
    {
        _traffic = new Window { Title = "住民と交通", Size = new Vector2I(680, 620), Visible = false, Transient = true, Exclusive = true, Theme = theme };
        AddChild(_traffic); _traffic.CloseRequested += () => _traffic.Hide();
        var panel = new PanelContainer(); _traffic.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); panel.AddThemeStyleboxOverride("panel", Style(new Color("122b38")));
        var box = new VBoxContainer(); panel.AddChild(box); box.AddChild(Text("住民と乗り物を選ぶ", 23));
        box.AddChild(Text("住宅・目的地・接続した道路が必要です。鉄道は駅と線路、船は港と航路を使います。", 12, Muted));
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; box.AddChild(scroll);
        _agentList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_agentList);
        _trafficEmpty = Text("用途を指定した住宅と目的地を作り、時間を進めてください。", 14, Muted);
        _trafficEmpty.AutowrapMode = TextServer.AutowrapMode.WordSmart; _agentList.AddChild(_trafficEmpty);
        _trafficLimit = Text("", 12, Muted); _trafficLimit.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _trafficLimit.Visible = false; _agentList.AddChild(_trafficLimit);
    }

    /// <summary>
    /// 停止中でも最後に受け取った都市から一覧を準備し、住民と交通の画面を開きます。
    /// 引数: なし。返り値: void。次の時間更新を待たずに個体を表示します。
    /// </summary>
    private void ShowTraffic()
    {
        _traffic.PopupCentered();
        if (_latestCityState is { } state) UpdateTraffic(state);
    }

    /// <summary>
    /// 都市を記憶し、表示中の一覧を個体識別子ごとの差分で更新します。
    /// 引数: state（CityState）都市。返り値: void。存在するボタンは維持して押下・フォーカスを中断しません。
    /// </summary>
    public void UpdateTraffic(CityState state)
    {
        _latestCityState = state;
        if (!_traffic.Visible) return;
        var agents = state.Simulation.Agents.Take(100).ToArray();
        var ids = agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _trafficRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var removed = _trafficRows[id]; _trafficRows.Remove(id);
            _agentList.RemoveChild(removed); removed.QueueFree();
        }
        for (var index = 0; index < agents.Length; index++)
        {
            var agent = agents[index]; var id = agent.Id;
            if (!_trafficRows.TryGetValue(id, out var button))
            {
                button = Button("", () => { _traffic.Hide(); AgentSelected?.Invoke(id); });
                button.FocusMode = Control.FocusModeEnum.All;
                _trafficRows.Add(id, button); _agentList.AddChild(button);
            }
            var text = $"{AgentKind(agent.Kind)}　{agent.Name}　{agent.Purpose}・{agent.Status}";
            if (button.Text != text) button.Text = text;
            if (button.GetIndex() != index) _agentList.MoveChild(button, index);
        }
        _trafficEmpty.Visible = agents.Length == 0;
        _trafficLimit.Text = string.Join('\n', new[] { state.Simulation.Agents.Count > 100 ? "先頭100件を表示しています。" : "", state.Simulation.Limitation }.Where(text => text.Length > 0));
        _trafficLimit.Visible = _trafficLimit.Text.Length > 0;
    }

    /// <summary>
    /// クリックした住民または乗り物の目的地と状態を表示します。
    /// 引数: agent（UrbanAgent）個体、origin/destination（string）今回の起終点名、details（UrbanAgentDetails?）住居・職場・滞在先と操作。
    /// 返り値: void。長い施設名と関連人物は独立したスクロール領域へ収めます。
    /// </summary>
    public void ShowAgent(UrbanAgent agent, string origin, string destination, UrbanAgentDetails? details = null)
    {
        if (_agentDetailsScroll is null)
        {
            var parent = (VBoxContainer)_agentInfo.GetParent();
            var index = _agentInfo.GetIndex();
            parent.RemoveChild(_agentInfo);
            _agentDetailsScroll = new ScrollContainer
            {
                Name = "AgentDetailsScroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            parent.AddChild(_agentDetailsScroll); parent.MoveChild(_agentDetailsScroll, index);
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", 10); _agentDetailsScroll.AddChild(content);
            _agentInfo.Name = "AgentInformation"; _agentInfo.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; content.AddChild(_agentInfo);
            _agentActions = new VBoxContainer { Name = "AgentActions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; content.AddChild(_agentActions);
            _agentFollowToggle = Button("追跡 オフ（開始）", () => _currentAgentDetails?.FollowRequested?.Invoke());
            _agentFollowToggle.Name = "AgentFollowToggle"; _agentActions.AddChild(_agentFollowToggle);
            _agentRouteToggle = new CheckBox { Name = "AgentRouteToggle", Text = "今回の経路を表示", FocusMode = Control.FocusModeEnum.None };
            _agentRouteToggle.Toggled += value => _currentAgentDetails?.RouteChanged?.Invoke(value); _agentActions.AddChild(_agentRouteToggle);
            _agentRelations = new VBoxContainer { Name = "AgentRelations", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; content.AddChild(_agentRelations);
            var designScroll = (Control)_fields.GetParent();
            var designTabs = parent.GetChildren().OfType<HBoxContainer>().FirstOrDefault(row => row.GetChildren().OfType<Button>().Any(button => button.Text == "設計"));
            _syncAgentDetails = () =>
            {
                var showingAgent = _selectedAgent is not null;
                _agentDetailsScroll.Visible = _agentInfo.Visible;
                _agentDetailsScroll.SizeFlagsVertical = showingAgent ? Control.SizeFlags.ExpandFill : Control.SizeFlags.ShrinkBegin;
                _agentDetailsScroll.CustomMinimumSize = new Vector2(0, showingAgent ? 150 : Math.Min(110, _agentInfo.GetCombinedMinimumSize().Y));
                _agentActions.Visible = showingAgent; _agentRelations.Visible = showingAgent;
                designScroll.Visible = !showingAgent;
                if (designTabs is not null) designTabs.Visible = !showingAgent;
            };
            _agentInfo.VisibilityChanged += () => _syncAgentDetails?.Invoke();
        }
        _currentAgentDetails = details;
        _selectedAgent = agent.Id; _hasSelection = false; _inspector.Visible = true; _fields.Visible = false; _agentInfo.Visible = true; _objectTools.Visible = true;
        foreach (var button in _objectTools.GetChildren().OfType<Button>()) button.Disabled = button.Text is "移動" or "回転" or "高さ";
        _selection.Text = agent.Name; _identity.Text = $"{AgentKind(agent.Kind)}　{agent.Id}";
        var person = agent.Kind == "person";
        var residence = person ? $"住居　{details?.Home ?? "未設定"}\n職場　{details?.Workplace ?? "未割当"}\n現在の滞在先　{details?.CurrentPlace ?? "未設定"}\n\n" : "";
        var occupants = person ? "" : $"\n{(agent.Kind == "car" ? "乗員" : "乗客")}　{agent.PassengerIds.Count}人\n{details?.OccupantNotice}";
        _agentInfo.Text = $"状態　{agent.Status}\n\n{residence}今回の目的　{agent.Purpose}\n今回の出発地　{origin}\n今回の行先　{destination}\n現在の移動手段　{AgentMode(agent.Mode)}\n残り経路　{Math.Max(0, agent.Route.Count - agent.RouteIndex)} 地点{occupants}{(agent.WaitingReason.Length > 0 ? "\n\n停止・待機の理由\n" + agent.WaitingReason : "")}";
        _agentFollowToggle!.Text = details?.Following == true ? "追跡 オン（解除）" : details?.CanFollow == true ? "追跡 オフ（開始）" : "追跡 オフ（所在地を見る）";
        _agentFollowToggle.Disabled = details?.FollowRequested is null;
        _agentRouteToggle!.SetPressedNoSignal(details?.RouteVisible ?? true); _agentRouteToggle.Disabled = details?.RouteChanged is null || agent.Route.Count == 0;
        var links = details?.Links ?? Array.Empty<UrbanAgentLink>();
        var signature = agent.Id + "|" + string.Join("|", links.Select(link => $"{link.Id}:{link.IsAgent}:{link.Label}"));
        if (_agentLinkSignature != signature)
        {
            _agentLinkSignature = signature;
            foreach (var child in _agentRelations!.GetChildren()) { _agentRelations.RemoveChild(child); child.QueueFree(); }
            foreach (var link in links)
            {
                var button = Button(link.Label, () => { if (link.IsAgent) AgentSelected?.Invoke(link.Id); else SelectRequested?.Invoke(link.Id); });
                button.Name = "AgentRelation"; button.ClipText = true; button.TooltipText = link.Label; button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                _agentRelations!.AddChild(button);
            }
        }
        _syncAgentDetails?.Invoke();
    }

    /// <summary>
    /// 選択建物へ入居した実住民数と接続待ちの理由を表示します。
    /// 引数: state（CityState）都市、id（string）建物。返り値: void。
    /// </summary>
    public void ShowBuildingRuntime(CityState state, string id)
    {
        if (!_hasSelection || _basis.Kind != "building") return;
        var residents = state.Simulation.Residents.Where(person => person.HomeId == id).ToList();
        var workers = state.Simulation.Residents.Count(person => person.WorkId == id);
        var reason = state.Simulation.Agents.FirstOrDefault(agent => residents.Any(person => person.Id == agent.Id) && agent.WaitingReason.Length > 0)?.WaitingReason;
        _agentInfo.Visible = true; _agentInfo.Text = $"実入居 {residents.Count}人　勤務先に設定 {workers}人{(reason is null ? "" : "\n" + reason)}";
    }

    /// <summary>
    /// 個体種別の日本語名を返します。
    /// 引数: kind（string）種別。返り値: string 表示名。
    /// </summary>
    private static string AgentKind(string kind) => kind switch { "person" => "住民", "car" => "自動車", "train" => "電車", "ship" => "船", _ => "移動体" };

    /// <summary>
    /// 移動手段の日本語名を返します。
    /// 引数: mode（string）手段。返り値: string 表示名。
    /// </summary>
    private static string AgentMode(string mode) => mode switch { "walk" => "徒歩", "car" => "自動車", "train" => "鉄道", "ship" => "船", _ => mode };

    /// <summary>
    /// 共同編集の接続画面を作成します。
    /// 引数: theme（Theme）共通テーマ。返り値: void。
    /// </summary>
    private void BuildNetwork(Theme theme)
    {
        _network = new Window { Title = "都市の共同編集", Size = new Vector2I(480, 370), Visible = false, Transient = true, Exclusive = true, Theme = theme };
        AddChild(_network); _network.CloseRequested += () => _network.Hide();
        var panel = new PanelContainer(); _network.AddChild(panel); panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); panel.AddThemeStyleboxOverride("panel", Style(new Color("122b38")));
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 14); panel.AddChild(box);
        box.AddChild(Text("同じ都市の全地物と地形を共同編集", 20));
        _address = new LineEdit { Text = "127.0.0.1", PlaceholderText = "接続先IPアドレス" }; _port = new LineEdit { Text = "24567", PlaceholderText = "UDPポート" }; box.AddChild(_address); box.AddChild(_port);
        box.AddChild(Button("この都市をホストとして開始", () => { if (ReadPort(out var port)) { HostRequested?.Invoke(port); _network.Hide(); } }));
        box.AddChild(Button("ホスト・専用サーバーに参加", () => { if (ReadPort(out var port)) { JoinRequested?.Invoke(_address.Text.Trim(), port); _network.Hide(); } }));
        box.AddChild(Button("接続を終了する", () => { DisconnectRequested?.Invoke(); _network.Hide(); }));
        box.AddChild(Text("保存できるのは都市を所有するホストだけです。\nホスト切断時は終了し、参加前の自分の都市へ戻ります。", 12, Muted));
    }

    /// <summary>
    /// UDPポートを読み取って範囲を確認します。
    /// 引数: port（out int）ポート。返り値: bool 入力が有効かどうか。
    /// </summary>
    private bool ReadPort(out int port) { if (int.TryParse(_port.Text, out port) && port is >= 1024 and <= 65535) return true; Notify("ポートは1024〜65535で指定してください。"); return false; }

    /// <summary>
    /// 種別に対応する設計入力を表示します。
    /// 引数: kind（string）地物種別。返り値: void。
    /// </summary>
    private void SetGroups(string kind)
    {
        foreach (var group in _groups.Values) group.Visible = false;
        _groups["actions"].Visible = true;
        _groups["common"].Visible = _panelTab == "design";
        _groups["building"].Visible = _panelTab == "design" && kind is "building" or "prop";
        _groups["road"].Visible = _panelTab == "operation" && kind == "road";
        _groups["junction"].Visible = _panelTab == "operation" && kind is "intersection" or "signal";
        _groups["signal"].Visible = _panelTab == "operation" && kind is "intersection" or "signal";
        _groups["geometry"].Visible = _panelTab == "geometry" && kind != "signal";
        _fieldRows["Depth"].Visible = kind != "road";
        _fieldRows["Height"].Visible = kind is "building" or "prop" or "signal";
        _fieldRows["Floors"].Visible = kind == "building";
        _fieldRows["FloorHeight"].Visible = kind == "building";
    }

    /// <summary>
    /// プロパティのグループを作成します。
    /// 引数: key/title（string）識別子と見出し。返り値: VBoxContainer 入力欄の親。
    /// </summary>
    private VBoxContainer Group(string key, string title) { var group = new VBoxContainer(); group.AddThemeConstantOverride("separation", 7); group.AddChild(Text(title, 16, Mint)); _fields.AddChild(group); _groups.Add(key, group); return group; }

    /// <summary>
    /// 文字列の入力欄を作成します。
    /// 引数: parent（Node）親、key/caption（string）識別子と見出し。返り値: void。
    /// </summary>
    private void StringField(Node parent, string key, string caption) { parent.AddChild(Text(caption, 12, Muted)); var edit = new LineEdit { Name = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; parent.AddChild(edit); _strings.Add(key, edit); }

    /// <summary>
    /// 単位と範囲を持つ数値入力を作成します。
    /// 引数: parent（Node）親、key/caption（string）識別子と見出し、min/max/step/value（double）入力範囲と値。返り値: void。
    /// </summary>
    private void Number(Node parent, string key, string caption, double min, double max, double step, double value = 0) { var row = new HBoxContainer(); parent.AddChild(row); var label = Text(caption, 12, Muted); label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; row.AddChild(label); var number = new SpinBox { Name = key, MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(112, 30) }; row.AddChild(number); _numbers.Add(key, number); _fieldRows.Add(key, row); }

    /// <summary>
    /// 数値入力を取得します。
    /// 引数: key（string）欄の識別子。返り値: float 入力値。
    /// </summary>
    private float Value(string key)
    {
        var number = _numbers[key]; if (number.GetLineEdit().HasFocus()) number.Apply();
        return _displayedNumbers.TryGetValue(key, out var shown) && number.Value == shown.Displayed ? (float)shown.Original : (float)number.Value;
    }

    /// <summary>
    /// 未変更座標は原典値をそのまま使い、単位変換による丸め変更を防ぎます。
    /// 引数: key（string）入力欄、original（float）元の都市座標。返り値: float 都市座標。
    /// </summary>
    private float Coordinate(string key, float original)
    {
        var meters = Value(key);
        return _displayedNumbers.TryGetValue(key, out var shown) && _numbers[key].Value == shown.Displayed ? original : meters / 10;
    }

    /// <summary>
    /// 数値入力を更新します。
    /// 引数: key（string）欄の識別子、value（double）値。返り値: void。
    /// </summary>
    private void Set(string key, double value) { _numbers[key].Value = value; _displayedNumbers[key] = (value, _numbers[key].Value); }

    /// <summary>
    /// 空行を除いた入力行を列挙します。
    /// 引数: text（string）複数行入力。返り値: IEnumerable&lt;string&gt; 有効な行。
    /// </summary>
    private static IEnumerable<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// メートル単位の相対頂点を都市座標へ変換します。
    /// 引数: text（string）頂点行、x/z（float）中心都市座標。返り値: List&lt;UrbanPoint&gt; 頂点列。
    /// </summary>
    private static List<UrbanPoint> ParsePoints(string text, float x, float z)
    {
        var points = new List<UrbanPoint>();
        foreach (var line in Lines(text)) { var pair = line.Replace('，', ',').Split(','); if (pair.Length != 2 || !float.TryParse(pair[0], CultureInfo.InvariantCulture, out var px) || !float.TryParse(pair[1], CultureInfo.InvariantCulture, out var pz)) throw new FormatException("頂点は1行に「東西,南北」を数値で入力してください。"); points.Add(new UrbanPoint(x + px / 10, z + pz / 10)); }
        return points;
    }

    /// <summary>
    /// 現示名・青時間・通行道路を入力行から復元します。
    /// 引数: text（string）現示行。返り値: List&lt;SignalPhase&gt; 現示の順序。
    /// </summary>
    private static List<SignalPhase> ParsePhases(string text)
    {
        var phases = new List<SignalPhase>();
        foreach (var line in Lines(text)) { var parts = line.Split('|', StringSplitOptions.TrimEntries); if (parts.Length != 3 || !float.TryParse(parts[1], CultureInfo.InvariantCulture, out var seconds)) throw new FormatException("信号は「名称 | 青時間 | 通行道路ID,ID」の形式で入力してください。"); phases.Add(new SignalPhase { Name = parts[0], DurationSeconds = seconds, GreenRoadIds = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() }); }
        return phases;
    }

    /// <summary>
    /// 選択欄を作成し内部値を保存します。
    /// 引数: parent（Node）親、caption（string）見出し、choices（(string,string)[]）内部値と表示名。返り値: OptionButton 選択欄。
    /// </summary>
    private static OptionButton Options(Node parent, string caption, (string Value, string Label)[] choices) { parent.AddChild(Text(caption, 12, Muted)); var result = new OptionButton(); foreach (var choice in choices) { result.AddItem(choice.Label); result.SetItemMetadata(result.ItemCount - 1, choice.Value); } parent.AddChild(result); return result; }

    /// <summary>
    /// 選択欄を内部値で選びます。
    /// 引数: options（OptionButton）選択欄、value（string）内部値。返り値: void。
    /// </summary>
    private static void SelectOption(OptionButton options, string value) { for (var i = 0; i < options.ItemCount; i++) if (options.GetItemMetadata(i).AsString() == value) { options.Select(i); return; } options.Select(0); }

    /// <summary>
    /// 複数行の入力欄を作成します。
    /// 引数: parent（Node）親、name（string）欄名、height（int）最小高。返り値: TextEdit 入力欄。
    /// </summary>
    private static TextEdit Editor(Node parent, string name, int height) { var result = new TextEdit { Name = name, CustomMinimumSize = new Vector2(0, height), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary }; parent.AddChild(result); return result; }

    /// <summary>
    /// 地物の表示用種別名を取得します。
    /// 引数: kind（string）内部種別。返り値: string 日本語表示。
    /// </summary>
    public static string KindName(string kind) => kind switch { "building" => "建築物", "road" => "道路", "intersection" => "交差点", "signal" => "信号", "prop" => "立体物", "surface" => "敷地", _ => "地物" };

    /// <summary>
    /// 日本語ラベルを作成します。
    /// 引数: text（string）文字、size（int）大きさ、color（Color?）色。返り値: Label 表示要素。
    /// </summary>
    private static Label Text(string text, int size, Color? color = null) { var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore }; label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color ?? Ink); return label; }

    /// <summary>
    /// 操作ボタンを作成します。
    /// 引数: text（string）文字、action（Action）操作、primary（bool）強調。返り値: Button ボタン。
    /// </summary>
    private static Button Button(string text, Action action, bool primary = false) { var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32) }; button.AddThemeFontSizeOverride("font_size", 13); button.AddThemeColorOverride("font_color", Ink); button.AddThemeStyleboxOverride("normal", Style(new Color(primary ? "296c5c" : "223d4b"))); button.AddThemeStyleboxOverride("hover", Style(new Color("396b70"))); button.Pressed += action; return button; }

    /// <summary>
    /// 共通パネルの背景と余白を作成します。
    /// 引数: color（Color）背景色。返り値: StyleBoxFlat 背景。
    /// </summary>
    private static StyleBoxFlat Style(Color color) => new() { BgColor = color, CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9, CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 9, ContentMarginBottom = 9 };

    /// <summary>
    /// 画面に追従するパネルを作成します。
    /// 引数: parent（Node）親、left/top/rightEdge/bottomEdge（float）余白、wide/right/bottom/bottomStretch（bool）アンカー。返り値: PanelContainer パネル。
    /// </summary>
    private static PanelContainer Panel(Node parent, float left, float top, float rightEdge, float bottomEdge, bool wide = false, bool right = false, bool bottom = false, bool bottomStretch = false)
    {
        var panel = new PanelContainer { AnchorLeft = right ? 1 : 0, AnchorRight = wide || right ? 1 : 0, AnchorTop = bottom ? 1 : 0, AnchorBottom = bottom || bottomStretch ? 1 : 0 };
        parent.AddChild(panel); panel.OffsetLeft = left; panel.OffsetTop = top; panel.OffsetRight = rightEdge; panel.OffsetBottom = bottomEdge; panel.AddThemeStyleboxOverride("panel", Style(new Color("112731f3"))); return panel;
    }
}
