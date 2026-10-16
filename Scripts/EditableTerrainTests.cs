using Godot;
using System;
using System.Diagnostics;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 地形の連続ブラシ、履歴復元、海面と陸水変化を描画なしで検証します。
/// </summary>
public static class EditableTerrainTests
{
    /// <summary>
    /// 地形編集を実データへ適用し、編集順・取消・保存復元の一致を確認します。
    /// 引数: data（RealWorldData）実地形のCPUデータ。
    /// 返り値: void。不一致の場合はInvalidOperationExceptionを送出します。
    /// </summary>
    public static void Run(RealWorldData data)
    {
        var terrain = new UrbanTerrain(data);
        var land = FindPoint(data, true);
        var water = FindPoint(data, false);
        var baseline = terrain.Height(land.X, land.Y);
        for (var z = 0; z < 24; z++)
            for (var x = 0; x < 24; x++)
            {
                var point = data.Bounds.Position + data.Bounds.Size * new Vector2((x + 0.37f) / 24, (z + 0.41f) / 24);
                Check(terrain.IsLand(point.X, point.Y) == data.IsLand(point.X, point.Y), "初期陸水が原典と一致");
            }
        var edits = new UrbanEdits();
        edits.Terrain.Add(Brush("raise", land, 30, 4));
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), baseline + 0.4f, "中心で4m上昇");
        var revision = terrain.Revision;
        terrain.SetEdits(edits);
        Check(terrain.Revision == revision, "同一状態は再計算しない");
        Check(terrain.ChangedBounds.HasPoint(land), "地形変更の局所範囲を公開");
        var outside = land + new Vector2(4, 0);
        var clean = new UrbanTerrain(data);
        Near(terrain.Height(outside.X, outside.Y), clean.Height(outside.X, outside.Y), "ブラシ外の高さを維持");

        // ----------------------------------------

        var raised = terrain.Height(land.X, land.Y);
        edits.Terrain.Add(Brush("smooth", land, 90, 10));
        terrain.SetEdits(edits);
        Check(terrain.Height(land.X, land.Y) < raised, "平滑化で山頂が周辺平均へ近づく");
        var smooth = terrain.Height(land.X, land.Y);
        var saved = JsonSerializer.Serialize(new CityState { Edits = edits.DeepClone() });
        var loaded = JsonSerializer.Deserialize<CityState>(saved) ?? throw new InvalidOperationException("地形保存復元が空です。");
        var restored = new UrbanTerrain(data);
        restored.SetEdits(loaded.Edits);
        Near(restored.Height(land.X, land.Y), smooth, "保存復元後の平滑化結果が一致");
        edits.Terrain.RemoveAt(1);
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), raised, "取消で一つ前の地形へ戻る");
        edits.Terrain.Clear();
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), baseline, "地形初期化で元の標高へ戻る");

        // ----------------------------------------

        edits.Terrain.Add(Brush("raise", land, 1, 1));
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), baseline + 0.1f, "1m半径のブラシを標高格子より細かく保持");
        var edge = land + new Vector2(0.11f, 0);
        Near(terrain.Height(edge.X, edge.Y), clean.Height(edge.X, edge.Y), "1m半径の外側に影響しない");
        edits.Terrain.Add(new TerrainEdit { Mode = "flatten", X = land.X, Z = land.Y, RadiusMeters = 20,
            TargetHeightMeters = (baseline + 2) * 10 });
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), baseline + 2, "整地は先行する上昇より後に適用");

        // ----------------------------------------

        edits.Terrain.Clear();
        edits.Terrain.Add(new TerrainEdit { Mode = "flatten", X = water.X, Z = water.Y, RadiusMeters = 40,
            TargetHeightMeters = (terrain.SeaLevel + 1) * 10 });
        terrain.SetEdits(edits);
        Check(terrain.IsLand(water.X, water.Y), "水域の埋立で陸地へ変化");
        edits.Terrain.Add(Brush("lower", water, 30, 20));
        terrain.SetEdits(edits);
        Check(!terrain.IsLand(water.X, water.Y), "埋立地の掘削で水域へ変化");
        edits.Terrain.Clear();
        edits.SeaLevelMeters = (baseline + 20) * 10;
        terrain.SetEdits(edits);
        Check(!terrain.IsLand(land.X, land.Y), "水面上昇で元の陸地が水没");
        Near(terrain.Height(land.X, land.Y), baseline, "水面変更で地表を持ち上げない");
        Check(terrain.ChangedBounds.Encloses(data.Bounds), "水面変更は全域を再描画");
        edits.SeaLevelMeters = null;
        terrain.SetEdits(edits);
        Check(terrain.IsLand(land.X, land.Y), "水面設定取消で陸地へ戻る");
        Near(terrain.SeaLevel, terrain.DefaultSeaLevel, "初期海面高へ復元");

        // ----------------------------------------

        edits.Terrain.Add(Brush("raise", land, 1, 200));
        terrain.SetEdits(edits);
        var peak = new Vector3(land.X, terrain.Height(land.X, land.Y), land.Y);
        var eye = peak + new Vector3(-8, 2, 0);
        var ray = (peak - eye).Normalized();
        Check(terrain.Raycast(eye, ray, out var slopeHit), "1m半径・200mの急斜面を視線が捉える");
        Check(new Vector2(slopeHit.X, slopeHit.Z).DistanceTo(land) <= 0.11f, "急斜面の照準が別の地形へ飛ばない");
        Check(Math.Abs(slopeHit.Y - terrain.Height(slopeHit.X, slopeHit.Z)) < 0.005f, "急斜面の交点が地表に一致");
        Check((slopeHit - eye).Cross(ray).Length() < 0.005f, "交点が画面の視線上にある");
        Check(terrain.Raycast(peak + Vector3.Up * 10, Vector3.Down, out var verticalHit), "真上からも地形を捉える");
        Near(verticalHit.Y, peak.Y, "垂直視線の交点が山頂に一致");
        Check(!terrain.Raycast(new Vector3(data.Bounds.End.X + 2, 100, data.Bounds.End.Y + 2), Vector3.Down, out _), "地図外の視線は地形に交差しない");
        edits.Terrain.Clear();
        terrain.SetEdits(edits);
        Near(terrain.Height(land.X, land.Y), baseline, "急斜面の取消で高さと探索範囲を復元");

        // ----------------------------------------

        var watch = Stopwatch.StartNew();
        float checksum = 0;
        for (var index = 0; index < 10000; index++) checksum += terrain.Height(land.X + index % 17 * 0.01f, land.Y);
        watch.Stop();
        Check(float.IsFinite(checksum), "連続するCPU標高照会が有限値");
        GD.Print($"BAYFRONT_TERRAIN_PASS sea_level_m={terrain.SeaLevel * 10:0.00} height_queries=10000 elapsed_ms={watch.Elapsed.TotalMilliseconds:0.0}");
    }

    /// <summary>
    /// 試験用の上げ下げブラシを生成します。
    /// 引数: mode（string）操作、point（Vector2）中心、radius/amount（float）半径と変化量m。
    /// 返り値: TerrainEdit 新規のブラシ。
    /// </summary>
    private static TerrainEdit Brush(string mode, Vector2 point, float radius, float amount) => new()
    {
        Mode = mode, X = point.X, Z = point.Y, RadiusMeters = radius, AmountMeters = amount
    };

    /// <summary>
    /// 地図境界から十分に離れた陸地または水域の試験点を探します。
    /// 引数: data（RealWorldData）地図、land（bool）陸地を探す場合はtrue。
    /// 返り値: Vector2 条件に一致するX・Z座標。
    /// </summary>
    private static Vector2 FindPoint(RealWorldData data, bool land)
    {
        for (var z = 4; z < 28; z++)
            for (var x = 4; x < 28; x++)
            {
                var point = data.Bounds.Position + data.Bounds.Size * new Vector2(x / 32f, z / 32f);
                if (data.IsLand(point.X, point.Y) == land) return point;
            }
        throw new InvalidOperationException("地形試験に必要な陸水点がありません。");
    }

    /// <summary>
    /// 計算誤差を許容して高さの一致を検証します。
    /// 引数: actual/expected（float）実測値と期待値、message（string）検証内容。
    /// 返り値: void。不一致の場合は例外を送出します。
    /// </summary>
    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < 0.0002f,
        $"{message}: 実測={actual}、期待={expected}");

    /// <summary>
    /// 条件が成立することを検証します。
    /// 引数: condition（bool）検証結果、message（string）検証内容。
    /// 返り値: void。不成立の場合はInvalidOperationExceptionを送出します。
    /// </summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"地形試験失敗: {message}");
    }
}
