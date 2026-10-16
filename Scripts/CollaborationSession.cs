using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Bayfront;

/// <summary>
/// ENet によるホスト権威の共同編集を管理します。全参加者でこのノードのパスを一致させます。
/// </summary>
public partial class CollaborationSession : Node
{
    public event Action<CityState>? StateChanged;
    public event Action<CityState>? RuntimeChanged;
    public event Action<string>? StatusChanged;
    public event Action<int>? PeerCountChanged;

    public CityState State => _state;
    public bool IsHost => _hosting;
    public bool IsGuest => _localStateBeforeJoin is not null;
    public new bool IsConnected => _peer is not null && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;
    public bool CanSaveState => !IsGuest && !_connecting && !_awaitingSnapshot && (_peer is null || _hosting);
    public bool CanReplaceState => CanSaveState;
    public bool CanHostState => CanSaveState;
    public int PeerCount => IsConnected ? (_api?.GetPeers().Length ?? 0) + 1 : 1;
    public string LastError { get; private set; } = "";
    public double SimulationSeconds => Math.Min(Math.BitDecrement((double)(CityModel.MaxSimulationTick + 1)),
        _state.SimulationTick + _state.SimulationRemainderSeconds + _guestClockOffset);
    public bool CanSetTimeScale => CanSaveState;
    public bool CanUndoEdit => !_connecting && !_awaitingSnapshot && (_undo.Count > 0 || IsGuest);
    public bool CanRedoEdit => !_connecting && !_awaitingSnapshot && (_redo.Count > 0 || IsGuest);
    public const float SecondsPerDay = CityModel.SimulationSecondsPerDay;

    private CityState _state = new();
    private string _mapId = CityModel.LegacyMapId;
    private int _mapVersion = 1;
    private Func<float, float, bool> _isLand = (_, _) => true;
    private Func<CityObject, bool>? _canPlace;
    private ENetMultiplayerPeer? _peer;
    private MultiplayerApi _api = null!;
    private bool _hosting;
    private bool _connecting;
    private bool _awaitingSnapshot;
    private bool _resetting;
    private double _guestClockOffset;
    private double _runtimeBroadcastSeconds;
    private CityState? _localStateBeforeJoin;
    private readonly Dictionary<long, (long Started, int Count)> _requestRates = new();
    private Func<string, bool>? _sourceExists;
    private Func<string, UrbanObject?>? _sourceLookup;
    private Func<string, CityState, IReadOnlyList<UrbanObject>>? _removalChanges;
    private bool _urbanConfigured;
    private Func<CityState, IReadOnlyList<UrbanObject>>? _simulationDesignProvider;
    private IReadOnlyList<UrbanObject>? _simulationDesign;
    private long _simulationDesignRevision = -1;
    private CityState? _simulationDesignState;
    private readonly List<(UrbanEdits Before, UrbanEdits After, double FundsDelta, int SizeBytes)> _undo = new();
    private readonly List<(UrbanEdits Before, UrbanEdits After, double FundsDelta, int SizeBytes)> _redo = new();
    private long _snapshotCacheRevision = -1;
    private long _snapshotCacheTick = -1;
    private double _snapshotCacheRemainder = -1;
    private byte[]? _snapshotCache;
    private long _nextSnapshotId;
    private long _receivingSnapshotId = -1;
    private long _completedSnapshotId = -1;
    private long _receivingRevision = -1;
    private byte[]? _receivingSnapshot;
    private bool[]? _receivedChunks;
    private int _receivedChunkCount;
    private const int SnapshotChunkSize = 192 * 1024;
    private const int MaxCompressedSnapshotBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 機能: ノード参加時に接続状態の通知を購読します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Ready() => SubscribeNetwork();

    /// <summary>
    /// 機能: ノード終了時に接続と通知購読を解放します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _ExitTree()
    {
        _resetting = true;
        _peer?.Close();
        if (_api is not null)
        {
            _api.PeerConnected -= OnPeerConnected;
            _api.PeerDisconnected -= OnPeerDisconnected;
            _api.ConnectedToServer -= OnConnectedToServer;
            _api.ConnectionFailed -= OnConnectionFailed;
            _api.ServerDisconnected -= OnServerDisconnected;
        }
        _peer = null;
        _api = null!;
    }

    /// <summary>
    /// 機能: 検証済みの初期都市と地形判定を設定します。接続中の呼び出しは拒否します。
    /// 引数: initial は CityState 型、isLand は Func&lt;float,float,bool&gt; 型の陸地判定、canPlace は全参加者で共通の任意の予約地判定 Func&lt;CityObject,bool&gt; 型です。
    /// 返り値: なし（void 型）。不正データでは ArgumentException 型の例外を送出します。
    /// </summary>
    public void Initialize(CityState initial, Func<float, float, bool> isLand, Func<CityObject, bool>? canPlace = null)
    {
        if (_peer is not null || IsGuest)
            throw new InvalidOperationException("共同編集中は初期化できません。");
        ArgumentNullException.ThrowIfNull(isLand);
        if (!CityModel.ValidateState(initial, isLand, out string error, canPlace))
            throw new ArgumentException(error, nameof(initial));
        if (_urbanConfigured && initial.Objects.Count > 0)
            throw new ArgumentException("旧施設を新しい都市編集形式へ移行してから初期化してください。", nameof(initial));
        _isLand = isLand;
        _canPlace = canPlace;
        _mapId = initial.MapId;
        _mapVersion = initial.MapVersion;
        _state = initial.DeepClone();
        ClearUrbanHistory();
        _guestClockOffset = 0;
        _runtimeBroadcastSeconds = 0;
        LastError = "";
        StateChanged?.Invoke(_state);
        StatusChanged?.Invoke("ひとりで開発中");
        PeerCountChanged?.Invoke(1);
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 全参加者で一致する原典地物の ID 検証を設定します。描画データは不要です。
    /// 引数: sourceExists は Func&lt;string,bool&gt; 型または null の原典 ID 判定、sourceLookup は Func&lt;string,UrbanObject?&gt; 型または null の原典解決、removalChanges は Func&lt;string,CityState,IReadOnlyList&lt;UrbanObject&gt;&gt; 型または null の撤去展開です。
    /// 返り値: なし（void 型）。不正な既存状態では ArgumentException 型の例外を送出します。
    /// </summary>
    public void ConfigureUrban(Func<string, bool>? sourceExists = null, Func<string, UrbanObject?>? sourceLookup = null,
        Func<string, CityState, IReadOnlyList<UrbanObject>>? removalChanges = null)
    {
        if (_state.Objects.Count > 0) throw new ArgumentException("旧施設を新しい都市編集形式へ移行してから設定してください。");
        if (!UrbanModel.Validate(_state.Edits, out string error, sourceExists, sourceLookup))
            throw new ArgumentException(error, nameof(sourceExists));
        _sourceExists = sourceExists;
        _sourceLookup = sourceLookup;
        _removalChanges = removalChanges;
        _urbanConfigured = true;
    }

    /// <summary>
    /// 機能: 描画を使わず取得できる確定設計を、個別交通計算へ接続します。
    /// 引数: designProvider は Func&lt;CityState,IReadOnlyList&lt;UrbanObject&gt;&gt; 型の全地物取得関数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public void ConfigureSimulation(Func<CityState, IReadOnlyList<UrbanObject>> designProvider)
    {
        ArgumentNullException.ThrowIfNull(designProvider);
        _simulationDesignProvider = designProvider;
        _simulationDesignRevision = -1;
        _simulationDesign = null;
    }

    /// <summary>
    /// 機能: ソロまたはホストが都市の保存名を変更し、確定状態として同期します。
    /// 引数: name は string 型の 1 ～ 80 文字の都市名です。
    /// 返り値: bool 型の変更成功可否です。参加者は所有都市を改名できません。
    /// </summary>
    public bool SetCityName(string name)
    {
        if (!CanSaveState) return Reject("都市名の変更は都市を所有するホストが行ってください。");
        if (!CityModel.ValidateCityName(name, out string nameError)) return Reject(nameError);
        name = name.Trim();
        if (_state.CityName == name) return true;
        if (_state.Revision >= CityModel.MaxRevision) return Reject("都市の更新回数が上限に達しました。");
        _state.CityName = name;
        if (_state.Simulation.DesignRevision == _state.Revision) _state.Simulation.DesignRevision++;
        _state.Revision++;
        CommitState();
        return true;
    }

    /// <summary>
    /// 機能: 建物、道路、交差点、信号、地表または地形の編集をホスト権威で確定します。
    /// 引数: command は UrbanCommand 型の編集要求です。ExpectedRevision は編集を開始した都市の版です。
    /// 返り値: bool 型の確定または送信成功可否です。参加者の確定は StateChanged で通知します。
    /// </summary>
    public bool SubmitEdit(UrbanCommand command)
    {
        if (_connecting || _awaitingSnapshot) return Reject("初回の都市同期が終わるまでお待ちください。");
        if (command is null || command.ExpectedRevision != _state.Revision) return Reject("別の編集が先に確定しました。最新の都市を確認して再度操作してください。");
        command = ExpandRemovals(command);
        string json = UrbanModel.SerializeCommand(command);
        if (!UrbanModel.TryDeserializeCommand(json, out UrbanCommand checkedCommand)) return Reject("編集要求が許容サイズを超えているか、形式が不正です。");
        CityState candidate = _state.DeepClone();
        if (!UrbanModel.TryApply(candidate, checkedCommand, out string error, _sourceExists, _sourceLookup)) return Reject(error);
        if (IsGuest)
        {
            if (!IsConnected || RpcId(1, nameof(RequestUrbanEdit), json) != Error.Ok)
                return Reject("都市編集をホストへ送信できませんでした。");
            LastError = "";
            return true;
        }
        return AcceptUrbanCandidate(candidate);
    }

    /// <summary>
    /// 機能: 共有都市の最後の編集を取り消します。参加者はホストへ要求します。
    /// 引数: なし。
    /// 返り値: bool 型の確定または送信成功可否です。
    /// </summary>
    public bool UndoEdit() => SubmitHistory("undo");

    /// <summary>
    /// 機能: 取り消した共有都市の編集をやり直します。参加者はホストへ要求します。
    /// 引数: なし。
    /// 返り値: bool 型の確定または送信成功可否です。
    /// </summary>
    public bool RedoEdit() => SubmitHistory("redo");

    /// <summary>
    /// 機能: ソロまたはホストが都市の時間倍率を変更し、停止を含めて全参加者へ同期します。
    /// 引数: scale は float 型の 0 から 8 までの倍率です。
    /// 返り値: bool 型の変更成功可否です。参加者は変更できません。
    /// </summary>
    public bool SetTimeScale(float scale)
    {
        if (!CanSetTimeScale) return Reject("都市時間の変更はホストが行ってください。");
        if (!float.IsFinite(scale) || scale < 0 || scale > 8) return Reject("都市の時間倍率は 0 から 8 の範囲で指定してください。");
        if (_state.TimeScale == scale) return true;
        if (_state.Revision >= CityModel.MaxRevision) return Reject("都市の更新回数が上限に達しました。");
        _state.TimeScale = scale;
        if (_state.Simulation.DesignRevision == _state.Revision) _state.Simulation.DesignRevision++;
        _state.Revision++;
        CommitState();
        return true;
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 現在の都市を共有する ENet ホストを開始します。
    /// 引数: port は int 型の UDP ポート番号で、既定値は 24567 です。
    /// 返り値: bool 型の開始成功可否です。
    /// </summary>
    public bool Host(int port = 24567)
    {
        if (!CanHostState)
            return Reject("参加中の共有都市をホストとして引き継ぐことはできません。共同編集から離脱し、自分の都市に戻ってください。");
        if (!PrepareNetwork(port))
            return false;
        ResetNetwork();
        ENetMultiplayerPeer candidate = new();
        Error result = candidate.CreateServer(port, 7);
        if (result != Error.Ok)
        {
            candidate.Dispose();
            return Reject($"ホストを開始できません。UDP ポート {port} の使用状況を確認してください。({result})");
        }
        _peer = candidate;
        _hosting = true;
        _api.MultiplayerPeer = _peer;
        LastError = "";
        StatusChanged?.Invoke($"共同編集ホスト · UDP {port}");
        PeerCountChanged?.Invoke(PeerCount);
        return true;
    }

    /// <summary>
    /// 機能: 自分の都市を一時退避して指定ホストへ参加し、共有都市の初回同期を待ちます。
    /// 引数: address は string 型のホスト名または IP、port は int 型の UDP ポート番号です。
    /// 返り値: bool 型の接続開始成功可否です。接続完了は StatusChanged で通知します。
    /// </summary>
    public bool Join(string address, int port = 24567)
    {
        if (IsGuest || _connecting || _awaitingSnapshot)
            return Reject("既に共同編集へ参加しています。別のホストに参加する場合は先に離脱してください。");
        if (!PrepareNetwork(port))
            return false;
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || address.Any(char.IsWhiteSpace))
            return Reject("接続先の IP アドレスまたはホスト名を入力してください。");
        ENetMultiplayerPeer candidate = new();
        Error result = candidate.CreateClient(address, port);
        if (result != Error.Ok)
        {
            candidate.Dispose();
            return Reject($"接続を開始できません。接続先と UDP ポートを確認してください。({result})");
        }
        CityState localState = _state.DeepClone();
        ResetNetwork();
        _localStateBeforeJoin = localState;
        ClearUrbanHistory();
        _peer = candidate;
        _connecting = true;
        _awaitingSnapshot = true;
        _api.MultiplayerPeer = _peer;
        LastError = "";
        StatusChanged?.Invoke($"{address}:{port} に接続中…");
        PeerCountChanged?.Invoke(PeerCount);
        return true;
    }

    /// <summary>
    /// 機能: 共同編集を終了します。参加者は共有都市を破棄して参加前の自分の都市へ戻り、ホストは自分の都市を保持します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public void Disconnect()
    {
        ResetNetwork();
        bool restored = RestoreLocalCity();
        LastError = "";
        StatusChanged?.Invoke(restored ? "共同編集から離脱し、参加前の自分の都市に戻りました。" : "共同編集を終了しました。自分の都市を開発中");
        PeerCountChanged?.Invoke(1);
    }

    /// <summary>
    /// 機能: 建設をローカル検証し、ホストまたはソロでは確定、参加者ではホストへ要求します。
    /// 引数: item は CityObject 型の建設地物です。
    /// 返り値: bool 型の確定または送信成功可否です。参加者の最終確定は StateChanged で通知します。
    /// </summary>
    public bool Submit(CityObject item)
    {
        if (_urbanConfigured) return Reject("この都市では全地物の設計パラメータを指定して建設してください。");
        if (_connecting || _awaitingSnapshot)
            return Reject("初回の都市同期が終わるまでお待ちください。");
        CityState candidate = _state.DeepClone();
        if (!CityModel.TryAdd(candidate, item, _isLand, out string error, _canPlace))
            return Reject(error);
        if (IsGuest)
        {
            if (!IsConnected)
                return Reject("ホストに接続していません。");
            Error result = RpcId(1, nameof(RequestConstruction), CityModel.SerializeObject(item));
            if (result != Error.Ok)
                return Reject("建設要求を送信できませんでした。");
            LastError = "";
            return true;
        }
        _state = candidate;
        CommitState();
        return true;
    }

    /// <summary>
    /// 機能: 撤去対象を検証し、ホストまたはソロでは確定、参加者ではホストへ要求します。
    /// 引数: id は string 型の撤去対象識別子です。
    /// 返り値: bool 型の確定または送信成功可否です。
    /// </summary>
    public bool Remove(string id)
    {
        if (_urbanConfigured) return Reject("この都市の撤去は全地物の編集画面から行ってください。");
        if (_connecting || _awaitingSnapshot)
            return Reject("初回の都市同期が終わるまでお待ちください。");
        CityState candidate = _state.DeepClone();
        if (!CityModel.TryRemove(candidate, id, out string error))
            return Reject(error);
        if (IsGuest)
        {
            if (!IsConnected)
                return Reject("ホストに接続していません。");
            if (RpcId(1, nameof(RequestRemoval), id) != Error.Ok)
                return Reject("撤去要求を送信できませんでした。");
            LastError = "";
            return true;
        }
        _state = candidate;
        CommitState();
        return true;
    }

    /// <summary>
    /// 機能: ソロまたはホストで保存都市を読み込み、リビジョンを前進させて全員に同期します。
    /// 引数: state は CityState 型の読み込み対象です。
    /// 返り値: bool 型の置換成功可否です。共同編集の参加者は置換できません。
    /// </summary>
    public bool ReplaceState(CityState state)
    {
        if (!CanReplaceState)
            return Reject("共有都市の保存と読み込みはホストが行ってください。参加者は保存できません。");
        if (!CityModel.ValidateMapIdentity(state, _mapId, _mapVersion, out string error))
            return Reject(error);
        if (!CityModel.ValidateState(state, _isLand, out error, _canPlace))
            return Reject(error);
        if (_urbanConfigured && state.Objects.Count > 0)
            return Reject("旧施設を新しい都市編集形式へ移行してから読み込んでください。");
        if (!UrbanModel.Validate(state.Edits, out error, _sourceExists, _sourceLookup))
            return Reject(error);
        if (_state.Revision >= CityModel.MaxRevision || state.Revision >= CityModel.MaxRevision)
            return Reject("都市の更新回数が上限に達しました。");
        CityState replacement = state.DeepClone();
        replacement.Revision = Math.Max(_state.Revision, replacement.Revision) + 1;
        _state = replacement;
        ClearUrbanHistory();
        _guestClockOffset = 0;
        _runtimeBroadcastSeconds = 0;
        CommitState();
        StatusChanged?.Invoke("都市を読み込みました。");
        return true;
    }

    /// <summary>
    /// 機能: ソロまたはホストの保存可能な都市時計を進め、実都市では編集版を変更せず小さな時計通知を配信します。
    /// 引数: seconds は float 型の経過秒数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public void Tick(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) return;
        double elapsed = Math.Min((double)seconds * _state.TimeScale, 60);
        if (elapsed <= 0) return;
        if (IsGuest && IsConnected && !_awaitingSnapshot)
        {
            _guestClockOffset += elapsed;
            RuntimeChanged?.Invoke(_state);
            return;
        }
        if (!CanReplaceState) return;
        long beforeRevision = _state.Revision;
        if (!CityModel.AdvanceSimulationClock(_state, elapsed)) return;
        if (_simulationDesignProvider is not null)
        {
            if (_simulationDesign is null || _simulationDesignRevision != _state.Revision || !ReferenceEquals(_simulationDesignState, _state))
            {
                _simulationDesign = _simulationDesignProvider(_state);
                _simulationDesignRevision = _state.Revision;
                _simulationDesignState = _state;
            }
            UrbanSimulation.Step(_state, _simulationDesign, elapsed);
        }
        RuntimeChanged?.Invoke(_state);
        if (_state.Revision != beforeRevision) CommitState();
        _runtimeBroadcastSeconds += seconds;
        if (_runtimeBroadcastSeconds < 1) return;
        _runtimeBroadcastSeconds %= 1;
        if (_hosting && IsConnected && _api.GetPeers().Length > 0)
        {
            Rpc(nameof(ReceiveRuntimeClock), _state.Revision, _state.SimulationTick, _state.SimulationRemainderSeconds, _state.Day, _state.Funds);
            if (_simulationDesignProvider is not null)
            {
                byte[] json = Encoding.UTF8.GetBytes(UrbanSimulation.Serialize(_state.Simulation));
                using MemoryStream output = new();
                using (BrotliStream compressor = new(output, CompressionLevel.Fastest, true)) compressor.Write(json);
                Rpc(nameof(ReceiveRuntimeTraffic), _state.Revision, _state.SimulationTick, output.ToArray());
            }
        }
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 参加者の詳細編集を直列に検証し、競合した要求は状態を変更せず拒否します。
    /// 引数: json は string 型の編集コマンド JSON です。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestUrbanEdit(string json)
    {
        long sender = _api.GetRemoteSenderId();
        if (!AcceptRequest(sender)) return;
        if (!UrbanModel.TryDeserializeCommand(json, out UrbanCommand command))
        {
            SendError(sender, "都市編集の形式またはサイズが不正です。");
            return;
        }
        if (command.ExpectedRevision != _state.Revision)
        {
            SendError(sender, "別の編集が先に確定しました。最新の都市を確認して再度操作してください。");
            SendSnapshot(sender);
            return;
        }
        if (command.Operation is "undo" or "redo")
        {
            if (!ApplyHistory(command.Operation == "redo", command.ExpectedRevision, out string historyError))
                SendError(sender, historyError);
            return;
        }
        command = ExpandRemovals(command);
        CityState candidate = _state.DeepClone();
        if (!UrbanModel.TryApply(candidate, command, out string error, _sourceExists, _sourceLookup))
        {
            SendError(sender, error);
            if (command.ExpectedRevision != _state.Revision) SendSnapshot(sender);
            return;
        }
        if (!AcceptUrbanCandidate(candidate)) SendError(sender, LastError);
    }

    /// <summary>
    /// 機能: 接続済み参加者の建設要求をホスト上で直列に検証して確定します。
    /// 引数: json は string 型の地物 JSON です。最大 2048 バイトです。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestConstruction(string json)
    {
        long sender = _api.GetRemoteSenderId();
        if (!AcceptRequest(sender))
            return;
        if (_urbanConfigured) { SendError(sender, "旧形式の定型施設の建設要求には対応していません。"); return; }
        if (!CityModel.TryDeserializeObject(json, out CityObject item))
        {
            SendError(sender, "建設要求の形式またはサイズが不正です。");
            return;
        }
        if (!CityModel.TryAdd(_state, item, _isLand, out string error, _canPlace))
        {
            SendError(sender, error);
            return;
        }
        CommitState();
    }

    /// <summary>
    /// 機能: 接続済み参加者の撤去要求をホスト上で検証して確定します。
    /// 引数: id は string 型の撤去対象識別子です。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRemoval(string id)
    {
        long sender = _api.GetRemoteSenderId();
        if (!AcceptRequest(sender))
            return;
        if (_urbanConfigured) { SendError(sender, "旧形式の定型施設の撤去要求には対応していません。"); return; }
        if (!CityModel.TryRemove(_state, id, out string error))
        {
            SendError(sender, error);
            return;
        }
        CommitState();
    }

    /// <summary>
    /// 機能: 接続完了した参加者からの初回同期要求に応答します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSnapshot()
    {
        long sender = _api.GetRemoteSenderId();
        if (AcceptRequest(sender))
            SendSnapshot(sender);
    }

    /// <summary>
    /// 機能: ホストからの完全状態を検証し、古い更新を除外して反映します。
    /// 引数: json は string 型の都市 JSON、revision は long 型の通信ヘッダーの編集版です。最大 64 メガバイトです。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void ReceiveSnapshot(string json, long revision)
    {
        if (!IsGuest || _hosting || _api.GetRemoteSenderId() != 1)
            return;
        if (!CityModel.TryDeserialize(json, _isLand, out CityState received, out string error, _canPlace)
            || !CityModel.ValidateMapIdentity(received, _mapId, _mapVersion, out error)
            || !UrbanModel.Validate(received.Edits, out error, _sourceExists, _sourceLookup))
        {
            ResetNetwork();
            RestoreLocalCity();
            Reject($"受信した都市を検証できなかったため接続を終了し、参加前の自分の都市に戻りました。{error}");
            PeerCountChanged?.Invoke(1);
            return;
        }
        if (received.Revision != revision)
        {
            RejectInvalidSnapshot("共有都市の編集版が通信ヘッダーと一致しません。");
            return;
        }
        if (_urbanConfigured && received.Objects.Count > 0)
        {
            RejectInvalidSnapshot("共有都市が旧施設形式です。ホストで都市を移行してから接続してください。");
            return;
        }
        if (!_awaitingSnapshot && received.Revision < _state.Revision)
            return;
        if (!_awaitingSnapshot && received.Revision == _state.Revision
            && received.SimulationTick + received.SimulationRemainderSeconds < _state.SimulationTick + _state.SimulationRemainderSeconds)
            return;
        bool wasWaiting = _awaitingSnapshot;
        bool designChanged = wasWaiting || received.Revision != _state.Revision;
        _state = received;
        _awaitingSnapshot = false;
        _connecting = false;
        _guestClockOffset = 0;
        LastError = "";
        if (designChanged) StateChanged?.Invoke(_state);
        RuntimeChanged?.Invoke(_state);
        if (wasWaiting)
            StatusChanged?.Invoke("共同編集に参加しました。都市を同期済みです。");
        PeerCountChanged?.Invoke(PeerCount);
    }

    /// <summary>
    /// 機能: ホストの確定時計と日次収支だけを受け取り、設計更新を通知せず描画用の補間時計を補正します。
    /// 引数: revision、tick は long 型、remainder は double 型の端数秒、day は int 型の日付、funds は double 型の資金です。
    /// 返り値: なし（void 型）。異なる編集版や古い時計は採用しません。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRuntimeClock(long revision, long tick, double remainder, int day, double funds)
    {
        if (!IsGuest || _hosting || _awaitingSnapshot || _api.GetRemoteSenderId() != 1 || revision != _state.Revision) return;
        if (!CityModel.ValidateSimulationClock(tick, remainder, day, out _)
            || !double.IsFinite(funds) || funds < 0 || funds > CityModel.MaxFunds)
        {
            RejectInvalidSnapshot("共有都市の時計通知が不正です。");
            return;
        }
        if (tick + remainder < _state.SimulationTick + _state.SimulationRemainderSeconds) return;
        _state.SimulationTick = tick;
        _state.SimulationRemainderSeconds = remainder;
        _state.Day = day;
        _state.Funds = funds;
        _guestClockOffset = 0;
        RuntimeChanged?.Invoke(_state);
    }

    /// <summary>
    /// 機能: 地物設計を含まない圧縮済み住民・交通状態をホストから受け取ります。
    /// 引数: revision、tick は long 型、compressed は byte[] 型の Brotli 圧縮 JSON です。
    /// 返り値: なし（void 型）。古い通知と不正なサイズ・数値は採用しません。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRuntimeTraffic(long revision, long tick, byte[] compressed)
    {
        if (!IsGuest || _hosting || _awaitingSnapshot || _api.GetRemoteSenderId() != 1 || revision != _state.Revision
            || tick < _state.Simulation.LastProcessedTick || tick > _state.SimulationTick || compressed is null || compressed.Length > 8 * 1024 * 1024) return;
        try
        {
            using MemoryStream input = new(compressed, false);
            using BrotliStream decompressor = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[32 * 1024];
            int count;
            while ((count = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > 12 * 1024 * 1024) return;
                output.Write(buffer, 0, count);
            }
            if (!UrbanSimulation.TryDeserialize(Encoding.UTF8.GetString(output.ToArray()), tick, out UrbanSimulationState simulation)) return;
            _state.Simulation = simulation;
            RuntimeChanged?.Invoke(_state);
        }
        catch (InvalidDataException) { Reject("共有都市の交通通知を展開できませんでした。"); }
    }

    /// <summary>
    /// 機能: ホストからの要求拒否理由を操作した参加者に通知します。
    /// 引数: message は string 型の説明です。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveStatus(string message)
    {
        if (IsGuest && !_hosting && _api.GetRemoteSenderId() == 1 && message is not null && message.Length <= 512)
            Reject(message);
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 撤去要求を原典全体の参照解除を含む一括変更へ展開します。参加者が最小要求を直接送った場合も適用します。
    /// 引数: command は UrbanCommand 型の要求です。
    /// 返り値: UrbanCommand 型の参照解除を含む要求です。
    /// </summary>
    private UrbanCommand ExpandRemovals(UrbanCommand command)
    {
        if (_removalChanges is null || command is null) return command!;
        bool directDelete = command.Operation == "delete" || command.Operation == "upsert" && command.Object?.Deleted == true;
        List<string> ids = directDelete ? new() { command.Object?.Id ?? command.ObjectId }
            : command.Operation == "batch" && command.Objects is not null ? command.Objects.Where(item => item is not null && item.Deleted).Select(item => item.Id).ToList() : new();
        if (ids.Count == 0 || ids.Any(id => !UrbanModel.ValidId(id))) return command;
        Dictionary<string, UrbanObject> changes = new(StringComparer.Ordinal);
        foreach (string id in ids)
            foreach (UrbanObject item in _removalChanges(id, _state))
            {
                if (!changes.TryGetValue(item.Id, out UrbanObject? combined)) changes[item.Id] = item.DeepClone();
                else
                {
                    combined.ConnectedRoadIds = combined.ConnectedRoadIds.Intersect(item.ConnectedRoadIds, StringComparer.Ordinal).ToList();
                    for (int phase = 0; phase < Math.Min(combined.Phases.Count, item.Phases.Count); phase++)
                        combined.Phases[phase].GreenRoadIds = combined.Phases[phase].GreenRoadIds.Intersect(item.Phases[phase].GreenRoadIds, StringComparer.Ordinal).ToList();
                    if (item.ApproachRoadId.Length == 0) combined.ApproachRoadId = "";
                    if (item.ParentId.Length == 0) combined.ParentId = "";
                    if (item.JunctionId.Length == 0) combined.JunctionId = "";
                    combined.Deleted |= item.Deleted;
                }
            }
        if (changes.Count == 0) return command;
        if (command.Operation == "batch" && command.Objects is not null)
            foreach (UrbanObject item in command.Objects) changes[item.Id] = item;
        return new() { Operation = "batch", ExpectedRevision = command.ExpectedRevision, Objects = changes.Values.ToList() };
    }

    /// <summary>
    /// 機能: ホストの圧縮状態を小分けに受け取り、全断片が揃ってからサイズを制限して復元します。
    /// 引数: snapshotId は long 型の独立した転送識別子、revision は long 型の編集版、index、count、totalBytes は int 型、chunk は byte[] 型の圧縮状態断片です。
    /// 返り値: なし（void 型）。
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSnapshotChunk(long snapshotId, long revision, int index, int count, int totalBytes, byte[] chunk)
    {
        if (!IsGuest || _hosting || _api.GetRemoteSenderId() != 1) return;
        if (snapshotId <= _completedSnapshotId || snapshotId < _receivingSnapshotId || !_awaitingSnapshot && revision < _state.Revision) return;
        if (snapshotId < 1 || revision < 0 || revision > CityModel.MaxRevision
            || totalBytes < 1 || totalBytes > MaxCompressedSnapshotBytes || count != (totalBytes + SnapshotChunkSize - 1) / SnapshotChunkSize
            || index < 0 || index >= count || chunk is null || chunk.Length != Math.Min(SnapshotChunkSize, totalBytes - index * SnapshotChunkSize))
        {
            RejectInvalidSnapshot("共有都市の通信断片が不正です。");
            return;
        }
        if (_receivingSnapshot is null || snapshotId != _receivingSnapshotId)
        {
            _receivingSnapshotId = snapshotId;
            _receivingRevision = revision;
            _receivingSnapshot = new byte[totalBytes];
            _receivedChunks = new bool[count];
            _receivedChunkCount = 0;
        }
        if (_receivingRevision != revision || _receivingSnapshot.Length != totalBytes || _receivedChunks!.Length != count)
        {
            RejectInvalidSnapshot("共有都市の通信サイズが一致しません。");
            return;
        }
        if (_receivedChunks[index]) return;
        Buffer.BlockCopy(chunk, 0, _receivingSnapshot, index * SnapshotChunkSize, chunk.Length);
        _receivedChunks[index] = true;
        if (++_receivedChunkCount != count) return;
        try
        {
            using MemoryStream input = new(_receivingSnapshot, false);
            using BrotliStream decompressor = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[64 * 1024];
            int length;
            while ((length = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + length > CityModel.MaxPayloadBytes) throw new InvalidDataException("共有都市が許容サイズを超えています。");
                output.Write(buffer, 0, length);
            }
            _completedSnapshotId = snapshotId;
            ReceiveSnapshot(new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length), revision);
            _receivingSnapshot = null;
            _receivedChunks = null;
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException or IOException)
        {
            RejectInvalidSnapshot("共有都市の展開に失敗しました。");
        }
    }

    /// <summary>
    /// 機能: 不正な共有状態を受け取った接続を終了し、自分の都市へ戻します。
    /// 引数: message は string 型の拒否理由です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void RejectInvalidSnapshot(string message)
    {
        ResetNetwork();
        RestoreLocalCity();
        Reject(message);
        PeerCountChanged?.Invoke(1);
    }

    /// <summary>
    /// 機能: ネットワーク開始前にノードの所属とポート番号を確認します。
    /// 引数: port は int 型の UDP ポート番号です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private bool PrepareNetwork(int port)
    {
        if (!IsInsideTree())
            return Reject("共同編集ノードがシーンに追加されていません。");
        if (port < 1 || port > 65_535)
            return Reject("UDP ポート番号は 1 から 65535 の範囲で指定してください。");
        SubscribeNetwork();
        return true;
    }

    /// <summary>
    /// 機能: 現在の MultiplayerApi の接続通知を一度だけ購読します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void SubscribeNetwork()
    {
        if (_api is not null)
            return;
        _api = Multiplayer;
        _api.PeerConnected += OnPeerConnected;
        _api.PeerDisconnected += OnPeerDisconnected;
        _api.ConnectedToServer += OnConnectedToServer;
        _api.ConnectionFailed += OnConnectionFailed;
        _api.ServerDisconnected += OnServerDisconnected;
    }

    /// <summary>
    /// 機能: 通信だけを破棄します。参加前の都市の復元と通知は呼び出し側で行います。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void ResetNetwork()
    {
        _resetting = true;
        _peer?.Close();
        if (_api is not null)
            _api.MultiplayerPeer = new OfflineMultiplayerPeer();
        _peer?.Dispose();
        _peer = null;
        _hosting = false;
        _connecting = false;
        _awaitingSnapshot = false;
        _requestRates.Clear();
        _receivingSnapshot = null;
        _receivedChunks = null;
        _receivingRevision = -1;
        _receivingSnapshotId = -1;
        _completedSnapshotId = -1;
        _nextSnapshotId = 0;
        _guestClockOffset = 0;
        _runtimeBroadcastSeconds = 0;
        _resetting = false;
    }

    /// <summary>
    /// 機能: 参加者が受信した共有都市を破棄し、退避した自分の都市と保存可能な時計を復元します。
    /// 引数: なし。
    /// 返り値: bool 型の復元実施可否です。ホストまたはソロでは false です。
    /// </summary>
    private bool RestoreLocalCity()
    {
        if (_localStateBeforeJoin is null)
            return false;
        _state = _localStateBeforeJoin;
        _guestClockOffset = 0;
        _localStateBeforeJoin = null;
        ClearUrbanHistory();
        LastError = "";
        StateChanged?.Invoke(_state);
        return true;
    }

    /// <summary>
    /// 機能: 確定状態を購読者へ通知し、ホストでは全参加者へ配信します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void CommitState()
    {
        _snapshotCacheRevision = -1;
        _snapshotCache = null;
        LastError = "";
        StateChanged?.Invoke(_state);
        if (_hosting && IsConnected && _api.GetPeers().Length > 0)
            SendSnapshot(0);
    }

    /// <summary>
    /// 機能: 確定都市のスナップショットを指定参加者または全参加者へ送信します。
    /// 引数: peerId は long 型の参加者識別子です。0 は全参加者を意味します。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void SendSnapshot(long peerId)
    {
        if (_snapshotCache is null || _snapshotCacheRevision != _state.Revision
            || _snapshotCacheTick != _state.SimulationTick || _snapshotCacheRemainder != _state.SimulationRemainderSeconds)
        {
            byte[] json = Encoding.UTF8.GetBytes(CityModel.Serialize(_state));
            if (json.Length > CityModel.MaxPayloadBytes) throw new InvalidDataException("共有都市が許容サイズを超えています。");
            using MemoryStream output = new();
            using (BrotliStream compressor = new(output, CompressionLevel.Fastest, true)) compressor.Write(json);
            _snapshotCache = output.ToArray();
            _snapshotCacheRevision = _state.Revision;
            _snapshotCacheTick = _state.SimulationTick;
            _snapshotCacheRemainder = _state.SimulationRemainderSeconds;
        }
        long snapshotId = ++_nextSnapshotId;
        int count = (_snapshotCache.Length + SnapshotChunkSize - 1) / SnapshotChunkSize;
        for (int index = 0; index < count; index++)
        {
            int start = index * SnapshotChunkSize;
            byte[] chunk = _snapshotCache.AsSpan(start, Math.Min(SnapshotChunkSize, _snapshotCache.Length - start)).ToArray();
            if (peerId == 0) Rpc(nameof(ReceiveSnapshotChunk), snapshotId, _state.Revision, index, count, _snapshotCache.Length, chunk);
            else RpcId(peerId, nameof(ReceiveSnapshotChunk), snapshotId, _state.Revision, index, count, _snapshotCache.Length, chunk);
        }
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 確定済み編集を履歴に追加して購読者と参加者へ通知します。
    /// 引数: candidate は CityState 型の検証済み都市です。
    /// 返り値: bool 型の確定成功可否です。
    /// </summary>
    private bool AcceptUrbanCandidate(CityState candidate)
    {
        int candidateBytes = Encoding.UTF8.GetByteCount(CityModel.Serialize(candidate));
        if (candidateBytes > CityModel.MaxPayloadBytes)
            return Reject("都市の編集データが保存と共同編集の許容サイズを超えます。編集を減らしてください。");
        int historyBytes = candidateBytes + Encoding.UTF8.GetByteCount(CityModel.Serialize(_state));
        _undo.Add((_state.Edits.DeepClone(), candidate.Edits.DeepClone(), candidate.Funds - _state.Funds, historyBytes));
        while (_undo.Count > 1 && (_undo.Count > 64 || _undo.Sum(entry => (long)entry.SizeBytes) > 256 * 1024 * 1024)) _undo.RemoveAt(0);
        _redo.Clear();
        _state = candidate;
        CommitState();
        return true;
    }

    /// <summary>
    /// 機能: 履歴の取り消しまたはやり直しをホストへ送信し、ソロでは直ちに確定します。
    /// 引数: operation は string 型の undo または redo です。
    /// 返り値: bool 型の確定または送信成功可否です。
    /// </summary>
    private bool SubmitHistory(string operation)
    {
        if (_connecting || _awaitingSnapshot) return Reject("初回の都市同期が終わるまでお待ちください。");
        if (IsGuest)
        {
            UrbanCommand command = new() { Operation = operation, ExpectedRevision = _state.Revision };
            return IsConnected && RpcId(1, nameof(RequestUrbanEdit), UrbanModel.SerializeCommand(command)) == Error.Ok
                || Reject("編集履歴の操作を送信できませんでした。");
        }
        return ApplyHistory(operation == "redo", _state.Revision, out string error) || Reject(error);
    }

    /// <summary>
    /// 機能: 履歴の状態へ切り替え、リビジョンを前進させて全員へ配信します。
    /// 引数: redo は bool 型、revision は long 型の要求時の版、error は string 型の出力です。
    /// 返り値: bool 型の確定成功可否です。
    /// </summary>
    private bool ApplyHistory(bool redo, long revision, out string error)
    {
        error = "";
        if (revision != _state.Revision) { error = "別の編集が先に確定しました。最新の都市を確認してください。"; return false; }
        List<(UrbanEdits Before, UrbanEdits After, double FundsDelta, int SizeBytes)> from = redo ? _redo : _undo;
        List<(UrbanEdits Before, UrbanEdits After, double FundsDelta, int SizeBytes)> to = redo ? _undo : _redo;
        if (from.Count == 0) { error = redo ? "やり直せる編集はありません。" : "取り消せる編集はありません。"; return false; }
        if (_state.Revision >= CityModel.MaxRevision) { error = "都市の更新回数が上限に達しました。"; return false; }
        var entry = from[^1];
        double funds = _state.Funds + (redo ? entry.FundsDelta : -entry.FundsDelta);
        if (funds < 0) { error = "履歴を反映するための資金が不足しています。"; return false; }
        _state.Edits = (redo ? entry.After : entry.Before).DeepClone();
        _state.Funds = Math.Min(CityModel.MaxFunds, funds);
        _state.Revision++;
        from.RemoveAt(from.Count - 1);
        to.Add(entry);
        CommitState();
        return true;
    }

    /// <summary>
    /// 機能: 都市切替時に別都市の履歴と通信キャッシュを破棄します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void ClearUrbanHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _snapshotCache = null;
        _snapshotCacheRevision = -1;
    }

    /// <summary>
    /// 機能: ホスト宛て要求の送信者と 1 秒あたり 12 回の頻度制限を検証します。
    /// 引数: sender は long 型のリモート参加者識別子です。
    /// 返り値: bool 型の受付可否です。
    /// </summary>
    private bool AcceptRequest(long sender)
    {
        if (!_hosting || sender <= 1 || !_api.GetPeers().Contains((int)sender))
            return false;
        long now = System.Environment.TickCount64;
        if (!_requestRates.TryGetValue(sender, out var rate) || now - rate.Started >= 1_000)
            rate = (now, 0);
        rate.Count++;
        _requestRates[sender] = rate;
        if (rate.Count > 12)
        {
            if (rate.Count == 13)
                SendError(sender, "操作の送信間隔が短すぎます。少し待ってから操作してください。");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 機能: 操作を送信した参加者だけに拒否理由を確実配送します。
    /// 引数: peerId は long 型の参加者識別子、message は string 型の説明です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void SendError(long peerId, string message) => RpcId(peerId, nameof(ReceiveStatus), message);

    /// <summary>
    /// 機能: 接続された参加者へ初回状態を配信し、人数を通知します。
    /// 引数: peerId は long 型の参加者識別子です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnPeerConnected(long peerId)
    {
        if (_hosting)
            SendSnapshot(peerId);
        PeerCountChanged?.Invoke(PeerCount);
    }

    /// <summary>
    /// 機能: 切断された参加者の頻度制限情報を削除し、人数を通知します。
    /// 引数: peerId は long 型の参加者識別子です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnPeerDisconnected(long peerId)
    {
        _requestRates.Remove(peerId);
        if (!_resetting)
            PeerCountChanged?.Invoke(PeerCount);
    }

    /// <summary>
    /// 機能: 接続完了後にホストの都市を要求します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnConnectedToServer()
    {
        if (_resetting || !IsGuest)
            return;
        _connecting = false;
        StatusChanged?.Invoke("ホストに接続しました。都市を同期中…");
        RpcId(1, nameof(RequestSnapshot));
    }

    /// <summary>
    /// 機能: 接続失敗時に参加前の自分の都市へ戻します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnConnectionFailed()
    {
        if (_resetting || !IsGuest)
            return;
        ResetNetwork();
        RestoreLocalCity();
        Reject("ホストに接続できませんでした。参加前の自分の都市に戻りました。接続先、UDP ポート、ファイアウォールを確認してください。");
        PeerCountChanged?.Invoke(1);
    }

    /// <summary>
    /// 機能: ホスト切断時に共有都市を破棄し、参加前の自分の都市へ戻します。ホスト移譲は行いません。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnServerDisconnected()
    {
        if (_resetting || !IsGuest)
            return;
        ResetNetwork();
        RestoreLocalCity();
        StatusChanged?.Invoke("ホストとの接続が切れました。共同編集を終了し、参加前の自分の都市に戻りました。");
        PeerCountChanged?.Invoke(1);
    }

    /// <summary>
    /// 機能: エラー理由を記録し、状態通知と失敗値を返します。
    /// 引数: message は string 型の説明です。
    /// 返り値: bool 型の false です。
    /// </summary>
    private bool Reject(string message)
    {
        LastError = message;
        StatusChanged?.Invoke(message);
        return false;
    }
}
