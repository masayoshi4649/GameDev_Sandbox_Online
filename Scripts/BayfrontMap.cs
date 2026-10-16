using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Bayfront;

public sealed record MapDistrict(string Name, Vector2 Center, Vector2[] Polygon, Color Color);
public sealed record MapRoad(string Name, Vector2[] Points, float Width);
public sealed record MapLandmark(string Name, string Kind, Vector2 Position, float Rotation);

/// <summary>
/// 自作した東京湾岸の簡略地形と初期都市を扱います。
/// 実測地図ではなく、東がX正、南がZ正、1単位が10メートルです。
/// </summary>
public sealed class BayfrontMap
{
    public RealWorldData? RealWorld { get; private set; }
    public bool IsRealWorld => RealWorld is not null;
    public string MapId => IsRealWorld ? "real-bayfront" : CityModel.LegacyMapId;
    public int MapVersion => 1;
    public string UserSavePath => IsRealWorld ? "user://cities/real-bayfront.json" : "user://cities/bayfront.json";
    public string ServerSavePath => IsRealWorld ? "user://server/real-bayfront.json" : "user://server/city.json";
    public IReadOnlyList<MapDistrict> Districts { get; private set; } = Array.Empty<MapDistrict>();
    public IReadOnlyList<MapRoad> Roads { get; private set; } = Array.Empty<MapRoad>();
    public IReadOnlyList<MapLandmark> Landmarks { get; private set; } = Array.Empty<MapLandmark>();

    /**
     * 機能: 同梱された日本語の概形データを読み込みます。
     * 引数: なし。
     * 返り値: BayfrontMap型の地形、道路、ランドマーク定義。
     */
    public static BayfrontMap Load()
    {
        if (!OS.GetCmdlineUserArgs().Contains("--sample-map"))
        {
            if (!RealWorldData.IsAvailable()) throw new InvalidOperationException("実都市データがありません。import-world.ps1 を実行してください。簡略地図は --sample-map で明示的に選択できます。");
            return LoadRealWorld();
        }
        using var file = Godot.FileAccess.Open("res://Data/bayfront.json", Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException("湾岸地形データを開けませんでした。");
        using var document = JsonDocument.Parse(file.GetAsText());
        var districts = new List<MapDistrict>();
        var roads = new List<MapRoad>();
        var landmarks = new List<MapLandmark>();
        foreach (var value in document.RootElement.GetProperty("districts").EnumerateArray())
            districts.Add(new MapDistrict(value.GetProperty("name").GetString()!, ReadPoint(value.GetProperty("center")),
                ReadPoints(value.GetProperty("polygon")), new Color(value.GetProperty("color").GetString()!)));
        foreach (var value in document.RootElement.GetProperty("roads").EnumerateArray())
            roads.Add(new MapRoad(value.GetProperty("name").GetString()!, ReadPoints(value.GetProperty("points")),
                value.GetProperty("width").GetSingle()));
        foreach (var value in document.RootElement.GetProperty("landmarks").EnumerateArray())
            landmarks.Add(new MapLandmark(value.GetProperty("name").GetString()!, value.GetProperty("kind").GetString()!,
                ReadPoint(value.GetProperty("position")), value.GetProperty("rotation").GetSingle()));
        return new BayfrontMap { Districts = districts, Roads = roads, Landmarks = landmarks };
    }

    // ----------------------------------------

    /**
     * 機能: 指定した位置が四地区の陸地に含まれるか調べます。
     * 引数: xはfloat型の東西座標、zはfloat型の南北座標。
     * 返り値: bool型。陸地であればtrue。
     */
    public bool IsLand(float x, float z)
    {
        if (RealWorld is not null) return RealWorld.IsLand(x, z);
        var point = new Vector2(x, z);
        foreach (var district in Districts)
            if (Geometry2D.IsPointInPolygon(point, district.Polygon)) return true;
        return false;
    }

    /**
     * 機能: 指定した位置に対応する地区名を取得します。
     * 引数: xはfloat型の東西座標、zはfloat型の南北座標。
     * 返り値: string型の地区名。海上では「湾岸水域」。
     */
    public string DistrictAt(float x, float z)
    {
        var point = new Vector2(x, z);
        if (RealWorld is not null)
        {
            if (!RealWorld.IsLand(x, z)) return "湾岸水域";
            return Districts.OrderBy(district => district.Center.DistanceSquaredTo(point)).First().Name;
        }
        foreach (var district in Districts)
            if (Geometry2D.IsPointInPolygon(point, district.Polygon)) return district.Name;
        return "湾岸水域";
    }

    // ----------------------------------------

    /**
     * 機能: 固定ランドマークの予約地と建築物または道路の占有領域の重複を防ぎます。
     * 引数: itemはCityObject型の配置予定地物。道路は両端を結ぶ線分全体を検査します。
     * 返り値: bool型。全予約地から地物の占有半径以上離れていればtrue。
     */
    public bool CanPlace(CityObject item)
    {
        if (RealWorld is not null) return RealWorld.CanPlace(item);
        if (item is null || !float.IsFinite(item.X) || !float.IsFinite(item.Z)
            || !float.IsFinite(item.EndX) || !float.IsFinite(item.EndZ)) return false;
        var radius = CityModel.GetRadius(item.Kind);
        if (radius <= 0) return false;
        var position = new Vector2(item.X, item.Z);
        var end = new Vector2(item.EndX, item.EndZ);
        foreach (var landmark in Landmarks)
        {
            var landmarkRadius = LandmarkRadius(landmark.Kind);
            var nearest = item.Kind == "road" && position != end
                ? Geometry2D.GetClosestPointToSegment(landmark.Position, position, end) : position;
            if (nearest.DistanceTo(landmark.Position) < landmarkRadius + radius) return false;
        }
        return true;
    }

    // ----------------------------------------

    /**
     * 機能: 道路、海岸、建築、ランドマークとの間隔を確保した初期都市を生成します。
     * 引数: なし。
     * 返り値: CityState型。同じ地形から同じ初期状態を生成します。
     */
    public CityState CreateSeedState()
    {
        if (RealWorld is not null) return new CityState { MapId = MapId, MapVersion = MapVersion };
        var state = new CityState();
        var sequence = 0;
        foreach (var road in Roads)
            for (var i = 1; i < road.Points.Length; i++)
            {
                var start = road.Points[i - 1];
                var end = road.Points[i];
                var item = new CityObject
                {
                    Id = $"seed-road-{++sequence:D3}", Kind = "road", X = start.X, Z = start.Y,
                    EndX = end.X, EndZ = end.Y, District = DistrictAt(start.X, start.Y)
                };
                if (!CanPlace(item))
                    throw new InvalidOperationException($"初期道路「{road.Name}」がランドマークの予約地と重なっています。");
                state.Objects.Add(item);
            }

        foreach (var district in Districts)
        {
            var placed = 0;
            for (var scanRow = 0; scanRow < 24 && placed < 24; scanRow++)
                for (var scanColumn = 0; scanColumn < 24 && placed < 24; scanColumn++)
                {
                    var row = scanRow * 7 % 24;
                    var column = scanColumn * 11 % 24;
                    var x = -245 + column * 21 + (row % 2) * 4;
                    var z = -270 + row * 21;
                    if (DistrictAt(x, z) != district.Name) continue;
                    var variant = (row * 13 + column * 7) % 7;
                    var kind = placed % 9 == 6 ? "park" : placed % 11 == 8 ? "civic"
                        : district.Name == "東雲" || placed % 3 != 0 ? "housing" : "office";
                    var item = new CityObject
                    {
                        Kind = kind, X = x, Z = z,
                        Variant = variant, Rotation = (placed % 3 == 0 ? 5.0f : 0), District = district.Name
                    };
                    if (!CanSeed(state.Objects, item)) continue;
                    item.Id = $"seed-building-{++sequence:D3}";
                    state.Objects.Add(item);
                    placed++;
                }
        }
        return state;
    }

    // ----------------------------------------

    /// <summary>
    /// 描画と配置に共通の実地表高を取得します。
    /// 引数: x/z（float）ゲーム座標。返り値: float ENUの地表高。
    /// </summary>
    public float GroundHeight(float x, float z) => RealWorld?.GroundHeight(x, z) ?? 0;

    /// <summary>
    /// 実データと地区へのカメラ移動先を設定します。
    /// 引数: なし。返り値: BayfrontMap 実地図のアダプター。
    /// </summary>
    private static BayfrontMap LoadRealWorld()
    {
        var data = RealWorldData.Load();
        var targets = new (string Name, double Longitude, double Latitude)[]
        {
            ("台場", 139.7752, 35.6265), ("有明", 139.7945, 35.6344),
            ("東雲", 139.8097, 35.6454), ("豊洲", 139.7966, 35.6537)
        };
        var districts = targets.Select(target =>
        {
            var point = RealWorldData.Project(target.Longitude, target.Latitude);
            var center = new Vector2(point.X, point.Z);
            var land = data.Lands.FirstOrDefault(item => Geometry2D.IsPointInPolygon(center, item.Points));
            return new MapDistrict(target.Name, center, land?.Points ?? data.Lands[0].Points, new Color("afbd9c"));
        }).ToArray();
        return new BayfrontMap { RealWorld = data, Districts = districts };
    }

    /**
     * 機能: 初期建築の陸地内包と既存地物からの離隔を判定します。
     * 引数: objectsはIEnumerable型の既存地物、candidateはCityObject型の配置予定建築。
     * 返り値: bool型。配置可能なときtrue。
     */
    private bool CanSeed(IEnumerable<CityObject> objects, CityObject candidate)
    {
        var x = candidate.X;
        var z = candidate.Z;
        var radius = CityModel.GetRadius(candidate.Kind);
        var point = new Vector2(x, z);
        var coastClearance = radius + 4;
        if (!IsLand(x, z) || !IsLand(x - coastClearance, z) || !IsLand(x + coastClearance, z)
            || !IsLand(x, z - coastClearance) || !IsLand(x, z + coastClearance)) return false;
        if (!CanPlace(candidate)) return false;
        foreach (var item in objects)
        {
            if (item.Kind == "road")
            {
                var nearest = Geometry2D.GetClosestPointToSegment(point, new Vector2(item.X, item.Z), new Vector2(item.EndX, item.EndZ));
                if (point.DistanceTo(nearest) < radius + 5) return false;
            }
            else if (point.DistanceTo(new Vector2(item.X, item.Z)) < radius + CityModel.GetRadius(item.Kind) + 3) return false;
        }
        return true;
    }

    /**
     * 機能: 固定モデルの水平投影を包含する予約地半径を取得します。
     * 引数: kindはstring型のランドマーク種別。
     * 返り値: float型の予約地半径。回転後もモデル全体を包含します。
     */
    private static float LandmarkRadius(string kind) => kind switch
    {
        "observatory" => 17,
        "exhibition" => 21,
        "market" => 21,
        "tower" => 13,
        _ => 21
    };

    // ----------------------------------------

    /**
     * 機能: JSONの二要素配列を座標へ変換します。
     * 引数: valueはJsonElement型の数値配列。
     * 返り値: Vector2型の座標。
     */
    private static Vector2 ReadPoint(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle());

    /**
     * 機能: JSONの座標列をGodotの座標配列へ変換します。
     * 引数: valueはJsonElement型の座標列。
     * 返り値: Vector2[]型の座標列。
     */
    private static Vector2[] ReadPoints(JsonElement value)
    {
        var points = new List<Vector2>();
        foreach (var point in value.EnumerateArray()) points.Add(ReadPoint(point));
        return points.ToArray();
    }
}
