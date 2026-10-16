using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Bayfront;

public sealed record RealBuilding(string Id, string Name, string Usage, Vector2[] Footprint, Vector3 Min, Vector3 Max,
    double OriginalHeight, int Lod, string TileId);
public sealed record RealRoad(Vector2[] Points, float WidthMeters, int Level);
public sealed record RealLand(Vector2[] Points, Vector2[][] Holes, Rect2 Bounds);
public sealed record RealRaster(string Path, string TerrainPath, Rect2 Bounds);
public sealed record RealTile(string Id, string Path, Aabb Bounds, int BuildingCount, int TriangleCount);

/// <summary>
/// 公開地理データの座標、地形高、固定建物と道路の空間索引を管理します。
/// 画像や描画メッシュは読み込まないため、専用サーバーでも同じ検証に使用できます。
/// </summary>
public sealed class RealWorldData
{
    public IReadOnlyList<RealBuilding> Buildings { get; private set; } = Array.Empty<RealBuilding>();
    public IReadOnlyList<RealRoad> Roads { get; private set; } = Array.Empty<RealRoad>();
    public IReadOnlyList<RealLand> Lands { get; private set; } = Array.Empty<RealLand>();
    public IReadOnlyList<RealRaster> Rasters { get; private set; } = Array.Empty<RealRaster>();
    public IReadOnlyList<RealTile> Tiles { get; private set; } = Array.Empty<RealTile>();
    public Rect2 Bounds { get; private set; }
    public int Lod2Count => Buildings.Count(item => item.Lod >= 2);
    public int TriangleCount { get; private set; }
    public const double OriginLongitude = 139.795;
    public const double OriginLatitude = 35.640;
    public const float MetersPerUnit = 10;
    private const float CellSize = 20;
    private readonly Dictionary<(int, int), List<int>> _buildingIndex = new();
    private readonly Dictionary<(int, int), List<int>> _roadIndex = new();
    private float[] _heights = Array.Empty<float>();
    private int _heightWidth, _heightHeight;
    private float _heightMinX, _heightMinZ, _heightStepX, _heightStepZ;

    /// <summary>
    /// 実地図の計算用データ一式が存在するか確認します。
    /// 引数: なし。返り値: bool データが揃っている場合はtrue。
    /// </summary>
    public static bool IsAvailable() => Godot.FileAccess.FileExists("res://Data/RealWorld/ground.json")
        && Godot.FileAccess.FileExists("res://Data/RealWorld/buildings.json")
        && Godot.FileAccess.FileExists("res://Data/RealWorld/plateau-manifest.json")
        && Godot.FileAccess.FileExists("res://Data/RealWorld/elevation.json");

    /// <summary>
    /// 確定した公開データと計算用の空間索引を読み込みます。
    /// 引数: なし。返り値: RealWorldData 実地図データ。
    /// </summary>
    public static RealWorldData Load()
    {
        var result = new RealWorldData();
        using (var document = ReadJson("res://Data/RealWorld/ground.json"))
        {
            var root = document.RootElement;
            result.Bounds = ReadRect(root.GetProperty("boundsWorld"));
            result.Lands = root.GetProperty("lands").EnumerateArray().Select(value =>
            {
                var points = ReadPoints(value.GetProperty("points"));
                var holes = value.TryGetProperty("holes", out var array) ? array.EnumerateArray().Select(ReadPoints).ToArray() : Array.Empty<Vector2[]>();
                return new RealLand(points, holes, PolygonBounds(points));
            }).ToArray();
            result.Roads = root.GetProperty("roads").EnumerateArray().Select(value => new RealRoad(
                ReadPoints(value.GetProperty("points")), value.GetProperty("widthMeters").GetSingle(),
                value.TryGetProperty("level", out var level) ? level.GetInt32() : 0)).ToArray();
            result.Rasters = root.GetProperty("rasters").EnumerateArray().Select(value => new RealRaster(
                value.GetProperty("path").GetString()!, value.GetProperty("terrainPath").GetString()!, ReadRect(value.GetProperty("worldrect")))).ToArray();
        }
        using (var document = ReadJson("res://Data/RealWorld/buildings.json"))
        {
            result.Buildings = document.RootElement.GetProperty("buildings").EnumerateArray().Select(value => new RealBuilding(
                value.GetProperty("id").GetString()!, value.GetProperty("name").GetString() ?? "",
                value.GetProperty("usage").GetString() ?? "不明", ReadPoints(value.GetProperty("footprint")),
                ReadVector3(value.GetProperty("bounds").GetProperty("min")), ReadVector3(value.GetProperty("bounds").GetProperty("max")),
                value.TryGetProperty("measuredHeightMeters", out var height) && height.ValueKind == JsonValueKind.Number ? height.GetDouble() : 0,
                value.GetProperty("lod").GetInt32(), value.GetProperty("tileId").GetString()!)).ToArray();
        }
        using (var document = ReadJson("res://Data/RealWorld/plateau-manifest.json"))
        {
            result.Tiles = document.RootElement.GetProperty("tiles").EnumerateArray().Select(value =>
            {
                var minimum = ReadVector3(value.GetProperty("bounds").GetProperty("min"));
                var maximum = ReadVector3(value.GetProperty("bounds").GetProperty("max"));
                return new RealTile(value.GetProperty("id").GetString()!, value.GetProperty("path").GetString()!,
                    new Aabb(minimum, maximum - minimum), value.GetProperty("buildingCount").GetInt32(), value.GetProperty("triangleCount").GetInt32());
            }).ToArray();
            result.TriangleCount = result.Tiles.Sum(tile => tile.TriangleCount);
        }
        using (var document = ReadJson("res://Data/RealWorld/elevation.json"))
        {
            var root = document.RootElement;
            result._heightWidth = root.GetProperty("width").GetInt32();
            result._heightHeight = root.GetProperty("height").GetInt32();
            result._heightMinX = root.GetProperty("minX").GetSingle();
            result._heightMinZ = root.GetProperty("minZ").GetSingle();
            result._heightStepX = root.GetProperty("stepX").GetSingle();
            result._heightStepZ = root.GetProperty("stepZ").GetSingle();
            result._heights = root.GetProperty("heights").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (result._heights.Length != result._heightWidth * result._heightHeight || result._heightStepX <= 0 || result._heightStepZ <= 0)
                throw new InvalidOperationException("実地形の高さデータが不正です。");
        }
        result.BuildSpatialIndices();
        return result;
    }

    // ----------------------------------------

    /// <summary>
    /// 水域を除いた実陸地の内側かを判定します。
    /// 引数: x/z（float）ゲーム内座標。返り値: bool 陸地の内側の場合はtrue。
    /// </summary>
    public bool IsLand(float x, float z)
    {
        var point = new Vector2(x, z);
        if (!Bounds.HasPoint(point)) return false;
        foreach (var land in Lands)
            if (land.Bounds.Grow(0.001f).HasPoint(point) && Geometry2D.IsPointInPolygon(point, land.Points)
                && !land.Holes.Any(hole => Geometry2D.IsPointInPolygon(point, hole))) return true;
        return false;
    }

    /// <summary>
    /// 固定建築と道路を空間索引で絞り込み、編集地物の重複を検証します。
    /// 引数: item（CityObject）配置予定の地物。返り値: bool 配置可能の場合はtrue。
    /// </summary>
    public bool CanPlace(CityObject item)
    {
        if (!float.IsFinite(item.X) || !float.IsFinite(item.Z) || !float.IsFinite(item.EndX) || !float.IsFinite(item.EndZ)) return false;
        var start = new Vector2(item.X, item.Z);
        var end = item.Kind == "road" ? new Vector2(item.EndX, item.EndZ) : start;
        var radius = CityModel.GetRadius(item.Kind);
        if (radius <= 0) return false;
        if (item.Kind != "road" && !ContainsLandFootprint(start, radius)) return false;
        var search = new Rect2(start.Min(end), (end - start).Abs()).Grow(radius + 4);
        foreach (var index in Query(_buildingIndex, search))
        {
            var building = Buildings[index];
            if (!new Rect2(new Vector2(building.Min.X, building.Min.Z), new Vector2(building.Max.X - building.Min.X, building.Max.Z - building.Min.Z)).Grow(radius).Intersects(search, true)) continue;
            if (TouchesPolygon(start, end, building.Footprint, radius)) return false;
        }
        if (item.Kind != "road")
            foreach (var index in Query(_roadIndex, search.Grow(4)))
            {
                var road = Roads[index];
                if (road.Level != 0) continue;
                var clearance = radius + road.WidthMeters / MetersPerUnit / 2;
                for (var i = 1; i < road.Points.Length; i++)
                    if (PointSegmentDistance(start, road.Points[i - 1], road.Points[i]) < clearance) return false;
            }
        return true;
    }

    /// <summary>
    /// 建物の敷地全体が実海岸線と水域の外側に収まるか検証します。
    /// 引数: center（Vector2）中心、radius（float）敷地半径。返り値: bool 敷地全体が陸地ならtrue。
    /// </summary>
    private bool ContainsLandFootprint(Vector2 center, float radius)
    {
        foreach (var land in Lands)
        {
            if (!land.Bounds.HasPoint(center) || !Geometry2D.IsPointInPolygon(center, land.Points)) continue;
            if (land.Holes.Any(hole => Geometry2D.IsPointInPolygon(center, hole))) continue;
            for (var i = 0; i < land.Points.Length; i++)
                if (PointSegmentDistance(center, land.Points[i], land.Points[(i + 1) % land.Points.Length]) < radius) return false;
            foreach (var hole in land.Holes)
                for (var i = 0; i < hole.Length; i++)
                    if (PointSegmentDistance(center, hole[i], hole[(i + 1) % hole.Length]) < radius) return false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 元の標高格子を双線形補間し、ENU Upの地表高を取得します。
    /// 引数: x/z（float）ゲーム内座標。返り値: float ゲーム単位の地表高。
    /// </summary>
    public float GroundHeight(float x, float z)
    {
        var column = Mathf.Clamp((x - _heightMinX) / _heightStepX, 0, _heightWidth - 1.001f);
        var row = Mathf.Clamp((z - _heightMinZ) / _heightStepZ, 0, _heightHeight - 1.001f);
        var ix = (int)column; var iz = (int)row;
        var north = Mathf.Lerp(_heights[iz * _heightWidth + ix], _heights[iz * _heightWidth + ix + 1], column - ix);
        var south = Mathf.Lerp(_heights[(iz + 1) * _heightWidth + ix], _heights[(iz + 1) * _heightWidth + ix + 1], column - ix);
        return Mathf.Lerp(north, south, row - iz);
    }

    /// <summary>
    /// 視線に最も近い固定建物を境界箱と投影輪郭から選択します。
    /// 引数: origin（Vector3）視線の原点、direction（Vector3）方向。返り値: RealBuilding? 選択建物。
    /// </summary>
    public RealBuilding? PickBuilding(Vector3 origin, Vector3 direction)
    {
        RealBuilding? selected = null;
        var nearest = float.PositiveInfinity;
        foreach (var building in Buildings)
        {
            if (!RayBox(origin, direction, building.Min, building.Max, out var distance) || distance >= nearest) continue;
            var hit = origin + direction * (distance + 0.001f);
            var point = new Vector2(hit.X, hit.Z);
            if (!Geometry2D.IsPointInPolygon(point, building.Footprint) && !TouchesPolygon(point, point, building.Footprint, 0.05f)) continue;
            selected = building; nearest = distance;
        }
        return selected;
    }

    /// <summary>
    /// WGS84の緯度経度と楕円体高を、倍精度ECEFから局地ENUへ変換します。
    /// 引数: longitude/latitude/height（double）度とメートル。返り値: Vector3 X東、Y上、Z南のゲーム座標。
    /// </summary>
    public static Vector3 Project(double longitude, double latitude, double height = 0)
    {
        const double a = 6378137.0, e2 = 6.6943799901413165e-3;
        var lon = longitude * Math.PI / 180; var lat = latitude * Math.PI / 180;
        var lon0 = OriginLongitude * Math.PI / 180; var lat0 = OriginLatitude * Math.PI / 180;
        var n = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        var n0 = a / Math.Sqrt(1 - e2 * Math.Sin(lat0) * Math.Sin(lat0));
        var dx = (n + height) * Math.Cos(lat) * Math.Cos(lon) - n0 * Math.Cos(lat0) * Math.Cos(lon0);
        var dy = (n + height) * Math.Cos(lat) * Math.Sin(lon) - n0 * Math.Cos(lat0) * Math.Sin(lon0);
        var dz = (n * (1 - e2) + height) * Math.Sin(lat) - n0 * (1 - e2) * Math.Sin(lat0);
        var east = -Math.Sin(lon0) * dx + Math.Cos(lon0) * dy;
        var north = -Math.Sin(lat0) * Math.Cos(lon0) * dx - Math.Sin(lat0) * Math.Sin(lon0) * dy + Math.Cos(lat0) * dz;
        var up = Math.Cos(lat0) * Math.Cos(lon0) * dx + Math.Cos(lat0) * Math.Sin(lon0) * dy + Math.Sin(lat0) * dz;
        return new Vector3((float)(east / MetersPerUnit), (float)(up / MetersPerUnit), (float)(-north / MetersPerUnit));
    }

    // ----------------------------------------

    /// <summary>
    /// 建物と道路を200mのセルに登録します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void BuildSpatialIndices()
    {
        for (var i = 0; i < Buildings.Count; i++)
        {
            var item = Buildings[i];
            Insert(_buildingIndex, new Rect2(new Vector2(item.Min.X, item.Min.Z), new Vector2(item.Max.X - item.Min.X, item.Max.Z - item.Min.Z)), i);
        }
        for (var i = 0; i < Roads.Count; i++) Insert(_roadIndex, PolygonBounds(Roads[i].Points).Grow(Roads[i].WidthMeters / MetersPerUnit / 2), i);
    }

    /// <summary>
    /// 指定範囲と重なるセルへ地物の番号を登録します。
    /// 引数: index（Dictionary）索引、bounds（Rect2）範囲、value（int）地物番号。返り値: void。
    /// </summary>
    private static void Insert(Dictionary<(int, int), List<int>> index, Rect2 bounds, int value)
    {
        for (var z = (int)Math.Floor(bounds.Position.Y / CellSize); z <= Math.Floor(bounds.End.Y / CellSize); z++)
            for (var x = (int)Math.Floor(bounds.Position.X / CellSize); x <= Math.Floor(bounds.End.X / CellSize); x++)
            {
                if (!index.TryGetValue((x, z), out var list)) index[(x, z)] = list = new List<int>();
                list.Add(value);
            }
    }

    /// <summary>
    /// 指定範囲にある地物番号を重複なく列挙します。
    /// 引数: index（Dictionary）索引、bounds（Rect2）範囲。返り値: IEnumerable&lt;int&gt; 地物番号。
    /// </summary>
    private static IEnumerable<int> Query(Dictionary<(int, int), List<int>> index, Rect2 bounds)
    {
        var found = new HashSet<int>();
        for (var z = (int)Math.Floor(bounds.Position.Y / CellSize); z <= Math.Floor(bounds.End.Y / CellSize); z++)
            for (var x = (int)Math.Floor(bounds.Position.X / CellSize); x <= Math.Floor(bounds.End.X / CellSize); x++)
                if (index.TryGetValue((x, z), out var list)) foreach (var item in list) if (found.Add(item)) yield return item;
    }

    /// <summary>
    /// 点または線分と多角形が指定距離以内で接するか調べます。
    /// 引数: a/b（Vector2）線分、polygon（Vector2[]）輪郭、radius（float）距離。返り値: bool 接触する場合はtrue。
    /// </summary>
    private static bool TouchesPolygon(Vector2 a, Vector2 b, Vector2[] polygon, float radius)
    {
        if (Geometry2D.IsPointInPolygon(a, polygon) || Geometry2D.IsPointInPolygon(b, polygon)) return true;
        for (var i = 0; i < polygon.Length; i++)
        {
            var c = polygon[i]; var d = polygon[(i + 1) % polygon.Length];
            var denominator = (b - a).Cross(d - c);
            if (Mathf.Abs(denominator) > 0.000001f)
            {
                var t = (c - a).Cross(d - c) / denominator;
                var u = (c - a).Cross(b - a) / denominator;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return true;
            }
            if (Mathf.Min(Mathf.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
                Mathf.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b))) < radius) return true;
        }
        return false;
    }

    /// <summary>
    /// 点から線分までの距離を取得します。
    /// 引数: point/a/b（Vector2）点と線分。返り値: float 最短距離。
    /// </summary>
    private static float PointSegmentDistance(Vector2 point, Vector2 a, Vector2 b) => point.DistanceTo(Geometry2D.GetClosestPointToSegment(point, a, b));

    /// <summary>
    /// 視線と軸に沿った境界箱の最初の交点を計算します。
    /// 引数: origin/direction/min/max（Vector3）視線と境界、distance（out float）交点距離。返り値: bool 交差の有無。
    /// </summary>
    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float distance)
    {
        var near = 0f; var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Mathf.Abs(direction[axis]) < 0.000001f) { if (origin[axis] < min[axis] || origin[axis] > max[axis]) { distance = 0; return false; } continue; }
            var first = (min[axis] - origin[axis]) / direction[axis];
            var second = (max[axis] - origin[axis]) / direction[axis];
            near = Mathf.Max(near, Mathf.Min(first, second)); far = Mathf.Min(far, Mathf.Max(first, second));
            if (far < near) { distance = 0; return false; }
        }
        distance = near; return true;
    }

    /// <summary>
    /// 輪郭全体の範囲を取得します。
    /// 引数: points（Vector2[]）輪郭。返り値: Rect2 境界。
    /// </summary>
    private static Rect2 PolygonBounds(Vector2[] points)
    {
        var min = points[0]; var max = points[0];
        foreach (var point in points) { min = min.Min(point); max = max.Max(point); }
        return new Rect2(min, max - min);
    }

    /// <summary>
    /// UTF-8のJSONをリソース領域から読みます。
    /// 引数: path（string）パス。返り値: JsonDocument JSON文書。
    /// </summary>
    public static JsonDocument ReadJson(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read) ?? throw new InvalidOperationException($"実地図データを開けません: {path}");
        return JsonDocument.Parse(file.GetAsText());
    }

    /// <summary>
    /// 二要素のJSON配列を座標へ変換します。
    /// 引数: value（JsonElement）配列。返り値: Vector2 座標。
    /// </summary>
    private static Vector2 ReadVector2(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle());

    /// <summary>
    /// 三要素のJSON配列を座標へ変換します。
    /// 引数: value（JsonElement）配列。返り値: Vector3 座標。
    /// </summary>
    private static Vector3 ReadVector3(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());

    /// <summary>
    /// JSONの座標列を変換します。
    /// 引数: value（JsonElement）座標配列。返り値: Vector2[] 座標列。
    /// </summary>
    private static Vector2[] ReadPoints(JsonElement value) => value.EnumerateArray().Select(ReadVector2).ToArray();

    /// <summary>
    /// JSONの地表範囲を変換します。
    /// 引数: value（JsonElement）範囲。返り値: Rect2 地表範囲。
    /// </summary>
    private static Rect2 ReadRect(JsonElement value) => new(value.GetProperty("x").GetSingle(), value.GetProperty("z").GetSingle(), value.GetProperty("width").GetSingle(), value.GetProperty("height").GetSingle());
}
