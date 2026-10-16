using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 実際の都市画面で、個体と地物の選択遷移および追跡解除を確認します。
/// </summary>
public static class UrbanSelectionTests
{
    /// <summary>
    /// 機能: 個体選択後の編集、造成、手動パン、回転拡縮、地区移動を回帰検証します。
    /// 引数: game（CityGame）初期化済み画面、source（UrbanObject）原典地物。
    /// 返り値: void。失敗時は InvalidOperationException を送出し、試験個体は必ず除去します。
    /// </summary>
    public static void Run(CityGame game, UrbanObject source)
    {
        var state = game.Session.State;
        var agent = new UrbanAgent
        {
            Id = "選択遷移試験の住民", Kind = "person", Name = "選択試験", Status = "移動中",
            OriginId = source.Id, DestinationId = source.Id, X = source.X + 3, Z = source.Z + 3
        };
        var places = new[]
        {
            new UrbanObject { Id = "選択試験の住居", Kind = "building", Name = "選択試験の住居", Usage = "housing", X = source.X + 10, Z = source.Z, Floors = 1 },
            new UrbanObject { Id = "選択試験の職場", Kind = "building", Name = "選択試験の職場", Usage = "office", X = source.X + 14, Z = source.Z, Floors = 1 },
            new UrbanObject { Id = "選択試験の滞在先", Kind = "surface", Name = "選択試験の滞在先", Usage = "park", X = source.X + 18, Z = source.Z }
        };
        var resident = new UrbanResident { Id = agent.Id, Name = agent.Name, HomeId = places[0].Id, WorkId = places[1].Id, CurrentPlaceId = places[2].Id };
        var car = new UrbanAgent { Id = "選択試験の乗用車", Kind = "car", Name = "選択試験の乗用車", Status = "移動中", PassengerIds = new() { agent.Id }, X = agent.X, Z = agent.Z };
        agent.OriginId = places[0].Id; agent.DestinationId = places[2].Id; agent.Purpose = "余暇";
        agent.Route.Add(new TravelWaypoint { X = agent.X + 10, Z = agent.Z });
        state.Edits.Objects.AddRange(places); state.Simulation.Residents.Add(resident); state.Simulation.Agents.Add(car);
        state.Simulation.Agents.Add(agent);
        game.AgentRenderer.Refresh(state);
        try
        {
            game.SelectAgent(agent.Id);
            var information = (Label)game.Hud.FindChild("AgentInformation", true, false);
            Require(information.Text.Contains("住居　選択試験の住居") && information.Text.Contains("職場　選択試験の職場")
                && information.Text.Contains("現在の滞在先　選択試験の滞在先") && information.Text.Contains("今回の行先　選択試験の滞在先"), "住居・固定職場・滞在先・今回の行先が区別されません。");
            var routeToggle = (CheckBox)game.Hud.FindChild("AgentRouteToggle", true, false);
            routeToggle.ButtonPressed = false; Require(!game.AgentRenderer.SelectedRouteVisible, "経路表示をオフにできません。");
            routeToggle.ButtonPressed = true; Require(game.AgentRenderer.SelectedRouteVisible, "経路表示をオンにできません。");
            var followToggle = (Button)game.Hud.FindChild("AgentFollowToggle", true, false);
            followToggle.EmitSignal(BaseButton.SignalName.Pressed); Require(followToggle.Text.StartsWith("追跡 オン", StringComparison.Ordinal), "追跡開始が明示されません。");
            followToggle.EmitSignal(BaseButton.SignalName.Pressed); Require(followToggle.Text.StartsWith("追跡 オフ", StringComparison.Ordinal), "追跡解除が明示されません。");
            game.SelectAgent(car.Id, true);
            Require(followToggle.Text.StartsWith("追跡 オン", StringComparison.Ordinal), "一覧から個体へ移った直後の追跡状態が表示されません。");
            Require(information.Text.Contains("乗員　1人") && !information.Text.Contains("所有者"), "車両の実乗員と未保持の所有者が混同されています。");
            Buttons(game.Hud).First(button => button.Text.StartsWith("乗員を見る　", StringComparison.Ordinal)).EmitSignal(BaseButton.SignalName.Pressed);
            Require(game.SelectedAgentId == agent.Id, "車両の乗員ボタンから住民を選べません。");
            Require(Buttons(game.Hud).Where(button => button.Text is "移動" or "回転" or "高さ").All(button => button.Disabled), "個体に対して地物移動が有効になっています。");
            game.Select(source.Id);
            Require(Buttons(game.Hud).Where(button => button.Text is "移動" or "回転" or "高さ").All(button => !button.Disabled), "個体から地物へ選び直しても変形ボタンが無効のままです。");
            game.Hud.ShowBuildingRuntime(state, source.Id);
            Require(information.Visible && ((ScrollContainer)game.Hud.FindChild("AgentDetailsScroll", true, false)).Visible
                && !((Control)game.Hud.FindChild("AgentActions", true, false)).Visible, "個体詳細から建物の運用表示へ戻れません。");
            foreach (var tool in new[] { "building", "road", "rail", "waterway", "terrain" })
            {
                game.SelectAgent(agent.Id, true);
                game.SetTool(tool);
                Require(game.SelectedAgentId is null && game.AgentRenderer.SelectedId is null && game.Hud.ActiveTool == tool, $"{tool} へ切替後に個体選択が残っています。");
            }
            game.SelectAgent(agent.Id, true);
            game.CameraRig._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true });
            game.CameraRig._UnhandledInput(new InputEventMouseMotion { Relative = new Vector2(80, 0) });
            game.CameraRig._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false });
            Require(followToggle.Text.StartsWith("追跡 オフ", StringComparison.Ordinal), "停止中の手動パン直後に追跡解除の表示が更新されません。");
            var panned = game.CameraRig.Camera.GlobalPosition;
            game._Process(0);
            Require(panned.DistanceTo(game.CameraRig.Camera.GlobalPosition) < 0.0001f, "手動パンが次フレームの追跡で上書きされます。");
            game.SelectAgent(agent.Id, true);
            game.CameraRig._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
            game.CameraRig._UnhandledInput(new InputEventMouseMotion { Relative = new Vector2(20, 5) });
            game.CameraRig._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
            game.CameraRig._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
            var rotated = game.CameraRig.Camera.GlobalPosition;
            agent.X += 1; game.AgentRenderer.Refresh(state); game._Process(0);
            Require(Math.Abs(game.CameraRig.Camera.GlobalPosition.X - rotated.X - 1) < 0.001f, "回転や拡縮で追跡が解除されました。");
            Buttons(game.Hud).First(button => button.Text == "全域").EmitSignal(BaseButton.SignalName.Pressed);
            var district = game.CameraRig.Camera.GlobalPosition;
            game._Process(0);
            Require(district.DistanceTo(game.CameraRig.Camera.GlobalPosition) < 0.0001f, "地区移動が追跡で上書きされます。");
            agent.Status = "在宅"; game.AgentRenderer.Refresh(state); game.SelectAgent(agent.Id, true);
            var building = game.CameraRig.Camera.GlobalPosition;
            agent.X += 5; game.AgentRenderer.Refresh(state); game._Process(0);
            Require(building.DistanceTo(game.CameraRig.Camera.GlobalPosition) < 0.0001f, "建物内の非表示個体を追い続けています。");
            var elevated = new Vector3(source.X, 20, source.Z);
            game.CameraRig.Follow(elevated);
            var ray = game.CameraRig.Camera.ProjectRayNormal(game.GetViewport().GetVisibleRect().Size * 0.5f);
            Require(ray.Dot((elevated - game.CameraRig.Camera.GlobalPosition).Normalized()) > 0.999f, "追跡高度が地表へ押し戻されます。");
            GD.Print("BAYFRONT_SELECTION_TRANSITIONS_PASS");
        }
        finally
        {
            state.Simulation.Agents.Remove(agent);
            state.Simulation.Agents.Remove(car); state.Simulation.Residents.Remove(resident);
            foreach (var place in places) state.Edits.Objects.Remove(place);
            game.AgentRenderer.Refresh(state);
            game.ClearSelection(); game.SetTool("select");
            game.CameraRig.FocusOn(new Vector2(source.X, source.Z), 50);
        }
    }

    /// <summary>
    /// 機能: 画面内のボタンを子孫から列挙します。
    /// 引数: root（Node）開始ノード。
    /// 返り値: IEnumerable&lt;Button&gt; 型の操作ボタンです。
    /// </summary>
    private static IEnumerable<Button> Buttons(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Button button) yield return button;
            foreach (var descendant in Buttons(child)) yield return descendant;
        }
    }

    /// <summary>
    /// 機能: 画面遷移の検査条件を確認します。
    /// 引数: condition（bool）判定、message（string）失敗理由。
    /// 返り値: void。不成立の場合は InvalidOperationException を送出します。
    /// </summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
