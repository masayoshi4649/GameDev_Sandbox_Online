using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bayfront;

/// <summary>
/// 地物の輪郭または道路中心線の点です。座標は 1 単位が 10 m の東・南方向です。
/// </summary>
public sealed class UrbanPoint
{
    public float X { get; set; }
    public float Z { get; set; }

    /// <summary>
    /// 機能: JSON 復元用の座標を生成します。
    /// 引数: なし。
    /// 返り値: なし（コンストラクター）。
    /// </summary>
    public UrbanPoint() { }

    /// <summary>
    /// 機能: 指定座標の点を生成します。
    /// 引数: x と z は float 型の都市座標です。
    /// 返り値: なし（コンストラクター）。
    /// </summary>
    public UrbanPoint(float x, float z) { X = x; Z = z; }

    /// <summary>
    /// 機能: 座標を独立した点へ複製します。
    /// 引数: なし。
    /// 返り値: UrbanPoint 型の複製です。
    /// </summary>
    public UrbanPoint DeepClone() => new(X, Z);
}

// ----------------------------------------

/// <summary>
/// 信号の一つの現示です。進行を許可する進入道路と継続秒数を保存します。
/// </summary>
public sealed class SignalPhase
{
    public string Name { get; set; } = "";
    public float DurationSeconds { get; set; } = 30;
    public List<string> GreenRoadIds { get; set; } = new();

    /// <summary>
    /// 機能: 道路一覧を含めて信号現示を深く複製します。
    /// 引数: なし。
    /// 返り値: SignalPhase 型の複製です。
    /// </summary>
    public SignalPhase DeepClone() => new() { Name = Name, DurationSeconds = DurationSeconds, GreenRoadIds = new(GreenRoadIds) };
}

// ----------------------------------------

/// <summary>
/// 原典地物と新設地物で共用する編集可能な定義です。寸法は m、座標は都市座標です。
/// Y は地形からの高さの加算値です。FollowTerrain が false の場合は都市原点からの高さです。
/// Floors が 0 の建物は原典で階数が不明な状態を表し、HeightMeters を使用します。
/// </summary>
public sealed class UrbanObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "building";
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Usage { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float Y { get; set; }
    public bool FollowTerrain { get; set; } = true;
    public float Rotation { get; set; }
    public float WidthMeters { get; set; } = 20;
    public float DepthMeters { get; set; } = 20;
    public int Floors { get; set; }
    public float FloorHeightMeters { get; set; } = 3.2f;
    public float HeightMeters { get; set; } = 10;
    public List<UrbanPoint> Footprint { get; set; } = new();
    public List<UrbanPoint> Path { get; set; } = new();
    public int LanesForward { get; set; } = 1;
    public int LanesBackward { get; set; } = 1;
    public float LaneWidthMeters { get; set; } = 3;
    public float SidewalkWidthMeters { get; set; } = 2;
    public float SpeedKph { get; set; } = 40;
    public int Level { get; set; }
    public string Control { get; set; } = "priority";
    public List<string> ConnectedRoadIds { get; set; } = new();
    public List<SignalPhase> Phases { get; set; } = new();
    public float CycleOffsetSeconds { get; set; }
    public float YellowSeconds { get; set; } = 3;
    public float AllRedSeconds { get; set; } = 1;
    public string JunctionId { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string ApproachRoadId { get; set; } = "";
    public string Material { get; set; } = "concrete";
    public string Roof { get; set; } = "flat";
    public string ColorHex { get; set; } = "#b8c4ce";
    public bool Deleted { get; set; }

    /// <summary>
    /// 機能: 輪郭、道路線、接続関係、信号現示を含めて地物を深く複製します。
    /// 引数: なし。
    /// 返り値: UrbanObject 型の独立した編集定義です。
    /// </summary>
    public UrbanObject DeepClone()
    {
        UrbanObject clone = (UrbanObject)MemberwiseClone();
        clone.Footprint = Footprint.Select(point => point.DeepClone()).ToList();
        clone.Path = Path.Select(point => point.DeepClone()).ToList();
        clone.ConnectedRoadIds = new(ConnectedRoadIds);
        clone.Phases = Phases.Select(phase => phase.DeepClone()).ToList();
        return clone;
    }
}

// ----------------------------------------

/// <summary>
/// 高さ格子へ順番に適用する地形編集です。半径、変化量、目標標高の単位は m です。
/// </summary>
public sealed class TerrainEdit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Mode { get; set; } = "raise";
    public float X { get; set; }
    public float Z { get; set; }
    public float RadiusMeters { get; set; } = 50;
    public float AmountMeters { get; set; } = 2;
    public float TargetHeightMeters { get; set; }

    /// <summary>
    /// 機能: 地形編集ストロークを複製します。
    /// 引数: なし。
    /// 返り値: TerrainEdit 型の複製です。
    /// </summary>
    public TerrainEdit DeepClone() => (TerrainEdit)MemberwiseClone();
}

// ----------------------------------------

/// <summary>
/// 都市の変更だけを保持します。原典を含めるかの設定も保存・共同編集の対象です。
/// </summary>
public sealed class UrbanEdits
{
    public bool IncludeSource { get; set; } = true;
    public bool FreeBuild { get; set; } = true;
    public float? SeaLevelMeters { get; set; }
    public List<UrbanObject> Objects { get; set; } = new();
    public List<TerrainEdit> Terrain { get; set; } = new();

    /// <summary>
    /// 機能: 都市の地物変更と地形変更を独立した履歴へ複製します。
    /// 引数: なし。
    /// 返り値: UrbanEdits 型の複製です。
    /// </summary>
    public UrbanEdits DeepClone() => new()
    {
        IncludeSource = IncludeSource,
        FreeBuild = FreeBuild,
        SeaLevelMeters = SeaLevelMeters,
        Objects = Objects.Select(item => item.DeepClone()).ToList(),
        Terrain = Terrain.Select(edit => edit.DeepClone()).ToList()
    };
}

// ----------------------------------------

/// <summary>
/// ホスト上で一度だけ確定する編集要求です。競合時は再読込を要求します。
/// </summary>
public sealed class UrbanCommand
{
    public string Operation { get; set; } = "upsert";
    public long ExpectedRevision { get; set; }
    public UrbanObject? Object { get; set; }
    public List<UrbanObject> Objects { get; set; } = new();
    public string ObjectId { get; set; } = "";
    public TerrainEdit? Terrain { get; set; }
    public bool? IncludeSource { get; set; }
    public float? SeaLevelMeters { get; set; }
}

// ----------------------------------------

/// <summary>
/// 描画処理を使わずに全地物の変更、信号設定、地形編集要求を検証します。
/// </summary>
public static class UrbanModel
{
    public const int MaxObjects = 60_000;
    public const int MaxTerrainEdits = 20_000;
    public const int MaxCommandBytes = 256 * 1024;
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "building", "road", "intersection", "signal", "prop", "surface" };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    /// <summary>
    /// 機能: 編集要求を検証して地物変更または地形変更を確定します。拒否時は都市を変更しません。
    /// 引数: state は CityState 型、command は UrbanCommand 型、error は string 型の出力、sourceExists は任意の原典 ID 判定 Func&lt;string,bool&gt; 型、sourceLookup は任意の原典解決 Func&lt;string,UrbanObject?&gt; 型です。
    /// 返り値: bool 型の確定成功可否です。
    /// </summary>
    public static bool TryApply(CityState state, UrbanCommand command, out string error, Func<string, bool>? sourceExists = null,
        Func<string, UrbanObject?>? sourceLookup = null)
    {
        error = "";
        if (command is null || command.ExpectedRevision != state.Revision)
            return Fail("別の編集が先に確定しました。最新の都市を確認して再度操作してください。", out error);
        if (state.Revision >= CityModel.MaxRevision)
            return Fail("都市の更新回数が上限に達しました。", out error);
        if (command.Operation == "batch")
        {
            if (command.Objects is null || command.Objects.Count is < 1 or > 256
                || command.Objects.Any(item => item is null) || command.Objects.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != command.Objects.Count)
                return Fail("道路接続の一括編集一覧または地物数が不正です。", out error);
            CityState candidate = state.DeepClone();
            foreach (UrbanObject item in command.Objects)
            {
                if (!TryApply(candidate, new() { ExpectedRevision = candidate.Revision, Object = item }, out error, sourceExists)) return false;
                candidate.Revision = state.Revision;
            }
            if (!Validate(candidate.Edits, out error, sourceExists, sourceLookup)) return false;
            state.Edits = candidate.Edits;
            state.Funds = candidate.Funds;
        }
        else if (command.Operation == "sources")
        {
            if (command.IncludeSource is not bool include)
                return Fail("初期都市の設定が指定されていません。", out error);
            state.Edits.IncludeSource = include;
        }
        else if (command.Operation == "water")
        {
            if (command.SeaLevelMeters is float sea && !Range(sea, -500, 5000))
                return Fail("水面の標高が許容範囲外です。", out error);
            state.Edits.SeaLevelMeters = command.SeaLevelMeters;
        }
        else if (command.Operation == "resetTerrain")
            state.Edits.Terrain.Clear();
        else if (command.Operation == "terrain")
        {
            if (!ValidateTerrain(command.Terrain, out error)) return false;
            if (state.Edits.Terrain.Count >= MaxTerrainEdits)
                return Fail("地形編集の上限に達しました。", out error);
            if (state.Edits.Terrain.Any(edit => edit.Id == command.Terrain!.Id))
                return Fail("同じ地形編集が既に確定しています。", out error);
            state.Edits.Terrain.Add(command.Terrain!.DeepClone());
        }
        else if (command.Operation is "upsert" or "delete")
        {
            UrbanObject? item = command.Object;
            string id = item?.Id ?? command.ObjectId;
            if (!ValidId(id)) return Fail("編集対象の識別子が不正です。", out error);
            UrbanObject? existing = state.Edits.Objects.Find(candidate => candidate.Id == id);
            if (command.Operation == "delete")
            {
                if (existing is null && (item is null || item.SourceId.Length == 0))
                    return Fail("撤去対象が見つかりません。", out error);
                item = (existing ?? item)!.DeepClone();
                item.Deleted = true;
            }
            if (!ValidateObject(item, out error)) return false;
            if (item!.SourceId.Length > 0 && sourceExists is not null && !sourceExists(item.SourceId))
                return Fail("指定された原典地物がこの都市に存在しません。", out error);
            UrbanObject? original = sourceLookup?.Invoke(item.Id);
            if (original is not null && (item.SourceId != item.Id || item.Kind != original.Kind))
                return Fail("原典地物の識別子と種別は保持してください。別種別への建て替えは撤去して新しい地物として作成します。", out error);
            if (!item.Deleted && sourceLookup is not null && !ValidateReferences(item, state.Edits, sourceLookup, out error)) return false;
            if (existing is null && state.Edits.Objects.Count >= MaxObjects)
                return Fail("都市の編集地物数の上限に達しました。", out error);
            double previousCost = existing is null || existing.Deleted ? 0 : GetConstructionCost(existing);
            double nextCost = item.Deleted ? 0 : GetConstructionCost(item);
            double change = Math.Max(0, nextCost - previousCost);
            if (!state.Edits.FreeBuild && state.Funds < change)
                return Fail("設定した規模の工事に必要な資金が不足しています。", out error);
            UrbanObject accepted = item.DeepClone();
            accepted.Rotation = (accepted.Rotation % 360 + 360) % 360;
            if (accepted.Deleted || existing is not null && existing.Kind != accepted.Kind)
                DetachReferences(state.Edits, accepted.Id);
            if (existing is null) state.Edits.Objects.Add(accepted);
            else state.Edits.Objects[state.Edits.Objects.IndexOf(existing)] = accepted;
            if (!state.Edits.FreeBuild)
                state.Funds = Math.Clamp(state.Funds - change + (item.Deleted ? previousCost * 0.25 : 0), 0, CityModel.MaxFunds);
        }
        else return Fail("対応していない都市編集です。", out error);
        state.Revision++;
        return true;
    }

    /// <summary>
    /// 機能: 保存データまたは共有状態の編集定義全体を検証します。
    /// 引数: edits は UrbanEdits 型、error は string 型の出力、sourceExists は任意の原典 ID 判定 Func&lt;string,bool&gt; 型、sourceLookup は任意の原典解決 Func&lt;string,UrbanObject?&gt; 型です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool Validate(UrbanEdits edits, out string error, Func<string, bool>? sourceExists = null,
        Func<string, UrbanObject?>? sourceLookup = null)
    {
        error = "";
        if (edits is null || edits.Objects is null || edits.Terrain is null || edits.Objects.Count > MaxObjects || edits.Terrain.Count > MaxTerrainEdits)
            return Fail("都市の編集一覧が不正です。", out error);
        if (edits.SeaLevelMeters is float sea && !Range(sea, -500, 5000))
            return Fail("保存された水面標高が不正です。", out error);
        HashSet<string> ids = new(StringComparer.Ordinal);
        Dictionary<string, UrbanObject> changes = new(StringComparer.Ordinal);
        foreach (UrbanObject item in edits.Objects)
        {
            if (!ValidateObject(item, out error)) return false;
            if (!ids.Add(item.Id)) return Fail("都市編集に重複した地物識別子があります。", out error);
            if (item.SourceId.Length > 0 && sourceExists is not null && !sourceExists(item.SourceId))
                return Fail("保存された原典地物が現在の都市に存在しません。", out error);
            UrbanObject? original = sourceLookup?.Invoke(item.Id);
            if (original is not null && (item.SourceId != item.Id || item.Kind != original.Kind))
                return Fail("保存された原典地物の識別子または種別が一致しません。", out error);
            changes.Add(item.Id, item);
        }
        if (sourceLookup is not null)
            foreach (UrbanObject item in edits.Objects)
                if (!item.Deleted && !ValidateReferences(item, edits, sourceLookup, out error, changes)) return false;
        ids.Clear();
        foreach (TerrainEdit edit in edits.Terrain)
        {
            if (!ValidateTerrain(edit, out error)) return false;
            if (!ids.Add(edit.Id)) return Fail("都市編集に重複した地形編集があります。", out error);
        }
        return true;
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 交差点や信号の参照先が有効な道路または交差点であるかを検証します。
    /// 引数: item は UrbanObject 型、edits は UrbanEdits 型、sourceLookup は原典解決 Func&lt;string,UrbanObject?&gt; 型、error は string 型の出力、changes は任意の変更済み地物索引 IReadOnlyDictionary&lt;string,UrbanObject&gt; 型です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private static bool ValidateReferences(UrbanObject item, UrbanEdits edits, Func<string, UrbanObject?> sourceLookup, out string error,
        IReadOnlyDictionary<string, UrbanObject>? changes = null)
    {
        error = "";
        /*
         * 機能: 変更済み地物を優先して参照先を解決します。
         * 引数: id は string 型の識別子です。返り値: UrbanObject 型または null です。
         */
        UrbanObject? Resolve(string id) => changes is not null
            ? changes.TryGetValue(id, out UrbanObject? target) ? target : sourceLookup(id)
            : edits.Objects.FirstOrDefault(candidate => candidate.Id == id) ?? sourceLookup(id);
        /*
         * 機能: 有効な参照先が指定種別であるかを判定します。
         * 引数: id と kind は string 型です。返り値: bool 型の一致可否です。
         */
        bool IsKind(string id, string kind) { UrbanObject? target = id == item.Id ? item : Resolve(id); return target is not null && !target.Deleted && target.Kind == kind; }
        if (item.ConnectedRoadIds.Any(id => !IsKind(id, "road")) || item.Phases.Any(phase => phase.GreenRoadIds.Any(id => !IsKind(id, "road")))
            || item.ApproachRoadId.Length > 0 && !IsKind(item.ApproachRoadId, "road"))
            return Fail("接続道路または信号の通行道路が存在しないか、道路以外を参照しています。", out error);
        if (item.Kind == "signal" && (item.ParentId.Length > 0 && !IsKind(item.ParentId, "intersection")
            || item.JunctionId.Length > 0 && !IsKind(item.JunctionId, "intersection")))
            return Fail("信号の所属交差点が存在しないか、交差点以外を参照しています。", out error);
        return true;
    }

    /// <summary>
    /// 機能: 撤去された地物への編集済み交差点や信号の参照を切り離します。
    /// 引数: edits は UrbanEdits 型、id は string 型の撤去対象です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void DetachReferences(UrbanEdits edits, string id)
    {
        foreach (UrbanObject item in edits.Objects)
        {
            item.ConnectedRoadIds.RemoveAll(road => road == id);
            foreach (SignalPhase phase in item.Phases) phase.GreenRoadIds.RemoveAll(road => road == id);
            if (item.ApproachRoadId == id) item.ApproachRoadId = "";
            if (item.ParentId == id) item.ParentId = "";
            if (item.JunctionId == id) item.JunctionId = "";
        }
    }

    /// <summary>
    /// 機能: 地物の寸法、輪郭、道路、信号現示を検証します。
    /// 引数: item は UrbanObject 型、error は string 型の出力です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool ValidateObject(UrbanObject? item, out string error)
    {
        error = "";
        if (item is null || !ValidId(item.Id) || !Kinds.Contains(item.Kind)) return Fail("地物の識別子または種別が不正です。", out error);
        if (item.SourceId is null || item.SourceId.Length > 0 && (!ValidId(item.SourceId) || item.Id != item.SourceId))
            return Fail("原典地物の識別子を変更することはできません。", out error);
        if (!Text(item.Name, 200) || !Text(item.Usage, 120) || !Text(item.Material, 60) || !Text(item.Roof, 40) || !Text(item.Control, 40))
            return Fail("地物の名称、用途または外観設定が不正です。", out error);
        if (!Inside(item.X, item.Z) || !Range(item.Y, -100, 1000) || !Range(item.Rotation, -36_000, 36_000))
            return Fail("地物の座標または回転が不正です。", out error);
        if (item.Footprint is null || item.Path is null || item.ConnectedRoadIds is null || item.Phases is null)
            return Fail("地物の輪郭、道路または信号一覧が不正です。", out error);
        if (item.Footprint.Count > 2048 || item.Path.Count > 4096 || item.ConnectedRoadIds.Count > 64 || item.Phases.Count > 64)
            return Fail("地物の輪郭、道路または信号設定が許容サイズを超えています。", out error);
        if (item.Footprint.Any(point => point is null || !Inside(point.X, point.Z)) || item.Path.Any(point => point is null || !Inside(point.X, point.Z)))
            return Fail("輪郭または道路中心線の座標が不正です。", out error);
        if (!OptionalId(item.JunctionId) || !OptionalId(item.ParentId) || !OptionalId(item.ApproachRoadId)
            || item.ConnectedRoadIds.Any(id => !ValidId(id)) || item.ConnectedRoadIds.Distinct(StringComparer.Ordinal).Count() != item.ConnectedRoadIds.Count)
            return Fail("交差点または信号の接続先識別子が不正です。", out error);
        if (item.ColorHex is null || item.ColorHex.Length is not (7 or 9) || item.ColorHex[0] != '#' || item.ColorHex.Skip(1).Any(value => !Uri.IsHexDigit(value)))
            return Fail("表示色は #RRGGBB または #RRGGBBAA で指定してください。", out error);
        if (!Range(item.WidthMeters, 0.1f, 3000) || !Range(item.DepthMeters, 0.1f, 3000) || !Range(item.HeightMeters, 0.05f, 2000)
            || item.Floors < 0 || item.Floors > 300 || !Range(item.FloorHeightMeters, 1.5f, 20) || item.Floors * item.FloorHeightMeters > 2000)
            return Fail("建物の幅、奥行、高さまたは階数が許容範囲外です。", out error);
        if (item.LanesForward < 0 || item.LanesBackward < 0 || item.LanesForward > 16 || item.LanesBackward > 16
            || !Range(item.LaneWidthMeters, 0.5f, 10) || !Range(item.SidewalkWidthMeters, 0, 40) || !Range(item.SpeedKph, 0, 200)
            || item.Level < -20 || item.Level > 20 || !Range(item.CycleOffsetSeconds, 0, 86_400)
            || !Range(item.YellowSeconds, 0, 30) || !Range(item.AllRedSeconds, 0, 30))
            return Fail("道路の車線、歩道、速度または信号時刻が許容範囲外です。", out error);
        foreach (SignalPhase phase in item.Phases)
            if (phase is null || !Text(phase.Name, 80) || !Range(phase.DurationSeconds, 1, 3600) || phase.GreenRoadIds is null
                || phase.GreenRoadIds.Count > 64 || phase.GreenRoadIds.Any(id => !ValidId(id)) || phase.GreenRoadIds.Distinct(StringComparer.Ordinal).Count() != phase.GreenRoadIds.Count)
                return Fail("信号の現示、継続時間または進入道路が不正です。", out error);
        if (item.Deleted) return true;
        if (item.Kind == "road" && (item.Path.Count < 2 || item.LanesForward + item.LanesBackward == 0 || PathLength(item.Path) < 0.00001))
            return Fail("道路には 2 点以上の中心線と 1 車線以上が必要です。", out error);
        if (item.Kind is "building" or "surface" && item.Footprint.Count > 0 && (item.Footprint.Count < 3 || !ValidPolygon(item.Footprint)))
            return Fail("輪郭は交差しない 3 点以上の多角形で、面積を持つ必要があります。", out error);
        return true;
    }

    /// <summary>
    /// 機能: 地形編集の種別、中心座標、半径、高さを検証します。
    /// 引数: edit は TerrainEdit 型、error は string 型の出力です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool ValidateTerrain(TerrainEdit? edit, out string error)
    {
        error = "";
        if (edit is null || !ValidId(edit.Id) || edit.Mode is not ("raise" or "lower" or "flatten" or "smooth") || !Inside(edit.X, edit.Z)
            || !Range(edit.RadiusMeters, 1, 1000) || !Range(edit.AmountMeters, 0, 200) || !Range(edit.TargetHeightMeters, -500, 5000))
            return Fail("地形編集の種類、座標、半径または高さが不正です。", out error);
        return true;
    }

    /// <summary>
    /// 機能: 設計形状から床面積を計算します。階数不明の原典地物は床面積に含めません。
    /// 引数: item は UrbanObject 型の地物です。
    /// 返り値: double 型の床面積で単位は平方 m です。
    /// </summary>
    public static double FloorAreaSquareMeters(UrbanObject item)
    {
        if (item.Deleted || item.Kind != "building" || item.Floors == 0) return 0;
        double area = item.WidthMeters * item.DepthMeters;
        if (item.Footprint.Count >= 3)
        {
            area = 0;
            for (int index = 0; index < item.Footprint.Count; index++)
            {
                UrbanPoint a = item.Footprint[index], b = item.Footprint[(index + 1) % item.Footprint.Count];
                area += (double)a.X * b.Z - (double)b.X * a.Z;
            }
            area = Math.Abs(area) * 50;
            double width = item.Footprint.Max(point => point.X) - item.Footprint.Min(point => point.X);
            double depth = item.Footprint.Max(point => point.Z) - item.Footprint.Min(point => point.Z);
            if (width > 0 && depth > 0) area *= item.WidthMeters * item.DepthMeters / (width * depth * 100);
        }
        return area * item.Floors;
    }

    /// <summary>
    /// 機能: ユーザーが設定した形状と規模から概算建設費を算出します。原典の実工事費ではありません。
    /// 引数: item は UrbanObject 型の地物です。
    /// 返り値: double 型の概算費用です。
    /// </summary>
    public static double GetConstructionCost(UrbanObject item) => item.Deleted ? 0 : item.Kind switch
    {
        "building" => FloorAreaSquareMeters(item) * 150_000,
        "road" => PathLength(item.Path) * 10 * item.WidthMeters * 25_000,
        "intersection" => item.WidthMeters * item.DepthMeters * 25_000,
        "signal" => 1_500_000 + item.Phases.Count * 50_000,
        "surface" => item.WidthMeters * item.DepthMeters * 8_000,
        _ => item.WidthMeters * item.DepthMeters * item.HeightMeters * 4_000
    };

    /// <summary>
    /// 機能: 設定済み階数、用途、道路寸法から設計定員と維持費を算出します。実在人口を示すものではありません。
    /// 引数: objects は IEnumerable&lt;UrbanObject&gt; 型の有効地物一覧です。
    /// 返り値: UrbanStatistics 型の設計集計です。
    /// </summary>
    public static UrbanStatistics CalculateStatistics(IEnumerable<UrbanObject> objects)
    {
        UrbanStatistics result = new();
        foreach (UrbanObject item in objects)
        {
            if (item.Deleted) continue;
            double area = FloorAreaSquareMeters(item);
            result.FloorAreaSquareMeters += area;
            if (item.Kind == "building")
            {
                result.BuildingCount++;
                if (item.Floors == 0) result.UnknownFloorCount++;
                if (item.Usage.Contains("住宅", StringComparison.Ordinal) || item.Usage is "residential" or "housing")
                    result.ResidentCapacity += (long)Math.Floor(area / 30);
                else if (item.Usage.Length > 0) result.JobCapacity += (long)Math.Floor(area / 18);
            }
            if (item.Kind == "road") result.RoadLengthMeters += PathLength(item.Path) * 10;
            result.EstimatedConstructionCost += GetConstructionCost(item);
        }
        result.DailyMaintenanceCost = result.EstimatedConstructionCost / 365 / 100;
        return result;
    }

    /// <summary>
    /// 機能: 設定した現示と秒数から信号の現在色、通行可能な道路、残り秒数を決定します。
    /// 引数: signal は UrbanObject 型の信号、junction は UrbanObject 型または null の交差点、seconds は double 型の都市内経過秒数です。
    /// 返り値: UrbanSignalState 型の現在現示です。現示未設定または無信号制御の場合は無灯です。
    /// </summary>
    public static UrbanSignalState SignalState(UrbanObject signal, UrbanObject? junction, double seconds)
    {
        UrbanObject control = junction is not null && junction.Phases.Count > 0 ? junction : signal;
        if (control.Deleted || control.Phases.Count == 0 || control.Control is "none" or "uncontrolled" or "priority" or "stop"
            || !double.IsFinite(seconds)) return new();
        double cycle = control.Phases.Sum(phase => phase.DurationSeconds + control.YellowSeconds + control.AllRedSeconds);
        if (!(cycle > 0)) return new();
        double position = ((seconds + control.CycleOffsetSeconds + (ReferenceEquals(control, signal) ? 0 : signal.CycleOffsetSeconds)) % cycle + cycle) % cycle;
        for (int index = 0; index < control.Phases.Count; index++)
        {
            SignalPhase phase = control.Phases[index];
            double duration = phase.DurationSeconds + control.YellowSeconds + control.AllRedSeconds;
            if (position >= duration) { position -= duration; continue; }
            bool permitted = signal.ApproachRoadId.Length == 0 || phase.GreenRoadIds.Contains(signal.ApproachRoadId, StringComparer.Ordinal);
            string aspect = !permitted || position >= phase.DurationSeconds + control.YellowSeconds ? "red"
                : position >= phase.DurationSeconds ? "yellow" : "green";
            double next = position < phase.DurationSeconds ? phase.DurationSeconds
                : position < phase.DurationSeconds + control.YellowSeconds ? phase.DurationSeconds + control.YellowSeconds : duration;
            return new() { Aspect = aspect, PhaseName = phase.Name, PhaseIndex = index, RemainingSeconds = next - position,
                GreenRoadIds = aspect == "green" ? phase.GreenRoadIds.ToArray() : Array.Empty<string>() };
        }
        return new();
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 編集コマンドを通信向け JSON へ変換します。
    /// 引数: command は UrbanCommand 型の要求です。
    /// 返り値: string 型の JSON です。
    /// </summary>
    public static string SerializeCommand(UrbanCommand command) => JsonSerializer.Serialize(command, JsonOptions);

    /// <summary>
    /// 機能: サイズ上限と未知のプロパティを検査して編集要求を復元します。
    /// 引数: json は string 型、command は UrbanCommand 型の出力です。
    /// 返り値: bool 型の復元成功可否です。
    /// </summary>
    public static bool TryDeserializeCommand(string json, out UrbanCommand command)
    {
        command = new();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxCommandBytes || Encoding.UTF8.GetByteCount(json) > MaxCommandBytes) return false;
        try
        {
            UrbanCommand? candidate = JsonSerializer.Deserialize<UrbanCommand>(json, JsonOptions);
            if (candidate is null) return false;
            command = candidate;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// 機能: 任意編集地物の ID 形式を調べます。原典 ID 内の区切り文字にも対応します。
    /// 引数: id は string 型の識別子です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 180
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/');

    /// <summary>
    /// 機能: 空値を許容する接続先 ID を検証します。
    /// 引数: id は string 型の識別子です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private static bool OptionalId(string? id) => id is not null && (id.Length == 0 || ValidId(id));

    /// <summary>
    /// 機能: 制御文字を含まない指定長以内の文字列であるかを調べます。
    /// 引数: value は string 型、limit は int 型の最大文字数です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private static bool Text(string? value, int limit) => value is not null && value.Length <= limit && !value.Any(char.IsControl);

    /// <summary>
    /// 機能: 有限数が指定範囲内であるかを調べます。
    /// 引数: value、minimum、maximum は float 型です。
    /// 返り値: bool 型の範囲内判定です。
    /// </summary>
    private static bool Range(float value, float minimum, float maximum) => float.IsFinite(value) && value >= minimum && value <= maximum;

    /// <summary>
    /// 機能: 座標が編集領域内の有限値かを調べます。
    /// 引数: x と z は float 型の都市座標です。
    /// 返り値: bool 型の範囲内判定です。
    /// </summary>
    private static bool Inside(float x, float z) => Range(x, -450, 450) && Range(z, -450, 450);

    /// <summary>
    /// 機能: 折れ線の全長を算出します。
    /// 引数: points は IReadOnlyList&lt;UrbanPoint&gt; 型の中心線です。
    /// 返り値: double 型の都市座標単位での長さです。
    /// </summary>
    private static double PathLength(IReadOnlyList<UrbanPoint> points)
    {
        double length = 0;
        for (int index = 1; index < points.Count; index++)
            length += Math.Sqrt(Math.Pow(points[index].X - points[index - 1].X, 2) + Math.Pow(points[index].Z - points[index - 1].Z, 2));
        return length;
    }

    /// <summary>
    /// 機能: 多角形の面積と非隣接辺の自己交差を検査します。
    /// 引数: points は IReadOnlyList&lt;UrbanPoint&gt; 型の輪郭です。
    /// 返り値: bool 型の妥当性です。
    /// </summary>
    private static bool ValidPolygon(IReadOnlyList<UrbanPoint> points)
    {
        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            UrbanPoint a = points[index], b = points[(index + 1) % points.Count];
            if (a.X == b.X && a.Z == b.Z) return false;
            area += (double)a.X * b.Z - (double)b.X * a.Z;
            for (int other = index + 2; other < points.Count; other++)
            {
                if (index == 0 && other == points.Count - 1) continue;
                UrbanPoint c = points[other], d = points[(other + 1) % points.Count];
                double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
                if (abC * abD <= 0 && cdA * cdB <= 0 && Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X))
                    && Math.Max(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)) <= Math.Min(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z))) return false;
            }
        }
        return Math.Abs(area) > 0.0001;
    }

    /// <summary>
    /// 機能: 3 点の外積を求めます。
    /// 引数: a、b、c は UrbanPoint 型の点です。
    /// 返り値: double 型の符号付き外積です。
    /// </summary>
    private static double Cross(UrbanPoint a, UrbanPoint b, UrbanPoint c) => ((double)b.X - a.X) * ((double)c.Z - a.Z) - ((double)b.Z - a.Z) * ((double)c.X - a.X);

    /// <summary>
    /// 機能: エラー文を設定して拒否値を返します。
    /// 引数: message は string 型の理由、error は string 型の出力です。
    /// 返り値: bool 型の false です。
    /// </summary>
    private static bool Fail(string message, out string error) { error = message; return false; }
}

// ----------------------------------------

/// <summary>
/// ユーザーが設計した寸法に基づく容量と概算費用です。実測人口または実収支を表しません。
/// </summary>
public sealed class UrbanStatistics
{
    public int BuildingCount { get; set; }
    public int UnknownFloorCount { get; set; }
    public double FloorAreaSquareMeters { get; set; }
    public double RoadLengthMeters { get; set; }
    public long ResidentCapacity { get; set; }
    public long JobCapacity { get; set; }
    public double EstimatedConstructionCost { get; set; }
    public double DailyMaintenanceCost { get; set; }
}

// ----------------------------------------

/// <summary>
/// 信号周期の決定的な計算結果です。描画と将来の車両進入判定で共用します。
/// </summary>
public sealed class UrbanSignalState
{
    public string Aspect { get; init; } = "off";
    public string PhaseName { get; init; } = "";
    public IReadOnlyList<string> GreenRoadIds { get; init; } = Array.Empty<string>();
    public double RemainingSeconds { get; init; }
    public int PhaseIndex { get; init; } = -1;
}
