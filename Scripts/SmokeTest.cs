using Godot;
using System;
using System.Linq;
using FileAccess = Godot.FileAccess;

namespace Bayfront;

/// <summary>
/// 描画操作に依存せず、都市モデル、生成アセット、実際の共同編集通信を検証します。
/// </summary>
public partial class SmokeTest : Node
{
    private const string SeedId = "network-seed";
    private const string ClientId = "network-client";
    private const string RejectedId = "network-rejected";
    private const string LocalId = "network-local";
    private const string DedicatedEditId = "dedicated-persisted-housing";
    private CollaborationSession? _session;
    private bool _host;
    private bool _finished;
    private bool _hostSawClientObject;
    private bool _serverRejected;
    private bool _urbanRejected;
    private bool _legacyDenied;
    private int _phase;
    private double _elapsed;
    private double _phaseStarted;
    private long _snapshotRevision;
    private string _disconnectedStateJson = "";
    private string _localStateJson = "";
    private int _networkPort;
    private bool _dedicated;
    private bool _mapMismatch;
    private bool _dedicatedCheck;
    private bool _dedicatedSnapshot;
    private BayfrontMap? _dedicatedMap;
    private UrbanCatalog? _dedicatedCatalog;
    private string _dedicatedSourceId = "";
    private UrbanObject? _dedicatedCandidate;
    private double _dedicatedFunds;
    private const string UrbanNetworkId = "network-urban-building";
    private const string UrbanSignalId = "network-urban-signal";
    private const string UrbanTerrainId = "network-urban-terrain";

    public static bool IsRequested => OS.GetCmdlineUserArgs().Any(argument =>
        argument is "--smoke-test" or "--network-test-host" or "--network-test-client"
            or "--network-test-map-mismatch" or "--dedicated-test-add" or "--dedicated-test-check");

    /**
     * 機能: 試験専用ノードをシーンへ追加します。
     * 引数: root は Node 型の実行中のシーンルートです。
     * 返り値: なし（void 型）。
     */
    public static void Run(Node root)
    {
        root.AddChild(new SmokeTest { Name = "SmokeTest", ProcessMode = ProcessModeEnum.Always });
    }

    /**
     * 機能: コマンドラインで指定された試験を開始します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    public override void _Ready()
    {
        try
        {
            string[] arguments = OS.GetCmdlineUserArgs();
            if (arguments.Contains("--smoke-test"))
            {
                TestModel();
                TestUrbanModel();
                TestUrbanHistory();
                TestMapAndAssets();
                Finish("BAYFRONT_SMOKE_PASS", 0);
                return;
            }

            // ----------------------------------------

            if (arguments.Contains("--dedicated-test-add") || arguments.Contains("--dedicated-test-check"))
            {
                _dedicated = true;
                _dedicatedCheck = arguments.Contains("--dedicated-test-check");
                Callable.From(InitializeDedicatedClient).CallDeferred();
                return;
            }

            _host = arguments.Contains("--network-test-host");
            _mapMismatch = arguments.Contains("--network-test-map-mismatch");
            _session = new CollaborationSession { Name = "Collaboration" };
            AddChild(_session);
            var initial = new CityState();
            if (_host) AddSnapshotPayload(initial);
            if (_mapMismatch) initial.MapId = "network-different-map";
            if (!_host) Assert(CityModel.TryAdd(initial, CreateHousing(LocalId, 80), AlwaysLand, out string localError), localError);
            _localStateJson = CityModel.Serialize(initial);
            _session.Initialize(initial, AlwaysLand);
            _session.StateChanged += HandleNetworkState;
            _session.StatusChanged += HandleNetworkStatus;
            int port = ReadPort(arguments);
            _networkPort = port;
            if (_host)
            {
                Assert(_session.Host(port), $"ホスト開始: {_session.LastError}");
                Assert(_session.CanSaveState && _session.CanHostState && _session.CanReplaceState && !_session.IsGuest, "ホストの保存と都市管理権限");
                Assert(_session.Submit(CreateHousing(SeedId, 0)), $"初期配置: {_session.LastError}");
                Assert(_session.State.Objects.Any(item => item.Id == SeedId), "ホストの初期配置反映");
                _snapshotRevision = _session.State.Revision;
                GD.Print($"BAYFRONT_NETWORK_HOST_READY ポート={port}");
            }
            else
            {
                Assert(_session.Join("127.0.0.1", port), $"接続開始: {_session.LastError}");
                Assert(_session.IsGuest && !_session.CanSaveState && !_session.CanHostState && !_session.CanReplaceState, "接続中の参加者権限制限");
                Assert(!_session.Host(NextPort(port)), "接続中の参加者からホスト開始を拒否すること");
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /**
     * 機能: 複数プロセス間の通信試験を段階的に進めます。
     * 引数: delta は double 型の前回更新からの秒数です。
     * 返り値: なし（void 型）。
     */
    public override void _Process(double delta)
    {
        if (_finished || _session is null) return;
        try
        {
            _elapsed += delta;
            Assert(_elapsed < 35, "通信試験が制限時間内に完了すること");
            if (_mapMismatch)
            {
                if (!_session.IsGuest && _session.LastError.Contains("地図", StringComparison.Ordinal))
                {
                    Assert(!_session.IsConnected && _session.CanSaveState && _session.CanHostState, "地図が異なるホストとの接続を終了すること");
                    Assert(CityModel.Serialize(_session.State) == _localStateJson, "地図不一致でも参加前の自都市を完全復帰すること");
                    Assert(!_session.State.Objects.Any(item => item.Id == SeedId), "別地図の共有都市が参加者に残らないこと");
                    Finish("BAYFRONT_NETWORK_MAP_MISMATCH_PASS", 0);
                }
                return;
            }
            if (_dedicated)
            {
                AdvanceDedicatedClient();
                return;
            }
            if (_host)
            {
                if (_hostSawClientObject && !_session.State.Objects.Any(item => item.Id == ClientId)
                    && _session.State.Revision >= _snapshotRevision + 2
                    && !_session.State.Edits.IncludeSource && _session.State.Edits.Terrain.Any(item => item.Id == UrbanTerrainId))
                {
                    Assert(_session.State.Objects.Count == 1, "ホスト側の撤去後の地物数");
                    Assert(_session.State.Objects[0].Id == SeedId, "ホスト側の初期配置の保持");
                    Assert(CityModel.ValidateState(_session.State, AlwaysLand, out string error), error);
                    if (_phase == 0)
                    {
                        Assert(_session.SetTimeScale(0), _session.LastError);
                        _phase = 1;
                        _phaseStarted = _elapsed;
                    }
                    if (_phase == 1 && _elapsed - _phaseStarted >= 0.75)
                    {
                        _session.Disconnect();
                        Assert(!_session.IsConnected && _session.CanReplaceState, "ホスト自身の切断後のソロ復帰");
                        GD.Print("BAYFRONT_NETWORK_HOST_DISCONNECTED");
                        _phase = 2;
                        _phaseStarted = _elapsed;
                    }
                    if (_phase == 2 && _elapsed - _phaseStarted >= 0.5) Finish("BAYFRONT_NETWORK_HOST_PASS", 0);
                }
                return;
            }

            // ----------------------------------------

            AdvanceClientTest();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /**
     * 機能: 建設検証、資金、日次更新、保存と復元の整合性を検証します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private static void TestModel()
    {
        var state = new CityState();
        var housing = CreateHousing("model-housing", 0);
        double initialFunds = state.Funds;
        Assert(CityModel.TryAdd(state, housing, AlwaysLand, out string error), error);
        Assert(state.Revision == 1 && state.Objects.Count == 1, "追加時の地物数とリビジョン");
        Assert(state.Funds == initialFunds - CityModel.GetCost(housing), "建設費の控除");
        housing.X = 100;
        Assert(state.Objects[0].X == 0, "入力オブジェクトを後で変更しても確定状態が変化しないこと");

        // ----------------------------------------

        string beforeRejected = CityModel.Serialize(state);
        Assert(!CityModel.TryAdd(state, CreateHousing("model-housing", 40), AlwaysLand, out error), "識別子の重複拒否");
        Assert(!CityModel.TryAdd(state, CreateHousing("model-overlap", 1), AlwaysLand, out error), "建築物の重なり拒否");
        Assert(!CityModel.TryAdd(state, CreateHousing("model-water", 40), NeverLand, out error), "水域への配置拒否");
        Assert(!CityModel.TryAdd(state, CreateHousing("model-nan", float.NaN), AlwaysLand, out error), "非有限座標の拒否");
        Assert(!CityModel.TryAdd(state, new CityObject { Id = "model-road", Kind = "road" }, AlwaysLand, out error), "長さのない道路の拒否");
        Assert(beforeRejected == CityModel.Serialize(state), "拒否された操作で都市が変化しないこと");
        var noFunds = new CityState { Funds = 0 };
        Assert(!CityModel.TryAdd(noFunds, CreateHousing("model-no-funds", 40), AlwaysLand, out error), "資金不足の拒否");

        // ----------------------------------------

        var office = new CityObject { Id = "model-office", Kind = "office", X = 40, District = "豊洲", Variant = 2 };
        Assert(CityModel.TryAdd(state, office, AlwaysLand, out error), error);
        CityStatistics statistics = CityModel.CalculateStatistics(state);
        Assert(statistics.Population > 0 && statistics.Jobs > 0, "住宅と業務施設の都市指標");
        double previousFunds = state.Funds;
        long previousRevision = state.Revision;
        int previousDay = state.Day;
        CityModel.AdvanceDay(state);
        Assert(state.Day == previousDay + 1 && state.Revision == previousRevision + 1, "日次更新の日付とリビジョン");
        Assert(state.Funds == previousFunds + statistics.Balance, "日次収支の資金反映");
        CityState clone = state.DeepClone();
        clone.Objects[0].X = -100;
        Assert(state.Objects[0].X == 0, "都市の深い複製");

        // ----------------------------------------

        string path = $"user://湾岸自動試験-{OS.GetProcessId()}.json";
        try
        {
            string json = CityModel.Serialize(state);
            using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
            {
                Assert(file is not null, "試験用保存ファイルの作成");
                file!.StoreString(json);
            }
            using var savedFile = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            Assert(savedFile is not null, "試験用保存ファイルの読み込み");
            Assert(CityModel.TryDeserialize(savedFile!.GetAsText(), AlwaysLand, out CityState restored, out error), error);
            Assert(CityModel.Serialize(restored) == json, "保存と復元で全都市状態が一致すること");
            Assert(!CityModel.TryDeserialize("{破損データ", AlwaysLand, out _, out _), "壊れた保存データの拒否");
            var invalid = state.DeepClone();
            invalid.Objects.Add(invalid.Objects[0].DeepClone());
            Assert(!CityModel.TryDeserialize(CityModel.Serialize(invalid), AlwaysLand, out _, out _), "重複地物を含む保存データの拒否");
        }
        finally
        {
            if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }

        // ----------------------------------------

        const string legacyJson = "{\"Objects\":[],\"Revision\":0,\"Funds\":25000000,\"Day\":1}";
        Assert(CityModel.TryDeserialize(legacyJson, AlwaysLand, out CityState legacy, out error), error);
        Assert(legacy.MapId == CityModel.LegacyMapId && legacy.MapVersion == 1, "地図情報のない旧保存は簡略地図として保持すること");
        Assert(!CityModel.ValidateMapIdentity(legacy, "real-bayfront", 1, out _), "旧保存を実測地図へ混在させないこと");
        var versioned = new CityState { MapId = "real-bayfront", MapVersion = 2 };
        Assert(CityModel.TryDeserialize(CityModel.Serialize(versioned), AlwaysLand, out CityState mapRestored, out error), error);
        Assert(mapRestored.MapId == versioned.MapId && mapRestored.MapVersion == 2, "地図識別子と版の保存復元");
        Assert(versioned.DeepClone().MapId == versioned.MapId && versioned.DeepClone().MapVersion == versioned.MapVersion, "地図識別子と版の複製");
        Assert(!CityModel.ValidateMapIdentity(versioned, "real-bayfront", 1, out _), "異なる地図バージョンの拒否");
        versioned.MapVersion = 0;
        Assert(!CityModel.ValidateState(versioned, AlwaysLand, out _), "不正な地図バージョンの拒否");

        // ----------------------------------------

        double beforeRemoval = state.Funds;
        double refund = Math.Floor(CityModel.GetCost(state.Objects[0]) * 0.25);
        long beforeRemovalRevision = state.Revision;
        Assert(CityModel.TryRemove(state, "model-housing", out error), error);
        Assert(state.Revision == beforeRemovalRevision + 1 && state.Funds == beforeRemoval + refund, "撤去時の返還とリビジョン");
        Assert(!CityModel.TryRemove(state, "model-missing", out error), "存在しない撤去対象の拒否");
        Assert(CityModel.ValidateState(state, AlwaysLand, out error), error);
        GD.Print("都市モデル・保存復元試験: 成功");
    }

    /**
     * 機能: 四地区の種状態と全建築種別の立体アセットを検証します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void TestMapAndAssets()
    {
        BayfrontMap map = BayfrontMap.Load();
        string[] names = { "台場", "有明", "東雲", "豊洲" };
        Assert(map.Districts.Count == names.Length, "湾岸四地区の読み込み");
        foreach (string name in names)
            Assert(map.Districts.Any(district => district.Name == name), $"地区「{name}」の存在");
        foreach (MapDistrict district in map.Districts)
        {
            Assert(district.Polygon.Length >= 3, $"{district.Name} の陸地ポリゴン");
            Assert(map.IsLand(district.Center.X, district.Center.Y), $"{district.Name} の中心の陸地判定");
            Assert(map.DistrictAt(district.Center.X, district.Center.Y) == district.Name, $"{district.Name} の地区判定");
            VerifyMesh(AssetFactory.CreateLand(district.Polygon, district.Color), $"{district.Name} の地形");
        }
        Assert(!map.IsLand(-100_000, -100_000), "地図外の陸地判定");
        CityState seed = map.CreateSeedState();
        Assert(map.IsRealWorld ? seed.Objects.Count == 0 : seed.Objects.Count > 20,
            map.IsRealWorld ? "実地物は編集状態へ混入せず固定データとして保持すること" : "初期都市の地物生成");
        Assert(CityModel.ValidateMapIdentity(seed, map.MapId, map.MapVersion, out string identityError), identityError);
        Assert(CityModel.ValidateState(seed, map.IsLand, out string error, map.CanPlace), $"初期都市の整合性: {error}");
        Assert(CityModel.TryDeserialize(CityModel.Serialize(seed), map.IsLand, out CityState restored, out error, map.CanPlace), error);
        Assert(restored.Objects.Count == seed.Objects.Count, "初期都市の保存復元");
        TestReservedLand(map);
        TestRealWorldCpuData(map, seed);
        TestMapIsolation(map, seed);
        MeasureSnapshots(seed, map);

        // ----------------------------------------

        foreach (string kind in new[] { "housing", "office", "park", "civic" })
            for (int variant = 0; variant < 8; variant++)
                VerifyNode(AssetFactory.CreateBuilding(kind, variant), $"{kind} 外観 {variant}");
        foreach (MapLandmark landmark in map.Landmarks)
            VerifyNode(AssetFactory.CreateLandmark(landmark.Kind), landmark.Name);
        VerifyMesh(AssetFactory.CreateRoad(new Vector3(-10, 0, 0), new Vector3(10, 0, 0)), "道路");
        GD.Print($"湾岸地図・アセット試験: 成功（初期地物 {seed.Objects.Count} 件）");
    }

    /**
     * 機能: 実建築の足跡と標高を描画なしで参照でき、既存建築を上書きする建設が拒否されることを検証します。
     * 引数: map は BayfrontMap 型の地図、seed は CityState 型の初期編集状態です。
     * 返り値: なし（void 型）。簡略地図の場合は実データ検証を省略します。
     */
    private static void TestRealWorldCpuData(BayfrontMap map, CityState seed)
    {
        if (!map.IsRealWorld) return;
        EditableTerrainTests.Run(map.RealWorld!);
        UrbanCatalog catalog = new(map.RealWorld!);
        UrbanGeometryTest.Run(catalog);
        var invalidSources = catalog.Objects.Select(item => (Item: item, Valid: UrbanModel.ValidateObject(item, out _)))
            .Where(result => !result.Valid).Select(result => result.Item).ToList();
        foreach (UrbanObject item in invalidSources.Take(12))
        {
            UrbanModel.ValidateObject(item, out string editError);
            GD.Print($"原典編集定義エラー {item.Id} {item.Kind}: {editError}");
        }
        Assert(invalidSources.Count == 0, $"全原典地物が編集可能であること。不正な定義は {invalidSources.Count} 件です。");
        TestSourceRemoval(catalog);
        GD.Print($"全原典の編集定義監査: 成功（{catalog.Objects.Count:N0} 件）");
        Assert(map.RealWorld is not null && map.RealWorld.Buildings.Count > 500, "高密度な実建築の足跡データを読み込めること");
        Assert(map.RealWorld!.Roads.Count > 0, "実際の道路中心線を読み込めること");
        var building = map.RealWorld.Buildings.First(item => item.Footprint.Length >= 3);
        Vector2 corner = building.Footprint[0];
        Assert(float.IsFinite(map.GroundHeight(corner.X, corner.Y)), "CPUで地表標高を取得できること");
        var housing = new CityObject
        {
            Id = "real-overlap-building", Kind = "housing", X = corner.X, Z = corner.Y,
            District = map.DistrictAt(corner.X, corner.Y)
        };
        Assert(!map.CanPlace(housing), "実建築の足跡と重なる住宅を拒否すること");
        Assert(!CityModel.TryAdd(seed.DeepClone(), housing, map.IsLand, out _, map.CanPlace), "実建築の上へ編集住宅が確定されないこと");
        var road = new CityObject
        {
            Id = "real-overlap-road", Kind = "road", X = corner.X - 20, Z = corner.Y,
            EndX = corner.X + 20, EndZ = corner.Y
        };
        Assert(!map.CanPlace(road), "実建築の足跡を横断する道路を拒否すること");
        var existingRoad = map.RealWorld.Roads.First(item => item.Points.Length >= 2 && item.Level == 0);
        Vector2 roadPoint = (existingRoad.Points[0] + existingRoad.Points[1]) * 0.5f;
        housing.X = roadPoint.X;
        housing.Z = roadPoint.Y;
        Assert(!map.CanPlace(housing), "既存道路の占有領域と重なる住宅を拒否すること");
        Assert(seed.Objects.Count == 0, "固定の実建築を編集状態へ複製しないこと");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int queryCount = 1_000;
        for (int index = 0; index < queryCount; index++)
        {
            var sample = map.RealWorld.Buildings[index * 1543 % map.RealWorld.Buildings.Count];
            housing.X = sample.Footprint[0].X;
            housing.Z = sample.Footprint[0].Y;
            Assert(!map.CanPlace(housing), "各地区の固定建築との重なりをCPUで拒否すること");
        }
        watch.Stop();
        GD.Print($"実世界CPUデータ試験: 成功（固定建築 {map.RealWorld.Buildings.Count:N0} 件、道路 {map.RealWorld.Roads.Count:N0} 件、初期編集状態 {System.Text.Encoding.UTF8.GetByteCount(CityModel.Serialize(seed)):N0} バイト）");
        GD.Print($"固定地物との重なり判定: {queryCount:N0} 回 / 平均 {watch.Elapsed.TotalMilliseconds / queryCount:F4} ミリ秒");
    }

    /// <summary>
    /// 機能: 原典道路の直接撤去要求でも全都市の交差点と信号参照が解除され、取り消しで復元することを検証します。
    /// 引数: catalog は UrbanCatalog 型の原典一覧です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestSourceRemoval(UrbanCatalog catalog)
    {
        UrbanObject junction = catalog.Intersections.First(item => item.ConnectedRoadIds.Count >= 2);
        string roadId = junction.ConnectedRoadIds[0];
        CollaborationSession session = new();
        try
        {
            session.Initialize(new CityState { MapId = "real-bayfront" }, AlwaysLand);
            session.ConfigureUrban(catalog.SourceExists, catalog.SourceObject, catalog.GetRemovalChanges);
            Assert(session.SubmitEdit(new() { Operation = "delete", ExpectedRevision = 0, ObjectId = roadId }), session.LastError);
            Assert(catalog.Find(roadId, session.State) is null, "原典道路も最小の撤去要求だけで削除できること");
            foreach (UrbanObject item in catalog.GetEffective(session.State))
                Assert(!item.ConnectedRoadIds.Contains(roadId) && item.ApproachRoadId != roadId
                    && !item.Phases.Any(phase => phase.GreenRoadIds.Contains(roadId)), "直接撤去でも全原典の道路参照を解除すること");
            Assert(session.State.Revision == 1 && session.UndoEdit() && catalog.Find(roadId, session.State) is not null,
                "原典道路と依存関係の撤去を一回で取り消せること");
            Assert(catalog.Find(junction.Id, session.State)!.ConnectedRoadIds.Contains(roadId), "取り消しで原典交差点の接続も復元すること");
            UrbanObject source = catalog.SourceObject(roadId)!.DeepClone();
            source.SourceId = "";
            Assert(!session.SubmitEdit(new() { ExpectedRevision = session.State.Revision, Object = source }), "原典IDを新規地物として偽装できないこと");
            source.SourceId = source.Id;
            source.Deleted = true;
            Assert(session.SubmitEdit(new() { ExpectedRevision = session.State.Revision, Object = source })
                && !catalog.Find(junction.Id, session.State)!.ConnectedRoadIds.Contains(roadId), "削除フラグを直接送る経路でも原典参照を解除すること");
        }
        finally { session.Free(); }
    }

    /**
     * 機能: 異なる地図の保存状態をセッションへ適用しても、現在の都市が変わらないことを検証します。
     * 引数: map は BayfrontMap 型の地形、seed は CityState 型の同じ地図の初期都市です。
     * 返り値: なし（void 型）。
     */
    private void TestMapIsolation(BayfrontMap map, CityState seed)
    {
        var session = new CollaborationSession { Name = "MapIsolationSession" };
        AddChild(session);
        try
        {
            session.Initialize(seed, map.IsLand, map.CanPlace);
            string before = CityModel.Serialize(session.State);
            CityState different = seed.DeepClone();
            different.MapId = "different-bayfront-map";
            Assert(!session.ReplaceState(different), "異なる地図の保存データの読込拒否");
            different = seed.DeepClone();
            different.MapVersion++;
            Assert(!session.ReplaceState(different), "異なる地図の版の保存データの読込拒否");
            Assert(CityModel.Serialize(session.State) == before, "地図不一致の拒否後に都市が変更されないこと");
        }
        finally
        {
            RemoveChild(session);
            session.Free();
        }
    }

    /**
     * 機能: ランドマーク予約地への建物、横断道路、保存データ混入を拒否することを確認します。
     * 引数: map は BayfrontMap 型の地形と予約地定義です。
     * 返り値: なし（void 型）。
     */
    private void TestReservedLand(BayfrontMap map)
    {
        var session = new CollaborationSession { Name = "ReservationSession" };
        AddChild(session);
        try
        {
            session.Initialize(new CityState(), map.IsLand, map.CanPlace);
            foreach (MapLandmark landmark in map.Landmarks)
            {
                var housing = new CityObject
                {
                    Id = "reserved-building", Kind = "housing", X = landmark.Position.X,
                    Z = landmark.Position.Y, District = map.DistrictAt(landmark.Position.X, landmark.Position.Y)
                };
                Assert(map.IsLand(housing.X, housing.Z), $"{landmark.Name} の予約地が陸地にあること");
                Assert(!map.CanPlace(housing), $"{landmark.Name} と重なる住宅の予約地判定");
                Assert(!CityModel.TryAdd(new CityState(), housing, map.IsLand, out _, map.CanPlace), $"{landmark.Name} 予約地での建設拒否");
                Assert(!session.Submit(housing), $"{landmark.Name} 予約地のセッション経由建設拒否");
                Assert(session.State.Objects.Count == 0 && session.State.Revision == 0, "予約地で拒否されたセッション状態の保持");
                var road = new CityObject
                {
                    Id = "reserved-road", Kind = "road", X = landmark.Position.X - 50,
                    Z = landmark.Position.Y, EndX = landmark.Position.X + 50, EndZ = landmark.Position.Y
                };
                Assert(!map.CanPlace(road), $"{landmark.Name} の予約地を横断する道路の拒否");
                var invalid = new CityState();
                invalid.Objects.Add(housing);
                string json = CityModel.Serialize(invalid);
                Assert(CityModel.TryDeserialize(json, map.IsLand, out _, out _), "予約地以外の保存形式と座標が有効であること");
                Assert(!CityModel.TryDeserialize(json, map.IsLand, out _, out _, map.CanPlace), $"{landmark.Name} 予約地へ混入した保存地物の拒否");
            }
        }
        finally
        {
            RemoveChild(session);
            session.Free();
        }
        GD.Print("ランドマーク予約地試験: 成功");
    }

    /**
     * 機能: 初期都市の共有データ容量と保存、復元、全体検証の平均処理時間を計測します。
     * 引数: seed は CityState 型の初期都市、map は BayfrontMap 型の検証条件です。
     * 返り値: なし（void 型）。
     */
    private static void MeasureSnapshots(CityState seed, BayfrontMap map)
    {
        string json = CityModel.Serialize(seed);
        int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        for (int index = 0; index < 3; index++)
            Assert(CityModel.TryDeserialize(CityModel.Serialize(seed), map.IsLand, out _, out _, map.CanPlace), "状態共有の計測前の準備");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int count = 100;
        for (int index = 0; index < count; index++)
            Assert(CityModel.TryDeserialize(CityModel.Serialize(seed), map.IsLand, out _, out _, map.CanPlace), "状態共有の反復検証");
        watch.Stop();
        GD.Print($"状態共有の計測: 地物 {seed.Objects.Count} 件 / UTF-8 {bytes:N0} バイト / 保存・復元検証 平均 {watch.Elapsed.TotalMilliseconds / count:F3} ミリ秒（{count} 回）");
    }

    /**
     * 機能: 生成した立体ノードをシーンへ追加して構成を確認し、破棄します。
     * 引数: node は Node3D 型の生成結果、description は string 型の試験名です。
     * 返り値: なし（void 型）。
     */
    private void VerifyNode(Node3D node, string description)
    {
        Assert(node is not null && GodotObject.IsInstanceValid(node), $"{description} の生成");
        AddChild(node!);
        Assert(node!.GetChildCount() > 0, $"{description} のメッシュ構成");
        RemoveChild(node);
        node.Free();
    }

    /**
     * 機能: 生成したメッシュのサーフェスを確認して破棄します。
     * 引数: node は MeshInstance3D 型の生成結果、description は string 型の試験名です。
     * 返り値: なし（void 型）。
     */
    private void VerifyMesh(MeshInstance3D node, string description)
    {
        Assert(node is not null && node.Mesh is not null && node.Mesh.GetSurfaceCount() > 0, $"{description} のメッシュ生成");
        AddChild(node!);
        RemoveChild(node!);
        node!.Free();
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 任意形状、全地物、地形、現示、競合拒否、保存復元を描画なしで検証します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void TestUrbanModel()
    {
        CityState state = new() { MapId = "real-bayfront" };
        UrbanObject building = new()
        {
            Id = "source-building-1", SourceId = "source-building-1", Kind = "building", Name = "形状と階数の試験", Usage = "住宅",
            Floors = 7, FloorHeightMeters = 3.5f, WidthMeters = 30, DepthMeters = 20, ColorHex = "#8ea6b4", Roof = "gable",
            Footprint = new() { new(-1, -1), new(2, -1), new(2, 1), new(-1, 1) }
        };
        Assert(UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = building }, out string error, id => id == building.Id), error);
        Assert(state.Edits.Objects.Count == 1 && state.Revision == 1 && state.Edits.Objects[0].Floors == 7, "原典建物の階数と輪郭を変更できること");
        Assert(Math.Abs(UrbanModel.FloorAreaSquareMeters(building) - 4200) < 0.1, "輪郭面積と階数から設計床面積を計算すること");
        UrbanStatistics statistics = UrbanModel.CalculateStatistics(state.Edits.Objects);
        Assert(statistics.ResidentCapacity == 140 && statistics.JobCapacity == 0 && statistics.UnknownFloorCount == 0, "床面積と住宅用途から設計定員を計算すること");
        building.Footprint[0].X = 99;
        Assert(state.Edits.Objects[0].Footprint[0].X == -1, "地物の輪郭が入力参照から独立していること");

        // ----------------------------------------

        string before = CityModel.Serialize(state);
        UrbanObject crossing = new() { Id = "self-crossing", Footprint = new() { new(0, 0), new(2, 2), new(0, 2), new(2, 0) } };
        Assert(!UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = crossing }, out _), "自己交差した建物輪郭を拒否すること");
        Assert(!UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision - 1, Object = new() { Id = "stale-edit" } }, out _), "古い版の編集を拒否すること");
        Assert(!UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = new() { Id = "nan-edit", WidthMeters = float.NaN } }, out _), "非有限寸法を拒否すること");
        Assert(!UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = new() { Id = "unknown-source", SourceId = "unknown-source" } }, out _, _ => false), "原典に存在しない地物IDを拒否すること");
        Assert(before == CityModel.Serialize(state), "競合と不正な形状の拒否後に都市が変わらないこと");
        UrbanObject road = new() { Id = "road-test", Kind = "road", LanesForward = 2, LanesBackward = 1, LaneWidthMeters = 3.25f,
            SidewalkWidthMeters = 1.8f, SpeedKph = 50, Level = 1, Path = new() { new(0, 0), new(10, 4), new(12, 12) } };
        Assert(UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = road }, out error), error);
        UrbanObject junction = new() { Id = "junction-test", Kind = "intersection", Control = "signal", ConnectedRoadIds = new() { road.Id },
            YellowSeconds = 3, AllRedSeconds = 2, Phases = new() { new() { Name = "湾岸通り", DurationSeconds = 20, GreenRoadIds = new() { road.Id } },
                new() { Name = "交差方向", DurationSeconds = 10, GreenRoadIds = new() { "road-other" } } } };
        Assert(UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = junction }, out error), error);
        UrbanObject signal = new() { Id = "signal-test", Kind = "signal", ParentId = junction.Id, ApproachRoadId = road.Id, Control = "signal" };
        Assert(UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = signal }, out error), error);
        Assert(UrbanModel.SignalState(signal, junction, 5).Aspect == "green", "進入道路の青信号を計算すること");
        Assert(UrbanModel.SignalState(signal, junction, 21).Aspect == "yellow", "設定した黄時間を計算すること");
        Assert(UrbanModel.SignalState(signal, junction, 24).Aspect == "red", "全赤時間を計算すること");
        Assert(UrbanModel.SignalState(signal, junction, 30).Aspect == "red", "別方向の青現示では赤信号になること");
        Assert(UrbanModel.SignalState(signal, junction, 45).Aspect == "green", "信号周期を繰り返すこと");
        Assert(UrbanModel.SignalState(signal, null, 5).Aspect == "off", "未設定の信号は固定周期を捏造せず無灯であること");

        // ----------------------------------------

        foreach (string kind in new[] { "prop", "surface" })
            Assert(UrbanModel.TryApply(state, new() { ExpectedRevision = state.Revision, Object = new() { Id = kind + "-test", Kind = kind } }, out error), error);
        Assert(UrbanModel.TryApply(state, new() { Operation = "terrain", ExpectedRevision = state.Revision,
            Terrain = new() { Id = "terrain-test", Mode = "raise", X = 2, Z = 4, RadiusMeters = 21, AmountMeters = 5 } }, out error), error);
        Assert(UrbanModel.TryApply(state, new() { Operation = "water", ExpectedRevision = state.Revision, SeaLevelMeters = 35 }, out error), error);
        Assert(UrbanModel.TryApply(state, new() { Operation = "sources", ExpectedRevision = state.Revision, IncludeSource = false }, out error), error);
        Assert(UrbanModel.TryApply(state, new() { Operation = "delete", ExpectedRevision = state.Revision, ObjectId = "source-building-1" }, out error), error);
        Assert(state.Edits.Objects.Single(item => item.Id == "source-building-1").Deleted, "原典建物も撤去できること");
        string json = CityModel.Serialize(state);
        Assert(CityModel.TryDeserialize(json, AlwaysLand, out CityState restored, out error), error);
        Assert(CityModel.Serialize(restored) == json, "地形・水面・原典撤去・信号・任意寸法が保存復元で一致すること");
        CityState clone = restored.DeepClone();
        clone.Edits.Objects[0].Footprint[0].X = 300;
        clone.Edits.Objects.Single(item => item.Id == junction.Id).Phases[0].GreenRoadIds.Clear();
        clone.Edits.Terrain[0].AmountMeters = 200;
        Assert(restored.Edits.Objects[0].Footprint[0].X == -1 && restored.Edits.Terrain[0].AmountMeters == 5
            && restored.Edits.Objects.Single(item => item.Id == junction.Id).Phases[0].GreenRoadIds.Count == 1, "履歴の全配列が独立していること");
        before = CityModel.Serialize(restored);
        Assert(!UrbanModel.TryApply(restored, new() { Operation = "terrain", ExpectedRevision = restored.Revision, Terrain = restored.Edits.Terrain[0] }, out _), "地形ストロークの二重確定を拒否すること");
        Assert(!UrbanModel.TryApply(restored, new() { Operation = "water", ExpectedRevision = restored.Revision, SeaLevelMeters = float.NaN }, out _), "不正な水面高度を拒否すること");
        Assert(before == CityModel.Serialize(restored), "地形の拒否後も都市を変更しないこと");
        double funds = restored.Funds;
        CityModel.AdvanceDay(restored);
        Assert(restored.Funds == funds, "自由設計の実都市へ旧固定住宅の税収を加算しないこと");
        CityState budget = new() { Funds = 1 };
        budget.Edits.FreeBuild = false;
        Assert(!UrbanModel.TryApply(budget, new() { ExpectedRevision = 0, Object = new() { Id = "no-budget", Floors = 2 } }, out _), "予算制約時は設定規模から工事費を検証すること");
        Assert(budget.Revision == 0 && budget.Edits.Objects.Count == 0 && budget.Funds == 1, "工事費不足の拒否は原子的であること");
        Assert(!UrbanModel.TryDeserializeCommand("{\"Operation\":\"upsert\",\"Unknown\":1}", out _), "未知の編集要求プロパティを拒否すること");
        CityState graph = new();
        UrbanObject graphRoad = new() { Id = "graph-road", Kind = "road", Path = new() { new(0, 0), new(5, 5) } };
        UrbanObject graphJunction = new() { Id = "graph-junction", Kind = "intersection", ConnectedRoadIds = new() { graphRoad.Id } };
        Assert(UrbanModel.TryApply(graph, new() { Operation = "batch", ExpectedRevision = 0, Objects = new() { graphJunction, graphRoad } }, out error,
            _ => false, _ => null), error);
        Assert(graph.Revision == 1 && graph.Edits.Objects.Count == 2, "前方参照を含む道路・交差点の一括編集は一つの版で確定すること");
        before = CityModel.Serialize(graph);
        Assert(!UrbanModel.TryApply(graph, new() { Operation = "batch", ExpectedRevision = 1,
            Objects = new() { new() { Id = "batch-valid" }, new() { Id = "batch-invalid", Floors = -5 } } }, out _), "一括編集の不正地物を拒否すること");
        Assert(before == CityModel.Serialize(graph), "一括編集の途中で失敗しても先頭の地物を残さないこと");
        Assert(!UrbanModel.TryApply(graph, new() { ExpectedRevision = 1,
            Object = new() { Id = "invalid-signal-parent", Kind = "signal", ParentId = graphRoad.Id } }, out _, _ => false, _ => null), "道路を信号の所属交差点に指定できないこと");
        Assert(UrbanModel.TryApply(graph, new() { Operation = "delete", ExpectedRevision = 1, ObjectId = graphRoad.Id }, out error, _ => false, _ => null), error);
        Assert(graph.Edits.Objects.Single(item => item.Id == graphJunction.Id).ConnectedRoadIds.Count == 0
            && UrbanModel.Validate(graph.Edits, out error, _ => false, _ => null), "道路撤去時に編集済み交差点の参照を切り離すこと");
        CityState legacyReal = new() { MapId = "real-bayfront", Revision = 10, Objects = new() { CreateHousing("migrate-housing", 10),
            new() { Id = "migrate-road", Kind = "road", X = 40, Z = 10, EndX = 50, EndZ = 20 } } };
        Assert(CityModel.MigrateLegacyObjects(legacyReal) == 2 && legacyReal.Revision == 11 && legacyReal.Objects.Count == 0,
            "旧実都市の追加施設を一度だけ新しい編集モデルへ移すこと");
        Assert(legacyReal.Edits.Objects.Any(item => item.Id == "migrate-housing" && item.Kind == "building" && item.Usage == "住宅")
            && legacyReal.Edits.Objects.Any(item => item.Id == "migrate-road" && item.Path.Count == 2), "旧建物と道路の位置と識別子を引き継ぐこと");
        Assert(CityModel.MigrateLegacyObjects(legacyReal) == 0 && legacyReal.Revision == 11, "移行済み保存では地物や版を増やさないこと");
        CityState legacySample = new() { Objects = new() { CreateHousing("sample-unchanged", 0) } };
        Assert(CityModel.MigrateLegacyObjects(legacySample) == 0 && legacySample.Objects.Count == 1, "簡略地図の旧保存は従来どおり保持すること");
        GD.Print("全地物編集・形状・信号制御・地形・保存復元・競合拒否試験: 成功");
    }

    /// <summary>
    /// 機能: 詳細編集の取り消し、やり直し、履歴切替時の版と入力複製を検証します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void TestUrbanHistory()
    {
        CollaborationSession session = new() { Name = "UrbanHistorySession" };
        AddChild(session);
        try
        {
            session.Initialize(new CityState(), AlwaysLand);
            session.ConfigureUrban();
            Assert(!session.Submit(CreateHousing("old-prefab-denied", 10)), "任意編集都市では旧定型施設の経路を拒否すること");
            Assert(session.SubmitEdit(new() { ExpectedRevision = 0, Object = new() { Id = "editable-building", Floors = 3 } }), session.LastError);
            Assert(session.SubmitEdit(new() { Operation = "terrain", ExpectedRevision = 1, Terrain = new() { Id = "undo-terrain", AmountMeters = 7 } }), session.LastError);
            Assert(session.UndoEdit() && session.State.Revision == 3 && session.State.Edits.Terrain.Count == 0 && session.State.Edits.Objects.Count == 1, "地形だけを取り消し、版を前進させること");
            Assert(session.UndoEdit() && session.State.Edits.Objects.Count == 0 && session.State.Revision == 4, "建物編集の取り消し");
            Assert(session.RedoEdit() && session.RedoEdit() && session.State.Edits.Objects[0].Floors == 3 && session.State.Edits.Terrain[0].AmountMeters == 7, "建物と地形のやり直し");
            Assert(session.UndoEdit(), session.LastError);
            Assert(session.SubmitEdit(new() { Operation = "water", ExpectedRevision = session.State.Revision, SeaLevelMeters = 30 }), session.LastError);
            Assert(!session.RedoEdit(), "新しい編集後は古いやり直しを無効にすること");
            Assert(session.ReplaceState(new CityState()) && !session.UndoEdit(), "保存都市切替時は別都市の履歴を保持しないこと");
            double clock = session.SimulationSeconds;
            Assert(session.SetTimeScale(0), session.LastError);
            session.Tick(10);
            Assert(session.SimulationSeconds == clock, "時間停止中は信号時計と都市時間を進めないこと");
            Assert(session.SetTimeScale(3), session.LastError);
            session.Tick(1);
            Assert(session.SimulationSeconds == clock + 3, "時間倍率を都市時計へ反映すること");
            Assert(CityModel.TryDeserialize(CityModel.Serialize(session.State), AlwaysLand, out CityState clockSaved, out string clockError)
                && clockSaved.TimeScale == 3, "時間倍率が保存と同期の対象であること: " + clockError);
        }
        finally { RemoveChild(session); session.Free(); }
        GD.Print("詳細編集の取り消し・やり直し試験: 成功");
    }

    // ----------------------------------------

    /**
     * 機能: 通常ゲームと同じ RPC ノードパスで専用サーバーへ試験クライアントを接続します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void InitializeDedicatedClient()
    {
        try
        {
            _dedicatedMap = BayfrontMap.Load();
            CityState local = _dedicatedMap.CreateSeedState();
            local.Funds = 12_345_678;
            _localStateJson = CityModel.Serialize(local);
            _session = new CollaborationSession { Name = "CollaborationSession" };
            GetParent().AddChild(_session);
            _session.Initialize(local, _dedicatedMap.IsLand, _dedicatedMap.CanPlace);
            if (_dedicatedMap.RealWorld is not null)
            {
                _dedicatedCatalog = new(_dedicatedMap.RealWorld);
                _session.ConfigureUrban(_dedicatedCatalog.SourceExists, _dedicatedCatalog.SourceObject, _dedicatedCatalog.GetRemovalChanges);
                _dedicatedSourceId = _dedicatedCatalog.Buildings.First(item => item.WidthMeters > 10 && item.DepthMeters > 10).Id;
            }
            _session.StateChanged += HandleDedicatedState;
            _session.StatusChanged += HandleNetworkStatus;
            _networkPort = ReadPort(OS.GetCmdlineUserArgs());
            Assert(_session.Join("127.0.0.1", _networkPort), $"専用サーバーへの接続: {_session.LastError}");
            Assert(!_session.CanSaveState && _session.IsGuest, "専用サーバー接続中の保存禁止");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /**
     * 機能: 専用サーバーへの新規建設と、再起動後に配信された同じ建設の永続化を検証します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void AdvanceDedicatedClient()
    {
        CollaborationSession session = _session!;
        BayfrontMap map = _dedicatedMap!;
        if (_phase == 0 && _dedicatedSnapshot && session.IsConnected)
        {
            Assert(session.IsGuest && !session.IsHost && !session.CanSaveState && !session.CanHostState && !session.CanReplaceState, "専用サーバー参加者の保存と管理権限制限");
            Assert(CityModel.ValidateState(session.State, map.IsLand, out string error, map.CanPlace), error);
            if (_dedicatedCheck)
            {
                Assert(session.State.Edits.Objects.Any(item => item.Id == DedicatedEditId && item.Kind == "building" && item.Floors == 6 && !item.Deleted),
                    "専用サーバー再起動後も任意設計した建物を保持すること");
                Assert(session.State.Edits.Objects.Any(item => item.Id == _dedicatedSourceId && item.Floors == 12), "専用サーバー再起動後も原典建物の階数変更が残ること");
                Assert(session.State.Edits.Terrain.Any(item => item.Id == "dedicated-urban-terrain") && session.State.Edits.SeaLevelMeters == 36
                    && !session.State.Edits.IncludeSource, "専用サーバー再起動後も地形・水面・初期都市設定が残ること");
                CompleteDedicatedClient("BAYFRONT_DEDICATED_RESTART_PASS");
                return;
            }
            Assert(!session.State.Edits.Objects.Any(item => item.Id == DedicatedEditId), "専用サーバー試験前の都市に同名地物がないこと");
            _dedicatedFunds = session.State.Funds;
            _snapshotRevision = session.State.Revision;
            _dedicatedCandidate = new() { Id = DedicatedEditId, Kind = "building", X = 25, Z = 20, Floors = 6,
                FloorHeightMeters = 3.6f, WidthMeters = 27, DepthMeters = 18, Usage = "住宅" };
            Assert(!session.Submit(CreateHousing("dedicated-legacy-denied", 40)), "詳細編集の都市では旧定型施設の送信を拒否すること");
            session.RpcId(1, "RequestConstruction", CityModel.SerializeObject(CreateHousing("direct-legacy-denied", 40)));
            Assert(session.SubmitEdit(new() { ExpectedRevision = session.State.Revision, Object = _dedicatedCandidate }), $"専用サーバーへの建設要求: {session.LastError}");
            _phase = 1;
        }
        else if (_phase == 1 && session.State.Edits.Objects.Any(item => item.Id == DedicatedEditId))
        {
            Assert(session.State.Revision == _snapshotRevision + 1, "専用サーバーで受理された建設のリビジョン");
            Assert(_legacyDenied && session.State.Objects.Count == 0, "旧形式の直接通信をホストが拒否し、不可視の定型施設を都市に残さないこと");
            Assert(session.State.Funds == _dedicatedFunds, "自由設計の専用サーバーでは旧定型施設の費用を課さないこと");
            UrbanObject source = _dedicatedCatalog!.Find(_dedicatedSourceId, session.State)!.DeepClone();
            source.Floors = 12;
            Assert(session.SubmitEdit(new() { ExpectedRevision = session.State.Revision, Object = source }), session.LastError);
            _phase = 2;
        }
        else if (_phase == 2 && session.State.Edits.Objects.Any(item => item.Id == _dedicatedSourceId && item.Floors == 12))
        {
            Assert(session.SubmitEdit(new() { Operation = "terrain", ExpectedRevision = session.State.Revision,
                Terrain = new() { Id = "dedicated-urban-terrain", Mode = "lower", X = 20, Z = 21, RadiusMeters = 35, AmountMeters = 4 } }), session.LastError);
            _phase = 3;
        }
        else if (_phase == 3 && session.State.Edits.Terrain.Any(item => item.Id == "dedicated-urban-terrain"))
        {
            Assert(session.SubmitEdit(new() { Operation = "water", ExpectedRevision = session.State.Revision, SeaLevelMeters = 36 }), session.LastError);
            _phase = 4;
        }
        else if (_phase == 4 && session.State.Edits.SeaLevelMeters == 36)
        {
            Assert(session.SubmitEdit(new() { Operation = "sources", ExpectedRevision = session.State.Revision, IncludeSource = false }), session.LastError);
            _phase = 5;
        }
        else if (_phase == 5 && !session.State.Edits.IncludeSource)
        {
            CompleteDedicatedClient("BAYFRONT_DEDICATED_BUILD_PASS");
        }
    }

    /**
     * 機能: 専用サーバーの初回都市スナップショットを受信したことを記録します。
     * 引数: state は CityState 型の受信した確定都市です。
     * 返り値: なし（void 型）。
     */
    private void HandleDedicatedState(CityState state)
    {
        _dedicatedSnapshot = true;
    }

    /**
     * 機能: 専用サーバー試験で既存地物と予約地を避けた住宅候補を選びます。
     * 引数: state は CityState 型の共有都市、map は BayfrontMap 型の地形です。
     * 返り値: CityObject 型の配置可能な住宅です。
     */
    private static CityObject FindDedicatedVacancy(CityState state, BayfrontMap map)
    {
        for (float z = CityModel.MinZ + 12; z <= CityModel.MaxZ - 12; z += 4)
            for (float x = CityModel.MinX + 12; x <= CityModel.MaxX - 12; x += 4)
            {
                var item = new CityObject
                {
                    Id = DedicatedEditId, Kind = "housing", X = x, Z = z, Variant = 1, District = map.DistrictAt(x, z)
                };
                if (map.IsLand(x, z) && map.CanPlace(item)
                    && CityModel.TryAdd(state.DeepClone(), item, map.IsLand, out _, map.CanPlace)) return item;
            }
        throw new InvalidOperationException("専用サーバー試験用の住宅用地がありません。");
    }

    /**
     * 機能: サーバー側の都市を残して退出し、参加前の自都市へ復帰したことを確認します。
     * 引数: marker は string 型の試験成功マーカーです。
     * 返り値: なし（void 型）。
     */
    private void CompleteDedicatedClient(string marker)
    {
        _session!.Disconnect();
        Assert(CityModel.Serialize(_session.State) == _localStateJson && _session.CanSaveState, "専用サーバー退出後に共有都市を捨てて自都市へ戻ること");
        Finish(marker, 0);
    }

    /**
     * 機能: 途中参加、ホスト側拒否、配置と撤去の同期、切断後の自都市復帰を検証します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void AdvanceClientTest()
    {
        CollaborationSession session = _session!;
        bool hasSeed = session.State.Objects.Any(item => item.Id == SeedId);
        bool hasClient = session.State.Objects.Any(item => item.Id == ClientId);
        if (_phase == 0 && session.IsConnected && hasSeed)
        {
            Assert(session.State.Objects.Count == 1 && session.State.Revision == 1, "途中参加時の確定スナップショット");
            Assert(!session.IsHost && session.IsGuest && !session.CanReplaceState && !session.CanSaveState && !session.CanHostState, "参加者の保存と都市管理権限制限");
            Assert(!session.ReplaceState(new CityState()), "参加者からの都市置換拒否");
            Assert(!session.SetTimeScale(0), "参加者からホストの時間停止を変更できないこと");
            Assert(!session.Host(NextPort(_networkPort)), "参加者からのホスト開始拒否");
            Assert(!session.Submit(CreateHousing("network-nan", float.NaN)), "クライアント側の不正座標拒否");
            _snapshotRevision = session.State.Revision;
            _phase = 1;
            _phaseStarted = _elapsed;
            session.RpcId(1, "RequestConstruction", CityModel.SerializeObject(CreateHousing(RejectedId, 0)));
        }
        else if (_phase == 1 && _serverRejected)
        {
            Assert(session.State.Revision == _snapshotRevision, "ホスト拒否時にリビジョンが変わらないこと");
            Assert(!session.State.Objects.Any(item => item.Id == RejectedId), "不正な直接通信をホストが拒否すること");
            Assert(session.Submit(CreateHousing(ClientId, 40)), $"共同配置の送信: {session.LastError}");
            _phase = 2;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 2 && hasClient)
        {
            Assert(hasSeed && session.State.Objects.Count == 2, "共同配置の全状態同期");
            Assert(session.State.Revision == _snapshotRevision + 1, "共同配置のリビジョン");
            Assert(session.Remove(ClientId), $"共同撤去の送信: {session.LastError}");
            _phase = 3;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 3 && !hasClient && session.State.Revision == _snapshotRevision + 2)
        {
            Assert(hasSeed && session.State.Objects.Count == 1, "共同撤去の全状態同期");
            Assert(CityModel.ValidateState(session.State, AlwaysLand, out string error), error);
            Assert(session.SubmitEdit(new() { ExpectedRevision = session.State.Revision,
                Object = new() { Id = UrbanNetworkId, Kind = "building", Floors = 6, WidthMeters = 27, DepthMeters = 18, FloorHeightMeters = 3.6f, Usage = "住宅" } }), session.LastError);
            _phase = 4;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 4 && session.State.Edits.Objects.Any(item => item.Id == UrbanNetworkId))
        {
            Assert(session.State.Edits.Objects.Single(item => item.Id == UrbanNetworkId).Floors == 6, "参加者の詳細建物設定を全員へ同期すること");
            Assert(session.SubmitEdit(new() { ExpectedRevision = session.State.Revision, Object = new() { Id = UrbanSignalId, Kind = "signal", Control = "signal",
                Phases = new() { new() { Name = "共同編集の現示", DurationSeconds = 41, GreenRoadIds = new() { "network-road" } } } } }), session.LastError);
            _phase = 5;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 5 && session.State.Edits.Objects.Any(item => item.Id == UrbanSignalId))
        {
            Assert(session.State.Edits.Objects.Single(item => item.Id == UrbanSignalId).Phases[0].DurationSeconds == 41, "信号現示を同期すること");
            session.RpcId(1, "RequestUrbanEdit", UrbanModel.SerializeCommand(new() { ExpectedRevision = session.State.Revision - 1,
                Object = new() { Id = UrbanNetworkId, Floors = 99 } }));
            _phase = 6;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 6 && _urbanRejected)
        {
            Assert(session.State.Edits.Objects.Single(item => item.Id == UrbanNetworkId).Floors == 6, "古い版を直接送信してもホストが階数を上書きしないこと");
            Assert(session.UndoEdit(), session.LastError);
            _phase = 7;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 7 && !session.State.Edits.Objects.Any(item => item.Id == UrbanSignalId))
        {
            Assert(session.RedoEdit(), session.LastError);
            _phase = 8;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 8 && session.State.Edits.Objects.Any(item => item.Id == UrbanSignalId))
        {
            Assert(session.SubmitEdit(new() { Operation = "terrain", ExpectedRevision = session.State.Revision,
                Terrain = new() { Id = UrbanTerrainId, Mode = "flatten", X = 5, Z = 7, RadiusMeters = 42, TargetHeightMeters = 38 } }), session.LastError);
            _phase = 9;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 9 && session.State.Edits.Terrain.Any(item => item.Id == UrbanTerrainId))
        {
            Assert(session.SubmitEdit(new() { Operation = "sources", ExpectedRevision = session.State.Revision, IncludeSource = false }), session.LastError);
            _phase = 10;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 10 && !session.State.Edits.IncludeSource && session.State.TimeScale == 0)
        {
            double pausedClock = session.SimulationSeconds;
            session.Tick(1);
            Assert(session.SimulationSeconds == pausedClock, "ホスト停止を受信した参加者も信号時計を進めないこと");
            _disconnectedStateJson = CityModel.Serialize(session.State);
            GD.Print("BAYFRONT_URBAN_NETWORK_PASS 詳細建物・信号・地形・競合拒否・取り消し・やり直し・圧縮分割同期");
            _phase = 11;
            _phaseStarted = _elapsed;
        }
        else if (_phase == 11 && !session.IsConnected && session.CanReplaceState)
        {
            Assert(CityModel.Serialize(session.State) == _localStateJson, "ホスト切断後は参加前の自分の都市へ完全に戻ること");
            Assert(CityModel.Serialize(session.State) != _disconnectedStateJson && !session.State.Objects.Any(item => item.Id == SeedId), "参加者側に共有都市が残らないこと");
            Assert(!session.IsHost && !session.IsGuest && session.CanSaveState && session.CanHostState, "自都市へ復帰した後の保存と都市管理権限");
            Assert(session.State.Objects.Count == 1 && session.State.Objects[0].Id == LocalId, "参加前の自都市の地物保持");
            Assert(session.Host(NextPort(_networkPort)), $"自分の都市からのホスト開始: {session.LastError}");
            Assert(session.IsHost && session.CanSaveState && CityModel.Serialize(session.State) == _localStateJson, "自分の都市だけを新しく共有すること");
            session.Disconnect();
            Assert(CityModel.ValidateState(session.State, AlwaysLand, out string error), error);
            GD.Print("BAYFRONT_NETWORK_LOCAL_RESTORED_PASS");
            Finish("BAYFRONT_NETWORK_CLIENT_PASS", 0);
        }
        double phaseLimit = _phase == 11 ? 28 : 12;
        Assert(_phase == 0 || _elapsed - _phaseStarted < phaseLimit, $"共同編集の段階 {_phase} が完了すること");
    }

    /**
     * 機能: ホストが共同配置を受理したことを記録します。
     * 引数: state は CityState 型の確定都市状態です。
     * 返り値: なし（void 型）。
     */
    private void HandleNetworkState(CityState state)
    {
        if (_host && state.Objects.Any(item => item.Id == ClientId)) _hostSawClientObject = true;
    }

    /**
     * 機能: ホストが不正要求を拒否した応答を記録します。
     * 引数: message は string 型の通信状態メッセージです。
     * 返り値: なし（void 型）。
     */
    private void HandleNetworkStatus(string message)
    {
        if (_dedicated && message.Contains("旧形式", StringComparison.Ordinal)) _legacyDenied = true;
        if (!_host && _phase == 6 && message.Contains("別の編集", StringComparison.Ordinal)) _urbanRejected = true;
        if (!_host && _phase == 1 && message.Contains("重な", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_session?.LastError))
        {
            _serverRejected = true;
            GD.Print($"ホストの不正要求拒否: {message}");
        }
    }

    /**
     * 機能: 通信試験用の独立した住宅定義を作成します。
     * 引数: id は string 型の識別子、x は float 型の東西座標です。
     * 返り値: CityObject 型の住宅定義です。
     */
    private static CityObject CreateHousing(string id, float x) => new()
    {
        Id = id, Kind = "housing", X = x, Z = 0, District = "豊洲", Variant = 0
    };

    /// <summary>
    /// 機能: 圧縮後も複数断片になる共有状態を作り、大容量の途中参加を検証します。
    /// 引数: state は CityState 型の試験ホスト都市です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void AddSnapshotPayload(CityState state)
    {
        Random random = new(20261016);
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (int index = 0; index < 2200; index++)
        {
            char[] name = new char[200];
            for (int character = 0; character < name.Length; character++) name[character] = alphabet[random.Next(alphabet.Length)];
            state.Edits.Objects.Add(new() { Id = "snapshot-prop-" + index, Kind = "prop", X = index % 100, Z = index / 100, Name = new string(name) });
        }
    }

    /**
     * 機能: 単体試験で全座標を陸地として扱います。
     * 引数: x と z は float 型の座標です。
     * 返り値: bool 型の true です。
     */
    private static bool AlwaysLand(float x, float z) => true;

    /**
     * 機能: 水域配置の拒否試験で全座標を水域として扱います。
     * 引数: x と z は float 型の座標です。
     * 返り値: bool 型の false です。
     */
    private static bool NeverLand(float x, float z) => false;

    /**
     * 機能: 通信試験用ポート番号をコマンドラインから取得します。
     * 引数: arguments は string[] 型のユーザー引数です。
     * 返り値: int 型のポート番号です。
     */
    private static int ReadPort(string[] arguments)
    {
        string? value = arguments.FirstOrDefault(argument => argument.StartsWith("--network-port=", StringComparison.Ordinal));
        if (value is null) return 24567;
        Assert(int.TryParse(value[(value.IndexOf('=') + 1)..], out int port) && port is >= 1024 and <= 65535, "通信試験ポート番号の範囲");
        return port;
    }

    /**
     * 機能: 再ホスト試験に使用する別の有効なポート番号を取得します。
     * 引数: port は int 型の最初のホストのポート番号です。
     * 返り値: int 型の別ポート番号です。
     */
    private static int NextPort(int port) => port == 65535 ? 65534 : port + 1;

    /**
     * 機能: 試験条件を確認し、失敗時に例外を送出します。
     * 引数: condition は bool 型の条件、description は string 型の説明です。
     * 返り値: なし（void 型）。
     */
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"試験失敗: {description}");
    }

    /**
     * 機能: 試験失敗を記録して終了します。
     * 引数: exception は Exception 型の失敗内容です。
     * 返り値: なし（void 型）。
     */
    private void Fail(Exception exception)
    {
        GD.PushError(exception.ToString());
        Finish("BAYFRONT_TEST_FAIL", 1);
    }

    /**
     * 機能: 結果マーカーを出力し、指定した終了コードでプロセスを終了します。
     * 引数: marker は string 型の結果、exitCode は int 型の終了コードです。
     * 返り値: なし（void 型）。
     */
    private void Finish(string marker, int exitCode)
    {
        if (_finished) return;
        _finished = true;
        GD.Print(marker);
        GetTree().Quit(exitCode);
    }
}
