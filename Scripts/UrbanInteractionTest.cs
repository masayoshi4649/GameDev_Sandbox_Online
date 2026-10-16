using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 実際の設計画面を通して原典編集、道路、信号、造成と保存復元を検証します。
/// </summary>
public partial class UrbanInteractionTest : Node
{
    private CityGame _game = null!;
    private readonly Dictionary<string, byte[]?> _savedFiles = new(StringComparer.OrdinalIgnoreCase);
    private int _phase;
    private double _elapsed, _lastPhase;
    private bool _finished;
    private UrbanObject _source = null!;
    private string _roadId = "", _signalId = "";
    private Vector3 _point;
    private float _oldHeight;
    private CityState _saved = null!;

    /// <summary>
    /// 初期化済み都市設計画面へ入力試験を追加します。
    /// 引数: game（CityGame）ゲーム。返り値: void。
    /// </summary>
    public static void Run(CityGame game) => game.AddChild(new UrbanInteractionTest { _game = game, Name = "UrbanInteractionTest" });

    /// <summary>
    /// 既存の保存ファイルを試験前に退避します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        var path = ProjectSettings.GlobalizePath(_game.Map.UserSavePath);
        foreach (var file in new[] { path, path + ".bak", path + ".tmp" }) _savedFiles[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    /// <summary>
    /// レイアウト更新を待ちつつ設計入力の手順を実行します。
    /// 引数: delta（double）経過秒。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        try
        {
            Check(_elapsed < 45, "都市設計の入力試験が時間内に完了すること");
            if (_elapsed - _lastPhase < 0.15) return;
            Advance(); _phase++; _lastPhase = _elapsed;
        }
        catch (Exception error) { Finish(error); }
    }

    /// <summary>
    /// 原典建物を編集・撤去し、道路と信号の設計・造成・保存を検証します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void Advance()
    {
        switch (_phase)
        {
            case 0:
                FindButton("Ⅱ").EmitSignal(BaseButton.SignalName.Pressed);
                _source = _game.Catalog.Buildings.OrderBy(b => new Vector2(b.X, b.Z).DistanceSquaredTo(_game.Map.Districts[1].Center)).First();
                UrbanSelectionTests.Run(_game, _source);
                _game.CameraRig.FocusOn(new Vector2(_source.X, _source.Z), 50);
                _game.Select(_source.Id);
                var unchanged = _game.Hud.ReadDesign();
                if (!_game.Catalog.UsesSourceGeometry(unchanged))
                {
                    var left = System.Text.Json.JsonSerializer.SerializeToElement(_source); var right = System.Text.Json.JsonSerializer.SerializeToElement(unchanged);
                    foreach (var p in left.EnumerateObject()) if (p.Value.GetRawText() != right.GetProperty(p.Name).GetRawText()) GD.Print($"未変更設計の差分: {p.Name} = {p.Value.GetRawText()} → {right.GetProperty(p.Name).GetRawText()}");
                }
                Check(_game.Catalog.UsesSourceGeometry(unchanged), "入力欄で未変更の値を丸めて原典の形状を失わないこと");
                Number("Floors").Value = 13; Number("FloorHeight").Value = 3.5; Number("Width").Value = _source.WidthMeters + 5;
                var draft = _game.Hud.ReadDesign(); GD.Print($"設計入力確認: 幅={draft.WidthMeters} 奥行={draft.DepthMeters} 階数={draft.Floors} 階高={draft.FloorHeightMeters} 高さ={draft.HeightMeters}");
                FindButton("設計値を反映").EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 1:
                var edited = _game.Catalog.Find(_source.Id, _game.Session.State)!;
                Check(edited.Floors == 13 && edited.FloorHeightMeters == 3.5f && edited.WidthMeters > _source.WidthMeters, $"原典建物の階数・階高・広さをUIで変更できること: floors={edited.Floors} 階高={edited.FloorHeightMeters} 幅={edited.WidthMeters}/{_source.WidthMeters} エラー={_game.Session.LastError}");
                Check(_source.Floors == 0 && Math.Abs(UrbanCatalog.HeightMetersOf(edited) - 45.5f) < 0.01f, "変更が設計高さに反映し、原典キャッシュを破壊しないこと");
                ((TextEdit)_game.Hud.FindChild("ShapePoints", true, false)).Text = "-12,-10\n12,-10\n15,8\n-12,10";
                FindButton("入力した頂点を反映").EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 2:
                Check(_game.Catalog.Find(_source.Id, _game.Session.State)!.Footprint.Count == 4, "敷地頂点の自由な変更");
                FindButton("選択した地物を撤去").EmitSignal(BaseButton.SignalName.Pressed);
                Check(_game.Catalog.Find(_source.Id, _game.Session.State) is null, "原典建物を撤去できること");
                FindButton("元に戻す").EmitSignal(BaseButton.SignalName.Pressed);
                Check(_game.Catalog.Find(_source.Id, _game.Session.State)!.Floors == 13, "撤去を戻すと変更済み設計が復元されること");
                FindButton("やり直す").EmitSignal(BaseButton.SignalName.Pressed);
                Check(_game.Catalog.Find(_source.Id, _game.Session.State) is null, "撤去を再実行できること");
                FindButton("元に戻す").EmitSignal(BaseButton.SignalName.Pressed);
                _point = new Vector3(_source.X + 4, _game.Terrain.Height(_source.X + 4, _source.Z + 4), _source.Z + 4);
                _game.CameraRig.FocusOn(new Vector2(_point.X, _point.Z), 50);
                _game.SetTool("road"); Number("LanesForward").Value = 2; Number("LanesBackward").Value = 1; Number("LaneWidth").Value = 3.5; Number("Speed").Value = 60;
                _game.HandleWorldClick(_game.CameraRig.Camera.UnprojectPosition(_point - Vector3.Right * 3));
                _game.HandleWorldClick(_game.CameraRig.Camera.UnprojectPosition(_point));
                _game.HandleWorldClick(_game.CameraRig.Camera.UnprojectPosition(_point + Vector3.Back * 4));
                FindButton("描画中の道路を確定 (Enter)").EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 3:
                var road = _game.Session.State.Edits.Objects.Last(o => o.Kind == "road" && !o.Deleted);
                _roadId = road.Id;
                Check(road.Path.Count >= 3 && road.LanesForward == 2 && road.LanesBackward == 1 && road.SpeedKph == 60, "複数頂点と非対称車線の道路を設計できること");
                _game.SetTool("signal");
                ((LineEdit)_game.Hud.FindChild("Approach", true, false)).Text = _roadId;
                ((TextEdit)_game.Hud.FindChild("SignalPhases", true, false)).Text = $"進行 | 17 | {_roadId}\n停止 | 11 | ";
                Number("Yellow").Value = 4; Number("AllRed").Value = 2;
                _game.HandleWorldClick(_game.CameraRig.Camera.UnprojectPosition(_point + Vector3.Right));
                break;
            case 4:
                var signal = _game.Session.State.Edits.Objects.Last(o => o.Kind == "signal" && !o.Deleted); _signalId = signal.Id;
                Check(signal.ApproachRoadId == _roadId && signal.Phases.Count == 2 && signal.Phases[0].DurationSeconds == 17 && signal.YellowSeconds == 4, "信号一基の進入道路・青黄時間をUIで設定できること");
                _game.SetTool("terrain"); Number("BrushRadius").Value = 25; Number("BrushAmount").Value = 5;
                _oldHeight = _game.Terrain.Height(_point.X, _point.Z);
                _game.Brush(_point);
                Check(_game.Terrain.Height(_point.X, _point.Z) > _oldHeight + 0.45f, "造成UIの変化量がCPU地表へ反映すること");
                _saved = _game.Session.State.DeepClone();
                FindButton("都市を保存").EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 5:
                var path = ProjectSettings.GlobalizePath(_game.Map.UserSavePath);
                Check(File.Exists(path), "全編集の保存ファイルを生成すること");
                _game.Select(_signalId); _game.DeleteSelected();
                FindButton("読み込む").EmitSignal(BaseButton.SignalName.Pressed);
                Check(_game.Catalog.Find(_signalId, _game.Session.State)!.Phases[0].DurationSeconds == 17, "保存から信号制御を復元すること");
                Check(Math.Abs(_game.Terrain.Height(_point.X, _point.Z) - (_oldHeight + 0.5f)) < 0.02f, "保存から造成結果を復元すること");
                Check(_game.Catalog.Find(_source.Id, _game.Session.State)!.Floors == 13, "保存から原典建物の設計を復元すること");
                _game.NewCity(false);
                Check(_game.Catalog.GetEffective(_game.Session.State).Count == 0, "更地開始で固定の建物・道路・信号が残らないこと");
                Check(_game.Session.ReplaceState(_saved), "全編集都市へ復帰できること");
                break;
            case 6:
                _game.Select(_source.Id);
                Number("Floors").Value = 77;
                FindButton("1倍").EmitSignal(BaseButton.SignalName.Pressed);
                var revision = _game.Session.State.Revision;
                _game.Session.Tick(CollaborationSession.SecondsPerDay + 0.5f);
                Check(_game.Session.State.Revision == revision, "日付をまたいでも設計版が変わらないこと");
                Check(Number("Floors").Value == 77 && _game.Catalog.Find(_source.Id, _game.Session.State)!.Floors == 13,
                    "時間進行中も未確定の設計入力を保持し、自動で確定しないこと");
                var clock = (Label)_game.Hud.FindChild("SimulationClock", true, false);
                Check(clock.Text.Contains($"経過 {Math.Floor(_game.Session.SimulationSeconds):N0} 秒"), "都市時計の通知が画面へ反映すること");
                FindButton("Ⅱ").EmitSignal(BaseButton.SignalName.Pressed);
                Check(clock.Text.Contains("一時停止"), "都市の停止状態を即座に表示すること");
                GD.Print("都市設計入力試験: 原典建物の階数・階高・敷地・撤去、道路経路・車線、信号現示、造成、更地、保存復元、時間進行中の入力維持に成功");
                Finish(null);
                break;
        }
    }

    /// <summary>
    /// 数値入力を名前で取得します。
    /// 引数: name（string）入力欄名。返り値: SpinBox 対象欄。
    /// </summary>
    private SpinBox Number(string name) => _game.Hud.FindChild(name, true, false) as SpinBox ?? throw new InvalidOperationException($"数値欄がありません: {name}");

    /// <summary>
    /// 全子ノードから表示名が一致する操作ボタンを取得します。
    /// 引数: text（string）表示名。返り値: Button 操作ボタン。
    /// </summary>
    private Button FindButton(string text) => Descendants(_game.Hud).OfType<Button>().FirstOrDefault(b => b.Text == text) ?? throw new InvalidOperationException($"ボタンがありません: {text}");

    /// <summary>
    /// 子孫ノードを列挙します。
    /// 引数: root（Node）開始点。返り値: IEnumerable&lt;Node&gt; 子孫。
    /// </summary>
    private static IEnumerable<Node> Descendants(Node root) { foreach (var child in root.GetChildren()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }

    /// <summary>
    /// 試験の条件を検査します。
    /// 引数: condition（bool）条件、message（string）説明。返り値: void。
    /// </summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>
    /// 保存を元に戻して試験を終了します。
    /// 引数: error（Exception?）失敗。返り値: void。
    /// </summary>
    private void Finish(Exception? error)
    {
        _finished = true; RestoreFiles();
        if (error is null) GD.Print("BAYFRONT_URBAN_INTERACTION_PASS"); else GD.PushError($"都市設計入力試験に失敗: {error}");
        GetTree().Quit(error is null ? 0 : 1);
    }

    /// <summary>
    /// 試験前のファイルを完全に復元します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void RestoreFiles() { foreach (var pair in _savedFiles) { if (pair.Value is null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); } else { Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!); File.WriteAllBytes(pair.Key, pair.Value); } } }

    /// <summary>
    /// 中断時も試験前の保存へ復元します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _ExitTree() { if (!_finished) RestoreFiles(); }
}
