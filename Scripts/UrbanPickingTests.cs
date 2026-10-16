using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 壁・屋根・凹形敷地・道路・小さな設備の三次元選択を描画なしで検証します。
/// </summary>
public partial class UrbanPickingTests : Node
{
    /// <summary>
    /// 描画形状と選択形状の一致、手前の地物優先、地形による遮蔽を検証します。
    /// 引数: なし。返り値: void。失敗時は非ゼロの終了コードを返します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            var catalog = new UrbanCatalog(new RealWorldData());
            var rotated = Building("rotated", 0, 0); rotated.Rotation = 45;
            var state = State(rotated);
            Check(catalog.Pick(new Vector3(4, 1, 0.6f), Vector3.Left, state, Flat)?.Id == rotated.Id, "回転した建物の壁を屋根より低い水平視線で選択");
            Check(catalog.Pick(new Vector3(4, 2, 0.6f), new Vector3(-1, -0.1f, 0), state, Flat)?.Id == rotated.Id, "斜め下向きの視線で壁を選択");
            Check(catalog.Pick(new Vector3(0, 6, 0), Vector3.Down, state, Flat)?.Id == rotated.Id, "屋根の選択も維持");
            Check(Math.Abs(catalog.LastPickDistance - 3) < 0.001f, "選択した表面の距離をエージェントの遮蔽判定へ提供");
            Check(catalog.Pick(new Vector3(4, 4, 0.6f), Vector3.Left, state, Flat) is null, "高さを越える空間を選択しない");
            Check(catalog.Pick(new Vector3(1.3f, 6, 1.3f), Vector3.Down, state, Flat) is null, "回転輪郭の外接箱の角を選択しない");

            // ----------------------------------------

            var concave = Building("concave", -1f / 3, -1f / 3);
            concave.WidthMeters = concave.DepthMeters = 40;
            concave.Footprint = new() { new(-2, -2), new(2, -2), new(2, -1), new(-1, -1), new(-1, 2), new(-2, 2) };
            state = State(concave);
            Check(catalog.Pick(new Vector3(4, 1, 1), Vector3.Left, state, Flat)?.Id == concave.Id, "凹形敷地の内側の壁を選択");
            Check(catalog.Pick(new Vector3(1, 6, 1), Vector3.Down, state, Flat) is null, "凹形の欠けた空間を選択しない");
            var hidden = Building("hidden", -5, 1);
            state = State(hidden, concave);
            Check(catalog.Pick(new Vector3(4, 1, 1), Vector3.Left, state, Flat)?.Id == concave.Id, "列挙順によらず手前の壁を選択し背後の建物を選択しない");
            state = State(concave, hidden);
            Check(catalog.Pick(new Vector3(4, 1, 1), Vector3.Left, state, Flat)?.Id == concave.Id, "逆の列挙順でも最短の実形状を選択");

            // ----------------------------------------

            rotated.Y = 1;
            state = State(rotated);
            Check(catalog.Pick(new Vector3(4, 7, 0.6f), Vector3.Left, state, Elevated)?.Id == rotated.Id, "地形高と建物の高さ差分を壁にも適用");
            rotated.FollowTerrain = false; rotated.Y = 6;
            Check(catalog.Pick(new Vector3(4, 7, 0.6f), Vector3.Left, state, Elevated)?.Id == rotated.Id, "地形追従しない建物の絶対高さを使用");
            rotated.Y = 0;
            Check(catalog.Pick(new Vector3(4, 7, 0.6f), new Vector3(-1, -2, 0), state, Elevated) is null, "地面に埋没した建物を地形越しに選択しない");
            rotated.Y = 0; rotated.FollowTerrain = true;
            Check(catalog.Pick(new Vector3(8, 2, 0.6f), Vector3.Left, state, Ridge) is null, "手前の盛土で遮られた建物を選択しない");

            // ----------------------------------------

            var roof = Building("gable", 0, 0); roof.Roof = "gable"; roof.DepthMeters = 40;
            state = State(roof);
            Check(catalog.Pick(new Vector3(0, 3.3f, 4), Vector3.Forward, state, Flat)?.Id == roof.Id, "軒より高い切妻壁と屋根を選択");
            roof.Roof = "hip";
            Check(catalog.Pick(new Vector3(0, 3.2f, 4), Vector3.Forward, state, Flat)?.Id == roof.Id, "軒より高い寄棟屋根を選択");

            // ----------------------------------------

            var road = new UrbanObject { Id = "road", Kind = "road", WidthMeters = 10, HeightMeters = 0.12f,
                Path = new() { new(-5, 0), new(0, 0), new(0, 5) }, SidewalkWidthMeters = 1 };
            var junction = new UrbanObject { Id = "junction", Kind = "intersection", WidthMeters = 10, DepthMeters = 10, HeightMeters = 0.12f };
            state = State(road, junction);
            Check(catalog.Pick(new Vector3(0, 5, 0), Vector3.Down, state, Flat)?.Id == junction.Id, "重なる道路より表示される交差点面を優先");
            Check(catalog.Pick(new Vector3(0, 5, 0), Vector3.Down, state, Flat, "road")?.Id == road.Id, "選択フィルタで道路だけを探索");
            Check(catalog.Pick(new Vector3(0, 5, 0), Vector3.Down, state, Flat, "building") is null && float.IsPositiveInfinity(catalog.LastPickDistance), "対象種別がない場合は距離を無限大へ戻す");
            Check(catalog.Pick(new Vector3(-3, 5, 0), Vector3.Down, state, Flat)?.Id == road.Id, "ポリラインの途中の路面を選択");
            Check(catalog.Pick(new Vector3(0, 5, 3), Vector3.Down, state, Flat)?.Id == road.Id, "曲がった道路の次の線分も選択");
            Check(catalog.Pick(new Vector3(-4, 5, 3), Vector3.Down, state, Flat) is null, "道路線形の外接矩形の空間を選択しない");
            Check(catalog.Pick(new Vector3(0.6f, 5, 3), Vector3.Down, state, Flat) is null, "道路の幅員外を選択しない");
            state = State(road);
            Check(catalog.Pick(new Vector3(-2, 8, 0), Vector3.Down, state, Slope)?.Id == road.Id, "傾斜地の実際の道路高で選択");
            road.Y = 3; road.FollowTerrain = false;
            Check(catalog.Pick(new Vector3(-2, 4, 0), Vector3.Down, state, Flat)?.Id == road.Id, "高架道路を地上道路の高さへ投影しない");
            Check(catalog.Pick(new Vector3(-2, 1, 0), Vector3.Down, state, Flat) is null, "頭上の高架道路を下向き視線で選択しない");

            // ----------------------------------------

            var signal = new UrbanObject { Id = "signal", Kind = "signal", WidthMeters = 5, DepthMeters = 0.6f, HeightMeters = 5.5f };
            state = State(signal);
            Check(catalog.Pick(new Vector3(0.5f, 2, 0), Vector3.Down, state, Flat)?.Id == signal.Id, "柱から張り出す信号灯を直接選択");
            Check(catalog.Pick(new Vector3(0.5f, 0.3f, 2), Vector3.Forward, state, Flat) is null, "灯器の下の空いた空間を大きな箱で誤選択しない");
            Check(catalog.Pick(new Vector3(2, 0.25f, 0), Vector3.Left, state, Flat)?.Id == signal.Id, "小さな信号柱も側面から選択");
            signal.Rotation = 90;
            Check(catalog.Pick(new Vector3(0, 2, -0.5f), Vector3.Down, state, Flat)?.Id == signal.Id, "信号灯の張り出し方向も回転に追従");
            Check(catalog.Pick(Vector3.Zero, Vector3.Zero, state, Flat) is null, "長さゼロの視線を安全に棄却");
            TestSourceTriangles();

            GD.Print("BAYFRONT_PICKING_PASS 壁・回転・凹形・最短距離・地形遮蔽・屋根・道路・交差点・信号灯");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    /// <summary>
    /// 小さな原典形式のGLBを一度だけ読み、既存頂点の表裏・欠けた空間・探索上限を検証します。
    /// 引数: なし。返り値: void。試験ファイルは終了時に削除します。
    /// </summary>
    private static void TestSourceTriangles()
    {
        var path = ProjectSettings.GlobalizePath("user://picking-source-" + Guid.NewGuid().ToString("N") + ".glb");
        var mappingPath = path[..^4] + ".features.json";
        try
        {
            var positions = new float[] { 1, 0, -1, 1, 3, -1, 1, 3, 1, 1, 0, 1 };
            var binary = new byte[60]; Buffer.BlockCopy(positions, 0, binary, 0, 48);
            var corners = new ushort[] { 0, 1, 2, 0, 2, 3 };
            for (var index = 0; index < corners.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(48 + index * 2), corners[index]);
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                asset = new { version = "2.0" }, buffers = new[] { new { byteLength = binary.Length } },
                bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 48 }, new { buffer = 0, byteOffset = 48, byteLength = 12 } },
                accessors = new[] { new { bufferView = 0, componentType = 5126, count = 4, type = "VEC3" }, new { bufferView = 1, componentType = 5123, count = 6, type = "SCALAR" } },
                meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, indices = 1 } } } }
            }));
            var padded = (json.Length + 3) / 4 * 4; var glb = new byte[28 + padded + binary.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546c67); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), padded);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4e4f534a); Array.Fill(glb, (byte)' ', 20, padded); json.CopyTo(glb, 20);
            BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(20 + padded), binary.Length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + padded), 0x004e4942);
            binary.CopyTo(glb, 28 + padded); File.WriteAllBytes(path, glb);
            File.WriteAllText(mappingPath, JsonSerializer.Serialize(new { glbSha256 = Convert.ToHexString(SHA256.HashData(glb)).ToLowerInvariant(),
                featureIds = new[] { "source-wall" }, primitives = new[] { new { triangleFeatures = new[] { 0, 0 } } } }), new UTF8Encoding(false));
            var sources = new UrbanSourceMeshes(); sources.LoadTile(new RealTile("選択試験", path, new Aabb(new Vector3(1, 0, -1), new Vector3(0, 3, 2)), 1, 2));
            Check(sources.IntersectRay("source-wall", new Vector3(4, 1, 0), Vector3.Left, 10, out var distance) && Math.Abs(distance - 3) < 0.001f, "描画に使う原典の壁三角形を直接選択");
            Check(sources.IntersectRay("source-wall", new Vector3(-2, 1, 0), Vector3.Right, 10, out _), "原典の三角面は表裏の向きにかかわらず選択");
            Check(!sources.IntersectRay("source-wall", new Vector3(4, 1, 2), Vector3.Left, 10, out _), "原典詳細形状の空いた空間で誤選択しない");
            Check(!sources.IntersectRay("source-wall", new Vector3(4, 1, 0), Vector3.Left, 2, out _), "手前にある別の地物を越えて原典を選択しない");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(mappingPath)) File.Delete(mappingPath); }
    }

    /// <summary>
    /// 試験地物だけを持つ都市を作成します。
    /// 引数: objects（UrbanObject[]）地物列。返り値: CityState 原典を含まない都市。
    /// </summary>
    private static CityState State(params UrbanObject[] objects) => new() { Edits = new() { IncludeSource = false, Objects = objects.ToList() } };

    /// <summary>
    /// 屋根の低い位置を選択する試験用建物を作ります。
    /// 引数: id（string）識別子、x/z（float）位置。返り値: UrbanObject 高さ30mの建物。
    /// </summary>
    private static UrbanObject Building(string id, float x, float z) => new() { Id = id, Kind = "building", X = x, Z = z, WidthMeters = 20, DepthMeters = 20, HeightMeters = 30, Floors = 0 };

    /// <summary>
    /// 基準の平らな地形高を返します。
    /// 引数: x/z（float）位置。返り値: float 地形高0。
    /// </summary>
    private static float Flat(float x, float z) => 0;

    /// <summary>
    /// 高い位置にある平らな地形高を返します。
    /// 引数: x/z（float）位置。返り値: float 地形高5。
    /// </summary>
    private static float Elevated(float x, float z) => 5;

    /// <summary>
    /// 視線の手前にある盛土の地形高を返します。
    /// 引数: x/z（float）位置。返り値: float 盛土部分は4、それ以外は0。
    /// </summary>
    private static float Ridge(float x, float z) => x > 3 && x < 5 ? 4 : 0;

    /// <summary>
    /// 一定勾配の地形高を返します。
    /// 引数: x/z（float）位置。返り値: float 位置に応じた地形高。
    /// </summary>
    private static float Slope(float x, float z) => x * 0.5f + 4;

    /// <summary>
    /// 選択結果の不一致を試験失敗として報告します。
    /// 引数: condition（bool）成立条件、message（string）期待する挙動。返り値: void。
    /// </summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
