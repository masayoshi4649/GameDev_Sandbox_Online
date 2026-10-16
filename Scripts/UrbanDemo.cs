using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 実地形の海岸に、後から全て編集できる交通試作街区を明示配置します。
/// 住民や車を飾りとして生成せず、住宅・目的地・交通網をシミュレーションへ渡します。
/// </summary>
public static class UrbanDemo
{
    /// <summary>
    /// 機能: 道路・鉄道・航路と四交通手段の住宅を備えた試作都市を作成します。
    /// 引数: map（BayfrontMap）原典地図、center（out Vector2）街区中心です。
    /// 返り値: CityState 型の未保存都市。交通網を置ける海岸がない場合は例外を送出します。
    /// </summary>
    public static CityState Create(BayfrontMap map, out Vector2 center)
    {
        var data = map.RealWorld ?? throw new InvalidOperationException("交通街区には実地形が必要です。");
        var terrain = new UrbanTerrain(data);
        var preferred = map.Districts.Count > 1 ? map.Districts[1].Center : data.Bounds.GetCenter();
        var frame = FindShore(terrain, preferred, data.Lands);
        var state = new CityState
        {
            MapId = map.MapId, MapVersion = map.MapVersion, CityName = "交通を試す街",
            Edits = new UrbanEdits { IncludeSource = false, FreeBuild = true }
        };
        var objects = state.Edits.Objects;
        var forward = frame.Direction;
        var inland = new Vector2(-forward.Y, forward.X);
        /*
         * 機能: 街区内の縦横距離を実地形座標へ変換します。
         * 引数: along と across（float）海岸方向と陸側方向の都市座標距離。
         * 返り値: Vector2 型の配置位置です。
         */
        Vector2 Point(float along, float across) => frame.Start + forward * along + inland * across;
        /*
         * 機能: 編集可能な施設の明示設計を生成します。
         * 引数: id、name、usage、color（string）識別と用途、along、across、width、depth（float）位置と寸法、floors（int）階数。
         * 返り値: UrbanObject 型の施設です。
         */
        UrbanObject Building(string id, string name, string usage, float along, float across, float width, float depth, int floors, string color)
        {
            var p = Point(along, across);
            return new UrbanObject
            {
                Id = id, Kind = "building", Name = name, Usage = usage, X = p.X, Z = p.Y,
                WidthMeters = width, DepthMeters = depth, Floors = floors, FloorHeightMeters = 3.2f,
                Rotation = Mathf.RadToDeg(forward.Angle()), ColorHex = color
            };
        }
        /*
         * 機能: 全ての頂点と通行設備を後から変更できる交通線を生成します。
         * 引数: id、name、usage（string）識別と用途、across、width、speed、sidewalk（float）横位置と設計値。
         * 返り値: UrbanObject 型の交通線です。
         */
        UrbanObject Route(string id, string name, string usage, float across, float width, float speed, float sidewalk)
        {
            var a = Point(0, across); var b = Point(frame.Length * 0.5f, across); var c = Point(frame.Length, across);
            return new UrbanObject
            {
                Id = id, Kind = "road", Name = name, Usage = usage, X = b.X, Z = b.Y,
                Path = new() { new(a.X, a.Y), new(b.X, b.Y), new(c.X, c.Y) },
                WidthMeters = width, SpeedKph = speed, SidewalkWidthMeters = sidewalk,
                LanesForward = 1, LanesBackward = 1, LaneWidthMeters = usage == "road" ? 3 : width * 0.5f,
                ColorHex = usage == "rail" ? "#7e776d" : usage == "waterway" ? "#429cb9" : "#41474b"
            };
        }
        var modes = new[] { ("walk", "徒歩で通う家", "#d4ccad"), ("car", "車で通う家", "#b4ccbf"), ("train", "鉄道で通う家", "#c4b8d5"), ("ship", "船で通う家", "#aecbd8") };
        for (var index = 0; index < modes.Length; index++)
        {
            var mode = modes[index];
            objects.Add(Building("demo-home-" + mode.Item1, mode.Item2, "housing:" + mode.Item1, 2 + index * 4, 6, 16, 18, 2, mode.Item3));
        }
        objects.Add(Building("demo-work", "共同事務所", "office", frame.Length - 5, 6, 26, 22, 4, "#a6bac6"));
        objects.Add(Building("demo-shop", "海辺の商店", "commercial", frame.Length * 0.5f + 5, 5.8f, 20, 18, 2, "#dbbca3"));
        var park = Building("demo-park", "散歩の広場", "park", frame.Length * 0.5f - 4, 6, 24, 22, 1, "#91b47c");
        park.Kind = "surface"; park.HeightMeters = 0.15f; objects.Add(park);
        objects.Add(Route("demo-road", "海岸通り", "road", 1.3f, 12, 30, 3));
        objects.Add(Route("demo-rail", "海岸鉄道", "rail", 3.2f, 6, 60, 0));
        objects.Add(Route("demo-waterway", "沿岸航路", "waterway", -2.2f, 12, 20, 0));
        foreach (var (suffix, along) in new[] { ("west", 5f), ("east", frame.Length - 4) })
        {
            objects.Add(Building("demo-station-" + suffix, suffix == "west" ? "住宅側の駅" : "職場側の駅", "station", along, 3.2f, 14, 14, 1, "#c5d5ce"));
            objects.Add(Building("demo-port-" + suffix, suffix == "west" ? "住宅側の船着場" : "職場側の船着場", "port", along, 0.3f, 14, 12, 1, "#bbd2da"));
        }
        var junction = Point(frame.Length * 0.5f, 1.3f);
        objects.Add(new UrbanObject
        {
            Id = "demo-junction", Kind = "intersection", Name = "海岸通りの信号", X = junction.X, Z = junction.Y,
            WidthMeters = 12, DepthMeters = 12, Control = "signals", ConnectedRoadIds = new() { "demo-road" },
            Phases = new() { new() { Name = "通行", DurationSeconds = 24, GreenRoadIds = new() { "demo-road" } }, new() { Name = "歩行者横断", DurationSeconds = 8 } },
            YellowSeconds = 3, AllRedSeconds = 1, ColorHex = "#454c50"
        });
        var signal = Point(frame.Length * 0.5f - 0.8f, 2.05f);
        objects.Add(new UrbanObject
        {
            Id = "demo-signal", Kind = "signal", Name = "海岸通りの灯器", X = signal.X, Z = signal.Y,
            JunctionId = "demo-junction", ParentId = "demo-junction", ApproachRoadId = "demo-road",
            HeightMeters = 5, WidthMeters = 4, DepthMeters = 0.4f, Rotation = Mathf.RadToDeg(forward.Angle())
        });
        center = Point(frame.Length * 0.5f, 1.8f);
        return state;
    }

    /// <summary>
    /// 機能: 街区全体を見切れさせず表示するため、路幅・建物・屋根の立体範囲を列挙します。
    /// 引数: state（CityState）試作都市、terrain（UrbanTerrain）都市へ適用済みの地形。
    /// 返り値: IReadOnlyList&lt;Vector3&gt; 型の注視範囲頂点です。
    /// </summary>
    public static IReadOnlyList<Vector3> GetViewPoints(CityState state, UrbanTerrain terrain)
    {
        var points = new List<Vector3>();
        foreach (var item in state.Edits.Objects)
        {
            if (item.Deleted) continue;
            if (item.Kind == "road")
            {
                for (var i = 1; i < item.Path.Count; i++)
                {
                    var a = new Vector2(item.Path[i - 1].X, item.Path[i - 1].Z); var b = new Vector2(item.Path[i].X, item.Path[i].Z);
                    var side = new Vector2(-(b - a).Y, (b - a).X).Normalized() * item.WidthMeters / 20;
                    foreach (var point in new[] { a - side, a + side, b - side, b + side })
                        points.Add(new Vector3(point.X, (item.FollowTerrain ? item.Usage == "waterway" ? terrain.SeaLevel : terrain.Height(point.X, point.Y) : 0) + item.Y + 0.05f, point.Y));
                }
                continue;
            }
            var bottom = UrbanCatalog.BaseHeight(item, terrain.Height);
            var top = bottom + UrbanCatalog.HeightMetersOf(item) / 10 + (item.Roof == "flat" ? 0 : 1.5f);
            foreach (var point in UrbanCatalog.GetFootprint(item))
            {
                points.Add(new Vector3(point.X, bottom, point.Y));
                points.Add(new Vector3(point.X, top, point.Y));
            }
        }
        return points;
    }

    /// <summary>
    /// 機能: 十分な陸上幅と平行する水面を持つ、直線海岸の街区基準を探します。
    /// 引数: terrain（UrbanTerrain）編集前地形、preferred（Vector2）優先中心、lands（IReadOnlyList&lt;RealLand&gt;）原典の陸域輪郭。
    /// 返り値: 開始点、海岸方向、長さを持つタプルです。
    /// </summary>
    private static (Vector2 Start, Vector2 Direction, float Length) FindShore(UrbanTerrain terrain, Vector2 preferred, IReadOnlyList<RealLand> lands)
    {
        var shores = new List<(Vector2 Center, Vector2 Along)>();
        foreach (var land in lands)
            foreach (var polygon in new[] { land.Points }.Concat(land.Holes))
                for (var i = 0; i < polygon.Length; i++)
                {
                    var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                    if (a.DistanceSquaredTo(b) > 1) shores.Add(((a + b) * 0.5f, (b - a).Normalized()));
                }
        foreach (var length in new[] { 70f, 50f, 35f })
            foreach (var shore in shores.OrderBy(point => point.Center.DistanceSquaredTo(preferred)))
                foreach (var sign in new[] { 1, -1 })
                {
                    var along = shore.Along * sign;
                    var start = shore.Center - along * (length * 0.5f);
                    if (Suitable(terrain, start, along, new Vector2(-along.Y, along.X), length)) return (start, along, length);
                }
        var candidates = new List<Vector2>();
        var bounds = terrain.Bounds.Grow(-15);
        for (var z = bounds.Position.Y; z < bounds.End.Y; z += 12)
            for (var x = bounds.Position.X; x < bounds.End.X; x += 12) candidates.Add(new Vector2(x, z));
        foreach (var length in new[] { 70f, 50f, 35f })
            foreach (var center in candidates.OrderBy(point => point.DistanceSquaredTo(preferred)))
                for (var direction = 0; direction < 8; direction++)
                {
                    var along = Vector2.Right.Rotated(direction * Mathf.Pi / 4);
                    var across = new Vector2(-along.Y, along.X);
                    var start = center - along * (length * 0.5f);
                    if (!Suitable(terrain, start, along, across, length)) continue;
                    return (start, along, length);
                }
        throw new InvalidOperationException("道路・線路・航路を平行配置できる海岸が見つかりませんでした。");
    }

    /// <summary>
    /// 機能: 住宅と線路が陸上に、航路が水上に収まることを海岸沿いに確認します。
    /// 引数: terrain（UrbanTerrain）地形、start、along、across（Vector2）街区基準、length（float）長さ。
    /// 返り値: bool 型の配置可能性です。
    /// </summary>
    private static bool Suitable(UrbanTerrain terrain, Vector2 start, Vector2 along, Vector2 across, float length)
    {
        foreach (var distance in new[] { length * 0.5f, 0, length })
            foreach (var offset in new[] { 0.6f, 6.5f, -1.5f, -2.9f })
            {
                var point = start + along * distance + across * offset;
                if (!terrain.Bounds.HasPoint(point) || terrain.IsLand(point.X, point.Y) != (offset > 0)) return false;
            }
        for (var distance = 0f; distance <= length; distance += 3)
            foreach (var offset in new[] { 0.6f, 3.2f, 6.5f, -1.5f, -2.9f })
            {
                var point = start + along * distance + across * offset;
                if (terrain.IsLand(point.X, point.Y) != (offset > 0)) return false;
            }
        return true;
    }
}
