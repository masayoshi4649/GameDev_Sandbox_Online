using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 地形、都市オブジェクト、建設操作、保存と共同編集を統合します。
/// </summary>
public partial class Main : Node3D
{
    private BayfrontMap _map = null!;
    private OrbitCamera _camera = null!;
    private GameHud _hud = null!;
    private CollaborationSession _session = null!;
    private Node3D _city = null!;
    private readonly Dictionary<string, Node3D> _objects = new();
    private readonly Dictionary<string, string> _signatures = new();
    private readonly List<(Node3D Car, Vector3 From, Vector3 To, float Offset)> _cars = new();
    private MeshInstance3D _cursor = null!;
    private Node3D? _roadPreview;
    private Vector3? _roadStart;
    private Vector3 _lastPreview = new(float.PositiveInfinity, 0, 0);
    private string _tool = "select";
    private string? _selected;
    private float _rotation;
    private int _speed = 1;
    private double _clock;
    private int _frames;
    private string? _capture;
    private string _previewKey = "";
    private bool _placementValid;
    private string _trafficSignature = "";
    private string SavePath => _map.UserSavePath;
    private RealWorldRenderer? _worldRenderer;
    private RealBuilding? _selectedRealBuilding;

    /// <summary>
    /// 都市とUIを初期化し、必要なら自動テストへ切り替えます。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        if (SmokeTest.IsRequested) { SmokeTest.Run(this); return; }
        if (DedicatedServer.IsRequested) { DedicatedServer.Run(this); return; }
        var args = OS.GetCmdlineUserArgs();
        if (!args.Contains("--sample-map") && !InteractionTest.IsRequested)
        {
            AddChild(new CityGame { Name = "CityGame" });
            return;
        }
        var captureIndex = Array.IndexOf(args, "--capture-preview");
        if (captureIndex >= 0 && captureIndex + 1 < args.Length) _capture = args[captureIndex + 1];
        _map = BayfrontMap.Load();
        CreateEnvironment();
        _city = new Node3D { Name = "CityObjects" };
        AddChild(_city);
        _camera = new OrbitCamera();
        AddChild(_camera);
        _camera.ConfigureTerrain(_map.GroundHeight, _map.IsRealWorld);
        if (_map.IsRealWorld) _camera.FocusOn(_map.Districts[1].Center, 330);
        _hud = new GameHud();
        AddChild(_hud);
        _hud.ConfigureMap(_map);
        CreateTerrain();
        _session = new CollaborationSession { Name = "CollaborationSession" };
        AddChild(_session);
        _session.StateChanged += RenderCity;
        _session.StatusChanged += message => { _hud.Notify(message); _hud.UpdateCity(_session.State, _session); };
        _session.PeerCountChanged += _ => _hud.UpdateCity(_session.State, _session);
        _session.Initialize(_map.CreateSeedState(), _map.IsLand, _map.CanPlace);
        GetWindow().Title = "都市づくり｜" + _session.State.CityName;
        _hud.ToolSelected += SetTool;
        _hud.DistrictSelected += index => _camera.FocusOn(_map.Districts[index].Center, _map.IsRealWorld ? 155 : 245);
        _hud.HomeRequested += () => _camera.FocusOn(new Vector2(0, 0), _map.IsRealWorld ? 780 : 510);
        _hud.SaveRequested += SaveCity;
        _hud.LoadRequested += LoadCity;
        _hud.HostRequested += port => _session.Host(port);
        _hud.JoinRequested += (address, port) => _session.Join(address, port);
        _hud.DisconnectRequested += () => _session.Disconnect();
        _hud.SpeedChanged += speed => { _speed = speed; _hud.Notify(speed == 0 ? "都市の時間を一時停止しました。" : $"都市の時間を{speed}倍速にしました。"); };
        _cursor = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 8, BottomRadius = 8, Height = 0.18f, RadialSegments = 48 },
            MaterialOverride = CursorMaterial(new Color("70efd095")),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        AddChild(_cursor);
        RenderCity(_session.State);
        _hud.Notify(_map.IsRealWorld ? "PLATEAUの実都市を読み込んでいます。地区を選ぶと近くから街を確認できます。" : "簡略サンプルを表示中です。実データを取り込むには import-world.ps1 を実行してください。");
        var districtArgument = args.FirstOrDefault(argument => argument.StartsWith("--capture-district=", StringComparison.Ordinal));
        if (districtArgument is not null && int.TryParse(districtArgument.Split('=')[1], out var districtIndex) && districtIndex >= 0 && districtIndex < 4)
            _camera.FocusOn(_map.Districts[districtIndex].Center, 155);
        if (InteractionTest.IsRequested) InteractionTest.Run(this);
    }

    /// <summary>
    /// 都市の時間、配置プレビュー、演出用車両を更新します。
    /// 引数: delta（double）経過秒数。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_session is null) return;
        _clock += delta;
        _session.Tick((float)delta * _speed);
        UpdatePreview();
        foreach (var car in _cars)
        {
            var t = Mathf.PosMod((float)_clock * 0.018f + car.Offset, 1);
            car.Car.Position = car.From.Lerp(car.To, t) + Vector3.Up * 0.52f;
        }
        if (_capture is not null && (_worldRenderer is null || _worldRenderer.IsComplete) && ++_frames == 90) CapturePreview();
    }

    /// <summary>
    /// 建設クリックとショートカットを処理します。
    /// 引数: input（InputEvent）入力。返り値: void。
    /// </summary>
    public override void _UnhandledInput(InputEvent input)
    {
        if (_session is null || GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (input is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.CtrlPressed && key.Keycode == Key.S) { SaveCity(); return; }
            if (key.Keycode == Key.Escape) { _roadStart = null; ClearRoadPreview(); SetTool("select"); }
            if (key.Keycode == Key.R) { _rotation = (_rotation + 90) % 360; _hud.Notify($"配置方向を{_rotation}度に変更しました。"); }
            var tools = new[] { "select", "road", "housing", "office", "park", "civic", "remove" };
            if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key7) SetTool(tools[(int)(key.Keycode - Key.Key1)]);
        }
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse) return;
        var point = Snap(_camera.GroundPosition(mouse.Position));
        if (_tool == "select" || _tool == "remove")
        {
            var target = PickObject(mouse.Position);
            if (_tool == "remove" && target is not null) _session.Remove(target.Id);
            else
            {
                _selected = target?.Id;
                _selectedRealBuilding = target is null ? _map.RealWorld?.PickBuilding(_camera.Camera.ProjectRayOrigin(mouse.Position), _camera.Camera.ProjectRayNormal(mouse.Position)) : null;
                if (_selectedRealBuilding is not null)
                {
                    _hud.SelectRealBuilding(_selectedRealBuilding);
                    if (_tool == "remove") _hud.Notify("原典の実建物は参照用です。撤去できるのは追加した施設と道路です。");
                }
                else _hud.SelectObject(target);
            }
            return;
        }
        if (_tool == "road")
        {
            if (_roadStart is null)
            {
                if (!_map.IsLand(point.X, point.Z)) { _hud.Notify("道路の始点は陸地を選んでください。"); return; }
                _roadStart = point;
                _hud.Notify("道路の終点をクリックしてください。運河をまたぐと橋になります。");
                return;
            }
            var start = _roadStart.Value;
            var road = NewObject("road", start);
            road.EndX = point.X; road.EndZ = point.Z;
            if (_session.Submit(road)) { _roadStart = null; ClearRoadPreview(); }
            return;
        }
        _session.Submit(NewObject(_tool, point));
    }

    // ----------------------------------------

    /// <summary>
    /// 光源、空と水面を生成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void CreateEnvironment()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("b8d2d5"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("c6e1e8"),
            AmbientLightEnergy = _map.IsRealWorld ? 0.65f : 0.25f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            SsaoEnabled = _map.IsRealWorld && RenderingServer.GetCurrentRenderingMethod() == "forward_plus",
            SsaoRadius = 0.7f,
            SsaoIntensity = 0.8f
        };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-49, -32, 0), LightColor = new Color("fff0d6"), LightEnergy = 0.55f,
            ShadowEnabled = true, DirectionalShadowMaxDistance = 1100, ShadowBias = 0.04f
        });
        var shader = new Shader { Code = @"
shader_type spatial;
render_mode cull_disabled;
void fragment() {
    float ripples = sin(VERTEX.x * 0.08 + TIME * 0.48) * sin(VERTEX.z * 0.12 + TIME * 0.24);
    ALBEDO = mix(vec3(0.18, 0.43, 0.51), vec3(0.29, 0.57, 0.61), ripples * 0.25 + 0.55);
    ROUGHNESS = 0.68;
    METALLIC = 0.08;
}" };
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(2400, 2400) }, Position = new Vector3(0, -0.68f, 0), MaterialOverride = new ShaderMaterial { Shader = shader } });
    }

    /// <summary>
    /// 地区の地形とランドマークを配置します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void CreateTerrain()
    {
        if (_map.RealWorld is { } data)
        {
            _worldRenderer = new RealWorldRenderer { Name = "RealWorld" };
            AddChild(_worldRenderer);
            _worldRenderer.ProgressChanged += _hud.ShowWorldProgress;
            _worldRenderer.Completed += () => _hud.Notify($"実建物 {data.Buildings.Count:N0}棟の読み込みが完了しました。");
            _worldRenderer.Failed += message => { _hud.Notify(message); _hud.ShowWorldError(); if (_capture is not null) GetTree().Quit(1); };
            _worldRenderer.Initialize(data, _map.Districts[1].Center);
            return;
        }
        var font = GD.Load<Font>("res://Assets/Fonts/NotoSansJP-Regular.otf");
        foreach (var district in _map.Districts)
        {
            AddChild(AssetFactory.CreateLand(district.Polygon, district.Color));
            var label = new Label3D
            {
                Text = district.Name, Font = font, FontSize = 100, PixelSize = 0.085f, OutlineSize = 12,
                Modulate = new Color("f3f5e6"), OutlineModulate = new Color("30545b"),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                Position = new Vector3(district.Center.X, 10, district.Center.Y), RenderPriority = 10
            };
            AddChild(label);
        }
        foreach (var landmark in _map.Landmarks)
        {
            var node = AssetFactory.CreateLandmark(landmark.Kind);
            node.Position = new Vector3(landmark.Position.X, 0.3f, landmark.Position.Y);
            node.Rotation = new Vector3(0, landmark.Rotation, 0);
            AddChild(node);
        }
    }

    /// <summary>
    /// 変更された都市オブジェクトだけを描画へ反映します。
    /// 引数: state（CityState）最新の都市。返り値: void。
    /// </summary>
    private void RenderCity(CityState state)
    {
        var ids = state.Objects.Select(item => item.Id).ToHashSet();
        foreach (var id in _objects.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _objects[id].QueueFree(); _objects.Remove(id); _signatures.Remove(id);
        }
        foreach (var item in state.Objects)
        {
            var signature = $"{item.Kind}:{item.X}:{item.Z}:{item.EndX}:{item.EndZ}:{item.Rotation}:{item.Variant}";
            if (_signatures.TryGetValue(item.Id, out var previous) && previous == signature) continue;
            if (_objects.Remove(item.Id, out var old)) old.QueueFree();
            Node3D node;
            if (item.Kind == "road") node = AssetFactory.CreateRoad(new Vector3(item.X, _map.GroundHeight(item.X, item.Z) + 0.1f, item.Z), new Vector3(item.EndX, _map.GroundHeight(item.EndX, item.EndZ) + 0.1f, item.EndZ));
            else
            {
                node = AssetFactory.CreateBuilding(item.Kind, item.Variant);
                node.Position = new Vector3(item.X, _map.GroundHeight(item.X, item.Z) + 0.3f, item.Z);
                node.RotationDegrees = new Vector3(0, item.Rotation, 0);
            }
            _city.AddChild(node);
            AddSelectionCollider(node, item);
            _objects[item.Id] = node; _signatures[item.Id] = signature;
        }
        _hud.UpdateCity(state, _session);
        if (_selectedRealBuilding is not null) _hud.SelectRealBuilding(_selectedRealBuilding);
        else _hud.SelectObject(state.Objects.Find(item => item.Id == _selected));
        var roadSignature = string.Join("|", state.Objects.Where(item => item.Kind == "road").Select(item => _signatures[item.Id] + item.Id));
        if (_trafficSignature != roadSignature) { _trafficSignature = roadSignature; CreateTraffic(); }
    }

    /// <summary>
    /// 配置予定位置を陸地・費用・衝突の検証結果とともに表示します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void UpdatePreview()
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        var visible = hovered is null && _tool != "select" && _tool != "remove";
        _cursor.Visible = visible;
        if (!visible) { if (_roadPreview is not null) _roadPreview.Visible = false; return; }
        var point = Snap(_camera.GroundPosition(GetViewport().GetMousePosition()));
        _cursor.Position = point + Vector3.Up * 0.4f;
        _cursor.RotationDegrees = new Vector3(0, _rotation, 0);
        var radius = CityModel.GetRadius(_tool);
        _cursor.Scale = new Vector3(radius / 8, 1, radius / 8);
        var previewKey = $"{point}:{_rotation}:{_tool}:{_roadStart}:{_session.State.Revision}";
        if (_previewKey != previewKey)
        {
            var candidate = NewObject(_tool, point);
            if (_tool == "road" && _roadStart is not null)
            {
                candidate.X = _roadStart.Value.X; candidate.Z = _roadStart.Value.Z;
                candidate.EndX = point.X; candidate.EndZ = point.Z;
            }
            var error = "";
            _placementValid = _tool == "road" && _roadStart is null ? _map.IsLand(point.X, point.Z)
                : CityModel.TryAdd(_session.State.DeepClone(), candidate, _map.IsLand, out error, _map.CanPlace);
            _hud.ShowPlacement(_placementValid ? (_tool == "road" && _roadStart is null ? "陸地で始点を選んでください。" : $"建設費  ¥ {CityModel.GetCost(candidate):N0}") : (error.Length == 0 ? "始点を陸地に置いてください。" : error), _placementValid);
            _previewKey = previewKey;
        }
        ((StandardMaterial3D)_cursor.MaterialOverride).AlbedoColor = _placementValid ? new Color("70efd095") : new Color("ff8b7295");
        if (_roadPreview is not null) _roadPreview.Visible = true;
        if (_tool == "road" && _roadStart is not null && point != _lastPreview)
        {
            ClearRoadPreview();
            if (_roadStart.Value.DistanceTo(point) > 0.5f)
            {
                _roadPreview = AssetFactory.CreateRoad(_roadStart.Value + Vector3.Up * 0.3f, point + Vector3.Up * 0.3f);
                AddChild(_roadPreview);
            }
            _lastPreview = point;
        }
    }

    /// <summary>
    /// 新規配置の識別子と地区情報を作ります。
    /// 引数: kind（string）種類、point（Vector3）地表位置。返り値: CityObject 配置命令。
    /// </summary>
    private CityObject NewObject(string kind, Vector3 point) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = kind, X = point.X, Z = point.Z, Rotation = _rotation,
        Variant = ((int)Math.Abs(point.X + point.Z) / 4) % 8, District = _map.DistrictAt(point.X, point.Z)
    };

    /// <summary>
    /// 近くの建物または道路を選択します。
    /// 引数: screen（Vector2）画面のクリック位置。返り値: CityObject? 選択対象。
    /// </summary>
    private CityObject? PickObject(Vector2 screen)
    {
        var origin = _camera.Camera.ProjectRayOrigin(screen);
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + _camera.Camera.ProjectRayNormal(screen) * 2400, 1);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is StaticBody3D body && body.HasMeta("city_id"))
            return _session.State.Objects.Find(item => item.Id == body.GetMeta("city_id").AsString());
        var point = _camera.GroundPosition(screen);
        CityObject? target = null;
        var closest = 14f;
        foreach (var item in _session.State.Objects)
        {
            var position = new Vector2(point.X, point.Z);
            var start = new Vector2(item.X, item.Z);
            var distance = item.Kind == "road" ? Geometry2D.GetClosestPointToSegment(position, start, new Vector2(item.EndX, item.EndZ)).DistanceTo(position) + 9 : start.DistanceTo(position);
            if (distance < closest) { target = item; closest = distance; }
        }
        return target;
    }

    /// <summary>
    /// 建設ツールを切り替え、道路の始点を初期化します。
    /// 引数: tool（string）識別子。返り値: void。
    /// </summary>
    private void SetTool(string tool)
    {
        _tool = tool; _roadStart = null; _previewKey = ""; ClearRoadPreview(); _hud.SetTool(tool);
    }

    /// <summary>
    /// 屋上や道路を直接クリックできる選択判定を追加します。
    /// 引数: node（Node3D）描画要素、item（CityObject）都市の地物。返り値: void。
    /// </summary>
    private static void AddSelectionCollider(Node3D node, CityObject item)
    {
        var body = new StaticBody3D { CollisionLayer = 1, CollisionMask = 0 };
        body.SetMeta("city_id", item.Id);
        Aabb bounds;
        if (item.Kind == "road")
            bounds = new Aabb(new Vector3(-1.6f, -0.2f, -new Vector2(item.EndX - item.X, item.EndZ - item.Z).Length() / 2),
                new Vector3(3.2f, 1, new Vector2(item.EndX - item.X, item.EndZ - item.Z).Length()));
        else bounds = VisualBounds(node, Transform3D.Identity);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = bounds.Size.Max(new Vector3(0.1f, 0.1f, 0.1f)) }, Position = bounds.GetCenter() });
        node.AddChild(body);
    }

    /// <summary>
    /// モデル内のメッシュを覆う境界箱を取得します。
    /// 引数: node（Node3D）対象、transform（Transform3D）モデル内の変換。返り値: Aabb 境界箱。
    /// </summary>
    private static Aabb VisualBounds(Node3D node, Transform3D transform)
    {
        var bounds = node is MeshInstance3D mesh ? transform * mesh.GetAabb() : new Aabb(transform.Origin, Vector3.Zero);
        foreach (var child in node.GetChildren())
            if (child is Node3D spatial) bounds = bounds.Merge(VisualBounds(spatial, transform * spatial.Transform));
        return bounds;
    }

    /// <summary>
    /// 道路の仮表示を消します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void ClearRoadPreview()
    {
        _roadPreview?.QueueFree(); _roadPreview = null; _lastPreview = new Vector3(float.PositiveInfinity, 0, 0);
    }

    // ----------------------------------------

    /// <summary>
    /// JSONを一時ファイルに書き、旧版を退避して保存します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void SaveCity()
    {
        if (!_session.CanSaveState) { _hud.Notify("参加者は都市データを保存できません。保存はホストが行います。"); return; }
        try
        {
            var path = ProjectSettings.GlobalizePath(SavePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", CityModel.Serialize(_session.State), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(path + ".tmp", path, true);
            _hud.Notify("都市を保存しました。次回は「読み込む」で続きから開発できます。");
        }
        catch (Exception error) { _hud.Notify($"保存できませんでした: {error.Message}"); }
    }

    /// <summary>
    /// 保存済みJSONを検証して都市を置き換えます。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void LoadCity()
    {
        if (!_session.CanReplaceState) { _hud.Notify("都市の読み込みはホストが操作してください。"); return; }
        try
        {
            var path = ProjectSettings.GlobalizePath(SavePath);
            if (!File.Exists(path)) { _hud.Notify("保存済みの都市がありません。先に「都市を保存」を選んでください。"); return; }
            if (new FileInfo(path).Length > CityModel.MaxPayloadBytes) { _hud.Notify("保存ファイルが上限を超えています。"); return; }
            if (!CityModel.TryDeserialize(File.ReadAllText(path), _map.IsLand, out var state, out var error, _map.CanPlace)
                || !CityModel.ValidateMapIdentity(state, _map.MapId, _map.MapVersion, out error)) { _hud.Notify(error); return; }
            if (_session.ReplaceState(state)) _hud.Notify("保存した都市を読み込みました。");
        }
        catch (Exception error) { _hud.Notify($"読み込めませんでした: {error.Message}"); }
    }

    /// <summary>
    /// 道路に沿って走る演出用車両を配置します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void CreateTraffic()
    {
        foreach (var car in _cars) car.Car.QueueFree();
        _cars.Clear();
        var roads = _session.State.Objects.Where(item => item.Kind == "road").Take(36).ToArray();
        for (var i = 0; i < roads.Length; i++)
        {
            var road = roads[i];
            var from = new Vector3(road.X, _map.GroundHeight(road.X, road.Z), road.Z); var to = new Vector3(road.EndX, _map.GroundHeight(road.EndX, road.EndZ), road.EndZ);
            var side = (to - from).Normalized().Cross(Vector3.Up) * 0.65f;
            var car = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.85f, 0.55f, 1.7f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = i % 3 == 0 ? new Color("f1bd69") : new Color("e4efed") }
            };
            AddChild(car);
            car.Rotation = new Vector3(0, Mathf.Atan2(to.X - from.X, to.Z - from.Z), 0);
            _cars.Add((car, from + side, to + side, i * 0.13f));
        }
    }

    /// <summary>
    /// 表示確認用の画面を保存して終了します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private async void CapturePreview()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(_capture!);
        GD.Print($"描画確認: {Engine.GetFramesPerSecond():F1} FPS / 実地図={_map.IsRealWorld} / 建物={_map.RealWorld?.Buildings.Count ?? 0}");
        GD.Print($"画面保存: {error} / {_capture}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    /// <summary>
    /// 実地図では1m、簡略地図では40m単位で配置し、実地表高を適用します。
    /// 引数: point（Vector3）地表位置。返り値: Vector3 補正位置。
    /// </summary>
    private Vector3 Snap(Vector3 point)
    {
        var step = _map.IsRealWorld ? 0.1f : 4;
        var x = Mathf.Snapped(point.X, step); var z = Mathf.Snapped(point.Z, step);
        return new Vector3(x, _map.GroundHeight(x, z), z);
    }

    /// <summary>
    /// 半透明の配置表示用マテリアルを生成します。
    /// 引数: color（Color）色。返り値: StandardMaterial3D マテリアル。
    /// </summary>
    private static StandardMaterial3D CursorMaterial(Color color) => new()
    {
        AlbedoColor = color, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true
    };
}
