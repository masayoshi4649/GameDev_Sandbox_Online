using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bayfront;

/// <summary>
/// 保存時計、編集版との分離、実通信と同じ編集版の断片再構成を検証します。
/// </summary>
public partial class SimulationClockTests : Node
{
    private CollaborationSession _session = null!;
    private bool _host, _network, _finished;
    private int _phase, _designUpdates, _baselineDesignUpdates;
    private double _elapsed, _finishHostAt;
    private string _localJson = "";
    private const int ChunkSize = 192 * 1024;

    /// <summary>
    /// 機能: 引数に応じて時計の単体試験または独立プロセス間の通信試験を開始します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            string[] arguments = OS.GetCmdlineUserArgs();
            _network = arguments.Contains("--clock-test-host") || arguments.Contains("--clock-test-client");
            if (!_network)
            {
                TestClock();
                Finish("BAYFRONT_CLOCK_PASS");
                return;
            }
            _host = arguments.Contains("--clock-test-host");
            _session = new CollaborationSession { Name = "Collaboration" };
            AddChild(_session);
            CityState state = new() { MapId = "clock-test", Revision = _host ? 12 : 7,
                SimulationTick = _host ? 19 : 7, SimulationRemainderSeconds = _host ? 0.75 : 0.5 };
            if (_host) AddPayload(state);
            _localJson = CityModel.Serialize(state);
            _session.Initialize(state, AlwaysLand);
            _session.ConfigureUrban();
            _session.StateChanged += _ => _designUpdates++;
            string? portArgument = arguments.FirstOrDefault(value => value.StartsWith("--network-port=", StringComparison.Ordinal));
            int port = portArgument is null ? 24679 : int.Parse(portArgument[15..]);
            Assert(_host ? _session.Host(port) : _session.Join("127.0.0.1", port), _session.LastError);
            if (_host) GD.Print("BAYFRONT_CLOCK_HOST_READY");
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>
    /// 機能: 通信の受信状態から時計更新、編集要求、断片転送を順に検証します。
    /// 引数: delta は double 型の実経過秒数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_network || _finished) return;
        try
        {
            _elapsed += delta;
            Assert(_elapsed < 25, "時計通信試験の時間制限");
            if (_host)
            {
                if (_finishHostAt > 0 && _elapsed >= _finishHostAt) Finish("BAYFRONT_CLOCK_HOST_PASS");
                return;
            }
            if (_phase == 0 && _session.IsConnected && _session.State.Edits.Objects.Count == 2200)
            {
                Assert(_session.IsGuest && !_session.CanSaveState && !_session.CanHostState, "時計同期中も参加者の保存と再ホストは禁止");
                _baselineDesignUpdates = _designUpdates;
                _phase = 1;
                RpcId(1, nameof(RequestClockAdvance));
            }
            else if (_phase == 1 && _session.State.SimulationTick == 21)
            {
                Assert(_session.State.SimulationRemainderSeconds == 0.25 && _session.State.Day == 2, "実通信で整数秒・端数秒・日付を同期");
                Assert(_session.State.Revision == 12 && _designUpdates == _baselineDesignUpdates, "時計の通信だけでは設計更新を通知しない");
                _phase = 2;
                Assert(_session.SubmitEdit(new() { ExpectedRevision = 12, Object = new() { Id = "clock-edit", Kind = "prop" } }), _session.LastError);
            }
            else if (_phase == 2 && _session.State.Edits.Objects.Any(item => item.Id == "clock-edit"))
            {
                Assert(_session.State.Revision == 13, "日付をまたぐ時計進行後も既存の設計版で編集を受理");
                _phase = 3;
                RpcId(1, nameof(RequestInterleavedSnapshots));
            }
        }
        catch (Exception error) { Fail(error); }
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 端数時計の保存復元、日付更新、停止、編集履歴、旧保存の移行と範囲を検証します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void TestClock()
    {
        _session = new CollaborationSession { Name = "Collaboration" };
        AddChild(_session);
        _session.Initialize(new() { MapId = "clock-test", Revision = 42, SimulationTick = 19, SimulationRemainderSeconds = 0.75 }, AlwaysLand);
        _session.ConfigureUrban();
        int designUpdates = 0, runtimeUpdates = 0;
        _session.StateChanged += _ => designUpdates++;
        _session.RuntimeChanged += _ => runtimeUpdates++;
        _session.Tick(0.5f);
        Assert(_session.State.Day == 2 && _session.State.SimulationTick == 20 && _session.State.SimulationRemainderSeconds == 0.25, "日付境界を越える端数時計");
        Assert(_session.State.Revision == 42 && designUpdates == 0 && runtimeUpdates == 1, "実都市の時計を編集版と更新通知から分離");
        Assert(_session.SubmitEdit(new() { ExpectedRevision = 42, Object = new() { Id = "clock-object", Kind = "prop" } }), _session.LastError);
        Assert(_session.State.Revision == 43 && designUpdates == 1, "時計進行前の編集版を引き続き使用できる");
        _session.Tick(1.5f);
        double savedClock = _session.SimulationSeconds;
        string saved = CityModel.Serialize(_session.State);
        Assert(CityModel.TryDeserialize(saved, AlwaysLand, out CityState restored, out string error), error);
        Assert(CityModel.Serialize(restored) == saved && restored.DeepClone().SimulationRemainderSeconds == 0.75, "端数を含む時計を保存・複製できる");
        _session.Tick(25);
        Assert(_session.UndoEdit() && _session.SimulationSeconds == savedClock + 25 && _session.State.Edits.Objects.Count == 0, "設計の取り消しで時計を巻き戻さない");
        Assert(_session.ReplaceState(restored) && _session.SimulationSeconds == savedClock, "読み込みで保存時計を正確に再開");
        Assert(_session.SetTimeScale(0), _session.LastError);
        _session.Tick(3);
        Assert(_session.SimulationSeconds == savedClock, "停止中は保存時計も進めない");
        Assert(_session.SetTimeScale(3), _session.LastError);
        _session.Tick(0.5f);
        Assert(_session.SimulationSeconds == savedClock + 1.5, "倍率を端数時間へ反映");

        // ----------------------------------------

        const string oldSave = "{\"Objects\":[],\"Revision\":5,\"Funds\":25000000,\"Day\":4}";
        Assert(CityModel.TryDeserialize(oldSave, AlwaysLand, out CityState old, out error), error);
        Assert(old.SimulationTick == 60 && old.SimulationRemainderSeconds == 0 && old.Revision == 5, "時計を持たない旧保存は日付から復元し版を維持");
        CityState legacy = new();
        Assert(CityModel.AdvanceSimulationClock(legacy, 20.25) && legacy.Day == 2 && legacy.Revision == 1, "旧簡略都市の日次版更新は維持");
        CityModel.AdvanceDay(legacy);
        Assert(legacy.Day == 3 && legacy.SimulationTick == 40 && legacy.SimulationRemainderSeconds == 0.25, "直接の日次処理も日内の端数時計を保持");
        CityState limit = new() { MapId = "clock-test", Day = CityModel.MaxDay, SimulationTick = CityModel.MaxSimulationTick, SimulationRemainderSeconds = 0.75 };
        CityModel.AdvanceSimulationClock(limit, 60);
        Assert(CityModel.ValidateState(limit, AlwaysLand, out error), error);
        CityState revisionLimit = new() { Revision = CityModel.MaxRevision - 1 };
        CityModel.AdvanceSimulationClock(revisionLimit, 60);
        Assert(revisionLimit.Revision == CityModel.MaxRevision && revisionLimit.Day == 2, "旧簡略都市の版上限で日付更新を安全に停止");
        Assert(CityModel.ValidateState(revisionLimit, AlwaysLand, out error), error);
        restored.SimulationRemainderSeconds = 1;
        Assert(!CityModel.ValidateState(restored, AlwaysLand, out _), "範囲外の端数秒を拒否");
        restored.SimulationRemainderSeconds = 0;
        restored.SimulationTick = -1;
        Assert(!CityModel.ValidateState(restored, AlwaysLand, out _), "負の計算時刻を拒否");
        Assert(!CityModel.TryDeserialize(oldSave[..^1] + ",\"SimulationTick\":60}", AlwaysLand, out _, out _), "時計の保存項目が片方だけのデータを拒否");
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 参加者の開始通知を受けて日付をまたぎ、小さい時計通知だけを送ります。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestClockAdvance()
    {
        if (!_host || Multiplayer.GetRemoteSenderId() <= 1) return;
        _session.Tick(1.5f);
    }

    /// <summary>
    /// 機能: 同じ編集版の時刻が異なる二つの圧縮状態を交互に送り、断片の混在を検証します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestInterleavedSnapshots()
    {
        if (!_host || Multiplayer.GetRemoteSenderId() <= 1) return;
        try
        {
            long peer = Multiplayer.GetRemoteSenderId();
            byte[] older = Compress(CityModel.Serialize(_session.State));
            _session.Tick(0.5f);
            string newerJson = CityModel.Serialize(_session.State);
            byte[] newer = Compress(newerJson);
            Assert(older.Length > ChunkSize && newer.Length > ChunkSize, "断片再構成の試験データは圧縮後も複数断片");
            SendChunk(peer, 100_000, older, 0);
            SendChunk(peer, 100_001, newer, 0);
            for (int index = 1; index * ChunkSize < older.Length; index++) SendChunk(peer, 100_000, older, index);
            for (int index = 1; index * ChunkSize < newer.Length; index++) SendChunk(peer, 100_001, newer, index);
            RpcId(peer, nameof(CheckInterleavedSnapshot), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newerJson))));
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>
    /// 機能: 独立した転送識別子を持つ一つの断片を実際のセッション受信口へ送ります。
    /// 引数: peer、snapshotId は long 型、payload は byte[] 型、index は int 型の断片番号です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void SendChunk(long peer, long snapshotId, byte[] payload, int index)
    {
        int start = index * ChunkSize;
        byte[] chunk = payload.AsSpan(start, Math.Min(ChunkSize, payload.Length - start)).ToArray();
        _session.RpcId(peer, "ReceiveSnapshotChunk", snapshotId, _session.State.Revision, index,
            (payload.Length + ChunkSize - 1) / ChunkSize, payload.Length, chunk);
    }

    /// <summary>
    /// 機能: 交互転送後の都市全体が新しい時計の保存内容と一致することを検証します。
    /// 引数: expectedHash は string 型の期待する保存内容のハッシュです。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CheckInterleavedSnapshot(string expectedHash)
    {
        if (_host || Multiplayer.GetRemoteSenderId() != 1) return;
        try
        {
            string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CityModel.Serialize(_session.State))));
            Assert(actual == expectedHash && _session.IsGuest, "同じ設計版の断片を時刻違いで混同しない");
            Assert(_designUpdates == _baselineDesignUpdates + 1, "編集後の時計だけの完全状態でも設計更新を通知しない");
            RpcId(1, nameof(AcknowledgeSuccess));
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>
    /// 機能: 受信試験の成功を受けて参加者へ切断試験の開始を通知します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AcknowledgeSuccess()
    {
        if (!_host || Multiplayer.GetRemoteSenderId() <= 1) return;
        RpcId(Multiplayer.GetRemoteSenderId(), nameof(FinishClient));
        _finishHostAt = _elapsed + 0.5;
    }

    /// <summary>
    /// 機能: 共有時計を破棄し、参加前の時計を含む自分の都市が復元されることを確認します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishClient()
    {
        if (_host || Multiplayer.GetRemoteSenderId() != 1) return;
        try
        {
            _session.Disconnect();
            Assert(CityModel.Serialize(_session.State) == _localJson && _session.SimulationSeconds == 7.5 && _session.CanSaveState,
                "切断時は共有都市を残さず参加前の自分の時計を復元");
            Finish("BAYFRONT_CLOCK_CLIENT_PASS");
        }
        catch (Exception error) { Fail(error); }
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 複数断片を必要とする独立した名称の地物を生成します。
    /// 引数: state は CityState 型の試験都市です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void AddPayload(CityState state)
    {
        Random random = new(20261016);
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (int index = 0; index < 2200; index++)
        {
            char[] name = new char[200];
            for (int position = 0; position < name.Length; position++) name[position] = alphabet[random.Next(alphabet.Length)];
            state.Edits.Objects.Add(new() { Id = "clock-payload-" + index, Kind = "prop", Name = new string(name) });
        }
    }

    /// <summary>
    /// 機能: 保存内容を実通信と同じ形式で圧縮します。
    /// 引数: json は string 型の保存内容です。
    /// 返り値: byte[] 型の圧縮状態です。
    /// </summary>
    private static byte[] Compress(string json)
    {
        using MemoryStream output = new();
        using (BrotliStream compressor = new(output, CompressionLevel.Fastest, true)) compressor.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    /// <summary>
    /// 機能: 試験用の全座標を陸地として扱います。
    /// 引数: x、z は float 型の座標です。
    /// 返り値: bool 型の true です。
    /// </summary>
    private static bool AlwaysLand(float x, float z) => true;

    /// <summary>
    /// 機能: 条件が偽の場合に試験を失敗させます。
    /// 引数: condition は bool 型の条件、message は string 型の説明です。
    /// 返り値: なし（void 型）。失敗時は InvalidOperationException 型の例外を送出します。
    /// </summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 機能: 成功を記録して試験プロセスを終了します。
    /// 引数: marker は string 型の成功識別子です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Finish(string marker)
    {
        _finished = true;
        GD.Print(marker);
        GetTree().Quit();
    }

    /// <summary>
    /// 機能: 失敗内容を記録して試験プロセスを異常終了します。
    /// 引数: error は Exception 型の失敗内容です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Fail(Exception error)
    {
        _finished = true;
        GD.PushError($"時計試験失敗: {error}");
        GetTree().Quit(1);
    }
}
