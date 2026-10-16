using Godot;
using System;
using System.Collections.Generic;

namespace Bayfront;

/// <summary>
/// 建設ツール、都市指標、共同編集の操作画面を組み立てます。
/// </summary>
public partial class GameHud : CanvasLayer
{
    public event Action<string>? ToolSelected;
    public event Action<int>? DistrictSelected;
    public event Action? SaveRequested;
    public event Action? LoadRequested;
    public event Action? HomeRequested;
    public event Action<int>? HostRequested;
    public event Action<string, int>? JoinRequested;
    public event Action? DisconnectRequested;
    public event Action<int>? SpeedChanged;
    private readonly Dictionary<string, Button> _tools = new();
    private readonly List<Button> _speedButtons = new();
    private Label _money = null!, _population = null!, _jobs = null!, _balance = null!, _day = null!;
    private Label _status = null!, _toast = null!, _toolTitle = null!, _toolDescription = null!, _toolCost = null!, _selection = null!, _network = null!;
    private Label _worldDetails = null!, _worldLoad = null!, _footer = null!;
    private BayfrontMap? _map;
    private Button _load = null!, _save = null!, _host = null!;
    private Window _connection = null!;
    private LineEdit _address = null!, _port = null!;
    private double _toastTime;
    private static readonly Color Ink = new("e8f2f0");
    private static readonly Color Muted = new("8da7b3");
    private static readonly Color Mint = new("83e0c5");
    private static readonly Color Panel = new("112b3bef");

    /// <summary>
    /// 日本語の画面と接続ダイアログを生成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var font = GD.Load<Font>("res://Assets/Fonts/NotoSansJP-Regular.otf");
        root.Theme = new Theme { DefaultFont = font, DefaultFontSize = 14 };
        var top = Surface(root, new Vector2(20, 18), new Vector2(-20, 100), true);
        var topRow = Spaced(new HBoxContainer(), 25);
        top.AddChild(Margin(topRow, 22, 12));
        var brand = Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) }, 0);
        brand.AddChild(Text("都市づくり", 22, Ink));
        brand.AddChild(Text("公的データで、東京湾岸の街をつくる。", 11, Muted));
        topRow.AddChild(brand);
        _money = Metric(topRow, "都市の資金", "¥ 25,000,000", 175);
        _population = Metric(topRow, "人口", "— 人", 95);
        _jobs = Metric(topRow, "雇用", "— 件", 95);
        _balance = Metric(topRow, "一日あたりの収支", "—", 150);
        topRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var session = Spaced(new VBoxContainer(), 6);
        _status = Text("●  ソロで開発中", 12, Mint);
        session.AddChild(_status);
        session.AddChild(Button("共同編集をはじめる", ShowConnection, true));
        topRow.AddChild(session);

        // ----------------------------------------

        var left = Surface(root, new Vector2(20, 120), new Vector2(222, 410));
        var districtBox = Spaced(new VBoxContainer(), 7);
        left.AddChild(Margin(districtBox, 16, 14));
        districtBox.AddChild(Text("開発エリア", 11, Muted));
        districtBox.AddChild(Text("東京湾岸", 23, Ink));
        var districtNames = new[] { "01    台場", "02    有明", "03    東雲", "04    豊洲" };
        for (var i = 0; i < districtNames.Length; i++)
        {
            var index = i;
            districtBox.AddChild(Button(districtNames[i] + "    ↗", () => DistrictSelected?.Invoke(index)));
        }
        districtBox.AddChild(Button("エリア全体を表示", () => HomeRequested?.Invoke()));

        var dataPanel = Surface(root, new Vector2(20, 430), new Vector2(252, 620));
        var dataBox = Spaced(new VBoxContainer(), 9);
        dataPanel.AddChild(Margin(dataBox, 16, 14));
        dataBox.AddChild(Text("実都市データ", 11, Mint));
        _worldDetails = Text("地図を準備しています。", 12, Ink);
        _worldDetails.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _worldDetails.CustomMinimumSize = new Vector2(180, 100);
        dataBox.AddChild(_worldDetails);
        _worldLoad = Text("", 11, Mint);
        _worldLoad.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _worldLoad.CustomMinimumSize = new Vector2(180, 28);
        dataBox.AddChild(_worldLoad);

        var right = Surface(root, new Vector2(-310, 120), new Vector2(-20, 558), false, true);
        var inspector = Spaced(new VBoxContainer(), 10);
        right.AddChild(Margin(inspector, 20, 18));
        inspector.AddChild(Text("開発ツール", 11, Muted));
        _toolTitle = Text("街を見渡す", 25, Ink);
        inspector.AddChild(_toolTitle);
        _toolDescription = Text("建物をクリックすると、施設の情報を確認できます。", 13, Muted);
        _toolDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toolDescription.CustomMinimumSize = new Vector2(215, 66);
        inspector.AddChild(_toolDescription);
        _toolCost = Text("自由な視点で、次のまちへ。", 12, Mint);
        _toolCost.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toolCost.CustomMinimumSize = new Vector2(215, 38);
        inspector.AddChild(_toolCost);
        inspector.AddChild(new HSeparator());
        _selection = Text("施設を選択してください。", 13, Ink);
        _selection.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _selection.CustomMinimumSize = new Vector2(215, 70);
        inspector.AddChild(_selection);
        inspector.AddChild(new HSeparator());
        inspector.AddChild(Text("共同編集", 11, Muted));
        _network = Text("接続端末  1台（ホストを含む）\n都市の保存はホストが行います。", 12, Muted);
        _network.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _network.CustomMinimumSize = new Vector2(215, 48);
        inspector.AddChild(_network);
        var files = Spaced(new HBoxContainer(), 8);
        _save = Button("都市を保存", () => SaveRequested?.Invoke());
        files.AddChild(_save);
        _load = Button("読み込む", () => LoadRequested?.Invoke());
        files.AddChild(_load);
        inspector.AddChild(files);

        // ----------------------------------------

        var help = Surface(root, new Vector2(20, -224), new Vector2(252, -114), false, false, true);
        var helpBox = Spaced(new VBoxContainer(), 5);
        help.AddChild(Margin(helpBox, 16, 12));
        helpBox.AddChild(Text("街を動かす", 13, Ink));
        helpBox.AddChild(Text("移動  WASD / 中ボタンドラッグ\n回転  QE / 右ドラッグ\n拡大  ホイール　配置回転  R", 11, Muted));
        var dock = Surface(root, new Vector2(20, -96), new Vector2(-20, -18), true, false, true);
        var dockRow = Spaced(new HBoxContainer(), 10);
        dock.AddChild(Margin(dockRow, 16, 12));
        var toolList = new (string Id, string Label)[]
        {
            ("select", "1  選択"), ("road", "2  道路"), ("housing", "3  住宅"), ("office", "4  商業"),
            ("park", "5  公園"), ("civic", "6  公共"), ("remove", "7  撤去")
        };
        foreach (var tool in toolList)
        {
            var id = tool.Id;
            var button = Button(tool.Label, () => ToolSelected?.Invoke(id));
            button.CustomMinimumSize = new Vector2(108, 48);
            _tools.Add(id, button);
            dockRow.AddChild(button);
        }
        dockRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var time = Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(105, 0) }, 2);
        _day = Text("開発  1日目", 13, Ink);
        time.AddChild(_day);
        time.AddChild(Text("1日 = 20秒", 10, Muted));
        dockRow.AddChild(time);
        foreach (var speed in new[] { 0, 1, 3 })
        {
            var captured = speed;
            var button = Button(speed == 0 ? "Ⅱ" : $"{speed}倍", () => SpeedChanged?.Invoke(captured));
            button.CustomMinimumSize = new Vector2(46, 44);
            dockRow.AddChild(button);
            _speedButtons.Add(button);
        }

        var footer = Text("", 10, Ink);
        _footer = footer;
        root.AddChild(footer);
        footer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        footer.OffsetLeft = -1080;
        footer.OffsetTop = -116;
        footer.OffsetRight = -20;
        footer.OffsetBottom = -92;
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        footer.AddThemeStyleboxOverride("normal", Style(new Color("112b3cf0"), 5));
        _toast = Text("", 14, Ink);
        root.AddChild(_toast);
        _toast.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _toast.OffsetLeft = -330;
        _toast.OffsetTop = -160;
        _toast.OffsetRight = 330;
        _toast.OffsetBottom = -122;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.AddThemeStyleboxOverride("normal", Style(new Color("112b3bee"), 10));
        BuildConnection();
        SetTool("select");
    }

    /// <summary>
    /// 通知の表示時間を更新します。
    /// 引数: delta（double）経過秒数。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        _toastTime -= delta;
        _toast.Visible = _toastTime > 0;
    }

    /// <summary>
    /// 指標と同期状態を画面へ反映します。
    /// 引数: state（CityState）都市、session（CollaborationSession）同期状態。返り値: void。
    /// </summary>
    public void UpdateCity(CityState state, CollaborationSession session)
    {
        var stats = CityModel.CalculateStatistics(state);
        _money.Text = $"¥ {state.Funds:N0}";
        _population.Text = _map?.RealWorld is { } world ? $"{world.Buildings.Count:N0} 棟" : $"{stats.Population:N0} 人";
        _jobs.Text = _map?.IsRealWorld == true ? $"{state.Objects.Count:N0} 件" : $"{stats.Jobs:N0} 件";
        _balance.Text = $"{(stats.Balance >= 0 ? "+" : "−")} ¥ {Math.Abs(stats.Balance):N0}";
        _balance.AddThemeColorOverride("font_color", stats.Balance >= 0 ? Mint : new Color("f7ad8b"));
        _day.Text = $"開発  {state.Day}日目";
        _status.Text = session.IsConnected ? (session.IsHost ? "●  ホストとして共同開発中" : "●  共同編集中") : "●  ソロで開発中";
        _network.Text = $"接続端末  {session.PeerCount}台（ホストを含む）\n都市の保存はホストが行います。";
        _load.Disabled = !session.CanReplaceState;
        _save.Disabled = !session.CanSaveState;
        _host.Disabled = !session.CanSaveState;
        foreach (var button in _speedButtons) button.Disabled = !session.CanReplaceState;
    }

    /// <summary>
    /// 選択中のツールと説明を更新します。
    /// 引数: tool（string）ツール識別子。返り値: void。
    /// </summary>
    public void SetTool(string tool)
    {
        foreach (var pair in _tools)
            pair.Value.AddThemeStyleboxOverride("normal", Style(pair.Key == tool ? new Color("2b655f") : new Color("203e4e"), 8));
        var data = tool switch
        {
            "road" => ("道をつなぐ", "始点と終点をクリック。運河をまたいで橋も架けられます。Escで始点を取り消します。", "¥ 2,800 / 10m"),
            "housing" => ("暮らしをつくる", "陸地をクリックして集合住宅を建設します。道路やほかの施設から離して配置します。", "建設費  ¥ 180,000"),
            "office" => ("仕事をつくる", "陸地をクリックして商業・業務施設を建設します。都市の雇用と税収を増やします。", "建設費  ¥ 320,000"),
            "park" => ("緑をひろげる", "陸地をクリックして公園を整備します。水辺に緑の余白をつくりましょう。", "建設費  ¥ 90,000"),
            "civic" => ("街を支える", "陸地をクリックして公共施設を建設します。都市のサービスを支える拠点です。", "建設費  ¥ 700,000"),
            "remove" => ("街を整える", "施設または道路をクリックして撤去します。建設費の一部が都市に返還されます。", "撤去は全参加者に反映されます。"),
            _ => ("街を見渡す", "建物をクリックすると、施設の情報を確認できます。地区を選んで街を巡りましょう。", "自由な視点で、次のまちへ。")
        };
        _toolTitle.Text = data.Item1;
        _toolDescription.Text = data.Item2;
        _toolCost.Text = data.Item3;
        _toolCost.AddThemeColorOverride("font_color", Mint);
        if (tool == "select" && _map?.IsRealWorld == true)
            _toolDescription.Text = "実建物をクリックすると、原典の用途・高さ・詳細度を確認できます。施設の追加は下のツールから操作します。";
        if (tool == "remove" && _map?.IsRealWorld == true)
            _toolDescription.Text = "追加した施設・道路をクリックして撤去します。原典の実建物は参照表示です。";
    }

    /// <summary>
    /// 選択施設の説明を更新します。
    /// 引数: item（CityObject?）選択施設。返り値: void。
    /// </summary>
    public void SelectObject(CityObject? item)
    {
        var name = item?.Kind switch { "housing" => "集合住宅", "office" => "商業・業務施設", "park" => "公園", "civic" => "公共施設", "road" => "道路・橋", _ => "" };
        _selection.Text = item is null ? "施設を選択してください。" : $"{item.District}  /  {name}\n位置  {item.X * 10:N0}m, {item.Z * 10:N0}m\n建設費  ¥ {CityModel.GetCost(item):N0}";
    }

    /// <summary>
    /// 一時的な操作結果を通知します。
    /// 引数: message（string）表示内容。返り値: void。
    /// </summary>
    public void Notify(string message)
    {
        _toast.Text = message;
        _toastTime = 5;
        _toast.Visible = true;
    }

    /// <summary>
    /// 配置予定の費用または配置できない理由を示します。
    /// 引数: message（string）説明、valid（bool）配置可能。返り値: void。
    /// </summary>
    public void ShowPlacement(string message, bool valid)
    {
        _toolCost.Text = message;
        _toolCost.AddThemeColorOverride("font_color", valid ? Mint : new Color("f6b49d"));
    }

    /// <summary>
    /// 実都市の収録範囲と原典の詳細度を表示します。
    /// 引数: map（BayfrontMap）地図データ。返り値: void。
    /// </summary>
    public void ConfigureMap(BayfrontMap map)
    {
        _map = map;
        if (map.RealWorld is { } world)
        {
            _population.GetParent().GetChild<Label>(0).Text = "原典の実建物";
            _jobs.GetParent().GetChild<Label>(0).Text = "追加施設";
            _balance.GetParent().GetChild<Label>(0).Text = "追加施設の収支（試算）";
            _worldDetails.Text = $"建物  {world.Buildings.Count:N0} 棟\nLOD2  {world.Lod2Count:N0} 棟\n航空写真  {world.Rasters.Count:N0} 枚\n地理院の実道路・水域\n緯度経度・地形高を維持";
            _footer.Text = "出典：国土交通省PLATEAU・東京都（2025）／国土地理院・Mapterhorn／PLATEAU-Ortho（2023）を加工";
        }
        else
        {
            _worldDetails.Text = "簡略サンプルモード\n実地図は未読み込みです。\nimport-world.ps1で\n公開データを取り込めます。";
            _worldLoad.Text = "実データと区別して表示";
            _footer.Text = "簡略サンプル：実測の地形・建物ではありません。";
        }
        SetTool("select");
    }

    /// <summary>
    /// 公開データの読み込み状況を表示します。
    /// 引数: loaded/total（int）完了数と総数。返り値: void。
    /// </summary>
    public void ShowWorldProgress(int loaded, int total)
    {
        _worldLoad.Text = loaded == total ? $"読み込み完了  {total:N0} タイル" : $"地表・建物の読み込み\n{loaded:N0} / {total:N0} タイル";
    }

    /// <summary>
    /// 地図が不完全な場合に再取り込みの案内を残します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public void ShowWorldError()
    {
        _worldLoad.Text = "実地図を読み込めません。\nデータを再取得してください。";
        _worldLoad.AddThemeColorOverride("font_color", new Color("f6b49d"));
    }

    /// <summary>
    /// 選択した実建物の原典属性を表示します。
    /// 引数: building（RealBuilding）実建物。返り値: void。
    /// </summary>
    public void SelectRealBuilding(RealBuilding building)
    {
        var title = string.IsNullOrWhiteSpace(building.Name) ? building.Usage : building.Name;
        _selection.Text = $"{title}  /  LOD{building.Lod}\nモデル高  {(building.Max.Y - building.Min.Y) * 10:F1}m\n原典の高さ属性  {building.OriginalHeight:F1}m\n参照用の既存建物";
    }

    // ----------------------------------------

    /// <summary>
    /// ホスト・参加の接続画面を生成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BuildConnection()
    {
        _connection = new Window { Title = "都市を一緒につくる", Size = new Vector2I(470, 390), Unresizable = true, Transient = true, Exclusive = true, Visible = false };
        AddChild(_connection);
        _connection.Theme = GetChild<Control>(0).Theme;
        _connection.CloseRequested += () => _connection.Hide();
        var background = new PanelContainer();
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        background.AddThemeStyleboxOverride("panel", Style(new Color("112b3c"), 0));
        _connection.AddChild(background);
        var box = Spaced(new VBoxContainer(), 14);
        background.AddChild(Margin(box, 24, 22));
        box.AddChild(Text("共同編集ルーム", 23, Ink));
        box.AddChild(Text("ホストの都市に参加して、同じ街を編集します。", 13, Muted));
        _address = new LineEdit { Text = "127.0.0.1", PlaceholderText = "ホストのIPアドレス" };
        box.AddChild(_address);
        _port = new LineEdit { Text = "24567", PlaceholderText = "UDPポート番号" };
        box.AddChild(_port);
        var row = Spaced(new HBoxContainer(), 10);
        _host = Button("この都市でホスト", () => WithPort(port => HostRequested?.Invoke(port)), true);
        row.AddChild(_host);
        row.AddChild(Button("都市に参加", () => WithPort(port => JoinRequested?.Invoke(_address.Text.Trim(), port))));
        box.AddChild(row);
        box.AddChild(Button("共同編集の接続を終了する", () => { DisconnectRequested?.Invoke(); _connection.Hide(); }));
        box.AddChild(Text("同じPCは 127.0.0.1、LANではホストのIPを指定。\nインターネット経由はUDPポート転送またはVPNが必要です。", 11, Muted));
    }

    /// <summary>
    /// 入力されたポート番号を検証して接続処理を呼び出します。
    /// 引数: action（Action&lt;int&gt;）接続処理。返り値: void。
    /// </summary>
    private void WithPort(Action<int> action)
    {
        if (!int.TryParse(_port.Text, out var port) || port < 1024 || port > 65535)
        {
            _port.Text = "24567";
            Notify("ポート番号は1024〜65535で指定してください。");
            return;
        }
        action(port);
        _connection.Hide();
    }

    /// <summary>
    /// 接続画面を中央に表示します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void ShowConnection() => _connection.PopupCentered();

    /// <summary>
    /// 指標の見出しと値を生成します。
    /// 引数: parent（Node）親、caption（string）見出し、value（string）値、width（float）幅。返り値: Label 値ラベル。
    /// </summary>
    private static Label Metric(Node parent, string caption, string value, float width)
    {
        var box = Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) }, 2);
        box.AddChild(Text(caption, 10, Muted));
        var label = Text(value, 20, Ink);
        box.AddChild(label);
        parent.AddChild(box);
        return label;
    }

    /// <summary>
    /// 文字と書式を持つラベルを生成します。
    /// 引数: text（string）文字、size（int）大きさ、color（Color）色。返り値: Label ラベル。
    /// </summary>
    private static Label Text(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>
    /// スタイルを統一したボタンを生成します。
    /// 引数: text（string）表示、action（Action）操作、accent（bool）強調。返り値: Button ボタン。
    /// </summary>
    private static Button Button(string text, Action action, bool accent = false)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 32), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", 13);
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeStyleboxOverride("normal", Style(accent ? new Color("2b655f") : new Color("203e4e"), 8));
        button.AddThemeStyleboxOverride("hover", Style(new Color("376b70"), 8));
        button.AddThemeStyleboxOverride("pressed", Style(new Color("367e70"), 8));
        button.Pressed += action;
        return button;
    }

    /// <summary>
    /// 角丸と余白のスタイルを作ります。
    /// 引数: color（Color）背景、radius（int）角丸。返り値: StyleBoxFlat スタイル。
    /// </summary>
    private static StyleBoxFlat Style(Color color, int radius) => new()
    {
        BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 4, ContentMarginBottom = 4
    };

    /// <summary>
    /// 内容の外側に余白を設定します。
    /// 引数: content（Control）内容、horizontal（int）左右、vertical（int）上下。返り値: MarginContainer コンテナ。
    /// </summary>
    private static MarginContainer Margin(Control content, int horizontal, int vertical)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        margin.AddChild(content);
        return margin;
    }

    /// <summary>
    /// ボックス内の要素間隔を設定します。
    /// 引数: box（T）ボックス、separation（int）間隔。返り値: T 設定済みのボックス。
    /// </summary>
    private static T Spaced<T>(T box, int separation) where T : BoxContainer
    {
        box.AddThemeConstantOverride("separation", separation);
        return box;
    }

    /// <summary>
    /// アンカー指定の背景パネルを生成します。
    /// 引数: parent（Node）親、from/to（Vector2）余白、stretch/right/bottom（bool）アンカー指定。返り値: PanelContainer パネル。
    /// </summary>
    private static PanelContainer Surface(Node parent, Vector2 from, Vector2 to, bool stretch = false, bool right = false, bool bottom = false)
    {
        var panel = new PanelContainer { AnchorLeft = right ? 1 : 0, AnchorRight = stretch || right ? 1 : 0, AnchorTop = bottom ? 1 : 0, AnchorBottom = bottom ? 1 : 0 };
        parent.AddChild(panel);
        panel.OffsetLeft = from.X; panel.OffsetTop = from.Y; panel.OffsetRight = to.X; panel.OffsetBottom = to.Y;
        panel.AddThemeStyleboxOverride("panel", Style(Panel, 13));
        return panel;
    }
}
