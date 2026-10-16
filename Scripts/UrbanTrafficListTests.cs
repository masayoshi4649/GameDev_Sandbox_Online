using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 一時停止中に開く交通一覧と、時間更新中の選択ボタン維持を検証します。
/// </summary>
public partial class UrbanTrafficListTests : Node
{
    /// <summary>
    /// 停止中の初回表示、都市置換後の再表示、同じ個体のボタン維持と選択を確認します。
    /// 引数: なし。返り値: void。失敗時は非ゼロの終了コードを返します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            var session = new CollaborationSession(); AddChild(session);
            session.Initialize(new CityState { TimeScale = 0, Edits = new() { IncludeSource = false } }, (_, _) => true);
            var hud = new UrbanHud(); AddChild(hud);
            var first = new UrbanAgent { Id = "traffic-list-first", Kind = "person", Name = "停止中の住民", Purpose = "余暇", Status = "移動中" };
            var second = new UrbanAgent { Id = "traffic-list-second", Kind = "car", Name = "停止中の車", Purpose = "通勤", Status = "停車中" };
            session.State.Simulation.Agents.AddRange(new[] { first, second });
            hud.UpdateRuntime(session);
            var open = Buttons(hud).Single(button => button.Text == "人・交通");
            open.EmitSignal(BaseButton.SignalName.Pressed);
            var window = hud.GetChildren().OfType<Window>().Single(item => item.Title == "住民と交通");
            var rows = Buttons(window).ToArray();
            Require(window.Visible && rows.Length == 2 && rows[0].Text.Contains(first.Name) && rows[1].Text.Contains(second.Name), "一時停止中の初回表示で実個体が一覧に現れません。");
            Require(session.State.TimeScale == 0 && session.State.SimulationTick == 0, "一覧を開くために都市時間が進みました。");

            // ----------------------------------------

            var row = rows[0]; var identity = row.GetInstanceId(); row.GrabFocus();
            Require(row.HasFocus(), "一覧の選択ボタンへフォーカスを設定できません。");
            for (var tick = 0; tick < 20; tick++)
            {
                first.Status = tick % 2 == 0 ? "待機" : "移動中"; first.Purpose = "買物";
                hud.UpdateTraffic(session.State);
                var updated = Buttons(window).First();
                Require(updated.GetInstanceId() == identity && updated.HasFocus() && !updated.IsQueuedForDeletion(), "交通状態の更新で選択中のボタンが作り直されました。");
                Require(updated.Text.Contains(first.Status) && updated.Text.Contains("買物"), "ボタンを保持したまま状態の文字列が更新されません。");
            }
            string? selected = null; hud.AgentSelected += value => selected = value;
            row.EmitSignal(BaseButton.SignalName.Pressed);
            Require(selected == first.Id && !window.Visible, "時間更新後の同じ選択ボタンから正しい個体を選べません。");

            // ----------------------------------------

            session.State.Simulation.Agents.Remove(first);
            hud.UpdateTraffic(session.State);
            open.EmitSignal(BaseButton.SignalName.Pressed);
            var remaining = Buttons(window).ToArray();
            Require(remaining.Length == 1 && remaining[0].GetInstanceId() == rows[1].GetInstanceId(), "非表示中の個体削除が反映されないか、残った個体まで再生成されました。");
            window.Hide();
            var replacement = new CityState { TimeScale = 0, CityName = "読み込んだ都市", Edits = new() { IncludeSource = false } };
            replacement.Simulation.Agents.Add(new UrbanAgent { Id = "traffic-list-replaced", Kind = "ship", Name = "読み込んだ船", Status = "移動中" });
            Require(session.ReplaceState(replacement), session.LastError);
            hud.UpdateCity(session, new UrbanStatistics());
            open.EmitSignal(BaseButton.SignalName.Pressed);
            Require(Buttons(window).Single().Text.Contains("読み込んだ船"), "停止中の都市読込後に以前の都市の一覧が残っています。");

            // ----------------------------------------

            session.State.Simulation.Agents.Clear(); hud.UpdateTraffic(session.State);
            Require(!Buttons(window).Any() && Descendants(window).OfType<Label>().Any(label => label.Visible && label.Text.StartsWith("用途を指定", StringComparison.Ordinal)), "個体がなくなった場合に空一覧の案内が表示されません。");
            for (var index = 0; index < 105; index++)
                session.State.Simulation.Agents.Add(new UrbanAgent { Id = "traffic-list-cap-" + index, Name = "試験個体 " + index });
            session.State.Simulation.Limitation = "試験の計算上限"; hud.UpdateTraffic(session.State);
            Require(Buttons(window).Count() == 100 && Descendants(window).OfType<Label>().Any(label => label.Visible && label.Text.Contains("先頭100件") && label.Text.Contains("試験の計算上限")), "表示上限と計算上限の案内が一致しません。");
            var preserved = Buttons(window).First().GetInstanceId();
            session.State.Simulation.Agents.RemoveRange(50, 55); hud.UpdateTraffic(session.State);
            Require(Buttons(window).Count() == 50 && Buttons(window).First().GetInstanceId() == preserved, "一覧の件数変更で既存ボタンまで作り直されました。");
            GD.Print("BAYFRONT_TRAFFIC_LIST_PASS 停止中の初回表示・同一個体ボタンの保持・選択・都市読込・空一覧・上限");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    /// <summary>
    /// 指定ノード以下のボタンだけを表示順に取得します。
    /// 引数: root（Node）開始ノード。返り値: IEnumerable&lt;Button&gt; ボタン列。
    /// </summary>
    private static IEnumerable<Button> Buttons(Node root) => Descendants(root).OfType<Button>();

    /// <summary>
    /// 指定ノードの子孫を表示順に列挙します。
    /// 引数: root（Node）開始ノード。返り値: IEnumerable&lt;Node&gt; 子孫列。
    /// </summary>
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>
    /// 期待する一覧の状態を満たさない場合に試験を中断します。
    /// 引数: condition（bool）条件、message（string）失敗時の説明。返り値: void。
    /// </summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
