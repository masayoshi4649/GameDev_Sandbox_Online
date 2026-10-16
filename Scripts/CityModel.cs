using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bayfront;

/// <summary>
/// 都市に配置された建築物または道路を表します。座標は 1 単位が約 10 m、回転は度です。
/// </summary>
public sealed class CityObject
{
    [JsonRequired]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired]
    public string Kind { get; set; } = "housing";
    [JsonRequired]
    public float X { get; set; }
    [JsonRequired]
    public float Z { get; set; }
    [JsonRequired]
    public float EndX { get; set; }
    [JsonRequired]
    public float EndZ { get; set; }
    [JsonRequired]
    public float Rotation { get; set; }
    [JsonRequired]
    public int Variant { get; set; }
    [JsonRequired]
    public string District { get; set; } = "";

    /// <summary>
    /// 機能: 配置情報を独立したインスタンスへ複製します。
    /// 引数: なし。
    /// 返り値: CityObject 型の複製です。
    /// </summary>
    public CityObject DeepClone() => (CityObject)MemberwiseClone();
}

// ----------------------------------------

/// <summary>
/// 保存および共同編集で共有する都市の確定状態です。原典は初期値として地図側に置き、全地物の変更を Edits に保持します。
/// </summary>
public sealed class CityState
{
    public string CityName { get; set; } = "新しい都市";
    public UrbanSimulationState Simulation { get; set; } = new();
    /// <summary>
    /// 地図の識別子です。地図情報のない従来の保存は簡略地図として復元します。
    /// </summary>
    public string MapId { get; set; } = CityModel.LegacyMapId;
    /// <summary>
    /// 固定地物と座標基準を共有する地図バージョンです。異なる版の都市は混在させません。
    /// </summary>
    public int MapVersion { get; set; } = 1;
    public UrbanEdits Edits { get; set; } = new();
    public float TimeScale { get; set; } = 1;
    public long SimulationTick { get; set; }
    public double SimulationRemainderSeconds { get; set; }
    [JsonRequired]
    public List<CityObject> Objects { get; set; } = new();
    [JsonRequired]
    public long Revision { get; set; }
    [JsonRequired]
    public double Funds { get; set; } = 25_000_000;
    [JsonRequired]
    public int Day { get; set; } = 1;

    /// <summary>
    /// 機能: 地物一覧を含めて都市状態を深く複製します。
    /// 引数: なし。
    /// 返り値: CityState 型の独立した都市状態です。
    /// </summary>
    public CityState DeepClone() => new()
    {
        CityName = CityName,
        Simulation = Simulation.DeepClone(),
        MapId = MapId,
        MapVersion = MapVersion,
        Edits = Edits.DeepClone(),
        TimeScale = TimeScale,
        SimulationTick = SimulationTick,
        SimulationRemainderSeconds = SimulationRemainderSeconds,
        Objects = Objects.Select(item => item.DeepClone()).ToList(),
        Revision = Revision,
        Funds = Funds,
        Day = Day
    };
}

// ----------------------------------------

/// <summary>
/// 地物から決定的に集計する簡易都市指標です。
/// </summary>
public sealed class CityStatistics
{
    public int Population { get; init; }
    public int Jobs { get; init; }
    public double DailyIncome { get; init; }
    public double DailyExpense { get; init; }
    public double Balance => DailyIncome - DailyExpense;
}

// ----------------------------------------

/// <summary>
/// Godot に依存せず、建設検証、経済計算、保存形式を管理します。
/// </summary>
public static class CityModel
{
    public const string LegacyMapId = "bayfront-sample";
    public const float MinX = -400;
    public const float MaxX = 400;
    public const float MinZ = -400;
    public const float MaxZ = 400;
    public const int MaxObjects = 2400;
    public const int MaxPayloadBytes = 64 * 1024 * 1024;
    public const double MaxFunds = 1_000_000_000_000;
    public const long MaxRevision = 9_000_000_000_000_000;
    public const int MaxDay = 100_000_000;
    public const int SimulationSecondsPerDay = 20;
    public const long MaxSimulationTick = (long)MaxDay * SimulationSecondsPerDay - 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = false
    };

    /// <summary>
    /// 機能: 地物の占有半径を取得します。道路は半幅を返します。
    /// 引数: kind は string 型の地物種別です。
    /// 返り値: float 型の占有半径です。不明な種別は 0 です。
    /// </summary>
    public static float GetRadius(string kind) => kind switch
    {
        "housing" => 7,
        "office" => 8,
        "park" => 9,
        "civic" => 10,
        "road" => 3,
        _ => 0
    };

    /// <summary>
    /// 機能: 建設に必要な費用を算出します。
    /// 引数: item は CityObject 型の建設予定地物です。
    /// 返り値: double 型の費用です。不明な種別は正の無限大です。
    /// </summary>
    public static double GetCost(CityObject item) => item.Kind switch
    {
        "housing" => 180_000,
        "office" => 320_000,
        "park" => 90_000,
        "civic" => 700_000,
        "road" => Math.Ceiling(Distance(item.X, item.Z, item.EndX, item.EndZ)) * 2_800,
        _ => double.PositiveInfinity
    };

    /// <summary>
    /// 機能: 人口、雇用、税収、維持費を乱数なしで集計します。
    /// 引数: state は CityState 型の確定した都市状態です。
    /// 返り値: CityStatistics 型の集計結果です。
    /// </summary>
    public static CityStatistics CalculateStatistics(CityState state)
    {
        int population = 0;
        int jobs = 0;
        double expense = 0;
        foreach (CityObject item in state.Objects)
        {
            switch (item.Kind)
            {
                case "housing":
                    population += 72 + item.Variant % 4 * 24;
                    expense += 1_000;
                    break;
                case "office":
                    jobs += 90 + item.Variant % 4 * 30;
                    expense += 2_200;
                    break;
                case "park":
                    expense += 2_800;
                    break;
                case "civic":
                    jobs += 36;
                    expense += 9_000;
                    break;
                case "road":
                    expense += Math.Ceiling(Distance(item.X, item.Z, item.EndX, item.EndZ)) * 20;
                    break;
            }
        }

        UrbanStatistics urban = UrbanModel.CalculateStatistics(state.Edits.Objects);
        population += (int)Math.Min(int.MaxValue - population, urban.ResidentCapacity);
        jobs += (int)Math.Min(int.MaxValue - jobs, urban.JobCapacity);
        expense += urban.DailyMaintenanceCost;

        return new CityStatistics
        {
            Population = population,
            Jobs = jobs,
            DailyIncome = population * 180d + jobs * 140d,
            DailyExpense = expense
        };
    }

    /// <summary>
    /// 機能: 建設条件と資金を検証し、成功時だけ地物とリビジョンを更新します。
    /// 引数: state は CityState 型、item は CityObject 型、isLand は陸地判定の Func&lt;float,float,bool&gt; 型、error は string 型の出力、canPlace は任意の予約地判定 Func&lt;CityObject,bool&gt; 型です。
    /// 返り値: bool 型の建設成功可否です。
    /// </summary>
    public static bool TryAdd(CityState state, CityObject item, Func<float, float, bool> isLand, out string error,
        Func<CityObject, bool>? canPlace = null)
    {
        error = "";
        if (state.Objects.Count >= MaxObjects)
            return Fail("配置上限に達しました。地物を撤去してください。", out error);
        if (state.Revision >= MaxRevision)
            return Fail("都市の更新回数が上限に達しました。", out error);
        if (!ValidateObject(item, isLand, out error, canPlace))
            return false;
        if (state.Objects.Any(existing => existing.Id == item.Id))
            return Fail("同じ識別子の地物が既に存在します。", out error);
        foreach (CityObject existing in state.Objects)
        {
            if (Overlaps(item, existing))
                return Fail("既存の建築物または道路と重なっています。", out error);
        }

        double cost = GetCost(item);
        if (!double.IsFinite(state.Funds) || state.Funds < cost)
            return Fail("建設資金が不足しています。", out error);
        CityObject accepted = item.DeepClone();
        accepted.Rotation = (accepted.Rotation % 360 + 360) % 360;
        state.Objects.Add(accepted);
        state.Funds -= cost;
        state.Revision++;
        return true;
    }

    /// <summary>
    /// 機能: 識別子で地物を撤去し、建設費の 25 パーセントを返還します。
    /// 引数: state は CityState 型、id は string 型の地物識別子、error は string 型の出力です。
    /// 返り値: bool 型の撤去成功可否です。
    /// </summary>
    public static bool TryRemove(CityState state, string id, out string error)
    {
        error = "";
        if (!ValidId(id))
            return Fail("撤去対象の識別子が不正です。", out error);
        int index = state.Objects.FindIndex(item => item.Id == id);
        if (index < 0)
            return Fail("撤去対象が見つかりません。既に撤去された可能性があります。", out error);
        if (state.Revision >= MaxRevision)
            return Fail("都市の更新回数が上限に達しました。", out error);
        state.Funds = Math.Min(MaxFunds, state.Funds + Math.Floor(GetCost(state.Objects[index]) * 0.25));
        state.Objects.RemoveAt(index);
        state.Revision++;
        return true;
    }

    /// <summary>
    /// 機能: 保存データとネットワーク受信状態の全体整合性を検証します。
    /// 引数: state は CityState 型、isLand は陸地判定の Func&lt;float,float,bool&gt; 型、error は string 型の出力、canPlace は任意の予約地判定 Func&lt;CityObject,bool&gt; 型です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool ValidateState(CityState state, Func<float, float, bool> isLand, out string error,
        Func<CityObject, bool>? canPlace = null)
    {
        error = "";
        if (state is null || state.Objects is null || state.Objects.Count > MaxObjects)
            return Fail("都市データの地物一覧が不正です。", out error);
        if (!ValidateCityName(state.CityName, out error)) return false;
        if (!ValidId(state.MapId) || state.MapVersion < 1 || state.MapVersion > 1_000_000)
            return Fail("都市データの地図識別子または地図バージョンが不正です。", out error);
        if (!double.IsFinite(state.Funds) || state.Funds < 0 || state.Funds > MaxFunds)
            return Fail("都市データの資金が不正です。", out error);
        if (state.Day < 1 || state.Day > MaxDay || state.Revision < 0 || state.Revision > MaxRevision)
            return Fail("都市データの日付またはリビジョンが不正です。", out error);
        if (!float.IsFinite(state.TimeScale) || state.TimeScale < 0 || state.TimeScale > 8)
            return Fail("都市データの時間倍率が不正です。", out error);
        if (!ValidateSimulationClock(state.SimulationTick, state.SimulationRemainderSeconds, state.Day, out error))
            return false;
        if (!UrbanSimulation.Validate(state.Simulation, state.SimulationTick, out error))
            return false;
        if (!UrbanModel.Validate(state.Edits, out error))
            return false;
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        for (int index = 0; index < state.Objects.Count; index++)
        {
            CityObject item = state.Objects[index];
            if (!ValidateObject(item, isLand, out error, canPlace))
                return false;
            if (!identifiers.Add(item.Id))
                return Fail("都市データに重複した識別子があります。", out error);
            for (int other = 0; other < index; other++)
            {
                if (Overlaps(item, state.Objects[other]))
                    return Fail($"都市データ内で地物が重なっています: {item.Id}", out error);
            }
        }
        return true;
    }

    /// <summary>
    /// 都市名が保存・改名・一覧表示に使用できる長さと文字列であることを確認します。
    /// 引数: name（string?）プレイヤー指定の都市名、error（out string）検証失敗の説明。
    /// 返り値: bool 制御文字を含まない1～80文字の空白以外の名称であればtrue。
    /// </summary>
    public static bool ValidateCityName(string? name, out string error)
    {
        error = "";
        return !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl)
            || Fail("都市名は制御文字を含まない 1 ～ 80 文字で指定してください。", out error);
    }

    /// <summary>
    /// 機能: 保存都市または共有都市の地図が、現在読み込んだ固定地物データと一致するかを調べます。
    /// 引数: state は CityState 型の都市、expectedId は string 型の地図識別子、expectedVersion は int 型の地図バージョン、error は string 型の出力です。
    /// 返り値: bool 型の一致判定です。異なる地図の編集データを同じ座標へ適用しません。
    /// </summary>
    public static bool ValidateMapIdentity(CityState state, string expectedId, int expectedVersion, out string error)
    {
        error = "";
        if (state is null || state.MapId != expectedId || state.MapVersion != expectedVersion)
            return Fail($"都市データの地図が現在の地図と一致しません。必要な地図: {expectedId} / バージョン {expectedVersion}。", out error);
        return true;
    }

    /// <summary>
    /// 機能: 実都市の旧版保存にある追加施設を任意編集可能な地物へ一度だけ移行します。簡略地図は変更しません。
    /// 引数: state は CityState 型の検証済み保存都市です。
    /// 返り値: int 型の移行件数です。上限または不正な元データは InvalidOperationException 型の例外を送出します。
    /// </summary>
    public static int MigrateLegacyObjects(CityState state)
    {
        if (state.MapId != "real-bayfront" || state.Objects.Count == 0) return 0;
        if (state.Edits.Objects.Count + state.Objects.Count > UrbanModel.MaxObjects || state.Revision >= MaxRevision)
            throw new InvalidOperationException("旧施設を移行するための地物数または都市更新回数の余地がありません。");
        List<UrbanObject> migrated = new();
        HashSet<string> ids = state.Edits.Objects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (CityObject previous in state.Objects)
        {
            string id = previous.Id;
            int suffix = 1;
            while (!ids.Add(id)) id = "migrated-" + previous.Id + "-" + suffix++;
            int variant = (previous.Variant % 7 + 7) % 7;
            UrbanObject item = new()
            {
                Id = id, X = previous.X, Z = previous.Z, Rotation = previous.Rotation,
                Name = "旧保存から移行した施設", Floors = 0, FollowTerrain = true
            };
            switch (previous.Kind)
            {
                case "housing":
                    item.Kind = "building"; item.Usage = "住宅"; item.WidthMeters = 65 + variant % 2 * 12; item.DepthMeters = 68;
                    item.HeightMeters = 95 + variant * 22; item.ColorHex = "#dfded2";
                    break;
                case "office":
                    item.Kind = "building"; item.Usage = "業務施設"; item.WidthMeters = 80; item.DepthMeters = 70 + variant % 2 * 10;
                    item.HeightMeters = 130 + variant * 30; item.ColorHex = "#648f99";
                    break;
                case "civic":
                    item.Kind = "building"; item.Usage = "公共施設"; item.WidthMeters = 110; item.DepthMeters = 80;
                    item.HeightMeters = 46; item.ColorHex = "#f1eee0";
                    break;
                case "park":
                    item.Kind = "surface"; item.Usage = "公園"; item.WidthMeters = 130; item.DepthMeters = 130;
                    item.HeightMeters = 0.35f; item.Material = "grass"; item.ColorHex = "#83ad76";
                    break;
                case "road":
                    item.Kind = "road"; item.Usage = "道路"; item.WidthMeters = 24; item.DepthMeters = 1; item.HeightMeters = 0.18f;
                    item.Material = "asphalt"; item.ColorHex = "#59656a";
                    item.Path = new() { new(previous.X, previous.Z), new(previous.EndX, previous.EndZ) };
                    break;
                default: throw new InvalidOperationException("移行元施設の種別が不正です。");
            }
            if (!UrbanModel.ValidateObject(item, out string error)) throw new InvalidOperationException("旧施設を移行できません。" + error);
            migrated.Add(item);
        }
        state.Edits.Objects.AddRange(migrated);
        state.Objects.Clear();
        state.Revision++;
        return migrated.Count;
    }

    /// <summary>
    /// 機能: 都市を 1 日進め、税収と維持費を資金に反映します。編集版の加算は旧簡略都市だけに適用します。
    /// 引数: state は CityState 型の都市状態です。
    /// 返り値: なし（void 型）。
    /// </summary>
    public static void AdvanceDay(CityState state)
    {
        bool legacy = state.MapId == LegacyMapId;
        if (state.Day >= MaxDay || legacy && state.Revision >= MaxRevision)
            return;
        if (legacy || !state.Edits.FreeBuild)
            state.Funds = Math.Clamp(state.Funds + CalculateStatistics(state).Balance, 0, MaxFunds);
        state.Day++;
        if (legacy) state.Revision++;
        long firstTickOfDay = (long)(state.Day - 1) * SimulationSecondsPerDay;
        if (state.SimulationTick < firstTickOfDay)
            state.SimulationTick += SimulationSecondsPerDay;
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 保存可能な都市時計を進め、日付の境界では既存の日次処理を適用します。実都市の編集版は変更しません。
    /// 引数: state は CityState 型、seconds は double 型の正の都市内経過秒数です。
    /// 返り値: bool 型の時計変更可否です。不正な経過秒数では状態を変更しません。
    /// </summary>
    public static bool AdvanceSimulationClock(CityState state, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        double before = state.SimulationTick + state.SimulationRemainderSeconds;
        double limit = Math.BitDecrement((double)(MaxSimulationTick + 1));
        if (state.MapId == LegacyMapId)
        {
            long lastDay = Math.Min(MaxDay, (long)state.Day + MaxRevision - state.Revision);
            limit = Math.Min(limit, Math.BitDecrement((double)lastDay * SimulationSecondsPerDay));
        }
        double after = Math.Min(limit, before + seconds);
        if (after <= before) return false;
        state.SimulationTick = (long)Math.Floor(after);
        state.SimulationRemainderSeconds = after - state.SimulationTick;
        int day = (int)(state.SimulationTick / SimulationSecondsPerDay) + 1;
        while (state.Day < day) AdvanceDay(state);
        return true;
    }

    /// <summary>
    /// 機能: 都市時計の範囲と日付との一致を検証します。
    /// 引数: tick は long 型の経過整数秒、remainder は double 型の端数秒、day は int 型の日付、error は string 型の出力です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool ValidateSimulationClock(long tick, double remainder, int day, out string error)
    {
        error = "";
        return tick >= 0 && tick <= MaxSimulationTick && double.IsFinite(remainder) && remainder >= 0 && remainder < 1
            && day == tick / SimulationSecondsPerDay + 1 || Fail("都市データの時計または日付との対応が不正です。", out error);
    }

    // ----------------------------------------

    /// <summary>
    /// 機能: 都市状態を日本語を保持した JSON 文字列へ変換します。
    /// 引数: state は CityState 型の都市状態です。
    /// 返り値: string 型の JSON 文字列です。
    /// </summary>
    public static string Serialize(CityState state) => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>
    /// 機能: サイズと全体整合性を確認しながら JSON を復元します。
    /// 引数: json は string 型、isLand は Func&lt;float,float,bool&gt; 型、state は CityState 型の出力、error は string 型の出力、canPlace は任意の予約地判定 Func&lt;CityObject,bool&gt; 型です。
    /// 返り値: bool 型の復元成功可否です。失敗時の state は空の都市です。
    /// </summary>
    public static bool TryDeserialize(string json, Func<float, float, bool> isLand, out CityState state, out string error,
        Func<CityObject, bool>? canPlace = null)
    {
        state = new CityState();
        error = "";
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes || Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
            return Fail("都市データが空か、許容サイズを超えています。", out error);
        try
        {
            CityState? candidate = JsonSerializer.Deserialize<CityState>(json, JsonOptions);
            if (candidate is null)
                return Fail("都市データが空です。", out error);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                bool hasTick = document.RootElement.TryGetProperty(nameof(CityState.SimulationTick), out _);
                bool hasRemainder = document.RootElement.TryGetProperty(nameof(CityState.SimulationRemainderSeconds), out _);
                if (hasTick != hasRemainder)
                    return Fail("都市データの時計の保存項目が不足しています。", out error);
                if (!hasTick) candidate.SimulationTick = (long)(candidate.Day - 1) * SimulationSecondsPerDay;
            }
            if (!ValidateState(candidate, isLand, out error, canPlace))
                return false;
            state = candidate;
            return true;
        }
        catch (JsonException)
        {
            return Fail("都市データの JSON 形式が不正です。", out error);
        }
    }

    /// <summary>
    /// 機能: 建設要求の JSON を最小限のサイズ制限付きで復元します。
    /// 引数: json は string 型、item は CityObject 型の出力です。
    /// 返り値: bool 型の復元成功可否です。地形と費用は別途検証します。
    /// </summary>
    public static bool TryDeserializeObject(string json, out CityObject item)
    {
        item = new CityObject();
        if (string.IsNullOrEmpty(json) || json.Length > 2_048 || Encoding.UTF8.GetByteCount(json) > 2_048)
            return false;
        try
        {
            CityObject? candidate = JsonSerializer.Deserialize<CityObject>(json, JsonOptions);
            if (candidate is null)
                return false;
            item = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 機能: 単一地物を建設要求用の JSON に変換します。
    /// 引数: item は CityObject 型の地物です。
    /// 返り値: string 型の JSON 文字列です。
    /// </summary>
    public static string SerializeObject(CityObject item) => JsonSerializer.Serialize(item, JsonOptions);

    // ----------------------------------------

    /// <summary>
    /// 機能: 地物の数値、種別、地図範囲、陸地条件を検証します。
    /// 引数: item は CityObject 型、isLand は Func&lt;float,float,bool&gt; 型、error は string 型の出力、canPlace は任意の予約地判定 Func&lt;CityObject,bool&gt; 型です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private static bool ValidateObject(CityObject item, Func<float, float, bool> isLand, out string error,
        Func<CityObject, bool>? canPlace)
    {
        error = "";
        if (item is null || !ValidId(item.Id) || GetRadius(item.Kind) == 0)
            return Fail("地物の識別子または種別が不正です。", out error);
        if (!float.IsFinite(item.X) || !float.IsFinite(item.Z) || !float.IsFinite(item.EndX) || !float.IsFinite(item.EndZ)
            || !float.IsFinite(item.Rotation) || Math.Abs(item.Rotation) > 36_000 || item.Variant < 0 || item.Variant > 7)
            return Fail("地物の座標、回転、外観番号が不正です。", out error);
        if (item.District is null || item.District.Length > 32 || item.District.Any(char.IsControl))
            return Fail("地区名が不正です。", out error);
        if (!Inside(item.X, item.Z))
            return Fail("地図の範囲内に配置してください。", out error);
        if (!isLand(item.X, item.Z))
            return Fail("陸地に配置してください。", out error);
        if (item.Kind == "road")
        {
            double length = Distance(item.X, item.Z, item.EndX, item.EndZ);
            if (!Inside(item.EndX, item.EndZ) || !isLand(item.EndX, item.EndZ))
                return Fail("道路と橋の両端は地図内の陸地に置いてください。", out error);
            if (length < 8 || length > 360)
                return Fail("道路の長さは 8 から 360 の範囲にしてください。", out error);
        }
        else
        {
            float radius = GetRadius(item.Kind);
            if (!Inside(item.X - radius, item.Z - radius) || !Inside(item.X + radius, item.Z + radius)
                || !isLand(item.X - radius, item.Z) || !isLand(item.X + radius, item.Z)
                || !isLand(item.X, item.Z - radius) || !isLand(item.X, item.Z + radius))
                return Fail("建築物全体が陸地に収まる位置に置いてください。", out error);
        }
        if (canPlace is not null && !canPlace(item))
            return Fail("固定地物の占有領域に重なっています。", out error);
        return true;
    }

    /// <summary>
    /// 機能: 地物識別子を英数字、下線、ハイフン、ピリオドに制限します。
    /// 引数: id は string 型の識別子です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 80
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    /// <summary>
    /// 機能: 2 地物の占有領域が重なるかを判定します。道路の交差と接続を許容します。
    /// 引数: first と second は CityObject 型の地物です。
    /// 返り値: bool 型の重なり判定結果です。
    /// </summary>
    private static bool Overlaps(CityObject first, CityObject second)
    {
        if (first.Kind != "road" && second.Kind != "road")
            return Distance(first.X, first.Z, second.X, second.Z) < GetRadius(first.Kind) + GetRadius(second.Kind);
        if (first.Kind == "road" && second.Kind == "road")
        {
            double ax = first.EndX - first.X;
            double az = first.EndZ - first.Z;
            double length = Math.Sqrt(ax * ax + az * az);
            double bx = second.EndX - second.X;
            double bz = second.EndZ - second.Z;
            double otherLength = Math.Sqrt(bx * bx + bz * bz);
            if (Math.Abs(ax * bz - az * bx) > length * otherLength * 0.03)
                return false;
            double perpendicular = Math.Abs(ax * (second.Z - first.Z) - az * (second.X - first.X)) / length;
            if (perpendicular >= 5.5)
                return false;
            double start = ((second.X - first.X) * ax + (second.Z - first.Z) * az) / length;
            double end = ((second.EndX - first.X) * ax + (second.EndZ - first.Z) * az) / length;
            return Math.Min(length, Math.Max(start, end)) - Math.Max(0, Math.Min(start, end)) > 3;
        }

        CityObject road = first.Kind == "road" ? first : second;
        CityObject building = first.Kind == "road" ? second : first;
        return PointSegmentDistance(building.X, building.Z, road.X, road.Z, road.EndX, road.EndZ) < GetRadius(building.Kind) + 3;
    }

    /// <summary>
    /// 機能: 点と線分の最短距離を求めます。
    /// 引数: x、z、ax、az、bx、bz は float 型の点および線分端点座標です。
    /// 返り値: double 型の最短距離です。
    /// </summary>
    private static double PointSegmentDistance(float x, float z, float ax, float az, float bx, float bz)
    {
        double dx = bx - ax;
        double dz = bz - az;
        double lengthSquared = dx * dx + dz * dz;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / lengthSquared, 0, 1);
        return Math.Sqrt(Math.Pow(x - ax - t * dx, 2) + Math.Pow(z - az - t * dz, 2));
    }

    /// <summary>
    /// 機能: 2 点間の距離を求めます。
    /// 引数: ax、az、bx、bz は float 型の座標です。
    /// 返り値: double 型の距離です。
    /// </summary>
    private static double Distance(float ax, float az, float bx, float bz) => Math.Sqrt(Math.Pow(bx - ax, 2) + Math.Pow(bz - az, 2));

    /// <summary>
    /// 機能: 点が編集可能な地図範囲内かを判定します。
    /// 引数: x と z は float 型の地図座標です。
    /// 返り値: bool 型の範囲内判定結果です。
    /// </summary>
    private static bool Inside(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    /// <summary>
    /// 機能: 検証エラーの文字列と失敗値をまとめて返します。
    /// 引数: message は string 型の説明、error は string 型の出力です。
    /// 返り値: bool 型の false です。
    /// </summary>
    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
