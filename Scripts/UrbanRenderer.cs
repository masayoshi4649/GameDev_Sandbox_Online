using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 全都市地物を編集パラメータから描画し、変更された400mセルだけを再構築します。
/// 建物や道路の数だけ描画ノードを作らず、頂点色付きメッシュへまとめます。
/// </summary>
public partial class UrbanRenderer : Node3D
{
    public event Action<int, int>? ProgressChanged;
    public event Action? Completed;
    public event Action<string>? Failed;
    public bool IsComplete { get; private set; }
    public int LoadedObjects { get; private set; }
    public int LoadedTiles { get; private set; }
    public int TotalTiles { get; private set; }
    public int EffectiveObjectCount { get; private set; }
    public int TriangleCount { get; private set; }
    public int LoadedSourceCount { get; private set; }
    public int PendingSourceCount => _sourceQueue.Count;
    private const float CellSize = 40;
    private UrbanCatalog _catalog = null!;
    private CityState _state = null!;
    private Func<float, float, float> _height = null!;
    private readonly Dictionary<(int, int), List<UrbanObject>> _cells = new();
    private readonly Dictionary<(int, int), MeshInstance3D> _meshes = new();
    private readonly Dictionary<string, (string Signature, (int, int) Cell)> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect2> _objectBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<(int, int), Rect2> _cellCoverage = new();
    private readonly Queue<(int, int)> _queue = new();
    private readonly Queue<RealTile> _sourceQueue = new();
    private readonly UrbanSourceMeshes _sources = new();
    private readonly HashSet<(int, int)> _queued = new();
    private StandardMaterial3D _material = null!;
    private StandardMaterial3D _glassMaterial = null!;
    private readonly Dictionary<string, Material> _finishes = new(StringComparer.Ordinal);
    private MeshInstance3D _selection = null!;
    private string? _selected;
    private bool _initial = true;
    private bool _failed;
    private readonly Stopwatch _timer = new();
    private int _terrainRevision = -1;
    private MultiMeshInstance3D _lamps = null!;
    private readonly List<(UrbanObject Signal, UrbanObject? Junction, int Index)> _lampSignals = new();
    private readonly Dictionary<string, string> _lampAspects = new(StringComparer.Ordinal);
    private double _signalTime;
    private double _lastSignalTime = double.NegativeInfinity;
    private bool _skipRendering;
    private bool _sourceVisible;

    /// <summary>
    /// 都市の描画セルと共有材質を初期化します。
    /// 引数: catalog（UrbanCatalog）原典都市、state（CityState）編集差分、height（Func）編集地形の高さ。
    /// 返り値: void。初期構築の進行はイベントで通知します。
    /// </summary>
    public void Initialize(UrbanCatalog catalog, CityState state, Func<float, float, float> height)
    {
        _catalog = catalog; _height = height; catalog.SetSourceMeshes(_sources);
        _material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.88f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _glassMaterial = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.3f, Metallic = 0.2f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _selection = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("#ffd66d"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_selection);
        var lightMaterial = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _lamps = new MultiMeshInstance3D { Name = "編集信号灯", Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
            Mesh = new BoxMesh { Size = new Vector3(0.026f, 0.026f, 0.01f), Material = lightMaterial }
        }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_lamps);
        if (DisplayServer.GetName() == "headless" && OS.GetCmdlineUserArgs().Contains("--urban-interaction-test"))
        {
            _skipRendering = true;
            _state = state; EffectiveObjectCount = catalog.GetEffective(state).Count;
            _timer.Start(); FinishInitial(); return;
        }
        foreach (var tile in catalog.Data.Tiles) _sourceQueue.Enqueue(tile);
        _timer.Start();
        Refresh(state);
        TotalTiles = _queue.Count + (_sourceVisible ? _sourceQueue.Count : 0);
        ProgressChanged?.Invoke(0, TotalTiles);
        if (_queue.Count == 0 && (!_sourceVisible || _sourceQueue.Count == 0)) FinishInitial();
    }

    /// <summary>
    /// 編集差分の前後を比較し、影響するセルだけを描画待ちにします。
    /// 引数: state（CityState）最新の同期済み都市状態。
    /// 返り値: void。地形変更時は全地物を地表へ追従させます。
    /// </summary>
    public void Refresh(CityState state)
    {
        var sourceChanged = _sourceVisible != state.Edits.IncludeSource;
        _sourceVisible = state.Edits.IncludeSource;
        _state = state;
        var effective = _catalog.GetEffective(state);
        EffectiveObjectCount = effective.Count;
        if (_skipRendering) return;
        var current = new Dictionary<string, (string Signature, (int, int) Cell)>(StringComparer.Ordinal);
        var changed = new HashSet<(int, int)>();
        _cells.Clear();
        _cellCoverage.Clear();
        foreach (var item in effective)
        {
            var cell = CellOf(item);
            if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<UrbanObject>();
            list.Add(item);
            var signature = _catalog.IsOriginal(item) ? item.Id : Signature(item);
            current[item.Id] = (signature, cell);
            if (!_previous.TryGetValue(item.Id, out var before)) { changed.Add(cell); _objectBounds[item.Id] = BoundsOf(item); }
            else if (before.Signature != signature || before.Cell != cell) { changed.Add(cell); changed.Add(before.Cell); _objectBounds[item.Id] = BoundsOf(item); }
            var bounds = _objectBounds[item.Id];
            _cellCoverage[cell] = _cellCoverage.TryGetValue(cell, out var coverage) ? coverage.Merge(bounds) : bounds;
        }
        foreach (var before in _previous)
            if (!current.ContainsKey(before.Key)) { changed.Add(before.Value.Cell); _objectBounds.Remove(before.Key); }
        if (_height.Target is UrbanTerrain terrain)
        {
            if (_terrainRevision != terrain.Revision)
            {
                foreach (var cell in _cellCoverage)
                    if (cell.Value.Intersects(terrain.ChangedBounds.Grow(0.1f), true)) changed.Add(cell.Key);
                _terrainRevision = terrain.Revision;
            }
        }
        else
        {
            var terrainSignature = string.Join(';', state.Edits.Terrain.Select(item => System.Text.Json.JsonSerializer.Serialize(item)));
            if (_terrainSignature != terrainSignature) { foreach (var cell in _cells.Keys) changed.Add(cell); _terrainSignature = terrainSignature; }
        }
        _previous.Clear();
        foreach (var entry in current) _previous.Add(entry.Key, entry.Value);
        if (sourceChanged)
        {
            foreach (var cell in _queue) changed.Add(cell);
            _queue.Clear(); _queued.Clear();
            foreach (var cell in changed.Where(cell => !_cells.ContainsKey(cell))) BuildCell(cell);
            changed.RemoveWhere(cell => !_cells.ContainsKey(cell));
            _initial = true; IsComplete = false; LoadedTiles = 0; LoadedObjects = 0; _timer.Restart();
        }
        foreach (var cell in changed) if (_queued.Add(cell)) _queue.Enqueue(cell);
        if (_initial) TotalTiles = _queue.Count + (_sourceVisible ? _sourceQueue.Count : 0) + LoadedTiles;
        UpdateSelection();
        RebuildLamps(effective);
        SetProcess(_queue.Count > 0 || _sourceVisible && _sourceQueue.Count > 0);
        if (_initial && _queue.Count == 0 && (!_sourceVisible || _sourceQueue.Count == 0)) FinishInitial();
    }
    private string _terrainSignature = "";

    /// <summary>
    /// 一フレームに一セルを再構築し、大規模都市の更新を分散します。
    /// 引数: delta（double）前フレームからの秒数。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_failed) { SetProcess(false); return; }
        try
        {
            if (_sourceVisible && _sourceQueue.TryDequeue(out var tile))
            {
                _sources.LoadTile(tile); LoadedTiles++; LoadedSourceCount++;
                if (LoadedSourceCount % 32 == 0) GD.Print($"BAYFRONT_EDITABLE_SOURCES tiles={LoadedSourceCount}/{_catalog.Data.Tiles.Count} features={_sources.FeatureCount} texture_limit={UrbanSourceMeshes.TextureSizeLimit}");
                ProgressChanged?.Invoke(LoadedTiles, TotalTiles);
                return;
            }
            if (!_queue.TryDequeue(out var cell)) { if (_initial) FinishInitial(); SetProcess(false); return; }
            _queued.Remove(cell);
            BuildCell(cell);
            if (_initial)
            {
                LoadedTiles++;
                ProgressChanged?.Invoke(LoadedTiles, TotalTiles);
                if (_queue.Count == 0) FinishInitial();
            }
        }
        catch (Exception error)
        {
            _failed = true; SetProcess(false);
            GD.PushError($"編集都市の描画に失敗しました: {error}");
            Failed?.Invoke(error.Message);
        }
    }

    /// <summary>
    /// 選択地物の輪郭を、元の描画メッシュと独立して強調します。
    /// 引数: id（string?）選択識別子。nullなら解除します。返り値: void。
    /// </summary>
    public void Select(string? id) { _selected = id; UpdateSelection(); }

    /// <summary>
    /// 設定された青・黄・全赤時間に従い、信号灯の表示をCPU上の制御状態へ同期します。
    /// 引数: timeSeconds（double）都市内の経過秒数。返り値: void。
    /// </summary>
    public void AdvanceTime(double timeSeconds)
    {
        _signalTime = timeSeconds;
        if (_lamps is null || _lamps.Multimesh is null || Math.Abs(timeSeconds - _lastSignalTime) < 0.1) return;
        _lastSignalTime = timeSeconds;
        foreach (var entry in _lampSignals)
        {
            var state = UrbanModel.SignalState(entry.Signal, entry.Junction, timeSeconds);
            if (_lampAspects.TryGetValue(entry.Signal.Id, out var previous) && previous == state.Aspect) continue;
            _lampAspects[entry.Signal.Id] = state.Aspect;
            for (var lamp = 0; lamp < 3; lamp++)
            {
                var on = state.Aspect == (lamp == 0 ? "green" : lamp == 1 ? "yellow" : "red");
                var color = lamp switch
                {
                    0 => on ? new Color("#39e7b2") : new Color("#1a302b"),
                    1 => on ? new Color("#ffe16b") : new Color("#3c371d"),
                    _ => on ? new Color("#ff6456") : new Color("#3f2625")
                };
                _lamps.Multimesh.SetInstanceColor(entry.Index + lamp, color);
            }
        }
    }

    // ----------------------------------------

    /// <summary>
    /// セル内の全地物を原典写真と仕上げごとの共通材質へまとめて置換します。
    /// 引数: cell（tuple）セル番号。返り値: void。
    /// </summary>
    private void BuildCell((int, int) cell)
    {
        if (_meshes.Remove(cell, out var old)) { TriangleCount -= old.GetMeta("triangles", 0).AsInt32(); old.QueueFree(); }
        if (!_cells.TryGetValue(cell, out var objects) || objects.Count == 0) return;
        var opaque = new GeometryBuffer(); var glass = new GeometryBuffer();
        var original = new Dictionary<Material, GeometryBuffer>();
        var generated = new Dictionary<Material, GeometryBuffer>();
        foreach (var item in objects)
        {
            var finish = FinishMaterial(item.Material);
            if (!generated.TryGetValue(finish, out var geometry)) generated[finish] = geometry = new GeometryBuffer();
            switch (item.Kind)
            {
                case "building":
                    if (_catalog.UsesSourceGeometry(item) && _sources.TryGet(item.Id, out var parts))
                    {
                        var delta = (item.FollowTerrain ? _height(item.X, item.Z) : 0) - _catalog.Data.GroundHeight(item.X, item.Z);
                        foreach (var part in parts)
                        {
                            if (!original.TryGetValue(part.Source.Material, out var sourceGeometry)) original[part.Source.Material] = sourceGeometry = new GeometryBuffer();
                            sourceGeometry.AppendOriginal(part, delta);
                        }
                    }
                    else
                    {
                        BuildBuilding(item, geometry, glass);
                    }
                    break;
                case "road": BuildRoad(item, geometry); break;
                case "intersection": BuildIntersection(item, geometry); break;
                case "signal": BuildSignal(item, geometry); break;
                case "surface": BuildSurface(item, geometry); break;
                case "prop":
                    BuildBuilding(item, geometry, glass); break;
                default: BuildProp(item, opaque); break;
            }
        }
        var mesh = new ArrayMesh();
        opaque.AppendSurface(mesh, _material); glass.AppendSurface(mesh, _glassMaterial);
        foreach (var surface in original) surface.Value.AppendSurface(mesh, surface.Key);
        foreach (var surface in generated) surface.Value.AppendSurface(mesh, surface.Key);
        if (mesh.GetSurfaceCount() == 0) return;
        var instance = new MeshInstance3D { Name = $"編集セル_{cell.Item1}_{cell.Item2}", Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        var triangles = (opaque.Vertices.Count + glass.Vertices.Count + original.Values.Sum(buffer => buffer.Vertices.Count) + generated.Values.Sum(buffer => buffer.Vertices.Count)) / 3;
        instance.SetMeta("triangles", triangles); TriangleCount += triangles;
        AddChild(instance); _meshes[cell] = instance;
        if (_initial) LoadedObjects += objects.Count;
    }

    /// <summary>
    /// 敷地輪郭を押し出し、階数指定に応じて階境界と窓帯を構築します。
    /// 引数: item（UrbanObject）建物、solid/glass（GeometryBuffer）壁・屋根と窓の出力先。
    /// 返り値: void。未知の階数を実際の階数として描画しません。
    /// </summary>
    private void BuildBuilding(UrbanObject item, GeometryBuffer solid, GeometryBuffer glass)
    {
        var points = UrbanCatalog.GetFootprint(item);
        if (points.Length < 3) return;
        var baseY = UrbanCatalog.BaseHeight(item, _height);
        var height = Math.Max(0.05f, UrbanCatalog.HeightMetersOf(item) / 10);
        var top = baseY + height;
        var color = ParseColor(item.ColorHex, "#b6c1c4");
        var dark = color.Darkened(0.22f);
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            solid.Quad(At(a, baseY), At(b, baseY), At(b, top), At(a, top), i % 2 == 0 ? color : color.Darkened(0.065f));
            if (item.Floors <= 0) continue;
            var edge = b - a;
            var normal = new Vector2(edge.Y, -edge.X).Normalized() * 0.002f;
            var floorHeight = height / item.Floors;
            for (var floor = 0; floor < item.Floors; floor++)
            {
                var y = baseY + floor * floorHeight;
                var lower = y + floorHeight * 0.26f;
                var upper = y + floorHeight * 0.78f;
                var inset = Math.Min(0.12f, edge.Length() * 0.08f);
                if (edge.Length() > inset * 3)
                {
                    var left = a + edge.Normalized() * inset + normal;
                    var right = b - edge.Normalized() * inset + normal;
                    glass.Quad(At(left, lower), At(right, lower), At(right, upper), At(left, upper), new Color("#587883"));
                }
                if (floor > 0) solid.Quad(At(a + normal, y - 0.007f), At(b + normal, y - 0.007f), At(b + normal, y + 0.007f), At(a + normal, y + 0.007f), dark);
            }
        }
        if (item.Roof is "gable" or "gabled") BuildGabledRoof(item, points, top, color, solid);
        else if (item.Roof is "hip" or "pyramid")
        {
            var bounds = UrbanCatalog.BoundsOf(points);
            var center = bounds.GetCenter();
            var ridge = top + Math.Min(1.5f, Math.Min(bounds.Size.X, bounds.Size.Y) * 0.3f);
            for (var i = 0; i < points.Length; i++)
                solid.Triangle(At(points[i], top), At(points[(i + 1) % points.Length], top), At(center, ridge), color.Darkened(0.15f));
        }
        else
        {
            var indices = Geometry2D.TriangulatePolygon(points);
            for (var i = 0; i + 2 < indices.Length; i += 3)
                solid.Triangle(At(points[indices[i]], top), At(points[indices[i + 1]], top), At(points[indices[i + 2]], top), color.Darkened(0.18f));
        }
    }

    /// <summary>
    /// 道路の幅員、歩道、車線構成から地形追従する道路帯を生成します。
    /// 引数: item（UrbanObject）道路、buffer（GeometryBuffer）出力先。返り値: void。
    /// </summary>
    private void BuildRoad(UrbanObject item, GeometryBuffer buffer)
    {
        var usage = item.Usage.Trim().ToLowerInvariant();
        if (usage is "rail" or "railway" or "鉄道") { BuildRail(item, buffer); return; }
        if (usage is "waterway" or "航路") { BuildWaterway(item, buffer); return; }
        var color = ParseColor(item.ColorHex, "#41474b");
        var half = item.WidthMeters / 20;
        var sidewalk = Math.Min(half * 0.6f, item.SidewalkWidthMeters / 10);
        var lanes = Math.Max(1, item.LanesForward + item.LanesBackward);
        for (var i = 1; i < item.Path.Count; i++)
        {
            var start = new Vector2(item.Path[i - 1].X, item.Path[i - 1].Z);
            var end = new Vector2(item.Path[i].X, item.Path[i].Z);
            var distance = start.DistanceTo(end);
            if (distance < 0.001f) continue;
            var steps = Math.Max(1, (int)MathF.Ceiling(distance / 2));
            for (var s = 0; s < steps; s++)
            {
                var a = start.Lerp(end, (float)s / steps); var b = start.Lerp(end, (float)(s + 1) / steps);
                Ribbon(buffer, item, a, b, -half, half, 0.018f, color);
                if (sidewalk > 0)
                {
                    Ribbon(buffer, item, a, b, -half, -half + sidewalk, 0.037f, new Color("#a8aaa4"));
                    Ribbon(buffer, item, a, b, half - sidewalk, half, 0.037f, new Color("#a8aaa4"));
                }
            }
            var usable = Math.Max(0.1f, half - sidewalk);
            var laneWidth = Math.Min(item.LaneWidthMeters / 10, usable * 2 / lanes);
            var carriageHalf = laneWidth * lanes / 2;
            foreach (var side in new[] { -1, 1 })
                Ribbon(buffer, item, start, end, side * carriageHalf - 0.006f, side * carriageHalf + 0.006f, 0.022f, new Color("#c6c8c0"));
            for (var lane = 1; lane < lanes; lane++)
            {
                var offset = -carriageHalf + laneWidth * lane;
                var lineColor = lane == item.LanesBackward ? new Color("#decf82") : new Color("#c6c8c0");
                for (var along = 0f; along < distance; along += 1.4f)
                {
                    var a = start.Lerp(end, along / distance);
                    var b = start.Lerp(end, Math.Min(distance, along + 0.7f) / distance);
                    Ribbon(buffer, item, a, b, offset - 0.006f, offset + 0.006f, 0.022f, lineColor);
                }
            }
        }
    }

    /// <summary>
    /// 機能: 鉄道路盤、枕木、軌間に沿う二本のレールを地形へ追従して描画します。
    /// 引数: item（UrbanObject）編集可能な鉄道線、buffer（GeometryBuffer）出力先。
    /// 返り値: void。CPU 経路と一致する中心線へ一組のレールを配置します。
    /// </summary>
    private void BuildRail(UrbanObject item, GeometryBuffer buffer)
    {
        var half = Math.Max(0.12f, item.WidthMeters / 20);
        var centers = new[] { 0f };
        foreach (var index in Enumerable.Range(1, Math.Max(0, item.Path.Count - 1)))
        {
            var start = new Vector2(item.Path[index - 1].X, item.Path[index - 1].Z);
            var end = new Vector2(item.Path[index].X, item.Path[index].Z);
            var distance = start.DistanceTo(end);
            if (distance < 0.001f) continue;
            var steps = Math.Max(1, (int)MathF.Ceiling(distance / 2));
            for (var step = 0; step < steps; step++)
            {
                var a = start.Lerp(end, (float)step / steps); var b = start.Lerp(end, (float)(step + 1) / steps);
                Ribbon(buffer, item, a, b, -half, half, 0.018f, new Color("#77746d"));
                foreach (var center in centers)
                    foreach (var side in new[] { -1, 1 })
                        Ribbon(buffer, item, a, b, center + side * 0.07175f - 0.004f, center + side * 0.07175f + 0.004f, 0.044f, new Color("#c7d0d0"));
            }
            for (var along = 0f; along < distance; along += 0.065f)
            {
                var a = start.Lerp(end, along / distance); var b = start.Lerp(end, Math.Min(distance, along + 0.019f) / distance);
                foreach (var center in centers) Ribbon(buffer, item, a, b, center - 0.115f, center + 0.115f, 0.033f, new Color("#514d45"));
            }
        }
    }

    /// <summary>
    /// 機能: 航路を舗装道路にせず、海面の点線と左右の航路標識で示します。
    /// 引数: item（UrbanObject）編集可能な航路、buffer（GeometryBuffer）出力先。
    /// 返り値: void。航路を描いても水域や地形そのものは変更しません。
    /// </summary>
    private void BuildWaterway(UrbanObject item, GeometryBuffer buffer)
    {
        var half = Math.Max(0.3f, item.WidthMeters / 20);
        for (var index = 1; index < item.Path.Count; index++)
        {
            var start = new Vector2(item.Path[index - 1].X, item.Path[index - 1].Z);
            var end = new Vector2(item.Path[index].X, item.Path[index].Z);
            var distance = start.DistanceTo(end);
            if (distance < 0.001f) continue;
            var normal = new Vector2(-(end - start).Y, (end - start).X).Normalized();
            for (var along = 0f; along < distance; along += 1.5f)
            {
                var a = start.Lerp(end, along / distance); var b = start.Lerp(end, Math.Min(distance, along + 0.55f) / distance);
                var al = a - normal * 0.025f; var ar = a + normal * 0.025f;
                var bl = b - normal * 0.025f; var br = b + normal * 0.025f;
                buffer.Quad(At(al, WaterHeight(item, al) + 0.035f), At(bl, WaterHeight(item, bl) + 0.035f),
                    At(br, WaterHeight(item, br) + 0.035f), At(ar, WaterHeight(item, ar) + 0.035f), new Color("#64c4d2"));
            }
            for (var along = 0f; along <= distance; along += 8)
                foreach (var side in new[] { -1, 1 })
                {
                    var point = start.Lerp(end, along / distance) + normal * half * side;
                    var y = WaterHeight(item, point);
                    buffer.Box(new Vector3(point.X, y + 0.065f, point.Y), new Vector3(0.07f, 0.13f, 0.07f), side < 0 ? new Color("#c64e45") : new Color("#4aa976"));
                }
        }
    }

    /// <summary>
    /// 機能: 航路標識の高さを現在の海面と明示高度から求めます。
    /// 引数: item（UrbanObject）航路、point（Vector2）都市座標。
    /// 返り値: float 型の水面基準高です。
    /// </summary>
    private float WaterHeight(UrbanObject item, Vector2 point) => (item.FollowTerrain ? _height.Target is UrbanTerrain terrain ? terrain.SeaLevel : _height(point.X, point.Y) : 0) + item.Y;

    /// <summary>
    /// 交差点の独立した接続面と、制御方式の識別輪郭を構築します。
    /// 引数: item（UrbanObject）交差点、buffer（GeometryBuffer）出力先。返り値: void。
    /// </summary>
    private void BuildIntersection(UrbanObject item, GeometryBuffer buffer)
    {
        var y = UrbanCatalog.BaseHeight(item, _height) + 0.023f;
        var center = new Vector2(item.X, item.Z);
        var radiusX = Math.Max(0.1f, item.WidthMeters / 20);
        var radiusZ = Math.Max(0.1f, item.DepthMeters / 20);
        var color = ParseColor(item.ColorHex, "#41474b");
        if (item.Footprint.Count >= 3)
        {
            var points = UrbanCatalog.GetFootprint(item); var indices = Geometry2D.TriangulatePolygon(points);
            for (var index = 0; index + 2 < indices.Length; index += 3)
                buffer.Triangle(At(points[indices[index]], y), At(points[indices[index + 1]], y), At(points[indices[index + 2]], y), color);
        }
        else
        {
            var rotation = Mathf.DegToRad(item.Rotation);
            for (var i = 0; i < 16; i++)
            {
                var a = i * Mathf.Tau / 16; var b = (i + 1) * Mathf.Tau / 16;
                var first = new Vector2(MathF.Cos(a) * radiusX, MathF.Sin(a) * radiusZ).Rotated(rotation) + center;
                var second = new Vector2(MathF.Cos(b) * radiusX, MathF.Sin(b) * radiusZ).Rotated(rotation) + center;
                buffer.Triangle(At(center, y), At(first, y), At(second, y), color);
            }
        }
        if (item.Control is not ("roundabout" or "rotary")) return;
        var inner = Math.Min(radiusX, radiusZ) * 0.38f;
        for (var i = 0; i < 16; i++)
            buffer.Triangle(At(center, y + 0.025f), new Vector3(center.X + MathF.Cos(i * Mathf.Tau / 16) * inner, y + 0.025f, center.Y + MathF.Sin(i * Mathf.Tau / 16) * inner),
                new Vector3(center.X + MathF.Cos((i + 1) * Mathf.Tau / 16) * inner, y + 0.025f, center.Y + MathF.Sin((i + 1) * Mathf.Tau / 16) * inner), new Color("#7c9362"));
    }

    /// <summary>
    /// 個別選択可能な信号柱、横桁、灯器、三色のレンズを生成します。
    /// 引数: item（UrbanObject）信号器、buffer（GeometryBuffer）出力先。返り値: void。
    /// </summary>
    private void BuildSignal(UrbanObject item, GeometryBuffer buffer)
    {
        var y = UrbanCatalog.BaseHeight(item, _height);
        var height = Math.Max(0.2f, item.HeightMeters / 10);
        var rotation = Mathf.DegToRad(item.Rotation);
        var forward = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
        var right = new Vector2(forward.Y, -forward.X);
        var origin = new Vector2(item.X, item.Z);
        var head = origin + right * Math.Max(0.1f, item.WidthMeters / 10);
        var color = ParseColor(item.ColorHex, "#353e43");
        buffer.Box(new Vector3(item.X, y + height / 2, item.Z), new Vector3(0.025f, height, 0.025f), color);
        var beamCenter = (origin + head) / 2;
        buffer.OrientedBox(At(beamCenter, y + height - 0.02f), new Vector3(origin.DistanceTo(head), 0.018f, 0.018f), -rotation, color);
        buffer.OrientedBox(At(head, y + height - 0.055f), new Vector3(0.12f, 0.055f, Math.Max(0.02f, item.DepthMeters / 10)), -rotation, color.Darkened(0.3f));
    }

    /// <summary>
    /// 独立した地表舗装・緑地を輪郭に沿って生成します。
    /// 引数: item（UrbanObject）地表地物、buffer（GeometryBuffer）出力先。返り値: void。
    /// </summary>
    private void BuildSurface(UrbanObject item, GeometryBuffer buffer)
    {
        var points = UrbanCatalog.GetFootprint(item);
        var indices = Geometry2D.TriangulatePolygon(points);
        var color = ParseColor(item.ColorHex, "#7e9667");
        var thickness = Math.Max(0.005f, item.HeightMeters / 10);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = points[indices[i]]; var b = points[indices[i + 1]]; var c = points[indices[i + 2]];
            buffer.Triangle(At(a, GroundAt(item, a) + thickness + 0.012f), At(b, GroundAt(item, b) + thickness + 0.012f), At(c, GroundAt(item, c) + thickness + 0.012f), color);
        }
        for (var index = 0; index < points.Length; index++)
        {
            var a = points[index]; var b = points[(index + 1) % points.Length];
            buffer.Quad(At(a, GroundAt(item, a) + 0.012f), At(b, GroundAt(item, b) + 0.012f),
                At(b, GroundAt(item, b) + thickness + 0.012f), At(a, GroundAt(item, a) + thickness + 0.012f), color.Darkened(0.15f));
        }
    }

    /// <summary>
    /// 独立設備を幅・奥行・高さ・角度から生成します。
    /// 引数: item（UrbanObject）設備、buffer（GeometryBuffer）出力先。返り値: void。
    /// </summary>
    private void BuildProp(UrbanObject item, GeometryBuffer buffer)
    {
        var height = Math.Max(0.02f, item.HeightMeters / 10);
        buffer.OrientedBox(new Vector3(item.X, UrbanCatalog.BaseHeight(item, _height) + height / 2, item.Z),
            new Vector3(Math.Max(0.02f, item.WidthMeters / 10), height, Math.Max(0.02f, item.DepthMeters / 10)),
            -Mathf.DegToRad(item.Rotation), ParseColor(item.ColorHex, "#c1a57f"));
    }

    // ----------------------------------------

    /// <summary>
    /// 道路の一部分を、地表高と横方向オフセットに追従する帯として出力します。
    /// 引数: buffer（GeometryBuffer）出力、item（UrbanObject）道路、a/b（Vector2）端点、left/right/lift（float）幅と上げ量、color（Color）色。
    /// 返り値: void。
    /// </summary>
    private void Ribbon(GeometryBuffer buffer, UrbanObject item, Vector2 a, Vector2 b, float left, float right, float lift, Color color)
    {
        var along = (b - a).Normalized(); var normal = new Vector2(-along.Y, along.X);
        var al = a + normal * left; var ar = a + normal * right;
        var bl = b + normal * left; var br = b + normal * right;
        buffer.Quad(At(al, GroundAt(item, al) + lift), At(bl, GroundAt(item, bl) + lift),
            At(br, GroundAt(item, br) + lift), At(ar, GroundAt(item, ar) + lift), color);
    }

    /// <summary>
    /// 任意地点の地物基準高を求めます。
    /// 引数: item（UrbanObject）地物、point（Vector2）地点。返り値: float 世界単位の高さ。
    /// </summary>
    private float GroundAt(UrbanObject item, Vector2 point) => (item.FollowTerrain ? _height(point.X, point.Y) : 0) + item.Y;

    /// <summary>
    /// 信号レンズを一つのMultiMeshへまとめ、現示変更時だけ色を更新できるようにします。
    /// 引数: effective（IReadOnlyList&lt;UrbanObject&gt;）現在の都市地物。返り値: void。
    /// </summary>
    private void RebuildLamps(IReadOnlyList<UrbanObject> effective)
    {
        var signals = effective.Where(item => item.Kind == "signal").ToArray();
        var junctions = effective.Where(item => item.Kind == "intersection").ToDictionary(item => item.Id, StringComparer.Ordinal);
        _lampSignals.Clear(); _lampAspects.Clear();
        _lamps.Multimesh.InstanceCount = signals.Length * 3;
        for (var index = 0; index < signals.Length; index++)
        {
            var signal = signals[index];
            var rotation = Mathf.DegToRad(signal.Rotation);
            var forward = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
            var right = new Vector2(forward.Y, -forward.X);
            var head = new Vector2(signal.X, signal.Z) + right * Math.Max(0.1f, signal.WidthMeters / 10);
            var y = UrbanCatalog.BaseHeight(signal, _height) + Math.Max(0.2f, signal.HeightMeters / 10) - 0.055f;
            for (var lamp = 0; lamp < 3; lamp++)
            {
                var point = head + right * ((lamp - 1) * 0.034f) + forward * (Math.Max(0.02f, signal.DepthMeters / 10) / 2 + 0.006f);
                _lamps.Multimesh.SetInstanceTransform(index * 3 + lamp, new Transform3D(new Basis(Vector3.Up, -rotation), At(point, y)));
            }
            junctions.TryGetValue(signal.ParentId.Length > 0 ? signal.ParentId : signal.JunctionId, out var parent);
            if (signal.Phases.Count > 0 || parent is { Phases.Count: > 0 }) _lampSignals.Add((signal, parent, index * 3));
            else
            {
                _lamps.Multimesh.SetInstanceColor(index * 3, new Color("#1a302b"));
                _lamps.Multimesh.SetInstanceColor(index * 3 + 1, new Color("#3c371d"));
                _lamps.Multimesh.SetInstanceColor(index * 3 + 2, new Color("#3f2625"));
            }
        }
        _lastSignalTime = double.NegativeInfinity; AdvanceTime(_signalTime);
    }

    /// <summary>
    /// 回転した敷地を棟線で分割し、二面の切妻屋根と妻壁を生成します。
    /// 引数: item（UrbanObject）建物、points（Vector2[]）敷地、top（float）軒高、color（Color）色、buffer（GeometryBuffer）出力先。
    /// 返り値: void。元の敷地外へ矩形屋根を張り出しません。
    /// </summary>
    private static void BuildGabledRoof(UrbanObject item, Vector2[] points, float top, Color color, GeometryBuffer buffer)
    {
        var origin = new Vector2(item.X, item.Z); var angle = Mathf.DegToRad(item.Rotation);
        var local = points.Select(point => (point - origin).Rotated(-angle)).ToArray();
        var bounds = UrbanCatalog.BoundsOf(local);
        var acrossX = bounds.Size.X <= bounds.Size.Y;
        var midpoint = acrossX ? bounds.GetCenter().X : bounds.GetCenter().Y;
        var half = Math.Max(0.001f, (acrossX ? bounds.Size.X : bounds.Size.Y) / 2);
        var rise = Math.Min(1.5f, half * 0.55f);
        var clips = acrossX
            ? new[] { new Rect2(bounds.Position, new Vector2(midpoint - bounds.Position.X, bounds.Size.Y)), new Rect2(new Vector2(midpoint, bounds.Position.Y), new Vector2(bounds.End.X - midpoint, bounds.Size.Y)) }
            : new[] { new Rect2(bounds.Position, new Vector2(bounds.Size.X, midpoint - bounds.Position.Y)), new Rect2(new Vector2(bounds.Position.X, midpoint), new Vector2(bounds.Size.X, bounds.End.Y - midpoint)) };
        foreach (var clip in clips)
        {
            var rectangle = new[] { clip.Position, new Vector2(clip.End.X, clip.Position.Y), clip.End, new Vector2(clip.Position.X, clip.End.Y) };
            foreach (var polygon in Geometry2D.IntersectPolygons(local, rectangle))
            {
                var indices = Geometry2D.TriangulatePolygon(polygon);
                for (var index = 0; index + 2 < indices.Length; index += 3)
                {
                    var a = polygon[indices[index]]; var b = polygon[indices[index + 1]]; var c = polygon[indices[index + 2]];
                    buffer.Triangle(RoofPoint(a, origin, angle, top, midpoint, half, rise, acrossX), RoofPoint(b, origin, angle, top, midpoint, half, rise, acrossX),
                        RoofPoint(c, origin, angle, top, midpoint, half, rise, acrossX), color.Darkened(0.18f));
                }
                for (var index = 0; index < polygon.Length; index++)
                {
                    var a = polygon[index]; var b = polygon[(index + 1) % polygon.Length];
                    if (MathF.Abs((acrossX ? a.X : a.Y) - midpoint) < 0.00001f && MathF.Abs((acrossX ? b.X : b.Y) - midpoint) < 0.00001f) continue;
                    buffer.Quad(At(a.Rotated(angle) + origin, top), At(b.Rotated(angle) + origin, top),
                        RoofPoint(b, origin, angle, top, midpoint, half, rise, acrossX), RoofPoint(a, origin, angle, top, midpoint, half, rise, acrossX), color);
                }
            }
        }
    }

    /// <summary>
    /// 局地座標の頂点を切妻屋根の三次元位置へ変換します。
    /// 引数: point/origin（Vector2）局地点と中心、angle/top/midpoint/half/rise（float）回転と屋根寸法、acrossX（bool）棟の直交軸。
    /// 返り値: Vector3 屋根頂点。
    /// </summary>
    private static Vector3 RoofPoint(Vector2 point, Vector2 origin, float angle, float top, float midpoint, float half, float rise, bool acrossX)
        => At(point.Rotated(angle) + origin, top + Math.Max(0, 1 - MathF.Abs((acrossX ? point.X : point.Y) - midpoint) / half) * rise);

    /// <summary>
    /// 選択地物の外周を太さのある帯で再構築します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void UpdateSelection()
    {
        if (_selection is null) return;
        var item = _selected is null || _state is null ? null : _catalog.Find(_selected, _state);
        _selection.Visible = item is not null;
        if (item is null) return;
        var buffer = new GeometryBuffer();
        var points = item.Kind == "road" ? item.Path.Select(point => new Vector2(point.X, point.Z)).ToArray()
            : item.Kind is "building" or "surface" or "intersection" or "prop" ? UrbanCatalog.GetFootprint(item)
            : new[] { new Vector2(item.X - Math.Max(0.18f, item.WidthMeters / 20), item.Z - Math.Max(0.18f, item.DepthMeters / 20)),
                new Vector2(item.X + Math.Max(0.18f, item.WidthMeters / 20), item.Z - Math.Max(0.18f, item.DepthMeters / 20)),
                new Vector2(item.X + Math.Max(0.18f, item.WidthMeters / 20), item.Z + Math.Max(0.18f, item.DepthMeters / 20)),
                new Vector2(item.X - Math.Max(0.18f, item.WidthMeters / 20), item.Z + Math.Max(0.18f, item.DepthMeters / 20)) };
        var count = item.Kind == "road" ? points.Length - 1 : points.Length;
        for (var i = 0; i < count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            var normal = new Vector2(-(b - a).Y, (b - a).X).Normalized() * 0.025f;
            var y = UrbanCatalog.BaseHeight(item, _height) + (item.Kind is "building" or "prop" or "signal" ? UrbanCatalog.HeightMetersOf(item) / 10 : 0) + 0.04f;
            buffer.Quad(At(a - normal, y), At(b - normal, y), At(b + normal, y), At(a + normal, y), Colors.White);
        }
        var mesh = new ArrayMesh(); buffer.AppendSurface(mesh, _material); _selection.Mesh = mesh;
    }

    /// <summary>
    /// 初期都市の描画完了を一度だけ通知します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void FinishInitial()
    {
        if (!_initial) return;
        _initial = false; IsComplete = true; _timer.Stop();
        GD.Print($"BAYFRONT_EDITABLE_READY elapsed_ms={_timer.ElapsedMilliseconds} objects={LoadedObjects} cells={LoadedTiles}/{TotalTiles} triangles={TriangleCount} source_features={_sources.FeatureCount} source_triangles={_sources.TriangleCount}");
        Completed?.Invoke();
    }

    /// <summary>
    /// 地物中心から描画セルを求めます。
    /// 引数: item（UrbanObject）地物。返り値: tuple セル番号。
    /// </summary>
    private static (int, int) CellOf(UrbanObject item) => ((int)MathF.Floor(item.X / CellSize), (int)MathF.Floor(item.Z / CellSize));

    /// <summary>
    /// 道路線形または地物輪郭の地形更新範囲を取得します。
    /// 引数: item（UrbanObject）地物。返り値: Rect2 影響範囲。
    /// </summary>
    private static Rect2 BoundsOf(UrbanObject item)
    {
        if (item.Kind == "road") return UrbanCatalog.BoundsOf(item.Path.Select(point => new Vector2(point.X, point.Z)).ToArray()).Grow(item.WidthMeters / 20);
        if (item.Kind is "building" or "surface" or "intersection" or "prop") return UrbanCatalog.BoundsOf(UrbanCatalog.GetFootprint(item));
        return new Rect2(new Vector2(item.X, item.Z), Vector2.Zero).Grow(Math.Max(item.WidthMeters, item.DepthMeters) / 20);
    }

    /// <summary>
    /// 差分検出用に地物の全編集属性をシリアライズします。
    /// 引数: item（UrbanObject）地物。返り値: string 決定的な属性文字列。
    /// </summary>
    private static string Signature(UrbanObject item) => System.Text.Json.JsonSerializer.Serialize(item);

    /// <summary>
    /// 平面座標と高さを3次元座標へ変換します。
    /// 引数: point（Vector2）X/Z、y（float）高さ。返り値: Vector3 座標。
    /// </summary>
    private static Vector3 At(Vector2 point, float y) => new(point.X, y, point.Y);

    /// <summary>
    /// 編集色をGodotの色へ変換します。
    /// 引数: text/fallback（string）指定色と代替色。返り値: Color 描画色。
    /// </summary>
    private static Color ParseColor(string text, string fallback) => Color.HtmlIsValid(text) ? new Color(text) : new Color(fallback);

    /// <summary>
    /// 仕上げの名称を、共有する粗さ・金属度・外観色へ変換します。
    /// 引数: finish（string）concrete、glass、brick、steel、wood、asphaltなどの仕上げ。
    /// 返り値: Material 仕上げごとに一つだけ生成する描画材質。
    /// </summary>
    private Material FinishMaterial(string finish)
    {
        var name = finish.Trim().ToLowerInvariant();
        if (_finishes.TryGetValue(name, out var existing)) return existing;
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        switch (name)
        {
            case "glass": case "ガラス": material.Roughness = 0.12f; material.Metallic = 0.25f; material.AlbedoColor = new Color("#9dbbc5"); break;
            case "steel": case "metal": case "鋼材": material.Roughness = 0.28f; material.Metallic = 0.85f; break;
            case "brick": case "れんが": material.Roughness = 1; material.AlbedoColor = new Color("#cfa28b"); break;
            case "wood": case "木材": material.Roughness = 0.87f; material.AlbedoColor = new Color("#d1b78d"); break;
            case "asphalt": case "アスファルト": material.Roughness = 1; material.AlbedoColor = new Color("#92999c"); break;
            default: material.Roughness = 0.94f; break;
        }
        _finishes[name] = material; return material;
    }

    // ----------------------------------------

    /// <summary>
    /// 共有材質ごとの三角形を蓄積するCPU上の描画バッファです。
    /// </summary>
    private sealed class GeometryBuffer
    {
        public readonly List<Vector3> Vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector2> _coordinates = new();

        /// <summary>
        /// 三角形を法線と頂点色付きで追加します。
        /// 引数: a/b/c（Vector3）頂点、color（Color）色。返り値: void。
        /// </summary>
        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            var normal = (c - a).Cross(b - a).Normalized();
            Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
            _normals.Add(normal); _normals.Add(normal); _normals.Add(normal);
            _colors.Add(color); _colors.Add(color); _colors.Add(color);
            _coordinates.Add(Vector2.Zero); _coordinates.Add(Vector2.Zero); _coordinates.Add(Vector2.Zero);
        }

        /// <summary>
        /// 一棟分の原典三角形を、法線・写真座標・頂点色を保って追加します。
        /// 引数: part（FeaturePart）建物と材質の対応、verticalOffset（float）地形編集による高さ差。
        /// 返り値: void。原典の頂点と三角形の順序を維持します。
        /// </summary>
        public void AppendOriginal(UrbanSourceMeshes.FeaturePart part, float verticalOffset)
        {
            foreach (var index in part.Indices)
            {
                var position = part.Source.Vertices[index]; position.Y += verticalOffset;
                Vertices.Add(position); _normals.Add(part.Source.Normals[index]);
                _colors.Add(part.Source.Colors[index]); _coordinates.Add(part.Source.Coordinates[index]);
            }
        }

        /// <summary>
        /// 四角形を二つの三角形へ分解して追加します。
        /// 引数: a/b/c/d（Vector3）周回順の頂点、color（Color）色。返り値: void。
        /// </summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        { Triangle(a, b, c, color); Triangle(a, c, d, color); }

        /// <summary>
        /// 軸に沿った直方体を追加します。
        /// 引数: center/size（Vector3）中心と寸法、color（Color）色。返り値: void。
        /// </summary>
        public void Box(Vector3 center, Vector3 size, Color color) => OrientedBox(center, size, 0, color);

        /// <summary>
        /// 垂直軸の回転を持つ直方体を追加します。
        /// 引数: center/size（Vector3）中心と寸法、rotation（float）ラジアン、color（Color）色。返り値: void。
        /// </summary>
        public void OrientedBox(Vector3 center, Vector3 size, float rotation, Color color)
        {
            var half = size / 2;
            var v = new[] { new Vector3(-half.X,-half.Y,-half.Z),new Vector3(half.X,-half.Y,-half.Z),new Vector3(half.X,-half.Y,half.Z),new Vector3(-half.X,-half.Y,half.Z),
                new Vector3(-half.X,half.Y,-half.Z),new Vector3(half.X,half.Y,-half.Z),new Vector3(half.X,half.Y,half.Z),new Vector3(-half.X,half.Y,half.Z) };
            for (var i = 0; i < v.Length; i++) v[i] = v[i].Rotated(Vector3.Up, rotation) + center;
            Quad(v[0],v[1],v[5],v[4],color); Quad(v[1],v[2],v[6],v[5],color.Darkened(0.08f));
            Quad(v[2],v[3],v[7],v[6],color); Quad(v[3],v[0],v[4],v[7],color.Darkened(0.08f));
            Quad(v[4],v[5],v[6],v[7],color.Lightened(0.03f)); Quad(v[0],v[3],v[2],v[1],color);
        }

        /// <summary>
        /// 蓄積した三角形をArrayMeshの一つのsurfaceとして転送します。
        /// 引数: mesh（ArrayMesh）出力先、material（Material）共有材質。返り値: void。
        /// </summary>
        public void AppendSurface(ArrayMesh mesh, Material material)
        {
            if (Vertices.Count == 0) return;
            var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Vertices.ToArray(); arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _coordinates.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
    }
}
