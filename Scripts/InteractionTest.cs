using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 通常のゲームシーンに入力を送り、建設、屋上選択、撤去、保存と読込を検証します。
/// </summary>
public partial class InteractionTest : Node
{
    private Main _main = null!;
    private BayfrontMap _map = null!;
    private CollaborationSession _session = null!;
    private OrbitCamera _camera = null!;
    private CityObject _candidate = null!;
    private CityObject? _built;
    private CityState? _savedState;
    private readonly Dictionary<string, byte[]?> _originalFiles = new(StringComparer.OrdinalIgnoreCase);
    private string _savePath = "";
    private bool _saveTouched;
    private bool _finished;
    private int _phase;
    private int _initialCount;
    private int _initialPopulation;
    private double _initialFunds;
    private double _elapsed;
    private double _phaseStarted;

    public static bool IsRequested => OS.GetCmdlineUserArgs().Contains("--interaction-test");

    /**
     * 機能: 初期化済みの通常シーンに入力試験ノードを追加します。
     * 引数: main は Main 型のゲームシーンです。
     * 返り値: なし（void 型）。
     */
    public static void Run(Main main)
    {
        main.AddChild(new InteractionTest { Name = "InteractionTest", _main = main, ProcessMode = ProcessModeEnum.Always });
    }

    /**
     * 機能: 試験で使用する公開ノードと保存ファイルの元データを取得します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    public override void _Ready()
    {
        try
        {
            _map = BayfrontMap.Load();
            _session = _main.GetNode<CollaborationSession>("CollaborationSession");
            _camera = _main.GetChildren().OfType<OrbitCamera>().Single();
            _savePath = ProjectSettings.GlobalizePath(_map.UserSavePath);
            foreach (string path in new[] { _savePath, _savePath + ".bak", _savePath + ".tmp" })
                _originalFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        catch (Exception exception)
        {
            Finish(exception);
        }
    }

    /**
     * 機能: UI レイアウトと物理更新を待ちながら実入力の各段階を実行します。
     * 引数: delta は double 型の経過秒数です。
     * 返り値: なし（void 型）。
     */
    public override void _Process(double delta)
    {
        if (_finished) return;
        try
        {
            _elapsed += delta;
            Assert(_elapsed < 20, "入力試験が 20 秒以内に完了すること");
            if (_elapsed - _phaseStarted < 0.25) return;
            AdvanceTest();
        }
        catch (Exception exception)
        {
            Finish(exception);
        }
    }

    /**
     * 機能: 途中終了時も試験前の保存ファイルを復元します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    public override void _ExitTree()
    {
        try { RestoreSaveFiles(); }
        catch (Exception exception) { GD.PushError($"入力試験の保存復元に失敗しました: {exception}"); }
    }

    // ----------------------------------------

    /**
     * 機能: 住宅建設、屋上選択、保存、撤去、読込を順に検証します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void AdvanceTest()
    {
        switch (_phase)
        {
            case 0:
                FindButton("Ⅱ").EmitSignal(BaseButton.SignalName.Pressed);
                _initialCount = _session.State.Objects.Count;
                _initialFunds = _session.State.Funds;
                _initialPopulation = CityModel.CalculateStatistics(_session.State).Population;
                _candidate = FindVacantHousing();
                _camera.FocusOn(new Vector2(_candidate.X, _candidate.Z), 180);
                SendKey(Key.Key3);
                NextPhase();
                break;
            case 1:
                Assert(Labels().Any(label => label.Text == "暮らしをつくる"), "キー 3 による住宅ツールへの切替");
                SendClick(_camera.Camera.UnprojectPosition(new Vector3(_candidate.X,
                    _map.GroundHeight(_candidate.X, _candidate.Z), _candidate.Z)));
                NextPhase();
                break;
            case 2:
                _built = _session.State.Objects.SingleOrDefault(item => item.Kind == "housing"
                    && Mathf.IsEqualApprox(item.X, _candidate.X) && Mathf.IsEqualApprox(item.Z, _candidate.Z));
                Assert(_built is not null && _session.State.Objects.Count == _initialCount + 1, $"画面クリックによる住宅建設: {_session.LastError}");
                Assert(_session.State.Funds == _initialFunds - CityModel.GetCost(_built!), "画面建設時の資金控除");
                int population = CityModel.CalculateStatistics(_session.State).Population;
                Assert(population > _initialPopulation, "追加住宅の仮人口計算の更新");
                if (_map.RealWorld is { } world)
                    Assert(Labels().Any(label => label.Text == $"{world.Buildings.Count:N0} 棟")
                        && Labels().Any(label => label.Text == $"{_session.State.Objects.Count:N0} 件"), "固定の実建物棟数と追加施設件数を区別した画面表示");
                else
                    Assert(Labels().Any(label => label.Text == $"{population:N0} 人"), "建設時の画面人口指標の更新");
                Assert(Labels().Any(label => label.Text == $"¥ {_session.State.Funds:N0}"), "建設時の画面資金指標の更新");
                SendKey(Key.Key1);
                SendClick(RoofScreenPosition());
                NextPhase();
                break;
            case 3:
                string location = $"位置  {_built!.X * 10:N0}m, {_built.Z * 10:N0}m";
                Assert(Labels().Any(label => label.Text.Contains("集合住宅", StringComparison.Ordinal)
                    && label.Text.Contains(location, StringComparison.Ordinal)), "屋上クリックで建設した住宅を選択できること");
                _savedState = _session.State.DeepClone();
                _saveTouched = true;
                FindButton("都市を保存").EmitSignal(BaseButton.SignalName.Pressed);
                NextPhase();
                break;
            case 4:
                Assert(File.Exists(_savePath), "保存ボタンによるファイル生成");
                Assert(CityModel.TryDeserialize(File.ReadAllText(_savePath), _map.IsLand, out CityState saved, out string error, _map.CanPlace), error);
                Assert(CityModel.Serialize(saved) == CityModel.Serialize(_savedState!), "保存ボタンによる全都市状態の保存");
                SendKey(Key.Key7);
                Assert(Labels().Any(label => label.Text == "街を整える"), "キー 7 による撤去ツールへの切替");
                SendClick(RoofScreenPosition());
                NextPhase();
                break;
            case 5:
                Assert(_session.State.Objects.Count == _initialCount && !_session.State.Objects.Any(item => item.Id == _built!.Id), "屋上クリックによる住宅撤去");
                Assert(_session.State.Funds == _initialFunds - CityModel.GetCost(_built!)
                    + Math.Floor(CityModel.GetCost(_built!) * 0.25), "撤去時の資金返還");
                FindButton("読み込む").EmitSignal(BaseButton.SignalName.Pressed);
                NextPhase();
                break;
            case 6:
                Assert(_session.State.Objects.Any(item => item.Id == _built!.Id), "読込ボタンによる撤去前の住宅復元");
                Assert(_session.State.Revision > _savedState!.Revision, "読込時にリビジョンが前進すること");
                CityState expected = _savedState.DeepClone();
                expected.Revision = _session.State.Revision;
                Assert(CityModel.Serialize(expected) == CityModel.Serialize(_session.State), "読込ボタンによる都市全体の復元");
                GD.Print("実入力試験: 建設・屋上選択・撤去・画面指標・保存読込に成功");
                Finish(null);
                break;
        }
    }

    /**
     * 機能: 四十メートルの配置格子上から既存施設と重ならない住宅用地を探します。
     * 引数: なし。
     * 返り値: CityObject 型の配置可能な住宅候補です。
     */
    private CityObject FindVacantHousing()
    {
        for (float z = CityModel.MinZ + 12; z <= CityModel.MaxZ - 12; z += 4)
            for (float x = CityModel.MinX + 12; x <= CityModel.MaxX - 12; x += 4)
            {
                var candidate = new CityObject
                {
                    Id = "interaction-candidate", Kind = "housing", X = x, Z = z,
                    Variant = ((int)Math.Abs(x + z) / 4) % 8, District = _map.DistrictAt(x, z)
                };
                if (_map.IsLand(x, z) && _map.CanPlace(candidate)
                    && CityModel.TryAdd(_session.State.DeepClone(), candidate, _map.IsLand, out _, _map.CanPlace)) return candidate;
            }
        throw new InvalidOperationException("入力試験用の空地が見つかりませんでした。");
    }

    /**
     * 機能: 建設した住宅の屋上座標を取得し、実際の物理判定がその住宅に当たることを確認します。
     * 引数: なし。
     * 返り値: Vector2 型の屋上の画面座標です。
     */
    private Vector2 RoofScreenPosition()
    {
        StaticBody3D body = _main.FindChildren("*", "StaticBody3D", true, false).OfType<StaticBody3D>()
            .Single(item => item.HasMeta("city_id") && item.GetMeta("city_id").AsString() == _built!.Id);
        CollisionShape3D shape = body.GetChild<CollisionShape3D>(0);
        Vector3 roof = shape.GlobalPosition + Vector3.Up * (((BoxShape3D)shape.Shape).Size.Y * 0.5f - 0.02f);
        Vector2 screen = _camera.Camera.UnprojectPosition(roof);
        Vector3 origin = _camera.Camera.ProjectRayOrigin(screen);
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + _camera.Camera.ProjectRayNormal(screen) * 2400, 1);
        var hit = _main.GetWorld3D().DirectSpaceState.IntersectRay(query);
        Assert(hit.Count > 0 && hit["collider"].AsGodotObject() == body, "屋上クリックが住宅の実コライダーに当たること");
        return screen;
    }

    /**
     * 機能: キーの押下と解放を通常のビューポート入力へ送ります。
     * 引数: key は Key 型のキーコードです。
     * 返り値: なし（void 型）。
     */
    private void SendKey(Key key)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }, true);
    }

    /**
     * 機能: 画面上の指定位置にマウス移動と左クリックを送ります。
     * 引数: screen は Vector2 型のビューポート内の画面座標です。
     * 返り値: なし（void 型）。
     */
    private void SendClick(Vector2 screen)
    {
        Assert(GetViewport().GetVisibleRect().HasPoint(screen), "試験対象が画面内に表示されること");
        GetViewport().PushInput(new InputEventMouseMotion { Position = screen, GlobalPosition = screen }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = screen, GlobalPosition = screen, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = screen, GlobalPosition = screen, ButtonIndex = MouseButton.Left, Pressed = false }, true);
    }

    /**
     * 機能: 表示テキストからゲーム内の操作ボタンを取得します。
     * 引数: text は string 型のボタン表示です。
     * 返り値: Button 型の一致するボタンです。
     */
    private Button FindButton(string text) => _main.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == text);

    /**
     * 機能: 公開シーンツリーから画面のラベルを列挙します。
     * 引数: なし。
     * 返り値: IEnumerable&lt;Label&gt; 型のラベル一覧です。
     */
    private IEnumerable<Label> Labels() => _main.FindChildren("*", "Label", true, false).OfType<Label>();

    /**
     * 機能: レイアウトと物理更新を待つため次の試験段階へ進みます。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void NextPhase()
    {
        _phase++;
        _phaseStarted = _elapsed;
    }

    /**
     * 機能: 試験で変更した通常保存、一時保存、バックアップを元の内容へ復元します。
     * 引数: なし。
     * 返り値: なし（void 型）。
     */
    private void RestoreSaveFiles()
    {
        if (!_saveTouched) return;
        foreach (var pair in _originalFiles)
        {
            if (pair.Value is null)
            {
                if (File.Exists(pair.Key)) File.Delete(pair.Key);
            }
            else File.WriteAllBytes(pair.Key, pair.Value);
        }
        _saveTouched = false;
    }

    /**
     * 機能: 条件が成立しない場合に説明付きの例外を送出します。
     * 引数: condition は bool 型の条件、description は string 型の検証内容です。
     * 返り値: なし（void 型）。
     */
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"入力試験失敗: {description}");
    }

    /**
     * 機能: 保存ファイルを必ず復元し、成功または失敗の終了コードを返します。
     * 引数: exception は Exception? 型の失敗内容で、成功時は null です。
     * 返り値: なし（void 型）。
     */
    private void Finish(Exception? exception)
    {
        if (_finished) return;
        _finished = true;
        try { RestoreSaveFiles(); }
        catch (Exception restoreException) { exception = new AggregateException("入力試験の保存復元に失敗しました。", exception is null ? new[] { restoreException } : new[] { exception, restoreException }); }
        if (exception is not null) GD.PushError(exception.ToString());
        GD.Print(exception is null ? "BAYFRONT_INTERACTION_PASS" : "BAYFRONT_INTERACTION_FAIL");
        GetTree().Quit(exception is null ? 0 : 1);
    }
}
