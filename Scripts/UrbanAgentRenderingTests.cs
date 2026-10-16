using Godot;
using System;

namespace Bayfront;

/// <summary>
/// 個体の実位置、選択、乗車時の非表示を描画ノード単位で確認します。
/// </summary>
public partial class UrbanAgentRenderingTests : Node
{
    /// <summary>
    /// 機能: CPU 状態を用いた個体描画と側面選択の検査を実行します。
    /// 引数: なし。
    /// 返り値: void。成功時は終了コード 0、失敗時は 1 で終了します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            var renderer = new UrbanAgentRenderer();
            AddChild(renderer);
            renderer.Initialize((_, _) => 2);
            var state = new CityState();
            var person = new UrbanAgent
            {
                Id = "選択試験の住民", Kind = "person", Name = "試験住民", X = 0, Z = 0,
                DirectionX = 0, DirectionZ = -1, Purpose = "通勤", Status = "移動中"
            };
            state.Simulation.Agents.Add(person);
            renderer.Refresh(state);
            renderer.AdvanceTime(1);
            Require(renderer.VisibleAgentCount == 1, "住民の表示数が一致しません。");
            Require(renderer.Pick(new Vector3(2, 2.12f, 0), Vector3.Left) == person.Id, "住民を側面から選択できません。");
            Require(renderer.Pick(new Vector3(0, 5, 0), Vector3.Down) == person.Id, "住民を上から選択できません。");
            renderer.Select(person.Id);
            Require(renderer.SelectedId == person.Id, "住民の選択状態が保存されません。");
            var before = renderer.GetAgentPosition(person.Id)!.Value;
            renderer.AdvanceTime(120);
            Require(renderer.GetAgentPosition(person.Id)!.Value.DistanceTo(before) < 0.00001f, "CPU が停止中なのに個体が移動しました。");
            person.X = 1;
            state.SimulationTick = 1;
            state.Simulation.LastProcessedTick = 1;
            renderer.Refresh(state);
            renderer.AdvanceTime(120.5);
            var midpoint = renderer.GetAgentPosition(person.Id)!.Value.X;
            Require(midpoint > 0.1f && midpoint < 0.9f, "CPU の確定位置の間が補間されません。");
            renderer.AdvanceTime(121);
            Require(Math.Abs(renderer.GetAgentPosition(person.Id)!.Value.X - 1) < 0.00001f, "確定位置へ到着しません。");
            state.TimeScale = 0;
            person.X = 2;
            renderer.Refresh(state);
            renderer.AdvanceTime(121);
            Require(Math.Abs(renderer.GetAgentPosition(person.Id)!.Value.X - 2) < 0.00001f, "一時停止時に確定位置を表示しません。");
            foreach (var status in new[] { "乗車中", "在宅", "就業中", "滞在中" })
            {
                person.Status = status;
                renderer.Refresh(state);
                renderer.AdvanceTime(120);
                Require(renderer.VisibleAgentCount == 0 && renderer.SelectedId is null, $"{status} の住民が屋外に重複して表示されます。");
            }
            foreach (var kind in new[] { "car", "train", "ship" })
            {
                state.Simulation.Agents.Clear();
                var agent = new UrbanAgent
                {
                    Id = $"選択試験の{kind}", Kind = kind, Name = "試験車両", X = 0, Z = 0,
                    DirectionX = 1, DirectionZ = 0, Purpose = "乗客輸送", Status = "移動中"
                };
                state.Simulation.Agents.Add(agent);
                renderer.Refresh(state);
                renderer.AdvanceTime(120);
                Require(renderer.Pick(new Vector3(0, 2.13f, -4), Vector3.Back) == agent.Id, $"{kind} を側面から選択できません。");
                renderer.Select(agent.Id);
                state.Simulation.Agents.Clear();
                renderer.Refresh(state);
                Require(renderer.SelectedId is null && renderer.VisibleAgentCount == 0, "消滅した車両が選択対象に残っています。");
            }
            var waterRenderer = new UrbanAgentRenderer();
            AddChild(waterRenderer);
            waterRenderer.Initialize((_, _) => -3, () => 2);
            state.Simulation.Agents.Add(new UrbanAgent { Id = "海面の船", Kind = "ship", Status = "移動中" });
            waterRenderer.Refresh(state);
            waterRenderer.AdvanceTime(120);
            Require(waterRenderer.Pick(new Vector3(0, 2.13f, -4), Vector3.Back) == "海面の船", "船が海面に表示されません。");
            Require(waterRenderer.Pick(new Vector3(0, -2.8f, -4), Vector3.Back) is null, "船が海底を走っています。");
            GD.Print("BAYFRONT_AGENT_RENDERING_PASS");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"個体描画の検査に失敗しました: {error}");
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// 機能: 試験条件が成立することを確認します。
    /// 引数: condition（bool）判定、message（string）失敗理由。
    /// 返り値: void。不成立の場合は InvalidOperationException を送出します。
    /// </summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
