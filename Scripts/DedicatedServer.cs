using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Bayfront;

/// <summary>
/// 描画ノードを生成せず、中央サーバーの都市所有、シミュレーション、永続化を管理します。
/// </summary>
public partial class DedicatedServer : Node
{
    public static bool IsRequested => OS.HasFeature("dedicated_server") || OS.GetCmdlineUserArgs().Any(argument =>
        argument == "--server" || argument.StartsWith("--server=", StringComparison.Ordinal));

    private CollaborationSession _session = null!;
    private string _savePath = "";
    private int _port = 24567;
    private double _autosaveSeconds = 30;
    private double? _maxSeconds;
    private double _elapsedSeconds;
    private double _sinceSaveCheck;
    private long _savedRevision = -1;
    private long _savedSimulationTick = -1;
    private double _savedSimulationRemainder = -1;
    private bool _running;
    private bool _maySave;
    private bool _failed;
    private bool _stopping;
    private int _stopRequested;
    private PosixSignalRegistration? _terminationSignal;
    private PosixSignalRegistration? _interruptSignal;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// 機能: 通常クライアントと同じパスに共同編集ノードを置き、専用サーバー管理ノードを追加します。
    /// 引数: root は Node 型の Main シーンルートです。
    /// 返り値: なし（void 型）。
    /// </summary>
    public static void Run(Node root)
    {
        root.AddChild(new CollaborationSession { Name = "CollaborationSession" });
        root.AddChild(new DedicatedServer { Name = "DedicatedServer", ProcessMode = ProcessModeEnum.Always });
    }

    /// <summary>
    /// 機能: 引数と既存都市を検証し、専用ホストと初回保存を開始します。不正な既存データは上書きしません。
    /// 引数: なし。
    /// 返り値: なし（void 型）。失敗時は終了コード 1 で停止します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            _session = GetParent().GetNode<CollaborationSession>("CollaborationSession");
            BayfrontMap map = BayfrontMap.Load();
            ParseArguments(OS.GetCmdlineUserArgs(), map.ServerSavePath);
            if (!OS.HasFeature("dedicated_server") && DisplayServer.GetName() != "headless")
                throw new InvalidOperationException("専用サーバーは --headless を付けて起動してください。");
            Engine.MaxFps = 30;
            Engine.PhysicsTicksPerSecond = 10;
            GetTree().AutoAcceptQuit = false;
            CityState state;
            if (File.Exists(_savePath))
            {
                if (new FileInfo(_savePath).Length > CityModel.MaxPayloadBytes)
                    throw new InvalidDataException("保存済み都市が許容サイズを超えています。元ファイルは変更しません。");
                if (!CityModel.TryDeserialize(File.ReadAllText(_savePath, Utf8), map.IsLand, out state, out string error, map.CanPlace)
                    || !CityModel.ValidateMapIdentity(state, map.MapId, map.MapVersion, out error))
                    throw new InvalidDataException($"保存済み都市を検証できません。元ファイルは変更しません。{error}");
                _savedRevision = state.Revision;
                _savedSimulationTick = state.SimulationTick;
                _savedSimulationRemainder = state.SimulationRemainderSeconds;
                int migrated = CityModel.MigrateLegacyObjects(state);
                if (migrated > 0) GD.Print($"旧保存の追加施設 {migrated} 件を任意編集可能な地物へ移行しました。");
                GD.Print($"保存済み都市を読み込みました。リビジョン={state.Revision} 地物={state.Objects.Count}");
            }
            else
            {
                state = map.CreateSeedState();
                GD.Print($"新しい中央サーバー都市を作成しました。地物={state.Objects.Count}");
            }

            _session.StatusChanged += OnSessionStatus;
            _session.PeerCountChanged += OnPeerCountChanged;
            if (map.RealWorld is not null)
            {
                UrbanCatalog catalog = new(map.RealWorld);
                UrbanTerrain terrain = new(map.RealWorld);
                terrain.SetEdits(state.Edits);
                _session.Initialize(state, terrain.IsLand);
                _session.ConfigureUrban(catalog.SourceExists, catalog.SourceObject, catalog.GetRemovalChanges);
                _session.ConfigureSimulation(catalog.GetEffective);
                _session.StateChanged += updated => terrain.SetEdits(updated.Edits);
            }
            else _session.Initialize(state, map.IsLand, map.CanPlace);
            if (!_session.Host(_port))
                throw new InvalidOperationException(_session.LastError);
            _maySave = true;
            SaveIfDirty();
            RegisterTerminationSignals();
            _running = true;
            GD.Print($"専用サーバーの描画ノード数: {GetParent().FindChildren("*", "VisualInstance3D", true, false).Count}");
            GD.Print($"BAYFRONT_SERVER_READY ポート={_port} 表示={DisplayServer.GetName()} 地図={state.MapId} 地図バージョン={state.MapVersion} 保存先={_savePath} 自動保存間隔={_autosaveSeconds.ToString(CultureInfo.InvariantCulture)}秒");
        }
        catch (Exception error)
        {
            FailAndStop(error);
        }
    }

    /// <summary>
    /// 機能: 描画を伴わず都市時間を進め、変更された都市の定期保存と停止要求を処理します。
    /// 引数: delta は double 型の経過秒数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_running || _stopping)
            return;
        try
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                StopNormally();
                return;
            }
            _session.Tick((float)delta);
            _elapsedSeconds += delta;
            _sinceSaveCheck += delta;
            if (_maxSeconds is double limit && _elapsedSeconds >= limit)
            {
                StopNormally();
                return;
            }
            if (_sinceSaveCheck >= _autosaveSeconds)
            {
                _sinceSaveCheck = 0;
                SaveIfDirty();
            }
        }
        catch (Exception error)
        {
            FailAndStop(error);
        }
    }

    /// <summary>
    /// 機能: 正常なウィンドウ終了通知でも最終保存して停止します。
    /// 引数: what は int 型の Godot 通知番号です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest && _running && !_stopping)
        {
            try { StopNormally(); }
            catch (Exception error) { FailAndStop(error); }
        }
    }

    /// <summary>
    /// 機能: SceneTree 終了時の未保存変更を保存し、標準停止シグナルの購読を解除します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    public override void _ExitTree()
    {
        Console.CancelKeyPress -= OnConsoleCancel;
        _terminationSignal?.Dispose();
        _interruptSignal?.Dispose();
        if (_maySave && !_failed)
        {
            try { SaveIfDirty(); }
            catch (Exception error)
            {
                GD.PrintErr($"専用サーバーの終了時保存に失敗しました。{error.Message}");
                GetTree().Quit(1);
            }
        }
        if (_session is not null)
        {
            _session.StatusChanged -= OnSessionStatus;
            _session.PeerCountChanged -= OnPeerCountChanged;
        }
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 専用サーバー引数を厳密に解析します。未知、重複、空値、範囲外は拒否します。
    /// 引数: arguments は string[] 型の Godot ユーザー引数、defaultSavePath は string 型の地図別の既定保存先です。
    /// 返り値: なし（void 型）。不正値は ArgumentException 型の例外を送出します。
    /// </summary>
    private void ParseArguments(string[] arguments, string defaultSavePath)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string savePath = defaultSavePath;
        foreach (string argument in arguments)
        {
            int equals = argument.IndexOf('=');
            string key = equals < 0 ? argument : argument[..equals];
            string value = equals < 0 ? "" : argument[(equals + 1)..];
            if (!seen.Add(key))
                throw new ArgumentException($"引数が重複しています: {key}");
            switch (key)
            {
                case "--server" when equals < 0:
                    break;
                case "--port" when equals >= 0:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _port) || _port < 1 || _port > 65_535)
                        throw new ArgumentException("--port は 1 から 65535 の整数で指定してください。");
                    break;
                case "--save-path" when equals >= 0 && !string.IsNullOrWhiteSpace(value):
                    savePath = value;
                    break;
                case "--autosave-seconds" when equals >= 0:
                    _autosaveSeconds = ParseSeconds(value, key);
                    break;
                case "--max-seconds" when equals >= 0:
                    _maxSeconds = ParseSeconds(value, key);
                    break;
                default:
                    throw new ArgumentException($"専用サーバーの引数が不正です: {argument}");
            }
        }

        if (savePath.StartsWith("user://", StringComparison.Ordinal))
            savePath = ProjectSettings.GlobalizePath(savePath);
        if (!Path.IsPathFullyQualified(savePath) || string.IsNullOrWhiteSpace(Path.GetFileName(savePath)))
            throw new ArgumentException("--save-path はファイル名を含む絶対パスまたは user:// パスで指定してください。");
        _savePath = Path.GetFullPath(savePath);
        if (Directory.Exists(_savePath))
            throw new ArgumentException("--save-path にディレクトリは指定できません。");
    }

    /// <summary>
    /// 機能: 有限かつ実用範囲の秒数をカルチャー非依存で読み込みます。
    /// 引数: value は string 型の秒数、key は string 型の引数名です。
    /// 返り値: double 型の 0.1 秒以上 86400 秒以下の値です。
    /// </summary>
    private static double ParseSeconds(string value, string key)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds < 0.1 || seconds > 86_400)
            throw new ArgumentException($"{key} は 0.1 から 86400 の有限な秒数で指定してください。");
        return seconds;
    }

    /// <summary>
    /// 機能: 前回保存から設計または時計が変更された都市を同一ディレクトリの一時ファイルから原子的に置換します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。保存失敗時は例外を呼び出し側へ伝え、既存 primary の直接上書きは行いません。
    /// </summary>
    private void SaveIfDirty()
    {
        if (!_maySave || _session.State.Revision == _savedRevision
            && _session.State.SimulationTick == _savedSimulationTick
            && _session.State.SimulationRemainderSeconds == _savedSimulationRemainder)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
        string temporary = _savePath + ".tmp";
        string backup = _savePath + ".bak";
        string json = CityModel.Serialize(_session.State);
        using (FileStream stream = new(temporary, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 4_096, FileOptions.WriteThrough))
        {
            using StreamWriter writer = new(stream, Utf8, 4_096, true);
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(_savePath))
            File.Replace(temporary, _savePath, backup, true);
        else
            File.Move(temporary, _savePath);
        _savedRevision = _session.State.Revision;
        _savedSimulationTick = _session.State.SimulationTick;
        _savedSimulationRemainder = _session.State.SimulationRemainderSeconds;
        GD.Print($"BAYFRONT_SERVER_SAVED リビジョン={_savedRevision} 計算時刻={_savedSimulationTick}+{_savedSimulationRemainder.ToString("R", CultureInfo.InvariantCulture)} 地物={_session.State.Objects.Count} 保存先={_savePath}");
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: Linux の SIGTERM と SIGINT、コンソール終了要求を安全なメインスレッド停止へ変換します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。追加ライブラリは使用しません。
    /// </summary>
    private void RegisterTerminationSignals()
    {
        Console.CancelKeyPress += OnConsoleCancel;
        if (System.OperatingSystem.IsLinux() || System.OperatingSystem.IsMacOS())
        {
            _terminationSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
            _interruptSignal = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
        }
    }

    /// <summary>
    /// 機能: OS シグナルから強制終了を抑止し、保存可能な停止を要求します。
    /// 引数: context は PosixSignalContext 型の標準シグナル情報です。
    /// 返り値: なし（void 型）。Godot API は呼び出しません。
    /// </summary>
    private void OnPosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Interlocked.Exchange(ref _stopRequested, 1);
    }

    /// <summary>
    /// 機能: コンソールの停止キーを最終保存付きの停止要求へ変換します。
    /// 引数: sender は object? 型の送信元、arguments は ConsoleCancelEventArgs 型のイベント情報です。
    /// 返り値: なし（void 型）。Godot API は呼び出しません。
    /// </summary>
    private void OnConsoleCancel(object? sender, ConsoleCancelEventArgs arguments)
    {
        arguments.Cancel = true;
        Interlocked.Exchange(ref _stopRequested, 1);
    }

    /// <summary>
    /// 機能: 最新都市を保存して参加者を切断し、正常終了します。
    /// 引数: なし。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void StopNormally()
    {
        if (_stopping)
            return;
        _stopping = true;
        _running = false;
        SaveIfDirty();
        _session.Disconnect();
        GD.Print("専用サーバーを正常に停止しました。");
        GetTree().Quit();
    }

    /// <summary>
    /// 機能: 起動または保存失敗時に追加保存を禁止し、失敗終了します。
    /// 引数: error は Exception 型の失敗情報です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void FailAndStop(Exception error)
    {
        _failed = true;
        _maySave = false;
        _running = false;
        _stopping = true;
        _session?.Disconnect();
        GD.PrintErr($"専用サーバーを停止しました。{error.Message}");
        GetTree().Quit(1);
    }

    /// <summary>
    /// 機能: ホストの接続状態を標準出力へ通知します。
    /// 引数: message は string 型の状態説明です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnSessionStatus(string message) => GD.Print($"専用サーバー: {message}");

    /// <summary>
    /// 機能: 現在の接続人数を標準出力へ通知します。
    /// 引数: count は int 型のホストを含む人数です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OnPeerCountChanged(int count) => GD.Print($"専用サーバーの接続人数: {count} 人（サーバーを含む）");
}
