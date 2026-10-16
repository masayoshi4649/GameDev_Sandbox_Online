using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// CPU 側で確定した住民と交通機関を描画し、個体ごとの選択を提供します。
/// 移動先を描画側で生成せず、都市状態から受け取った位置の間だけを補間します。
/// </summary>
public partial class UrbanAgentRenderer : Node3D
{
    public int VisibleAgentCount => _agents.Count;
    public string? SelectedId { get; private set; }
    public float LastPickDistance { get; private set; } = float.PositiveInfinity;
    public bool SelectedRouteVisible { get; private set; } = true;
    private readonly Dictionary<string, DisplayAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MultiMeshInstance3D> _batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DisplayAgent>> _groups = new(StringComparer.Ordinal);
    private Func<float, float, float> _height = (_, _) => 0;
    private Func<float>? _seaLevel;
    private MeshInstance3D _selection = null!;
    private MeshInstance3D _route = null!;
    private Label3D _selectionLabel = null!;
    private bool _initialized;
    private string? _routeAgentId;
    private List<TravelWaypoint>? _routePoints;
    private Vector3 _routePosition;
    private int _routeIndex;
    private bool _routeRenderable;
    private long _lastSnapshotTick = -1;
    private double _lastClock = double.NaN;
    private bool _paused;

    private sealed class DisplayAgent
    {
        public UrbanAgent Agent = null!;
        public Vector3 Position;
        public Vector3 Start;
        public Vector3 Target;
        public float Yaw;
        public float StartYaw;
        public float TargetYaw;
        public float TransitionSeconds;
        public float TransitionDuration = 1;
        public float WalkDistance;
        public Color Tint;
    }

    /// <summary>
    /// 機能: 個体の種類ごとに共有メッシュと選択表示を生成します。
    /// 引数: height（Func&lt;float, float, float&gt;）都市座標から地表高を求める関数、seaLevel（Func&lt;float&gt;?）海面高。省略時は地形インスタンスから取得します。
    /// 返り値: void。専用サーバーではこのノードを生成する必要がありません。
    /// </summary>
    public void Initialize(Func<float, float, float> height, Func<float>? seaLevel = null)
    {
        if (_initialized) return;
        _initialized = true;
        _height = height;
        _seaLevel = seaLevel ?? (height.Target is UrbanTerrain terrain ? () => terrain.SeaLevel : null);
        foreach (var kind in new[] { "person", "car", "train", "ship" })
        {
            var material = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.78f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            var mesh = BuildMesh(kind);
            mesh.SurfaceSetMaterial(0, material);
            var node = new MultiMeshInstance3D
            {
                Name = kind switch { "person" => "歩行者", "car" => "自動車", "train" => "列車", _ => "船舶" },
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    Mesh = mesh
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(node);
            _batches.Add(kind, node);
            _groups.Add(kind, new List<DisplayAgent>());
        }
        _selection = new MeshInstance3D
        {
            Name = "選択個体の輪郭",
            Mesh = BuildSelectionRing(),
            Visible = false,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("#82e8ff"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_selection);
        _route = new MeshInstance3D
        {
            Name = "選択個体の残り経路",
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_route);
        _selectionLabel = new Label3D
        {
            Name = "選択個体の名前",
            FontSize = 28,
            Font = GD.Load<Font>("res://Assets/Fonts/NotoSansJP-Regular.otf"),
            OutlineSize = 6,
            PixelSize = 0.004f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Visible = false
        };
        AddChild(_selectionLabel);
    }

    /// <summary>
    /// 機能: 同期済みの個体を読み取り、表示先と選択対象を更新します。
    /// 引数: state（CityState）ホストで確定した都市状態。
    /// 返り値: void。乗車中や建物内の住民は屋外の歩行者として重複表示しません。
    /// </summary>
    public void Refresh(CityState state)
    {
        if (!_initialized) return;
        _paused = state.TimeScale <= 0;
        var tickInterval = _lastSnapshotTick < 0 ? 1 : Math.Clamp(state.Simulation.LastProcessedTick - _lastSnapshotTick, 1, 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in state.Simulation.Agents)
        {
            if (!_groups.ContainsKey(agent.Kind) || agent.Kind == "person" && (agent.Status is "乗車中" or "在宅" or "就業中" or "滞在中")) continue;
            if (!float.IsFinite(agent.X) || !float.IsFinite(agent.Z) || !float.IsFinite(agent.Y)) continue;
            seen.Add(agent.Id);
            var target = new Vector3(agent.X, SurfaceHeight(agent.Kind, agent.X, agent.Z) + agent.Y + (agent.Kind == "ship" ? 0.025f : 0.035f), agent.Z);
            var yaw = agent.DirectionX * agent.DirectionX + agent.DirectionZ * agent.DirectionZ > 0.00001f
                ? Mathf.Atan2(-agent.DirectionX, -agent.DirectionZ) : 0;
            if (!_agents.TryGetValue(agent.Id, out var display))
            {
                display = new DisplayAgent { Position = target, Start = target, Target = target, Yaw = yaw, StartYaw = yaw, TargetYaw = yaw, Tint = AgentTint(agent.Id) };
                _agents.Add(agent.Id, display);
            }
            else if (display.Target != target || !Mathf.IsEqualApprox(display.TargetYaw, yaw))
            {
                if (display.Position.DistanceSquaredTo(target) > 400) display.Position = target;
                display.Start = display.Position;
                display.StartYaw = display.Yaw;
                display.TransitionSeconds = 0;
                display.TransitionDuration = tickInterval;
            }
            display.Agent = agent;
            display.Target = target;
            display.TargetYaw = yaw;
        }
        _lastSnapshotTick = state.Simulation.LastProcessedTick;
        foreach (var id in _agents.Keys.Where(id => !seen.Contains(id)).ToArray()) _agents.Remove(id);
        foreach (var group in _groups.Values) group.Clear();
        foreach (var display in _agents.Values) _groups[display.Agent.Kind].Add(display);
        foreach (var group in _groups)
        {
            var multimesh = _batches[group.Key].Multimesh;
            if (multimesh.InstanceCount != group.Value.Count) multimesh.InstanceCount = group.Value.Count;
        }
        if (SelectedId is not null && !_agents.ContainsKey(SelectedId)) Select(null);
        else UpdateRoute();
    }

    /// <summary>
    /// 機能: 固定刻みの実移動結果を都市時計に合わせて補間し、共有描画バッファを更新します。
    /// 引数: simulationSeconds（double）都市の累積シミュレーション秒。進路や移動量は生成しません。
    /// 返り値: void。一時停止時は最後に受け取った確定位置を表示します。
    /// </summary>
    public void AdvanceTime(double simulationSeconds)
    {
        if (!_initialized) return;
        var delta = double.IsNaN(_lastClock) ? 0 : (float)Math.Clamp(simulationSeconds - _lastClock, 0, 2);
        _lastClock = simulationSeconds;
        foreach (var group in _groups)
        {
            var multimesh = _batches[group.Key].Multimesh;
            for (var i = 0; i < group.Value.Count; i++)
            {
                var display = group.Value[i];
                var before = display.Position;
                display.TransitionSeconds += (float)delta;
                var weight = _paused ? 1 : Math.Clamp(display.TransitionSeconds / display.TransitionDuration, 0, 1);
                display.Position = display.Start.Lerp(display.Target, weight);
                display.WalkDistance += new Vector2(display.Position.X - before.X, display.Position.Z - before.Z).Length();
                display.Yaw = Mathf.LerpAngle(display.StartYaw, display.TargetYaw, weight);
                var position = display.Position;
                if (group.Key == "person" && before.DistanceSquaredTo(position) > 0.0000001f)
                    position.Y += Math.Abs(Mathf.Sin(display.WalkDistance * 60)) * 0.007f;
                multimesh.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Up, display.Yaw), position));
                multimesh.SetInstanceColor(i, SelectedId == display.Agent.Id ? new Color(1.2f, 1.25f, 1.3f) : display.Tint);
            }
        }
        UpdateSelection();
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 建物とは独立した個体の立体へレイを当て、最も手前の個体を選びます。
    /// 引数: rayOrigin（Vector3）始点、rayDirection（Vector3）方向、camera（Camera3D?）画面上の許容幅用、screenPosition（Vector2?）押下位置。
    /// 返り値: string? 型の個体識別子。交差しない場合は null です。
    /// </summary>
    public string? Pick(Vector3 rayOrigin, Vector3 rayDirection, Camera3D? camera = null, Vector2? screenPosition = null)
    {
        LastPickDistance = float.PositiveInfinity;
        if (rayDirection.LengthSquared() < 0.00001f) return null;
        rayDirection = rayDirection.Normalized();
        var nearest = float.PositiveInfinity;
        string? selected = null;
        foreach (var display in _agents.Values)
        {
            var size = AgentSize(display.Agent.Kind);
            var basis = new Basis(Vector3.Up, display.Yaw);
            var localOrigin = basis.Inverse() * (rayOrigin - display.Position);
            var localDirection = basis.Inverse() * rayDirection;
            var tolerance = 0.025f;
            if (camera is not null && screenPosition.HasValue)
            {
                var center = display.Position + Vector3.Up * size.Y * 0.5f;
                if (camera.IsPositionBehind(center)) continue;
                var pixelScale = camera.Projection == Camera3D.ProjectionType.Orthogonal
                    ? camera.Size / Math.Max(1, camera.GetViewport().GetVisibleRect().Size.Y)
                    : camera.GlobalPosition.DistanceTo(center) * 2 * Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f)) / Math.Max(1, camera.GetViewport().GetVisibleRect().Size.Y);
                // 遠景で数百人を巨大な当たり判定にせず、近景の小さな個体だけ押しやすくします。
                tolerance = Math.Clamp(pixelScale * 4, 0.025f, display.Agent.Kind == "person" ? 0.15f : 0.3f);
            }
            var min = new Vector3(-size.X * 0.5f - tolerance, -tolerance, -size.Z * 0.5f - tolerance);
            var max = new Vector3(size.X * 0.5f + tolerance, size.Y + tolerance, size.Z * 0.5f + tolerance);
            if (RayBox(localOrigin, localDirection, min, max, out var distance) && distance < nearest)
            {
                nearest = distance;
                selected = display.Agent.Id;
            }
        }
        LastPickDistance = nearest;
        return selected;
    }

    /// <summary>
    /// 機能: 選択中の個体を変更し、名前と足元の輪郭を表示します。
    /// 引数: id（string?）選択する個体識別子。null は選択解除です。
    /// 返り値: void。都市に存在しない識別子は選択解除として扱います。
    /// </summary>
    public void Select(string? id)
    {
        SelectedId = id is not null && _agents.ContainsKey(id) ? id : null;
        if (_initialized) { UpdateSelection(); UpdateRoute(); }
    }

    /// <summary>
    /// 機能: 個体追従カメラなどに表示中の位置を渡します。
    /// 引数: id（string）個体識別子。
    /// 返り値: Vector3? 型の表示位置。非表示または削除済みの場合は null です。
    /// </summary>
    public Vector3? GetAgentPosition(string id) => _agents.TryGetValue(id, out var display) ? display.Position : null;

    /// <summary>
    /// 機能: 選択個体の残り経路の表示を切り替えます。
    /// 引数: visible（bool）経路を表示する場合は true。
    /// 返り値: void。移動計算や目的地は変更しません。
    /// </summary>
    public void SetRouteVisible(bool visible)
    {
        SelectedRouteVisible = visible;
        if (_initialized) UpdateRoute();
    }

    /// <summary>
    /// 機能: 選択個体の輪郭と短い説明を現在位置へ追従させます。
    /// 引数: なし。
    /// 返り値: void。
    /// </summary>
    private void UpdateSelection()
    {
        var visible = SelectedId is not null && _agents.TryGetValue(SelectedId, out _);
        _selection.Visible = visible;
        _selectionLabel.Visible = visible;
        if (!visible) return;
        var display = _agents[SelectedId!];
        var size = AgentSize(display.Agent.Kind);
        _selection.Position = display.Position + Vector3.Up * 0.02f;
        _selection.Basis = new Basis(Vector3.Up, display.Yaw).Scaled(new Vector3(Math.Max(0.17f, size.X * 0.75f), 1, Math.Max(0.17f, size.Z * 0.65f)));
        _selectionLabel.Position = display.Position + Vector3.Up * (size.Y + 0.3f);
        _selectionLabel.Text = $"{display.Agent.Name}\n{display.Agent.Purpose} · {display.Agent.Status}";
    }

    /// <summary>
    /// 機能: 選択個体の確定した残り経路を交通手段ごとの色で地表へ示します。
    /// 引数: なし。
    /// 返り値: void。経路がない場合や選択解除時は線を消します。
    /// </summary>
    private void UpdateRoute()
    {
        _route.Visible = false;
        if (!SelectedRouteVisible) return;
        if (SelectedId is null || !_agents.TryGetValue(SelectedId, out var display)) return;
        var agent = display.Agent;
        if (agent.Route.Count == 0 || agent.RouteIndex >= agent.Route.Count) return;
        if (_routeAgentId == agent.Id && ReferenceEquals(_routePoints, agent.Route) && _routePosition == display.Target && _routeIndex == agent.RouteIndex)
        {
            _route.Visible = _routeRenderable;
            return;
        }
        _routeAgentId = agent.Id; _routePoints = agent.Route; _routePosition = display.Target; _routeIndex = agent.RouteIndex; _routeRenderable = false;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var a = display.Target + Vector3.Up * 0.06f;
        var vertices = 0;
        for (var i = Math.Max(0, agent.RouteIndex); i < agent.Route.Count; i++)
        {
            var point = agent.Route[i];
            var b = new Vector3(point.X, SurfaceHeight(point.Mode, point.X, point.Z) + point.Y + 0.095f, point.Z);
            var direction = b - a;
            var side = new Vector3(-direction.Z, 0, direction.X).Normalized() * 0.025f;
            if (side.LengthSquared() > 0)
            {
                tool.SetColor(point.Mode switch { "car" => new Color("#ffc879"), "train" => new Color("#d0abff"), "ship" => new Color("#80cfff"), _ => new Color("#88f3bd") });
                tool.SetNormal(Vector3.Up);
                foreach (var vertex in new[] { a - side, a + side, b + side, a - side, b + side, b - side }) tool.AddVertex(vertex);
                vertices += 6;
            }
            a = b;
        }
        if (vertices == 0) return;
        _route.Mesh = tool.Commit();
        _routeRenderable = true;
        _route.Visible = true;
    }

    /// <summary>
    /// 機能: 陸上交通には地表、船には海底ではなく現在の海面を基準面として返します。
    /// 引数: kind（string）交通手段、x と z（float）都市座標。
    /// 返り値: float 型の基準高さです。
    /// </summary>
    private float SurfaceHeight(string kind, float x, float z) => kind == "ship" && _seaLevel is not null ? _seaLevel() : _height(x, z);

    /// <summary>
    /// 機能: 種類に対応した車体や人体の外形寸法を取得します。
    /// 引数: kind（string）移動体の種類。
    /// 返り値: Vector3 型の幅、高さ、全長。単位は都市座標です。
    /// </summary>
    private static Vector3 AgentSize(string kind) => kind switch
    {
        "car" => new Vector3(0.19f, 0.16f, 0.45f),
        "train" => new Vector3(0.3f, 0.4f, 8.2f),
        "ship" => new Vector3(0.9f, 0.7f, 3.5f),
        _ => new Vector3(0.07f, 0.175f, 0.06f)
    };

    /// <summary>
    /// 機能: 同じ個体で変化しない控えめな色差を生成します。
    /// 引数: id（string）個体識別子。
    /// 返り値: Color 型の頂点色へ掛け合わせる色です。
    /// </summary>
    private static Color AgentTint(string id)
    {
        uint hash = 2166136261;
        foreach (var character in id) hash = (hash ^ character) * 16777619;
        return Color.FromHsv((hash % 360) / 360f, 0.16f, 1);
    }

    /// <summary>
    /// 機能: 回転済みの個体座標系でレイと直方体の交差距離を計算します。
    /// 引数: origin、direction、min、max（Vector3）レイと境界、distance（out float）交差距離。
    /// 返り値: bool 型の交差有無です。
    /// </summary>
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(direction[axis]) < 0.000001f)
            {
                if (origin[axis] < min[axis] || origin[axis] > max[axis]) { distance = 0; return false; }
                continue;
            }
            var a = (min[axis] - origin[axis]) / direction[axis];
            var b = (max[axis] - origin[axis]) / direction[axis];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (near > far) { distance = 0; return false; }
        }
        distance = near;
        return far >= 0;
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 人体、車、四両編成、船を外部画像なしで区別できる共有立体に組み立てます。
    /// 引数: kind（string）移動体の種類。
    /// 返り値: ArrayMesh 型の色付き三角形メッシュです。
    /// </summary>
    private static ArrayMesh BuildMesh(string kind)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var glass = new Color("#294c65");
        var dark = new Color("#25323c");
        if (kind == "person")
        {
            AddBox(tool, new Vector3(0, 0.091f, 0), new Vector3(0.046f, 0.066f, 0.035f), new Color("#58c0ce"));
            AddBox(tool, new Vector3(0, 0.151f, 0), new Vector3(0.039f, 0.044f, 0.037f), new Color("#edc6a3"));
            foreach (var side in new[] { -1, 1 })
            {
                AddBox(tool, new Vector3(side * 0.014f, 0.031f, 0), new Vector3(0.019f, 0.062f, 0.023f), dark);
                AddBox(tool, new Vector3(side * 0.031f, 0.088f, 0), new Vector3(0.014f, 0.065f, 0.019f), new Color("#edc6a3"));
            }
        }
        else if (kind == "car")
        {
            AddBox(tool, new Vector3(0, 0.06f, 0), new Vector3(0.18f, 0.075f, 0.43f), new Color("#e8dbb0"));
            AddBox(tool, new Vector3(0, 0.112f, 0.015f), new Vector3(0.145f, 0.062f, 0.23f), glass);
            AddBox(tool, new Vector3(0, 0.148f, 0.02f), new Vector3(0.15f, 0.012f, 0.2f), new Color("#eff3ee"));
            foreach (var side in new[] { -1, 1 })
                foreach (var axle in new[] { -1, 1 })
                    AddBox(tool, new Vector3(side * 0.086f, 0.033f, axle * 0.14f), new Vector3(0.026f, 0.064f, 0.066f), dark);
            AddBox(tool, new Vector3(0, 0.065f, -0.219f), new Vector3(0.145f, 0.025f, 0.012f), new Color("#fff5bc"));
            AddBox(tool, new Vector3(0, 0.065f, 0.219f), new Vector3(0.145f, 0.025f, 0.012f), new Color("#c84639"));
        }
        else if (kind == "train")
        {
            for (var car = 0; car < 4; car++)
            {
                var z = (car - 1.5f) * 2.07f;
                AddBox(tool, new Vector3(0, 0.19f, z), new Vector3(0.29f, 0.31f, 1.98f), new Color("#d2ddde"));
                AddBox(tool, new Vector3(0, 0.2f, z), new Vector3(0.3f, 0.045f, 1.98f), new Color("#40abbd"));
                AddBox(tool, new Vector3(0, 0.37f, z), new Vector3(0.25f, 0.045f, 1.82f), new Color("#8d9da4"));
                for (var window = 0; window < 7; window++)
                    AddBox(tool, new Vector3(0, 0.28f, z + (window - 3) * 0.25f), new Vector3(0.303f, 0.075f, 0.17f), glass);
                foreach (var axle in new[] { -1, 1 })
                    AddBox(tool, new Vector3(0, 0.04f, z + axle * 0.64f), new Vector3(0.27f, 0.08f, 0.24f), dark);
            }
        }
        else
        {
            AddBox(tool, new Vector3(0, 0.14f, 0), new Vector3(0.82f, 0.28f, 3.45f), new Color("#285b73"));
            AddBox(tool, new Vector3(0, 0.3f, 0), new Vector3(0.88f, 0.06f, 3.3f), new Color("#e4e5df"));
            AddBox(tool, new Vector3(0, 0.42f, 0.35f), new Vector3(0.62f, 0.24f, 1.8f), new Color("#e6e8df"));
            AddBox(tool, new Vector3(0, 0.58f, -0.15f), new Vector3(0.5f, 0.16f, 0.5f), glass);
            AddBox(tool, new Vector3(0, 0.69f, -0.15f), new Vector3(0.56f, 0.045f, 0.6f), new Color("#f2f0df"));
        }
        return tool.Commit();
    }

    /// <summary>
    /// 機能: 指定された色と寸法の直方体を共有メッシュへ追加します。
    /// 引数: tool（SurfaceTool）構築先、center と size（Vector3）中心と寸法、color（Color）頂点色。
    /// 返り値: void。
    /// </summary>
    private static void AddBox(SurfaceTool tool, Vector3 center, Vector3 size, Color color)
    {
        var half = size * 0.5f;
        var p = new[]
        {
            center + new Vector3(-half.X, -half.Y, -half.Z), center + new Vector3(half.X, -half.Y, -half.Z),
            center + new Vector3(half.X, half.Y, -half.Z), center + new Vector3(-half.X, half.Y, -half.Z),
            center + new Vector3(-half.X, -half.Y, half.Z), center + new Vector3(half.X, -half.Y, half.Z),
            center + new Vector3(half.X, half.Y, half.Z), center + new Vector3(-half.X, half.Y, half.Z)
        };
        var faces = new[] { (0, 3, 2, 1), (4, 5, 6, 7), (0, 4, 7, 3), (1, 2, 6, 5), (3, 7, 6, 2), (0, 1, 5, 4) };
        foreach (var (a, b, c, d) in faces)
        {
            tool.SetColor(color);
            tool.SetNormal((p[b] - p[a]).Cross(p[c] - p[a]).Normalized());
            foreach (var index in new[] { a, b, c, a, c, d }) tool.AddVertex(p[index]);
        }
    }

    /// <summary>
    /// 機能: 選択個体の足元へ表示する楕円輪郭の原形を作成します。
    /// 引数: なし。
    /// 返り値: ArrayMesh 型の円環メッシュです。
    /// </summary>
    private static ArrayMesh BuildSelectionRing()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        for (var segment = 0; segment < 40; segment++)
        {
            var a = Mathf.Tau * segment / 40;
            var b = Mathf.Tau * (segment + 1) / 40;
            var innerA = new Vector3(Mathf.Cos(a) * 0.87f, 0, Mathf.Sin(a) * 0.87f);
            var outerA = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var innerB = new Vector3(Mathf.Cos(b) * 0.87f, 0, Mathf.Sin(b) * 0.87f);
            var outerB = new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b));
            foreach (var vertex in new[] { innerA, outerA, outerB, innerA, outerB, innerB })
            {
                tool.SetNormal(Vector3.Up);
                tool.AddVertex(vertex);
            }
        }
        return tool.Commit();
    }
}
