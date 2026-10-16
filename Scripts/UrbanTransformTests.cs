using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 地物の一体変換と、都市名に依存しない複数保存を描画なしで検証します。
/// </summary>
public partial class UrbanTransformTests : Node
{
    /// <summary>
    /// 移動・回転・高さ変更の独立性と、都市保存の分離・復元・参照制限を検証します。
    /// 引数: なし。返り値: void。失敗時は非ゼロの終了コードを返します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            TestBuildingTransform(); TestRoadTransform(); TestCitySaves();
            GD.Print("BAYFRONT_EDIT_TRANSFORM_PASS 建物・道路の一体変換・元設計の保持・複数保存・任意都市名・参照制限");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    /// <summary>
    /// 回転済みの凹形建物を中心から変換し、画面上の輪郭と元設計の不変性を検査します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private static void TestBuildingTransform()
    {
        var source = new UrbanObject
        {
            Id = "transform-building", Kind = "building", X = 10, Z = 20, Y = 0.7f, Rotation = 30,
            WidthMeters = 60, DepthMeters = 40, Floors = 5, FloorHeightMeters = 3.4f, Material = "brick", Roof = "gable",
            Footprint = new() { new(8, 18), new(12, 18), new(12, 19), new(10, 19), new(10, 22), new(8, 22) },
            ConnectedRoadIds = new() { "first-road" }, Phases = new() { new() { Name = "現示", GreenRoadIds = new() { "first-road" } } }
        };
        var before = UrbanCatalog.GetFootprint(source);
        var translation = new Vector2(7, -3);
        var changed = UrbanTransform.Apply(source, translation, 90, 1.2f);
        Require(changed.X == 17 && changed.Z == 17 && Near(changed.Y, 1.9f), "水平移動と上下移動が同じ地物へ適用されません。");
        Require(Near(changed.Rotation, 120) && changed.Floors == 5 && changed.Material == "brick" && changed.Roof == "gable", "回転以外の建物設定が失われました。");
        var after = UrbanCatalog.GetFootprint(changed);
        for (var index = 0; index < before.Length; index++)
        {
            var expected = (before[index] - new Vector2(source.X, source.Z)).Rotated(Mathf.Pi / 2) + new Vector2(source.X, source.Z) + translation;
            Require(after[index].DistanceTo(expected) < 0.0001f, "表示される凹形輪郭が移動・回転と一致しません。");
        }
        var restored = UrbanTransform.Apply(changed, -translation, -90, -1.2f);
        Require(Near(restored.X, source.X) && Near(restored.Z, source.Z) && Near(restored.Y, source.Y) && Near(restored.Rotation, source.Rotation), "逆変換で元の位置・角度へ戻りません。");
        var restoredPoints = UrbanCatalog.GetFootprint(restored);
        for (var index = 0; index < before.Length; index++) Require(before[index].DistanceTo(restoredPoints[index]) < 0.0001f, "逆変換で輪郭が変形しました。");
        changed.Footprint[0].X = -100; changed.ConnectedRoadIds.Clear(); changed.Phases[0].GreenRoadIds.Clear();
        Require(source.X == 10 && source.Z == 20 && Near(source.Y, 0.7f) && source.Rotation == 30
            && source.Footprint[0].X == 8 && source.ConnectedRoadIds.Count == 1 && source.Phases[0].GreenRoadIds.Count == 1, "編集案を変更すると元の設計または接続設定が変わりました。");
        Require(Near(UrbanTransform.Apply(source, Vector2.Zero, 360).Rotation, 30), "一回転で元の角度へ戻りません。");
    }

    /// <summary>
    /// 折れ線道路を中心まわりに変換し、線形への適用が一度だけであることを検査します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private static void TestRoadTransform()
    {
        var road = new UrbanObject { Id = "transform-road", Kind = "road", X = 2, Z = 3, Y = 0.4f, FollowTerrain = false,
            WidthMeters = 14, LanesForward = 2, LanesBackward = 1, Path = new() { new(0, 1), new(2, 3), new(5, 3) } };
        var changed = UrbanTransform.Apply(road, new Vector2(10, -4), -90, -0.2f);
        var expected = new[] { new Vector2(10, 1), new Vector2(12, -1), new Vector2(12, -4) };
        Require(changed.X == 12 && changed.Z == -1 && Near(changed.Y, 0.2f) && changed.Rotation == 0 && !changed.FollowTerrain, "道路の基準位置・絶対高さ設定が変換に一致しません。");
        for (var index = 0; index < expected.Length; index++)
            Require(new Vector2(changed.Path[index].X, changed.Path[index].Z).DistanceTo(expected[index]) < 0.0001f, "折れ線道路が中心まわりの移動・回転に追従しません。");
        Require(changed.WidthMeters == road.WidthMeters && changed.LanesForward == 2 && changed.LanesBackward == 1, "道路の移動で車線設定が変わりました。");
        changed.Path[0].X = 100;
        Require(road.Path[0].X == 0 && road.Path[0].Z == 1 && road.X == 2 && road.Z == 3, "道路の編集案が元の線形を変更しました。");
    }

    /// <summary>
    /// 都市名を表示値として保持し、異なる保存の復元・上書き前の控え・不正経路の拒否を検査します。
    /// 引数: なし。返り値: void。試験専用フォルダーは検査後に削除します。
    /// </summary>
    private static void TestCitySaves()
    {
        var testParent = Path.GetFullPath(ProjectSettings.GlobalizePath("user://tests"));
        var root = Path.Combine(testParent, "edit-transform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "saves"); var store = new CitySaveStore(directory);
            var first = new CityState { CityName = "東京ベイ", Revision = 7, Funds = 1_234_567, SimulationTick = 63,
                SimulationRemainderSeconds = 0.25, Day = 4, TimeScale = 2,
                Edits = new() { IncludeSource = false, Objects = new() { new() { Id = "saved-building", Floors = 6, X = 3, Z = 4 } } } };
            first.Simulation.LastProcessedTick = 63; first.Simulation.CompletedTrips = 17;
            var second = new CityState { CityName = "丘陵都市", Funds = 7_654_321, Edits = new() { IncludeSource = false } };
            var firstId = Guid.NewGuid().ToString("N"); var secondId = Guid.NewGuid().ToString("N");
            store.Save(first, firstId); store.Save(second, secondId);
            var entries = store.List();
            Require(entries.Count == 2 && entries.Any(value => value.Name == "東京ベイ") && entries.Any(value => value.Name == "丘陵都市"), "プレイヤー指定名による複数の都市が保存一覧へ現れません。");
            var firstPath = store.PathFor(firstId); var secondPath = store.PathFor(secondId);
            Require(firstPath != secondPath && !Path.GetFileName(firstPath).Contains("東京ベイ", StringComparison.Ordinal), "都市名が保存識別子や別都市の保存先に混ざりました。");
            Require(store.IdFor(firstPath) == firstId && File.ReadAllBytes(firstPath)[0] == (byte)'{', "保存識別子またはUTF-8保存形式が不正です。");
            var savedJson = CityModel.Serialize(first);
            Require(CityModel.TryDeserialize(store.Read(firstPath), (_, _) => true, out var restored, out var error), error);
            Require(CityModel.Serialize(restored) == savedJson && restored.CityName == "東京ベイ" && restored.Simulation.CompletedTrips == 17, "都市名・設計・時計・進行状態が保存前と一致しません。");
            first.CityName = "東京ベイ・増築後"; first.Edits.Objects[0].Floors = 12; store.Save(first, firstId);
            Require(File.ReadAllText(firstPath + ".bak", Encoding.UTF8) == savedJson && !File.Exists(firstPath + ".tmp"), "上書き前の控えが残らないか、一時ファイルが保存後も残っています。");
            Require(CityModel.TryDeserialize(store.Read(secondPath), (_, _) => true, out var unchanged, out error) && unchanged.CityName == second.CityName && unchanged.Funds == second.Funds, "別都市の上書きで保存済み都市が変更されました。");
            first.CityName = "../都市/東京ベイ"; var thirdId = Guid.NewGuid().ToString("N"); store.Save(first, thirdId);
            Require(store.List().Count == 3 && Path.GetDirectoryName(store.PathFor(thirdId)) == directory, "都市名に含まれる区切り文字が保存先の経路として扱われました。");
            foreach (var invalidId in new[] { "", "..", "../" + firstId, "..\\" + firstId, firstId + ".json", Path.Combine(root, firstId) })
            {
                RequireThrows<ArgumentException>(() => store.PathFor(invalidId), "不正な保存識別子が経路として受理されました。");
                RequireThrows<ArgumentException>(() => store.Save(first, invalidId), "不正な保存識別子で書き込みできました。");
            }
            var outside = Path.Combine(root, "outside.json"); File.WriteAllText(outside, savedJson, new UTF8Encoding(false));
            RequireThrows<IOException>(() => store.Read(outside), "保存一覧の外にあるJSONを読み込めました。");
            RequireThrows<IOException>(() => store.Read(Path.Combine(directory, "..", "outside.json")), "親フォルダーを通る経路で一覧外のJSONを読み込めました。");
            Require(store.IdFor(outside) is null, "保存フォルダー外のファイルへ現行都市の識別子を付けました。");
            File.WriteAllText(Path.Combine(directory, "broken.json"), "{破損", new UTF8Encoding(false));
            var invalidNames = new[] { "null", "42", "[]", "{}", JsonSerializer.Serialize(" "), JsonSerializer.Serialize(new string('都', 81)), JsonSerializer.Serialize("都市\n名") };
            for (var index = 0; index < invalidNames.Length; index++)
                File.WriteAllText(Path.Combine(directory, $"invalid-name-{index}.json"), "{\"CityName\":" + invalidNames[index] + "}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "array.json"), "[]", new UTF8Encoding(false));
            Require(store.List().Count == 3, "破損JSON・異なる型・不正都市名が保存一覧を壊しました。");
            var legacy = Path.Combine(directory, "legacy.json"); File.WriteAllText(legacy, "{}", new UTF8Encoding(false));
            Require(store.List().Count == 4 && store.List().Single(value => value.Path == legacy).Name == "名前未設定の都市", "都市名項目がない旧形式の保存が一覧から失われました。");
            Require(CityModel.ValidateCityName("東京ベイ", out _) && !CityModel.ValidateCityName(null, out _) && !CityModel.ValidateCityName("都市\t名", out _), "名称単体の検証が保存・一覧・改名で一致しません。");
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(testParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("edit-transform-", StringComparison.Ordinal)) throw new InvalidOperationException("試験フォルダーの削除範囲が不正です。");
            Directory.Delete(absolute, true);
        }
    }

    /// <summary>
    /// 浮動小数点の計算誤差を許容して値を比較します。
    /// 引数: first/second（float）比較する値。返り値: bool 許容誤差以内の場合はtrue。
    /// </summary>
    private static bool Near(float first, float second) => Math.Abs(first - second) < 0.0001f;

    /// <summary>
    /// 指定した操作が期待する例外で拒否されることを確認します。
    /// 引数: action（Action）操作、message（string）失敗時の説明。型引数: TException（Exception）期待する例外型。
    /// 返り値: void。
    /// </summary>
    private static void RequireThrows<TException>(Action action, string message) where TException : Exception
    {
        try { action(); } catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 期待する条件を満たさない場合に試験を中断します。
    /// 引数: condition（bool）成立条件、message（string）失敗時の説明。返り値: void。
    /// </summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
