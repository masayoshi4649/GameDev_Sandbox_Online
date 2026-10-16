using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 実地形に配置した交通試作街区が保存可能で、四種類の実交通を生むことを確認します。
/// </summary>
public partial class UrbanDemoTests : Node
{
    /// <summary>
    /// 機能: 原典地形上で試作街区を生成し、個体の目的移動を進めて検査します。
    /// 引数: なし。
    /// 返り値: void。成功は終了コード 0、失敗は 1 で通知します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            var map = BayfrontMap.Load();
            var state = UrbanDemo.Create(map, out var center);
            var terrain = new UrbanTerrain(map.RealWorld!);
            var camera = new OrbitCamera(); AddChild(camera); camera.ConfigureTerrain(terrain.Height, true); camera.FocusOn(center, 85);
            var viewportSize = GetViewport().GetVisibleRect().Size;
            var viewPoints = UrbanDemo.GetViewPoints(state, terrain);
            var road = state.Edits.Objects.First(item => item.Id == "demo-road");
            var along = new Vector2(road.Path[^1].X - road.Path[0].X, road.Path[^1].Z - road.Path[0].Z);
            camera.FramePoints(viewPoints, along);
            var positioned = camera.Camera.GlobalPosition; camera._Process(0);
            if (positioned.DistanceTo(camera.Camera.GlobalPosition) > 0.0001f) throw new InvalidOperationException("操作していないのに街区の注視位置が変わります。");
            var projected = viewPoints.Select(point => camera.Camera.UnprojectPosition(point) / viewportSize).ToArray();
            var screenBounds = UrbanCatalog.BoundsOf(projected);
            if (screenBounds.Position.X < 0.07f || screenBounds.End.X > 0.93f || screenBounds.Position.Y < 0.17f || screenBounds.End.Y > 0.80f)
                throw new InvalidOperationException($"交通街区が編集ツールの裏へ見切れます: {screenBounds}");
            if (screenBounds.Size.X < 0.55f) throw new InvalidOperationException("交通街区が遠すぎるか長手方向を向いて小さく表示されます。");
            GD.Print($"交通街区の視点検査: 中心={center} 画面範囲={screenBounds} 海面m={terrain.SeaLevel * 10} 中心高m={terrain.Height(center.X, center.Y) * 10}");
            if (!CityModel.TryDeserialize(CityModel.Serialize(state), map.IsLand, out _, out var error)) throw new InvalidOperationException(error);
            if (state.MapId != map.MapId || state.MapVersion != map.MapVersion || state.Edits.IncludeSource) throw new InvalidOperationException("交通街区の保存形式が一致しません。");
            var homes = state.Edits.Objects.Where(item => item.Usage.StartsWith("housing:", StringComparison.Ordinal)).ToArray();
            for (var a = 0; a < homes.Length; a++)
                for (var b = a + 1; b < homes.Length; b++)
                    if (Geometry2D.IntersectPolygons(UrbanCatalog.GetFootprint(homes[a]), UrbanCatalog.GetFootprint(homes[b])).Count > 0)
                        throw new InvalidOperationException("交通手段ごとの住宅が重なっています。");
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            for (var second = 0; second < 600; second++)
            {
                CityModel.AdvanceSimulationClock(state, 1);
                UrbanSimulation.Step(state, state.Edits.Objects, 1);
                foreach (var agent in state.Simulation.Agents) kinds.Add(agent.Kind);
            }
            foreach (var kind in new[] { "person", "car", "train", "ship" })
                if (!kinds.Contains(kind)) throw new InvalidOperationException($"交通街区で {kind} の実移動が開始されません。");
            if (state.Simulation.CompletedTrips == 0) throw new InvalidOperationException("住民が目的地へ到着しません。");
            GD.Print($"BAYFRONT_TRAFFIC_CITY_PASS 中心={center} 地物={state.Edits.Objects.Count} 住民={state.Simulation.Population} 到着={state.Simulation.CompletedTrips}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"交通街区の検査に失敗しました: {error}");
            GetTree().Quit(1);
        }
    }
}
