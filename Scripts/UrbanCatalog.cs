using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 公開都市データを、固定背景を持たない編集可能な初期都市へ変換します。
/// 原典の建物輪郭・道路線形を保持し、不明な階数・信号制御を確定値として扱いません。
/// </summary>
public sealed class UrbanCatalog
{
    public IReadOnlyList<UrbanObject> Objects { get; }
    public IReadOnlyList<UrbanObject> Buildings { get; }
    public IReadOnlyList<UrbanObject> Roads { get; }
    public IReadOnlyList<UrbanObject> Intersections { get; }
    public IReadOnlyList<UrbanObject> Signals { get; }
    public RealWorldData Data => _data;
    public float LastPickDistance { get; private set; } = float.PositiveInfinity;
    private readonly Dictionary<string, UrbanObject> _source;
    private readonly RealWorldData _data;
    private readonly Dictionary<string, Aabb> _sourceBuildingBounds = new(StringComparer.Ordinal);
    private UrbanSourceMeshes? _sourceMeshes;
    private const float JunctionSnap = 0.1f;
    private const float GraphCell = 8;

    /// <summary>
    /// 原典建物・道路と、同じ高さ階層の道路接続点を編集地物へ変換します。
    /// 引数: data（RealWorldData）CPU用の公開都市データ。
    /// 返り値: なし。全地物は安定した識別子を持ち、削除・変更できます。
    /// </summary>
    public UrbanCatalog(RealWorldData data)
    {
        _data = data;
        var buildings = new List<UrbanObject>(data.Buildings.Count);
        foreach (var building in data.Buildings)
        {
            if (building.Footprint.Length < 3) continue;
            var footprint = NormalizeFootprint(building.Footprint, building.Min, building.Max);
            var center = footprint.Aggregate(Vector2.Zero, (sum, point) => sum + point) / footprint.Length;
            var bounds = BoundsOf(footprint);
            var item = new UrbanObject
            {
                Id = building.Id, SourceId = building.Id, Kind = "building", Name = building.Name,
                Usage = string.IsNullOrWhiteSpace(building.Usage) ? "用途不明" : building.Usage,
                X = center.X, Z = center.Y, Y = building.Min.Y - data.GroundHeight(center.X, center.Y),
                WidthMeters = Math.Max(0.5f, bounds.Size.X * RealWorldData.MetersPerUnit),
                DepthMeters = Math.Max(0.5f, bounds.Size.Y * RealWorldData.MetersPerUnit),
                HeightMeters = Math.Max(0.5f, (building.Max.Y - building.Min.Y) * RealWorldData.MetersPerUnit),
                Floors = 0, FloorHeightMeters = 3.2f, FollowTerrain = true,
                Footprint = footprint.Select(point => new UrbanPoint(point.X, point.Y)).ToList(),
                Material = "concrete", Roof = "flat", ColorHex = ColorFor(building.Usage)
            };
            buildings.Add(item);
            _sourceBuildingBounds[item.Id] = new Aabb(building.Min, building.Max - building.Min);
        }
        Buildings = buildings;

        // ----------------------------------------

        var roads = new List<UrbanObject>(data.Roads.Count);
        for (var index = 0; index < data.Roads.Count; index++)
        {
            var road = data.Roads[index];
            if (road.Points.Length < 2) continue;
            var id = $"source-road-{index:D5}";
            var center = road.Points[road.Points.Length / 2];
            var lanes = Math.Max(1, (int)MathF.Floor(road.WidthMeters / 3.4f));
            roads.Add(new UrbanObject
            {
                Id = id, SourceId = id, Kind = "road", Name = $"原典道路 {index + 1}",
                X = center.X, Z = center.Y, Y = road.Level * 0.65f, Level = road.Level,
                WidthMeters = Math.Max(1, road.WidthMeters), DepthMeters = 1, HeightMeters = 0.12f,
                Path = road.Points.Select(point => new UrbanPoint(point.X, point.Y)).ToList(),
                LanesForward = (lanes + 1) / 2, LanesBackward = lanes / 2,
                LaneWidthMeters = Math.Min(3.3f, road.WidthMeters / lanes),
                SidewalkWidthMeters = road.WidthMeters >= 9 ? 1.5f : 0,
                SpeedKph = road.WidthMeters >= 16 ? 50 : 30,
                Material = "asphalt", ColorHex = "#41474b", FollowTerrain = true
            });
        }
        Roads = roads;
        var topology = BuildSourceTopology(roads);
        Intersections = topology.Junctions;
        Signals = topology.Signals;
        Objects = buildings.Concat(roads).Concat(Intersections).Concat(Signals).ToArray();
        _source = Objects.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    // ----------------------------------------

    /// <summary>
    /// 共有命令が指す原典識別子の存在を確認します。
    /// 引数: id（string）原典地物の識別子。返り値: bool 存在する場合はtrue。
    /// </summary>
    public bool SourceExists(string id) => _source.ContainsKey(id);

    /// <summary>
    /// 原典の種別と参照先を共有命令の検証へ提供します。
    /// 引数: id（string）原典地物の識別子。返り値: UrbanObject? 原典定義。
    /// </summary>
    public UrbanObject? SourceObject(string id) => _source.TryGetValue(id, out var source) ? source : null;

    /// <summary>
    /// 未変更の原典インスタンスかを判定し、毎回の大規模JSON比較を避けます。
    /// 引数: item（UrbanObject）地物。返り値: bool 原典そのものならtrue。
    /// </summary>
    public bool IsOriginal(UrbanObject item) => _source.TryGetValue(item.Id, out var source) && ReferenceEquals(item, source);

    /// <summary>
    /// 描画側が読み込んだ詳細形状を共有し、原典の壁・屋根を同じ三角形で選択できるようにします。
    /// 引数: meshes（UrbanSourceMeshes）既存の原典メッシュ管理。
    /// 返り値: void。追加のファイル読み込みや物理ノードは生成しません。
    /// </summary>
    public void SetSourceMeshes(UrbanSourceMeshes meshes) => _sourceMeshes = meshes;

    /// <summary>
    /// 原典と描画形状・材質が一致し、元の詳細メッシュを使用できるか判定します。
    /// 引数: item（UrbanObject）現在の地物。返り値: bool 原典形状と同じ場合はtrue。
    /// </summary>
    public bool UsesSourceGeometry(UrbanObject item)
    {
        if (!_source.TryGetValue(item.Id, out var source) || source.Kind != "building") return false;
        if (ReferenceEquals(source, item)) return true;
        return item.X == source.X && item.Z == source.Z && item.Y == source.Y && item.FollowTerrain == source.FollowTerrain
            && item.Rotation == source.Rotation && item.WidthMeters == source.WidthMeters && item.DepthMeters == source.DepthMeters
            && item.Floors == source.Floors && item.HeightMeters == source.HeightMeters && item.ColorHex == source.ColorHex
            && item.Material == source.Material && item.Roof == source.Roof
            && item.Footprint.Count == source.Footprint.Count && item.Footprint.Zip(source.Footprint).All(pair => pair.First.X == pair.Second.X && pair.First.Z == pair.Second.Z);
    }

    /// <summary>
    /// 初期都市と編集差分を統合し、削除済み地物を除いた都市を列挙します。
    /// 引数: state（CityState）保存・同期対象の都市状態。
    /// 返り値: IReadOnlyList&lt;UrbanObject&gt; 全て編集可能な有効地物。
    /// </summary>
    public IReadOnlyList<UrbanObject> GetEffective(CityState state)
    {
        var edits = state.Edits.Objects.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var result = new List<UrbanObject>((state.Edits.IncludeSource ? Objects.Count : 0) + edits.Count);
        if (state.Edits.IncludeSource)
            foreach (var source in Objects)
                if (!edits.TryGetValue(source.Id, out var replacement)) result.Add(source);
                else if (!replacement.Deleted) result.Add(replacement);
        foreach (var item in edits.Values)
            if (!item.Deleted && (!state.Edits.IncludeSource || !_source.ContainsKey(item.Id))) result.Add(item);
        return result;
    }

    /// <summary>
    /// 現在の都市から識別子に対応する地物を取得します。
    /// 引数: id（string）識別子、state（CityState）都市状態。
    /// 返り値: UrbanObject? 編集済みまたは原典の地物。削除済みならnull。
    /// </summary>
    public UrbanObject? Find(string id, CityState state)
    {
        var edited = state.Edits.Objects.FirstOrDefault(item => item.Id == id);
        if (edited is not null) return edited.Deleted ? null : edited;
        return state.Edits.IncludeSource && _source.TryGetValue(id, out var source) ? source : null;
    }

    /// <summary>
    /// 輪郭、幅、奥行、中心位置、回転から建物・地表の有効輪郭を取得します。
    /// 引数: item（UrbanObject）編集地物。
    /// 返り値: Vector2[] 絶対世界座標の輪郭。1世界単位は10m。
    /// </summary>
    public static Vector2[] GetFootprint(UrbanObject item)
    {
        Vector2[] source;
        if (item.Footprint.Count >= 3) source = item.Footprint.Select(point => new Vector2(point.X, point.Z)).ToArray();
        else source = new[] { new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f) };
        var bounds = BoundsOf(source);
        var center = source.Aggregate(Vector2.Zero, (sum, point) => sum + point) / source.Length;
        var scale = new Vector2(item.WidthMeters / 10 / Math.Max(0.0001f, bounds.Size.X), item.DepthMeters / 10 / Math.Max(0.0001f, bounds.Size.Y));
        var rotation = Mathf.DegToRad(item.Rotation);
        return source.Select(point => ((point - center) * scale).Rotated(rotation) + new Vector2(item.X, item.Z)).ToArray();
    }

    /// <summary>
    /// 階数指定がある場合は階高から、未設定の場合は高さ属性から有効高さを求めます。
    /// 引数: item（UrbanObject）建物または設備。
    /// 返り値: float メートル単位の有効高さ。
    /// </summary>
    public static float HeightMetersOf(UrbanObject item) => item.Kind == "building" && item.Floors > 0
        ? item.Floors * item.FloorHeightMeters : item.HeightMeters;

    /// <summary>
    /// 地形追従設定と高さ差分から、設備の基準高さを計算します。
    /// 引数: item（UrbanObject）地物、height（Func&lt;float,float,float&gt;）編集地形関数。
    /// 返り値: float 世界単位の基準高さ。
    /// </summary>
    public static float BaseHeight(UrbanObject item, Func<float, float, float> height) =>
        (item.FollowTerrain ? height(item.X, item.Z) : 0) + item.Y;

    /// <summary>
    /// 道路線形の端点を既存道路・交差点へ近接接続します。
    /// 引数: road（UrbanObject）新規または編集中道路、state（CityState）都市状態。
    /// 返り値: UrbanObject 接続先座標へ端点を補正した独立地物。
    /// </summary>
    public UrbanObject CreateConnectedRoad(UrbanObject road, CityState state)
    {
        var result = road.DeepClone();
        if (result.Path.Count < 2) return result;
        var effective = GetEffective(state);
        foreach (var index in new[] { 0, result.Path.Count - 1 })
        {
            var point = new Vector2(result.Path[index].X, result.Path[index].Z);
            var nearest = Math.Max(0.35f, result.WidthMeters / 20);
            var target = point;
            foreach (var item in effective)
            {
                if (item.Id == result.Id || item.Level != result.Level) continue;
                if (item.Kind == "intersection")
                {
                    var candidate = new Vector2(item.X, item.Z);
                    if (candidate.DistanceTo(point) < nearest) { nearest = candidate.DistanceTo(point); target = candidate; }
                }
                if (item.Kind != "road") continue;
                for (var i = 1; i < item.Path.Count; i++)
                {
                    var candidate = Geometry2D.GetClosestPointToSegment(point, new Vector2(item.Path[i - 1].X, item.Path[i - 1].Z), new Vector2(item.Path[i].X, item.Path[i].Z));
                    if (candidate.DistanceTo(point) < nearest) { nearest = candidate.DistanceTo(point); target = candidate; }
                }
            }
            result.Path[index] = new UrbanPoint(target.X, target.Y);
        }
        return result;
    }

    /// <summary>
    /// 新設・変更道路と同じ階層の全交点を接続し、道路頂点と交差点の一括変更を作ります。
    /// 引数: road（UrbanObject）作図道路、state（CityState）現在の都市。
    /// 返り値: IReadOnlyList&lt;UrbanObject&gt; 道路本体、交差道路、交差点の変更案。
    /// </summary>
    public IReadOnlyList<UrbanObject> GetRoadConnections(UrbanObject road, CityState state)
    {
        var connectedRoad = CreateConnectedRoad(road, state);
        var effective = GetEffective(state);
        var changes = new Dictionary<string, UrbanObject>(StringComparer.Ordinal) { [road.Id] = connectedRoad };
        var junctions = effective.Where(item => item.Kind == "intersection" && item.Level == road.Level).ToList();
        var cuts = new Dictionary<string, List<Vector2>>(StringComparer.Ordinal) { [road.Id] = new List<Vector2>() };
        foreach (var other in effective)
        {
            if (other.Kind != "road" || other.Id == road.Id || other.Level != road.Level) continue;
            for (var segment = 1; segment < connectedRoad.Path.Count; segment++)
                for (var existing = 1; existing < other.Path.Count; existing++)
                {
                    var a = new Vector2(connectedRoad.Path[segment - 1].X, connectedRoad.Path[segment - 1].Z);
                    var b = new Vector2(connectedRoad.Path[segment].X, connectedRoad.Path[segment].Z);
                    var c = new Vector2(other.Path[existing - 1].X, other.Path[existing - 1].Z);
                    var d = new Vector2(other.Path[existing].X, other.Path[existing].Z);
                    if (!TrySegmentIntersection(a, b, c, d, out var point)) continue;
                    var junction = junctions.FirstOrDefault(item => new Vector2(item.X, item.Z).DistanceSquaredTo(point) <= JunctionSnap * JunctionSnap);
                    if (junction is null)
                    {
                        var id = $"junction-{road.Level}-{(int)MathF.Round(point.X / JunctionSnap)}-{(int)MathF.Round(point.Y / JunctionSnap)}";
                        junction = new UrbanObject
                        {
                            Id = id, Kind = "intersection", Name = "道路接続点", X = point.X, Z = point.Y,
                            Level = road.Level, Y = connectedRoad.Y + 0.012f, FollowTerrain = connectedRoad.FollowTerrain,
                            WidthMeters = Math.Max(road.WidthMeters, other.WidthMeters), DepthMeters = Math.Max(road.WidthMeters, other.WidthMeters),
                            HeightMeters = 0.12f, ColorHex = "#41474b", Material = "asphalt", Control = "priority"
                        };
                        junctions.Add(junction);
                    }
                    if (!changes.TryGetValue(junction.Id, out var modified)) changes[junction.Id] = modified = junction.DeepClone();
                    foreach (var id in new[] { road.Id, other.Id })
                        if (!modified.ConnectedRoadIds.Contains(id, StringComparer.Ordinal)) modified.ConnectedRoadIds.Add(id);
                    cuts[road.Id].Add(point);
                    if (!cuts.TryGetValue(other.Id, out var oldCuts)) cuts[other.Id] = oldCuts = new List<Vector2>();
                    oldCuts.Add(point);
                    if (!changes.ContainsKey(other.Id)) changes[other.Id] = other.DeepClone();
                }
        }
        foreach (var pair in cuts)
        {
            var item = changes[pair.Key];
            item.Path = InsertPathPoints(item.Path, pair.Value);
            item.X = item.Path.Average(point => point.X); item.Z = item.Path.Average(point => point.Z);
        }
        return changes.Values.ToArray();
    }

    /// <summary>
    /// 地物変更と、交差点・道路・信号の接続関係の連動変更をまとめます。
    /// 引数: item（UrbanObject）変更案、state（CityState）現在の都市。
    /// 返り値: IReadOnlyList&lt;UrbanObject&gt; 一つの取り消し単位として適用する変更。
    /// </summary>
    public IReadOnlyList<UrbanObject> GetObjectChanges(UrbanObject item, CityState state)
    {
        if (item.Deleted) return GetRemovalChanges(item.Id, state);
        if (item.Kind == "road") return GetChangedRoadConnections(item, state);
        if (item.Kind != "intersection") return new[] { item.DeepClone() };
        var previous = Find(item.Id, state);
        var changes = new Dictionary<string, UrbanObject>(StringComparer.Ordinal) { [item.Id] = item.DeepClone() };
        var before = previous is null ? new Vector2(item.X, item.Z) : new Vector2(previous.X, previous.Z);
        var after = new Vector2(item.X, item.Z);
        foreach (var id in item.ConnectedRoadIds)
        {
            var road = Find(id, state);
            if (road is null || road.Kind != "road" || road.Path.Count < 2) continue;
            var changed = road.DeepClone();
            var nearest = float.PositiveInfinity; var nearestSegment = 1; var projection = before;
            for (var segment = 1; segment < changed.Path.Count; segment++)
            {
                var a = new Vector2(changed.Path[segment - 1].X, changed.Path[segment - 1].Z);
                var b = new Vector2(changed.Path[segment].X, changed.Path[segment].Z);
                var point = Geometry2D.GetClosestPointToSegment(before, a, b);
                if (point.DistanceSquaredTo(before) < nearest) { nearest = point.DistanceSquaredTo(before); nearestSegment = segment; projection = point; }
            }
            var existing = changed.Path.FindIndex(point => new Vector2(point.X, point.Z).DistanceSquaredTo(before) < JunctionSnap * JunctionSnap);
            if (existing >= 0) changed.Path[existing] = new UrbanPoint(after.X, after.Y);
            else if (nearest < Math.Max(0.04f, road.WidthMeters * road.WidthMeters / 400))
            {
                var a = new Vector2(changed.Path[nearestSegment - 1].X, changed.Path[nearestSegment - 1].Z);
                var b = new Vector2(changed.Path[nearestSegment].X, changed.Path[nearestSegment].Z);
                if (projection.DistanceSquaredTo(a) < 0.0001f) changed.Path[nearestSegment - 1] = new UrbanPoint(after.X, after.Y);
                else if (projection.DistanceSquaredTo(b) < 0.0001f) changed.Path[nearestSegment] = new UrbanPoint(after.X, after.Y);
                else changed.Path.Insert(nearestSegment, new UrbanPoint(after.X, after.Y));
            }
            else
            {
                var first = changed.Path[0]; var last = changed.Path[^1];
                if (new Vector2(first.X, first.Z).DistanceSquaredTo(after) <= new Vector2(last.X, last.Z).DistanceSquaredTo(after))
                    changed.Path.Insert(0, new UrbanPoint(after.X, after.Y));
                else changed.Path.Add(new UrbanPoint(after.X, after.Y));
            }
            changed.X = changed.Path.Average(point => point.X); changed.Z = changed.Path.Average(point => point.Z);
            changes[id] = changed;
        }
        if (previous is not null)
            foreach (var signal in GetEffective(state).Where(value => value.Kind == "signal" && (value.ParentId == item.Id || value.JunctionId == item.Id)))
            {
                var changed = signal.DeepClone(); changed.X += after.X - before.X; changed.Z += after.Y - before.Y;
                changed.Y += item.Y - previous.Y; changed.Level = item.Level;
                if (changed.ApproachRoadId.Length > 0 && !item.ConnectedRoadIds.Contains(changed.ApproachRoadId, StringComparer.Ordinal))
                {
                    changed.ConnectedRoadIds.Remove(changed.ApproachRoadId); changed.ApproachRoadId = "";
                    changed.Control = "unconfigured"; changed.Phases.Clear();
                }
                changes[changed.Id] = changed;
            }
        return changes.Values.ToArray();
    }

    /// <summary>
    /// 撤去地物と、全原典・追加地物に残る接続参照の解除をまとめます。
    /// 引数: id（string）撤去対象、state（CityState）現在の都市。
    /// 返り値: IReadOnlyList&lt;UrbanObject&gt; 撤去差分と参照解除の変更一覧。
    /// </summary>
    public IReadOnlyList<UrbanObject> GetRemovalChanges(string id, CityState state)
    {
        var target = Find(id, state);
        if (target is null) return Array.Empty<UrbanObject>();
        var deleted = target.DeepClone(); deleted.Deleted = true;
        var result = new List<UrbanObject> { deleted };
        foreach (var item in GetEffective(state))
        {
            if (item.Id == id) continue;
            if (!References(item, id)) continue;
            result.Add(WithoutReference(item, id));
        }
        return result;
    }

    /// <summary>
    /// 道路変更に伴い交点を作り直し、外れた交差点と信号から旧接続を解除します。
    /// 引数: road（UrbanObject）変更道路、state（CityState）現在の都市。
    /// 返り値: IReadOnlyList&lt;UrbanObject&gt; 道路・交差点・信号の一括変更。
    /// </summary>
    private IReadOnlyList<UrbanObject> GetChangedRoadConnections(UrbanObject road, CityState state)
    {
        if (road.Path.Count < 2) return new[] { road.DeepClone() };
        var changes = GetRoadConnections(road, state).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var disconnectedJunctions = new HashSet<string>(StringComparer.Ordinal);
        var effective = GetEffective(state);
        foreach (var junction in effective.Where(item => item.Kind == "intersection" && item.ConnectedRoadIds.Contains(road.Id, StringComparer.Ordinal)))
        {
            var point = new Vector2(junction.X, junction.Z);
            var touches = junction.Level == road.Level && Enumerable.Range(1, road.Path.Count - 1).Any(index =>
                Geometry2D.GetClosestPointToSegment(point, new Vector2(road.Path[index - 1].X, road.Path[index - 1].Z),
                    new Vector2(road.Path[index].X, road.Path[index].Z)).DistanceSquaredTo(point) <= JunctionSnap * JunctionSnap);
            if (touches) continue;
            changes[junction.Id] = WithoutReference(changes.TryGetValue(junction.Id, out var changed) ? changed : junction, road.Id);
            disconnectedJunctions.Add(junction.Id);
        }
        foreach (var signal in effective.Where(item => item.Kind == "signal" && item.ApproachRoadId == road.Id))
            if (disconnectedJunctions.Contains(signal.ParentId) || disconnectedJunctions.Contains(signal.JunctionId))
                changes[signal.Id] = WithoutReference(signal, road.Id);
        return changes.Values.ToArray();
    }

    /// <summary>
    /// 接続先、所属、進入道路、現示に対象識別子を参照しているか判定します。
    /// 引数: item（UrbanObject）地物、id（string）対象識別子。返り値: bool 参照がある場合はtrue。
    /// </summary>
    private static bool References(UrbanObject item, string id) => item.ConnectedRoadIds.Contains(id, StringComparer.Ordinal)
        || item.ParentId == id || item.JunctionId == id || item.ApproachRoadId == id
        || item.Phases.Any(phase => phase.GreenRoadIds.Contains(id, StringComparer.Ordinal));

    /// <summary>
    /// 対象識別子の参照を解除した地物を作り、接続を失った信号を未設定へ戻します。
    /// 引数: item（UrbanObject）元の地物、id（string）対象識別子。返り値: UrbanObject 独立した変更案。
    /// </summary>
    private static UrbanObject WithoutReference(UrbanObject item, string id)
    {
        var changed = item.DeepClone();
        changed.ConnectedRoadIds.RemoveAll(value => value == id);
        foreach (var phase in changed.Phases) phase.GreenRoadIds.RemoveAll(value => value == id);
        var disconnectedSignal = changed.ApproachRoadId == id || changed.ParentId == id || changed.JunctionId == id;
        if (changed.ApproachRoadId == id) changed.ApproachRoadId = "";
        if (changed.ParentId == id) changed.ParentId = "";
        if (changed.JunctionId == id) changed.JunctionId = "";
        if (changed.Kind == "signal" && disconnectedSignal) { changed.Control = "unconfigured"; changed.Phases.Clear(); }
        return changed;
    }

    /// <summary>
    /// 道路線形の元の順序を維持し、各線分上へ交差点座標を挿入します。
    /// 引数: path（List&lt;UrbanPoint&gt;）元の線形、cuts（List&lt;Vector2&gt;）追加交点。
    /// 返り値: List&lt;UrbanPoint&gt; 交点で分割された線形。
    /// </summary>
    private static List<UrbanPoint> InsertPathPoints(List<UrbanPoint> path, List<Vector2> cuts)
    {
        var result = new List<UrbanPoint>();
        for (var index = 1; index < path.Count; index++)
        {
            var a = new Vector2(path[index - 1].X, path[index - 1].Z); var b = new Vector2(path[index].X, path[index].Z);
            if (index == 1) result.Add(new UrbanPoint(a.X, a.Y));
            var edge = b - a; var squared = edge.LengthSquared();
            if (squared < 0.000001f) continue;
            foreach (var point in cuts.Where(point => Geometry2D.GetClosestPointToSegment(point, a, b).DistanceSquaredTo(point) < 0.0001f)
                .Select(point => (Point: point, Ratio: (point - a).Dot(edge) / squared)).Where(value => value.Ratio > 0.00001f && value.Ratio < 0.99999f).OrderBy(value => value.Ratio))
            {
                var previous = result[^1];
                if (new Vector2(previous.X, previous.Z).DistanceSquaredTo(point.Point) > 0.00001f)
                    result.Add(new UrbanPoint(point.Point.X, point.Point.Y));
            }
            if (new Vector2(result[^1].X, result[^1].Z).DistanceSquaredTo(b) > 0.00001f) result.Add(new UrbanPoint(b.X, b.Y));
        }
        return result;
    }

    /// <summary>
    /// 視線と建物・道路・交差点・信号・設備の形状を交差判定します。
    /// 引数: origin/direction（Vector3）視線、state（CityState）都市、height（Func）編集地形高、kindFilter（string?）選択種別。nullまたはallは全種別。
    /// 返り値: UrbanObject? 最も近い編集可能な地物。
    /// </summary>
    public UrbanObject? Pick(Vector3 origin, Vector3 direction, CityState state, Func<float, float, float> height, string? kindFilter = null)
    {
        LastPickDistance = float.PositiveInfinity;
        if (!origin.IsFinite() || !direction.IsFinite() || direction.LengthSquared() < 0.0000001f) return null;
        direction = direction.Normalized();
        UrbanObject? selected = null;
        var nearest = float.PositiveInfinity;
        foreach (var item in GetEffective(state))
        {
            if (kindFilter is not (null or "all") && item.Kind != kindFilter) continue;
            var distance = float.PositiveInfinity;
            if (item.Kind == "building" && UsesSourceGeometry(item) && _sourceBuildingBounds.TryGetValue(item.Id, out var sourceBounds))
            {
                var delta = (item.FollowTerrain ? height(item.X, item.Z) : 0) - _data.GroundHeight(item.X, item.Z);
                var localOrigin = origin - Vector3.Up * delta;
                if (!RayBox(localOrigin, direction, sourceBounds.Position, sourceBounds.End, out var broadDistance) || broadDistance > nearest + 0.001f) continue;
                if (_sourceMeshes is not null && _sourceMeshes.TryGet(item.Id, out _))
                    _sourceMeshes.IntersectRay(item.Id, localOrigin, direction, nearest + 0.001f, out distance);
                else distance = BuildingHit(item, origin, direction, height);
            }
            else if (item.Kind is "building" or "prop") distance = BuildingHit(item, origin, direction, height);
            else if (item.Kind == "road") distance = RoadHit(item, origin, direction, height, nearest + 0.001f);
            else if (item.Kind == "signal") distance = SignalHit(item, origin, direction, height);
            else if (item.Kind == "surface")
            {
                var points = GetFootprint(item);
                if (!RayHorizontalBounds(origin, direction, BoundsOf(points))) continue;
                distance = SlopedPrismHit(points, origin, direction, point => GroundAt(item, point, height) + 0.012f,
                    Math.Max(0.005f, item.HeightMeters / 10));
            }
            else if (item.Kind == "intersection")
            {
                var points = IntersectionFootprint(item);
                if (!RayHorizontalBounds(origin, direction, BoundsOf(points))) continue;
                distance = PrismHit(points, BaseHeight(item, height) + 0.018f, 0.005f, origin, direction);
            }
            else
            {
                var points = GetFootprint(item);
                if (!RayHorizontalBounds(origin, direction, BoundsOf(points))) continue;
                distance = PrismHit(points, BaseHeight(item, height), Math.Max(0.02f, HeightMetersOf(item) / 10), origin, direction);
            }
            if (!float.IsFinite(distance)) continue;
            if (distance > nearest + 0.001f || (Math.Abs(distance - nearest) <= 0.001f && selected is not null && PickPriority(item) <= PickPriority(selected))) continue;
            selected = item; nearest = distance;
        }
        if (selected is null || TerrainOccludes(origin, direction, nearest, height)) return null;
        LastPickDistance = nearest;
        return selected;
    }

    /// <summary>
    /// 同一面に重なる道路・交差点では編集対象の小さい地物を優先します。
    /// 引数: item（UrbanObject）地物。返り値: int 同距離の選択優先度。
    /// </summary>
    private static int PickPriority(UrbanObject item) => item.Kind switch { "signal" => 5, "intersection" => 4, "building" or "prop" => 3, "road" => 2, _ => 1 };

    /// <summary>
    /// 外接箱の入口だけでは棄却せず、凹形・回転輪郭の全ての壁と屋根を検査します。
    /// 引数: item（UrbanObject）建物、origin/direction（Vector3）視線、height（Func）地形高。
    /// 返り値: float 最も近い交差距離。交差しない場合は正の無限大。
    /// </summary>
    private static float BuildingHit(UrbanObject item, Vector3 origin, Vector3 direction, Func<float, float, float> height)
    {
        var points = GetFootprint(item); var bounds = BoundsOf(points);
        var baseY = BaseHeight(item, height); var tall = Math.Max(0.05f, HeightMetersOf(item) / 10);
        var roofed = item.Roof is "gable" or "gabled" or "hip" or "pyramid";
        if (!RayBox(origin, direction, new Vector3(bounds.Position.X, baseY, bounds.Position.Y),
            new Vector3(bounds.End.X, baseY + tall + (roofed ? 1.5f : 0), bounds.End.Y), out _)) return float.PositiveInfinity;
        var distance = PrismHit(points, baseY, tall, origin, direction);
        if (!roofed) return distance;
        var top = baseY + tall;
        if (item.Roof is "hip" or "pyramid")
        {
            var center = bounds.GetCenter();
            var peak = new Vector3(center.X, top + Math.Min(1.5f, Math.Min(bounds.Size.X, bounds.Size.Y) * 0.3f), center.Y);
            for (var index = 0; index < points.Length; index++)
                TestTriangle(origin, direction, At(points[index], top), At(points[(index + 1) % points.Length], top), peak, ref distance);
            return distance;
        }
        var centerXZ = new Vector2(item.X, item.Z); var angle = Mathf.DegToRad(item.Rotation);
        var local = points.Select(point => (point - centerXZ).Rotated(-angle)).ToArray();
        var localBounds = BoundsOf(local); var acrossX = localBounds.Size.X <= localBounds.Size.Y;
        var midpoint = acrossX ? localBounds.GetCenter().X : localBounds.GetCenter().Y;
        var half = Math.Max(0.001f, (acrossX ? localBounds.Size.X : localBounds.Size.Y) / 2);
        var clips = acrossX
            ? new[] { new Rect2(localBounds.Position, new Vector2(midpoint - localBounds.Position.X, localBounds.Size.Y)), new Rect2(new Vector2(midpoint, localBounds.Position.Y), new Vector2(localBounds.End.X - midpoint, localBounds.Size.Y)) }
            : new[] { new Rect2(localBounds.Position, new Vector2(localBounds.Size.X, midpoint - localBounds.Position.Y)), new Rect2(new Vector2(localBounds.Position.X, midpoint), new Vector2(localBounds.Size.X, localBounds.End.Y - midpoint)) };
        foreach (var clip in clips)
            foreach (var polygon in Geometry2D.IntersectPolygons(local, new[] { clip.Position, new Vector2(clip.End.X, clip.Position.Y), clip.End, new Vector2(clip.Position.X, clip.End.Y) }))
            {
                /*
                 * 機能: 切妻の局地点を棟からの距離に応じた三次元頂点へ変換します。
                 * 引数: point は Vector2 型の局地座標です。
                 * 返り値: Vector3 型の屋根頂点です。
                 */
                Vector3 RoofPoint(Vector2 point) => At(point.Rotated(angle) + centerXZ, top + Math.Max(0, 1 - Math.Abs((acrossX ? point.X : point.Y) - midpoint) / half) * Math.Min(1.5f, half * 0.55f));
                var indices = Geometry2D.TriangulatePolygon(polygon);
                for (var index = 0; index + 2 < indices.Length; index += 3)
                    TestTriangle(origin, direction, RoofPoint(polygon[indices[index]]), RoofPoint(polygon[indices[index + 1]]), RoofPoint(polygon[indices[index + 2]]), ref distance);
                for (var index = 0; index < polygon.Length; index++)
                {
                    var a = polygon[index]; var b = polygon[(index + 1) % polygon.Length];
                    TestQuad(origin, direction, At(a.Rotated(angle) + centerXZ, top), At(b.Rotated(angle) + centerXZ, top), RoofPoint(b), RoofPoint(a), ref distance);
                }
            }
        return distance;
    }

    /// <summary>
    /// 描画と同じ16辺の楕円または指定輪郭から交差点の選択面を求めます。
    /// 引数: item（UrbanObject）交差点。返り値: Vector2[] 世界座標の輪郭。
    /// </summary>
    private static Vector2[] IntersectionFootprint(UrbanObject item)
    {
        if (item.Footprint.Count >= 3) return GetFootprint(item);
        var radius = new Vector2(Math.Max(0.1f, item.WidthMeters / 20), Math.Max(0.1f, item.DepthMeters / 20));
        var points = new Vector2[16];
        for (var index = 0; index < points.Length; index++)
        {
            var angle = index * Mathf.Tau / points.Length;
            points[index] = new Vector2(MathF.Cos(angle) * radius.X, MathF.Sin(angle) * radius.Y).Rotated(Mathf.DegToRad(item.Rotation)) + new Vector2(item.X, item.Z);
        }
        return points;
    }

    /// <summary>
    /// 道路を描画と同じ間隔の地形追従帯へ分け、路面・歩道・縁を有限厚で判定します。
    /// 引数: item（UrbanObject）道路、origin/direction（Vector3）視線、height（Func）地形高、limit（float）探索距離。
    /// 返り値: float 最も近い交差距離。
    /// </summary>
    private static float RoadHit(UrbanObject item, Vector3 origin, Vector3 direction, Func<float, float, float> height, float limit)
    {
        var distance = float.PositiveInfinity; var half = item.WidthMeters / 20;
        var sidewalk = Math.Min(half * 0.6f, item.SidewalkWidthMeters / 10);
        for (var index = 1; index < item.Path.Count; index++)
        {
            var start = new Vector2(item.Path[index - 1].X, item.Path[index - 1].Z);
            var end = new Vector2(item.Path[index].X, item.Path[index].Z);
            if (!RayHorizontalBounds(origin, direction, new Rect2(start.Min(end), (end - start).Abs()).Grow(half))) continue;
            var along = end - start; var length = along.Length(); if (length < 0.001f) continue;
            var normal = new Vector2(-along.Y, along.X) / length;
            var steps = Math.Max(1, (int)MathF.Ceiling(length / 2));
            for (var step = 0; step < steps; step++)
            {
                var a = start.Lerp(end, (float)step / steps); var b = start.Lerp(end, (float)(step + 1) / steps);
                foreach (var band in sidewalk > 0
                    ? new[] { (-half, -half + sidewalk, 0.037f), (-half + sidewalk, half - sidewalk, 0.018f), (half - sidewalk, half, 0.037f) }
                    : new[] { (-half, half, 0.018f) })
                {
                    var points = new[] { a + normal * band.Item1, b + normal * band.Item1, b + normal * band.Item2, a + normal * band.Item2 };
                    if (!RayHorizontalBounds(origin, direction, BoundsOf(points))) continue;
                    var hit = SlopedPrismHit(points, origin, direction, point => GroundAt(item, point, height) + band.Item3 - 0.005f, 0.005f);
                    if (hit <= limit) distance = Math.Min(distance, hit);
                }
            }
        }
        return distance;
    }

    /// <summary>
    /// 信号柱・片側へ張り出す横桁・灯器をそれぞれの回転箱で判定します。
    /// 引数: item（UrbanObject）信号器、origin/direction（Vector3）視線、height（Func）地形高。
    /// 返り値: float 最も近い交差距離。
    /// </summary>
    private static float SignalHit(UrbanObject item, Vector3 origin, Vector3 direction, Func<float, float, float> height)
    {
        var y = BaseHeight(item, height); var tall = Math.Max(0.2f, item.HeightMeters / 10);
        var angle = Mathf.DegToRad(item.Rotation); var right = new Vector2(MathF.Cos(angle), -MathF.Sin(angle));
        var center = new Vector2(item.X, item.Z); var reach = Math.Max(0.1f, item.WidthMeters / 10);
        var head = center + right * reach; var beam = (center + head) / 2;
        var distance = OrientedBoxHit(origin, direction, new Vector3(item.X, y + tall / 2, item.Z), new Vector3(0.06f, tall, 0.06f), 0);
        distance = Math.Min(distance, OrientedBoxHit(origin, direction, At(beam, y + tall - 0.02f), new Vector3(reach, 0.04f, 0.04f), angle));
        return Math.Min(distance, OrientedBoxHit(origin, direction, At(head, y + tall - 0.055f), new Vector3(0.14f, 0.075f, Math.Max(0.04f, item.DepthMeters / 10)), angle));
    }

    /// <summary>
    /// 視線を回転箱の局所座標へ移し、前方の箱表面までの距離を求めます。
    /// 引数: origin/direction/center/size（Vector3）視線と箱、angle（float）平面回転ラジアン。
    /// 返り値: float 交差距離。
    /// </summary>
    private static float OrientedBoxHit(Vector3 origin, Vector3 direction, Vector3 center, Vector3 size, float angle)
    {
        var local = origin - center;
        var xz = new Vector2(local.X, local.Z).Rotated(-angle); var ray = new Vector2(direction.X, direction.Z).Rotated(-angle);
        return RayBox(new Vector3(xz.X, local.Y, xz.Y), new Vector3(ray.X, direction.Y, ray.Y), -size / 2, size / 2, out var distance) ? distance : float.PositiveInfinity;
    }

    /// <summary>
    /// 多角形を押し出した体積について全ての壁と上下の面を検査します。
    /// 引数: points（Vector2[]）輪郭、baseY/tall（float）底面高と高さ、origin/direction（Vector3）視線。
    /// 返り値: float 交差距離。外接箱の空いた角は選択しません。
    /// </summary>
    private static float PrismHit(Vector2[] points, float baseY, float tall, Vector3 origin, Vector3 direction)
        => SlopedPrismHit(points, origin, direction, _ => baseY, tall);

    /// <summary>
    /// 地形追従する上下面と側面を三角形として判定します。
    /// 引数: points（Vector2[]）輪郭、origin/direction（Vector3）視線、bottom（Func）底面高、thickness（float）厚さ。
    /// 返り値: float 交差距離。
    /// </summary>
    private static float SlopedPrismHit(Vector2[] points, Vector3 origin, Vector3 direction, Func<Vector2, float> bottom, float thickness)
    {
        var distance = float.PositiveInfinity; var vertices = new Vector3[points.Length];
        for (var index = 0; index < points.Length; index++) vertices[index] = At(points[index], bottom(points[index]));
        var rise = Vector3.Up * thickness; var indices = Geometry2D.TriangulatePolygon(points);
        for (var index = 0; index + 2 < indices.Length; index += 3)
        {
            var a = vertices[indices[index]]; var b = vertices[indices[index + 1]]; var c = vertices[indices[index + 2]];
            TestTriangle(origin, direction, a, b, c, ref distance); TestTriangle(origin, direction, a + rise, b + rise, c + rise, ref distance);
        }
        for (var index = 0; index < vertices.Length; index++)
        {
            var a = vertices[index]; var b = vertices[(index + 1) % vertices.Length];
            TestQuad(origin, direction, a, b, b + rise, a + rise, ref distance);
        }
        return distance;
    }

    /// <summary>
    /// 地物の地形追従と高さ差分を地点ごとに適用します。
    /// 引数: item（UrbanObject）地物、point（Vector2）地点、height（Func）地形関数。
    /// 返り値: float 世界単位の基準高。
    /// </summary>
    private static float GroundAt(UrbanObject item, Vector2 point, Func<float, float, float> height) => (item.FollowTerrain ? height(point.X, point.Y) : 0) + item.Y;

    /// <summary>
    /// 二次元座標に高さを与えます。
    /// 引数: point（Vector2）平面座標、y（float）高さ。返り値: Vector3 三次元位置。
    /// </summary>
    private static Vector3 At(Vector2 point, float y) => new(point.X, y, point.Y);

    /// <summary>
    /// 平面上の外接矩形と視線を検査し、遠い地物の高価な三角形検査を省きます。
    /// 引数: origin/direction（Vector3）視線、bounds（Rect2）平面境界。返り値: bool 前方で交差する場合はtrue。
    /// </summary>
    private static bool RayHorizontalBounds(Vector3 origin, Vector3 direction, Rect2 bounds)
        => RayBox(origin, new Vector3(direction.X, 0, direction.Z), new Vector3(bounds.Position.X, float.NegativeInfinity, bounds.Position.Y),
            new Vector3(bounds.End.X, float.PositiveInfinity, bounds.End.Y), out _);

    /// <summary>
    /// 交差した四角面を二枚の三角形として最短距離へ反映します。
    /// 引数: origin/direction/a/b/c/d（Vector3）視線と頂点、nearest（ref float）最短距離。返り値: void。
    /// </summary>
    private static void TestQuad(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, Vector3 d, ref float nearest)
    {
        TestTriangle(origin, direction, a, b, c, ref nearest); TestTriangle(origin, direction, a, c, d, ref nearest);
    }

    /// <summary>
    /// 三角面の交差距離を最短距離へ反映します。
    /// 引数: origin/direction/a/b/c（Vector3）視線と頂点、nearest（ref float）最短距離。返り値: void。
    /// </summary>
    private static void TestTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, ref float nearest)
    {
        if (RayTriangle(origin, direction, a, b, c, out var distance)) nearest = Math.Min(nearest, distance);
    }

    /// <summary>
    /// 表裏を区別せず視線と三角形を交差させ、壁の辺でも選択を取りこぼさないようにします。
    /// 引数: origin/direction/a/b/c（Vector3）視線と三角形、distance（out float）交差距離。
    /// 返り値: bool 前方の三角面で交差する場合はtrue。
    /// </summary>
    internal static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = float.PositiveInfinity; var edge = b - a; var other = c - a;
        var cross = direction.Cross(other); var determinant = edge.Dot(cross);
        if (Math.Abs(determinant) < 0.0000001f) return false;
        var inverse = 1 / determinant; var offset = origin - a;
        var u = offset.Dot(cross) * inverse; if (u < -0.00001f || u > 1.00001f) return false;
        var q = offset.Cross(edge); var v = direction.Dot(q) * inverse;
        if (v < -0.00001f || u + v > 1.00001f) return false;
        var t = other.Dot(q) * inverse; if (t < 0) return false;
        distance = t; return true;
    }

    /// <summary>
    /// 選択物の手前で地形が視線を遮るかを調べ、山の背後や埋没した地物の選択を防ぎます。
    /// 引数: origin/direction（Vector3）視線、distance（float）地物までの距離、height（Func）地形高。
    /// 返り値: bool 手前の地形に遮られる場合はtrue。
    /// </summary>
    private static bool TerrainOccludes(Vector3 origin, Vector3 direction, float distance, Func<float, float, float> height)
    {
        var steps = Math.Clamp((int)MathF.Ceiling(distance / 0.5f), 1, 4096);
        for (var index = 0; index < steps; index++)
        {
            var point = origin + direction * (distance * index / steps);
            if (point.Y < height(point.X, point.Z) - 0.015f) return true;
        }
        return false;
    }

    // ----------------------------------------

    private sealed record RoadEdge(int Road, int Segment, Vector2 A, Vector2 B, int Level);
    private sealed class JunctionNode
    {
        public Vector2 Position;
        public int Level;
        public HashSet<int> Roads = new();
        public HashSet<int> Directions = new();
    }

    /// <summary>
    /// 同じ階層の道路線分を空間索引で交差させ、接続点と未設定信号器を生成します。
    /// 引数: roads（List&lt;UrbanObject&gt;）編集可能な原典道路。
    /// 返り値: tuple 交差点と信号器の一覧。信号の実在・制御設定を表すものではありません。
    /// </summary>
    private static (List<UrbanObject> Junctions, List<UrbanObject> Signals) BuildSourceTopology(List<UrbanObject> roads)
    {
        var edges = new List<RoadEdge>();
        var cells = new Dictionary<(int, int, int), List<int>>();
        var nodes = new Dictionary<(int, int, int), JunctionNode>();
        for (var r = 0; r < roads.Count; r++)
            for (var s = 1; s < roads[r].Path.Count; s++)
            {
                var a = new Vector2(roads[r].Path[s - 1].X, roads[r].Path[s - 1].Z);
                var b = new Vector2(roads[r].Path[s].X, roads[r].Path[s].Z);
                if (a.DistanceSquaredTo(b) < 0.00001f) continue;
                var edge = new RoadEdge(r, s, a, b, roads[r].Level);
                var number = edges.Count;
                var seen = new HashSet<int>();
                var min = a.Min(b); var max = a.Max(b);
                for (var z = (int)MathF.Floor(min.Y / GraphCell); z <= MathF.Floor(max.Y / GraphCell); z++)
                    for (var x = (int)MathF.Floor(min.X / GraphCell); x <= MathF.Floor(max.X / GraphCell); x++)
                    {
                        var cell = (x, z, edge.Level);
                        if (!cells.TryGetValue(cell, out var existing)) cells[cell] = existing = new List<int>();
                        foreach (var index in existing)
                        {
                            if (!seen.Add(index)) continue;
                            var other = edges[index];
                            if (other.Road == r) continue;
                            if (TrySegmentIntersection(a, b, other.A, other.B, out var hit))
                            {
                                AddNode(nodes, hit, edge, hit.DistanceSquaredTo(a) > 0.001f, hit.DistanceSquaredTo(b) > 0.001f);
                                AddNode(nodes, hit, other, hit.DistanceSquaredTo(other.A) > 0.001f, hit.DistanceSquaredTo(other.B) > 0.001f);
                            }
                        }
                        existing.Add(number);
                    }
                edges.Add(edge);
                AddNode(nodes, a, edge, false, true);
                AddNode(nodes, b, edge, true, false);
            }

        var junctions = new List<UrbanObject>();
        var signals = new List<UrbanObject>();
        foreach (var pair in nodes.OrderBy(pair => pair.Key.Item3).ThenBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2))
        {
            var node = pair.Value;
            if (node.Roads.Count < 2) continue;
            var id = $"source-junction-{pair.Key.Item3}-{pair.Key.Item1}-{pair.Key.Item2}";
            var connected = node.Roads.Select(index => roads[index]).ToArray();
            var width = connected.Max(item => item.WidthMeters);
            var junction = new UrbanObject
            {
                Id = id, SourceId = id, Kind = "intersection", Name = "原典道路から推定した接続点",
                X = node.Position.X, Z = node.Position.Y, Y = node.Level * 0.65f + 0.012f,
                Level = node.Level, WidthMeters = width, DepthMeters = width,
                HeightMeters = 0.12f, Control = "unconfigured", ColorHex = "#41474b", Material = "asphalt",
                ConnectedRoadIds = connected.Select(item => item.Id).ToList(), FollowTerrain = true
            };
            junctions.Add(junction);
            if (node.Level != 0 || node.Directions.Count < 3) continue;
            foreach (var road in connected)
            {
                var points = road.Path.Select(point => new Vector2(point.X, point.Z)).ToArray();
                var closest = Enumerable.Range(1, points.Length - 1)
                    .Select(index => (A: points[index - 1], B: points[index], Distance: Geometry2D.GetClosestPointToSegment(node.Position, points[index - 1], points[index]).DistanceSquaredTo(node.Position)))
                    .OrderBy(value => value.Distance).First();
                var along = closest.A.DistanceSquaredTo(node.Position) > closest.B.DistanceSquaredTo(node.Position) ? closest.A : closest.B;
                var direction = (along - node.Position).Normalized();
                var right = new Vector2(-direction.Y, direction.X);
                var point = node.Position + direction * (width / 20 + 0.35f) + right * (road.WidthMeters / 20 + 0.15f);
                var signalId = $"source-signal-{id}-{road.Id}";
                signals.Add(new UrbanObject
                {
                    Id = signalId, SourceId = signalId, Kind = "signal", Name = "未設定信号器（道路形状から推定）",
                    X = point.X, Z = point.Y, Level = node.Level, ParentId = id, JunctionId = id, ApproachRoadId = road.Id,
                    WidthMeters = 1.1f, DepthMeters = 0.6f, HeightMeters = 5.5f,
                    Rotation = Mathf.RadToDeg(Mathf.Atan2(direction.X, direction.Y)),
                    Control = "unconfigured", Material = "metal", ColorHex = "#353e43", FollowTerrain = true,
                    ConnectedRoadIds = new List<string> { road.Id }
                });
            }
        }
        return (junctions, signals);
    }

    /// <summary>
    /// 道路方向を含む接続点を1m格子へ登録します。
    /// 引数: nodes（Dictionary）索引、point（Vector2）接続点、edge（RoadEdge）道路、towardA/towardB（bool）接続方向。
    /// 返り値: void。
    /// </summary>
    private static void AddNode(Dictionary<(int, int, int), JunctionNode> nodes, Vector2 point, RoadEdge edge, bool towardA, bool towardB)
    {
        var key = ((int)MathF.Round(point.X / JunctionSnap), (int)MathF.Round(point.Y / JunctionSnap), edge.Level);
        if (!nodes.TryGetValue(key, out var node)) nodes[key] = node = new JunctionNode { Position = point, Level = edge.Level };
        node.Roads.Add(edge.Road);
        if (towardA) node.Directions.Add((int)MathF.Round(Mathf.Atan2(edge.A.Y - point.Y, edge.A.X - point.X) * 18 / Mathf.Pi));
        if (towardB) node.Directions.Add((int)MathF.Round(Mathf.Atan2(edge.B.Y - point.Y, edge.B.X - point.X) * 18 / Mathf.Pi));
    }

    /// <summary>
    /// 二線分の交差または共有端点を計算します。
    /// 引数: a/b/c/d（Vector2）端点、point（out Vector2）交点。
    /// 返り値: bool 線分上で交差する場合はtrue。
    /// </summary>
    public static bool TrySegmentIntersection(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 point)
    {
        var denominator = (b - a).Cross(d - c);
        if (MathF.Abs(denominator) > 0.000001f)
        {
            var t = (c - a).Cross(d - c) / denominator;
            var u = (c - a).Cross(b - a) / denominator;
            if (t >= -0.0001f && t <= 1.0001f && u >= -0.0001f && u <= 1.0001f)
            { point = a + (b - a) * Math.Clamp(t, 0, 1); return true; }
        }
        foreach (var first in new[] { a, b })
            foreach (var second in new[] { c, d })
                if (first.DistanceSquaredTo(second) < JunctionSnap * JunctionSnap)
                { point = (first + second) / 2; return true; }
        point = Vector2.Zero; return false;
    }

    /// <summary>
    /// 座標列の外接矩形を求めます。
    /// 引数: points（Vector2[]）座標。返り値: Rect2 外接矩形。
    /// </summary>
    public static Rect2 BoundsOf(Vector2[] points)
    {
        if (points.Length == 0) return new Rect2();
        var min = points[0]; var max = min;
        foreach (var point in points) { min = min.Min(point); max = max.Max(point); }
        return new Rect2(min, max - min);
    }

    /// <summary>
    /// 原典の量子化による重複点と長さゼロの辺を取り除きます。
    /// 引数: source（Vector2[]）原典輪郭、minimum/maximum（Vector3）原典境界。
    /// 返り値: Vector2[] 編集検証に使用できる輪郭。退化した輪郭のみ原典境界矩形へ補正します。
    /// </summary>
    private static Vector2[] NormalizeFootprint(Vector2[] source, Vector3 minimum, Vector3 maximum)
    {
        var result = new List<Vector2>();
        foreach (var point in source)
            if (result.Count == 0 || result[^1].DistanceSquaredTo(point) > 0.000000001f) result.Add(point);
        if (result.Count > 1 && result[0].DistanceSquaredTo(result[^1]) < 0.000000001f) result.RemoveAt(result.Count - 1);
        var area = 0f;
        for (var index = 0; index < result.Count; index++) area += result[index].Cross(result[(index + 1) % result.Count]);
        if (result.Count >= 3 && Math.Abs(area) > 0.000001f) return result.ToArray();
        var minX = minimum.X; var minZ = minimum.Z;
        var maxX = Math.Max(minX + 0.02f, maximum.X); var maxZ = Math.Max(minZ + 0.02f, maximum.Z);
        return new[] { new Vector2(minX, minZ), new Vector2(maxX, minZ), new Vector2(maxX, maxZ), new Vector2(minX, maxZ) };
    }

    /// <summary>
    /// 用途から、編集画面で識別しやすい初期外壁色を設定します。
    /// 引数: usage（string）原典用途。返り値: string HTML形式の色。
    /// </summary>
    private static string ColorFor(string usage) => usage.Contains("住宅", StringComparison.Ordinal) ? "#b9b2a0"
        : usage.Contains("倉庫", StringComparison.Ordinal) || usage.Contains("工場", StringComparison.Ordinal) ? "#97a8ae"
        : usage.Contains("商業", StringComparison.Ordinal) || usage.Contains("業務", StringComparison.Ordinal) ? "#9db4c2"
        : usage.Contains("公共", StringComparison.Ordinal) || usage.Contains("学校", StringComparison.Ordinal) ? "#c3bfae" : "#b5c0c4";

    /// <summary>
    /// 視線と境界箱の交差距離を計算します。
    /// 引数: origin/direction/min/max（Vector3）視線と境界、distance（out float）交差距離。
    /// 返り値: bool 前方で交差する場合はtrue。
    /// </summary>
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f; var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(direction[axis]) < 0.000001f)
            { if (origin[axis] < min[axis] || origin[axis] > max[axis]) { distance = 0; return false; } continue; }
            var first = (min[axis] - origin[axis]) / direction[axis];
            var second = (max[axis] - origin[axis]) / direction[axis];
            near = Math.Max(near, Math.Min(first, second)); far = Math.Min(far, Math.Max(first, second));
            if (near > far) { distance = 0; return false; }
        }
        distance = near; return far >= 0;
    }
}
