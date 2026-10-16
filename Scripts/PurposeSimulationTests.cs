using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 目的を持つ四交通手段、切断、保存再開、個体上限、名称検証を描画なしで検証します。
/// </summary>
public partial class PurposeSimulationTests : Node
{
    private CollaborationSession? _session;
    private bool _network, _host, _finished, _requested;
    private double _elapsed, _finishAt;
    private string _localJson = "";
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    /// <summary>
    /// 機能: CPU の交通試験を実行し、成否を終了コードで返します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            string[] arguments = OS.GetCmdlineUserArgs();
            _network = arguments.Contains("--traffic-test-host") || arguments.Contains("--traffic-test-client");
            if (_network)
            {
                _host = arguments.Contains("--traffic-test-host");
                _session = new CollaborationSession { Name = "Collaboration" };
                AddChild(_session);
                CityState state = _host ? CreateCity() : new() { MapId = "purpose-test", CityName = "参加前の都市" };
                state.TimeScale = 8;
                _localJson = CityModel.Serialize(state);
                _session.Initialize(state, (_, _) => true);
                _session.ConfigureUrban();
                _session.ConfigureSimulation(value => value.Edits.Objects);
                string? portArgument = arguments.FirstOrDefault(value => value.StartsWith("--network-port=", StringComparison.Ordinal));
                int port = portArgument is null ? 24739 : int.Parse(portArgument[15..]);
                if (_host) Assert(_session.SetCityName("交通の共同都市"), _session.LastError);
                Assert(_host ? _session.Host(port) : _session.Join("127.0.0.1", port), _session.LastError);
                if (_host) GD.Print("BAYFRONT_TRAFFIC_HOST_READY");
                return;
            }
            TestModesAndSave();
            TestDisconnection();
            TestDirectionsAndMissingNetwork();
            TestTrafficSignals();
            TestWorkVisitAndHome();
            TestCommercialJobsAndVisits();
            GD.Print("BAYFRONT_PURPOSE_SIMULATION_PASS");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// 機能: 実通信で交通状態だけが同期され、参加者が所有権を得ないことを確認します。
    /// 引数: delta は double 型の実経過秒数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_network || _finished || _session is null) return;
        try
        {
            _elapsed += delta;
            Assert(_elapsed < 30, "交通通信試験の時間制限");
            if (_host)
            {
                _session.Tick((float)delta);
                if (_finishAt > 0 && _elapsed >= _finishAt) Complete("BAYFRONT_TRAFFIC_HOST_PASS");
                return;
            }
            foreach (UrbanAgent agent in _session.State.Simulation.Agents) _seen.Add(agent.Kind);
            if (_requested) return;
            if (_session.State.Simulation.LastProcessedTick < 60 || !_seen.IsSupersetOf(new[] { "person", "car", "train", "ship" })) return;
            Assert(_session.State.CityName == "交通の共同都市" && _session.State.Simulation.Population == 4, "名称と実住民が共有される");
            Assert(!_session.CanSaveState && !_session.SetCityName("横取り都市"), "参加者は保存と改名をできない");
            Assert(_session.State.Revision == 1, "交通同期が編集版を増やさない");
            Assert(_session.State.Simulation.Agents.Any(value => value.Route.Count > 0 && value.DestinationId == "work"), "個体の目的地と経路が同期される");
            _requested = true;
            RpcId(1, nameof(AcknowledgeTraffic));
        }
        catch (Exception error)
        {
            _finished = true;
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// 機能: 通信試験の完了通知を受けてホストを少し後に終了します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AcknowledgeTraffic()
    {
        if (_host && Multiplayer.GetRemoteSenderId() > 1)
        {
            RpcId(Multiplayer.GetRemoteSenderId(), nameof(FinishTrafficClient));
            _finishAt = _elapsed + 0.5;
        }
    }

    /// <summary>
    /// 機能: ホストの応答後に切断し、共有住民を破棄して参加前の都市へ復帰します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishTrafficClient()
    {
        if (_host || _session is null || Multiplayer.GetRemoteSenderId() != 1) return;
        try
        {
            _session.Disconnect();
            Assert(CityModel.Serialize(_session.State) == _localJson, "共有住民を残さず参加前の所有都市へ戻る");
            Complete("BAYFRONT_TRAFFIC_CLIENT_PASS");
        }
        catch (Exception error) { _finished = true; GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    /// <summary>
    /// 機能: 成功を記録し試験プロセスを終了します。
    /// 引数: marker は string 型の成功マーカーです。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Complete(string marker)
    {
        _finished = true;
        GD.Print(marker);
        GetTree().Quit();
    }

    /// <summary>
    /// 機能: 四つの明示交通網で住民が目的地へ進み、保存再開後も同じ状態へ到達することを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestModesAndSave()
    {
        CityState state = CreateCity();
        for (int tick = 0; tick < 60; tick++) Advance(state, 1);
        Assert(state.Simulation.Population == 4, "設計定員ではなく段階入居した実住民を数える");
        Assert(state.Simulation.Agents.Any(agent => agent.Kind == "car"), "目的地を持つ自動車が存在する");
        Assert(state.Simulation.Agents.Any(agent => agent.Kind == "train"), "駅と線路を使う列車が存在する");
        Assert(state.Simulation.Agents.Any(agent => agent.Kind == "ship"), "港と航路を使う船が存在する");
        foreach (UrbanAgent agent in state.Simulation.Agents.Where(value => value.Kind != "person"))
            Assert(agent.DestinationId == "work" && agent.PassengerIds.Count == 1 && agent.X > 0, "乗り物は住民の目的地と乗客を持つ");
        string json = CityModel.Serialize(state);
        Assert(CityModel.TryDeserialize(json, (_, _) => true, out CityState resumed, out string error), "保存した交通状態を復元: " + error);
        for (int tick = 0; tick < 120; tick++) { Advance(state, 1); Advance(resumed, 1); }
        Assert(UrbanSimulation.Serialize(state.Simulation) == UrbanSimulation.Serialize(resumed.Simulation), "保存再開で個体の経路・位置・実績が一致");
        Assert(state.Simulation.WorkArrivals > 0, "到着したときに就業実績が増える");
        Assert(state.Revision == 0, "交通計算で編集の版を増やさない");
        Assert(UrbanSimulation.Validate(state.Simulation, state.SimulationTick, out error), error);
        resumed.CityName = "任意の都市名";
        Assert(CityModel.ValidateState(resumed, (_, _) => true, out error), error);
        resumed.CityName = "不正\n都市";
        Assert(!CityModel.ValidateState(resumed, (_, _) => true, out _), "都市名の制御文字を拒否");
        resumed.CityName = "新しい都市";
        resumed.Simulation.Agents[0].X = float.NaN;
        Assert(!CityModel.ValidateState(resumed, (_, _) => true, out _), "保存の不正な個体座標を拒否");
    }

    /// <summary>
    /// 機能: 途中の道路を撤去すると移動を中断し、目的地へ瞬間移動しないことを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestDisconnection()
    {
        CityState state = CreateCity();
        for (int tick = 0; tick < 12; tick++) Advance(state, 1);
        UrbanAgent person = state.Simulation.Agents.Single(agent => agent.Id == state.Simulation.Residents.Single(value => value.PreferredMode == "walk").Id);
        float before = person.X;
        state.Edits.Objects.RemoveAll(value => value.Id == "road");
        state.Revision++;
        for (int tick = 0; tick < 8; tick++) Advance(state, 1);
        Assert(person.Status == "待機" && person.WaitingReason.Contains("道路"), "接続がない場合は理由付きで待機");
        Assert(Math.Abs(person.X - before) < 0.001, "道路撤去で目的地へ瞬間移動しない");
        Assert(state.Simulation.Population == 4, "交通断絶で住民を消滅させない");
    }

    /// <summary>
    /// 機能: 逆方向だけの道路と不在の鉄道・航路を利用して自由移動しないことを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestDirectionsAndMissingNetwork()
    {
        CityState state = CreateCity();
        state.Edits.Objects.RemoveAll(value => value.Id is "rail" or "water");
        UrbanObject road = state.Edits.Objects.Single(value => value.Id == "road");
        road.LanesForward = 0;
        for (int tick = 0; tick < 15; tick++) Advance(state, 1);
        Assert(!state.Simulation.Agents.Any(value => value.Kind is "car" or "train" or "ship"), "到達不能な交通手段の車両を捏造しない");
        foreach (UrbanResident resident in state.Simulation.Residents.Where(value => value.PreferredMode != "walk"))
            Assert(UrbanSimulation.FindAgent(state, resident.Id)?.WaitingReason.Length > 0, "到達不能の原因を個体が持つ");
        UrbanAgent walker = state.Simulation.Agents.Single(value => value.Id == state.Simulation.Residents.Single(resident => resident.PreferredMode == "walk").Id);
        Assert(walker.X > 0 && walker.Route.Count > 0, "一方通行の道路でも歩道は双方向に歩ける");
    }

    /// <summary>
    /// 機能: 信号現示が車両の交差点進入と実際の到着を制御することを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestTrafficSignals()
    {
        CityState state = new() { MapId = "purpose-test", Edits = new() { IncludeSource = false } };
        state.Edits.Objects.Add(new() { Id = "home", Kind = "building", Usage = "housing:car", X = 0, Z = 0.2f, WidthMeters = 5, DepthMeters = 7, Floors = 1 });
        state.Edits.Objects.Add(new() { Id = "work", Kind = "building", Usage = "office", X = 20, Z = 0.2f, WidthMeters = 10, DepthMeters = 10, Floors = 1 });
        state.Edits.Objects.Add(new() { Id = "left", Kind = "road", SpeedKph = 36, Path = new() { new(0, 0), new(10, 0) } });
        state.Edits.Objects.Add(new() { Id = "right", Kind = "road", SpeedKph = 36, Path = new() { new(10, 0), new(20, 0) } });
        UrbanObject junction = new()
        {
            Id = "junction", Kind = "intersection", X = 10, Z = 0, Control = "signal",
            ConnectedRoadIds = new() { "left", "right" },
            Phases = new() { new() { Name = "右側からの進入", DurationSeconds = 100, GreenRoadIds = new() { "right" } } }
        };
        state.Edits.Objects.Add(junction);
        for (int tick = 0; tick < 30; tick++) Advance(state, 1);
        UrbanAgent person = state.Simulation.Agents.Single(value => value.Kind == "person");
        Assert(person.X < 10 && person.WaitingReason.Contains("信号") && state.Simulation.WorkArrivals == 0, "赤信号は実車両を停止させて到着を遅らせる");
        junction.Phases[0].GreenRoadIds = new() { "left" };
        state.Revision++;
        for (int tick = 0; tick < 25; tick++) Advance(state, 1);
        Assert(state.Simulation.WorkArrivals > 0, "許可現示への変更後は交差点を通って職場へ到着する");
    }

    /// <summary>
    /// 機能: 固定職場、今回の目的地、滞在場所を区別して就業・余暇・帰宅を繰り返すことを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestWorkVisitAndHome()
    {
        CityState state = CreateLifeCity();
        bool sawWork = false, sawPark = false, returnedFromPark = false, reachedHomeAgain = false;
        for (int tick = 0; tick < 350; tick++)
        {
            Advance(state, 1);
            UrbanResident? resident = state.Simulation.Residents.FirstOrDefault();
            UrbanAgent? person = resident is null ? null : UrbanSimulation.FindAgent(state, resident.Id);
            if (resident is null || person is null) continue;
            Assert(resident.WorkId is "" or "work", "公園を固定職場へ割り当てない");
            if (person.Status == "就業中") sawWork |= resident.WorkId == "work" && resident.CurrentPlaceId == "work";
            if (person.Status == "滞在中") sawPark |= person.Purpose == "余暇" && resident.VisitDestinationId == "park" && resident.CurrentPlaceId == "park" && resident.WorkId == "work";
            if (person.Purpose == "帰宅" && person.OriginId == "park") returnedFromPark |= person.DestinationId == resident.HomeId && resident.WorkId == "work";
            if (returnedFromPark && resident.CurrentPlaceId == resident.HomeId && person.Status == "在宅") reachedHomeAgain = true;
        }
        Assert(sawWork && sawPark && returnedFromPark && reachedHomeAgain, "住居→固定職場→住居→公園→住居の実移動と滞在が成立する");
        Assert(state.Simulation.WorkArrivals > 0 && state.Simulation.VisitArrivals > 0, "就業到着と訪問到着を別々に数える");
        string json = CityModel.Serialize(state);
        Assert(CityModel.TryDeserialize(json, (_, _) => true, out CityState resumed, out string error), error);
        for (int tick = 0; tick < 90; tick++) { Advance(state, 1); Advance(resumed, 1); }
        Assert(UrbanSimulation.Serialize(state.Simulation) == UrbanSimulation.Serialize(resumed.Simulation), "生活目的と現在地を含めて保存再開後も一致する");

        CityState onlyPark = CreateLifeCity();
        onlyPark.Edits.Objects.RemoveAll(value => value.Id == "work");
        for (int tick = 0; tick < 80; tick++) Advance(onlyPark, 1);
        Assert(onlyPark.Simulation.Residents.All(value => value.WorkId.Length == 0 && value.VisitDestinationId == "park")
            && onlyPark.Simulation.WorkArrivals == 0 && onlyPark.Simulation.VisitArrivals > 0, "職場のない街でも公園へ余暇で訪問し、雇用を捏造しない");
    }

    /// <summary>
    /// 機能: 商業施設が従業員の職場と買物客の訪問先の双方になり、旧保存の公園職場を移行することを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestCommercialJobsAndVisits()
    {
        CityState state = CreateLifeCity();
        state.Edits.Objects.RemoveAll(value => value.Id is "work" or "park");
        state.Edits.Objects.Add(new() { Id = "shop", Kind = "building", Usage = "commercial", X = 5, Z = 0.2f, WidthMeters = 10, DepthMeters = 10, Floors = 1 });
        bool workingInShop = false, shopping = false;
        for (int tick = 0; tick < 350; tick++)
        {
            Advance(state, 1);
            UrbanResident? resident = state.Simulation.Residents.FirstOrDefault();
            UrbanAgent? person = resident is null ? null : UrbanSimulation.FindAgent(state, resident.Id);
            if (resident is null || person is null) continue;
            workingInShop |= resident.WorkId == "shop" && person.Purpose == "就業";
            shopping |= person.Purpose == "買物" && person.DestinationId == "shop" && resident.VisitDestinationId == "shop";
        }
        Assert(workingInShop && shopping, "商業施設は固定職場にも買物目的地にもなる");

        CityState oldState = CreateLifeCity();
        for (int tick = 0; tick < 2; tick++) Advance(oldState, 1);
        oldState.Simulation.Residents[0].WorkId = "park";
        oldState.Simulation.Residents[0].CurrentPlaceId = "";
        string json = CityModel.Serialize(oldState);
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var savedResident = document["Simulation"]!["Residents"]![0]!;
        savedResident.AsObject().Remove("VisitDestinationId");
        savedResident.AsObject().Remove("CurrentPlaceId");
        savedResident.AsObject().Remove("NextActivity");
        document["Simulation"]!.AsObject().Remove("VisitArrivals");
        Assert(CityModel.TryDeserialize(document.ToJsonString(), (_, _) => true, out CityState restored, out string error), error);
        Assert(restored.Simulation.VisitArrivals == 0 && restored.Simulation.Residents[0].NextActivity == "work", "旧保存は追加項目の既定値で読み込める");
        Advance(restored, 1);
        Assert(restored.Simulation.Residents[0].WorkId != "park" && restored.Simulation.Residents[0].VisitDestinationId == "park", "旧保存の公園職場を訪問先へ移行する");
    }

    /// <summary>
    /// 機能: 固定職場と屋外公園を歩道で結ぶ一人用の生活試験都市を生成します。
    /// 引数: なし。
    /// 返り値: CityState 型の試験都市です。
    /// </summary>
    private static CityState CreateLifeCity()
    {
        CityState state = new() { MapId = "purpose-test", Edits = new() { IncludeSource = false } };
        state.Edits.Objects.Add(new() { Id = "home", Kind = "building", Usage = "housing:walk", X = 0, Z = 0.2f, WidthMeters = 5, DepthMeters = 7, Floors = 1 });
        state.Edits.Objects.Add(new() { Id = "work", Kind = "building", Usage = "office", X = 5, Z = 0.2f, WidthMeters = 10, DepthMeters = 10, Floors = 1 });
        state.Edits.Objects.Add(new() { Id = "park", Kind = "surface", Usage = "park", X = 2, Z = 0.2f, WidthMeters = 10, DepthMeters = 10 });
        state.Edits.Objects.Add(new() { Id = "road", Kind = "road", Path = new() { new(0, 0), new(5, 0) } });
        return state;
    }

    /// <summary>
    /// 機能: 四種類の通勤手段を比較する小さな明示設計を生成します。
    /// 引数: なし。
    /// 返り値: CityState 型の試験都市です。
    /// </summary>
    public static CityState CreateCity()
    {
        CityState state = new() { MapId = "purpose-test", Edits = new() { IncludeSource = false } };
        int index = 0;
        foreach (string mode in new[] { "walk", "car", "train", "ship" })
            state.Edits.Objects.Add(new() { Id = "home-" + mode, Kind = "building", Name = "住宅", Usage = "housing:" + mode, X = 0, Z = 0.5f + index++ * 0.2f, WidthMeters = 5, DepthMeters = 7, Floors = 1 });
        state.Edits.Objects.Add(new() { Id = "work", Kind = "building", Name = "職場", Usage = "office", X = 100, Z = 1, WidthMeters = 10, DepthMeters = 10, Floors = 2 });
        foreach (var (id, usage, z, speed) in new[] { ("road", "road", 0f, 40f), ("rail", "rail", 2f, 80f), ("water", "waterway", -2f, 20f) })
            state.Edits.Objects.Add(new() { Id = id, Kind = "road", Usage = usage, Path = new() { new(0, z), new(100, z) }, WidthMeters = 10, SidewalkWidthMeters = 2, SpeedKph = speed });
        foreach (var (prefix, usage, z) in new[] { ("station", "station", 2f), ("port", "port", -2f) })
            foreach (int x in new[] { 2, 98 })
                state.Edits.Objects.Add(new() { Id = prefix + x, Kind = "building", Usage = usage, X = x, Z = z, WidthMeters = 10, DepthMeters = 10, Floors = 1 });
        return state;
    }

    /// <summary>
    /// 機能: 保存時計を進めてから固定秒の交通計算を適用します。
    /// 引数: state は CityState 型、seconds は float 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void Advance(CityState state, float seconds)
    {
        CityModel.AdvanceSimulationClock(state, seconds);
        UrbanSimulation.Step(state, state.Edits.Objects, seconds);
    }

    /// <summary>
    /// 機能: 条件を満たさない試験を例外として報告します。
    /// 引数: condition は bool 型、message は string 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
