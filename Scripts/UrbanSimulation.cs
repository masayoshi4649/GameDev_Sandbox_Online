using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 描画や物理サーバーに依存せず、実住民の通勤・帰宅と交通網に沿った移動を計算します。
/// </summary>
public sealed class UrbanSimulation
{
    public const int MaxResidents = 512;
    public const int MaxAgents = MaxResidents * 2;
    public const int MaxRoutePoints = 4096;
    private static readonly ConditionalWeakTable<CityState, UrbanSimulation> Engines = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    private long _compiledRevision = -2;
    private readonly Dictionary<string, UrbanObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Network> _networks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UrbanObject> _junctions = new(StringComparer.Ordinal);
    private readonly List<UrbanObject> _homes = new();
    private readonly List<UrbanObject> _workplaces = new();
    private readonly List<UrbanObject> _visitPlaces = new();
    private readonly List<UrbanObject> _stations = new();
    private readonly List<UrbanObject> _ports = new();

    /// <summary>
    /// 機能: 都市ごとにキャッシュした交通網を使い、整数秒の固定刻みで個体を進めます。
    /// 引数: state は CityState 型、design は IReadOnlyList&lt;UrbanObject&gt; 型の確定設計、seconds は double 型の経過秒数です。
    /// 返り値: なし（void 型）。時計自体は呼び出し側が先に進めます。
    /// </summary>
    public static void Step(CityState state, IReadOnlyList<UrbanObject> design, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        Engines.GetOrCreateValue(state).Advance(state, design);
    }

    /// <summary>
    /// 機能: 選択対象の実個体を識別子から取得します。
    /// 引数: state は CityState 型、id は string 型の個体識別子です。
    /// 返り値: UrbanAgent 型、存在しない場合は null です。
    /// </summary>
    public static UrbanAgent? FindAgent(CityState state, string id) => state.Simulation.Agents.Find(value => value.Id == id);

    /// <summary>
    /// 機能: 稼働状態だけを日本語を保持する JSON に変換します。
    /// 引数: simulation は UrbanSimulationState 型です。
    /// 返り値: string 型の JSON です。
    /// </summary>
    public static string Serialize(UrbanSimulationState simulation) => JsonSerializer.Serialize(simulation, JsonOptions);

    /// <summary>
    /// 機能: サイズと数値を検証して稼働状態の同期通知を復元します。
    /// 引数: json は string 型、clock は long 型の確定時計、simulation は UrbanSimulationState 型の出力です。
    /// 返り値: bool 型の復元成功可否です。
    /// </summary>
    public static bool TryDeserialize(string json, long clock, out UrbanSimulationState simulation)
    {
        simulation = new();
        if (string.IsNullOrEmpty(json) || json.Length > 12 * 1024 * 1024 || Encoding.UTF8.GetByteCount(json) > 12 * 1024 * 1024) return false;
        try
        {
            UrbanSimulationState? value = JsonSerializer.Deserialize<UrbanSimulationState>(json, JsonOptions);
            if (!Validate(value, clock, out _)) return false;
            simulation = value!;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// 機能: 保存または受信した住民・経路の上限と有限数を検証します。
    /// 引数: value は UrbanSimulationState 型または null、clock は long 型、error は string 型の出力です。
    /// 返り値: bool 型の検証成功可否です。
    /// </summary>
    public static bool Validate(UrbanSimulationState? value, long clock, out string error)
    {
        error = "稼働中の住民または交通データが不正です。";
        if (value is null || value.Version != 1 || value.LastProcessedTick < 0 || value.LastProcessedTick > clock
            || value.DesignRevision < -1 || value.DesignRevision > CityModel.MaxRevision || value.NextResidentId < 1
            || value.NextResidentId > CityModel.MaxRevision || value.CompletedTrips < 0 || value.WorkArrivals < 0 || value.VisitArrivals < 0
            || value.Residents is null || value.Agents is null || value.Residents.Count > MaxResidents || value.Agents.Count > MaxAgents
            || !Text(value.Limitation, 256)) return false;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (UrbanResident resident in value.Residents)
            if (resident is null || !Text(resident.Id, 128, false) || !ids.Add(resident.Id) || !Text(resident.Name, 128)
                || !Text(resident.HomeId, 180) || !Text(resident.WorkId, 180) || !Text(resident.VisitDestinationId, 180) || !Text(resident.CurrentPlaceId, 180)
                || resident.NextActivity is not ("work" or "visit") || !Modes.Contains(resident.PreferredMode)
                || !double.IsFinite(resident.ReadyAtSeconds) || resident.ReadyAtSeconds < 0 || resident.ReadyAtSeconds > CityModel.MaxSimulationTick + 600d) return false;
        ids.Clear();
        long routePoints = 0;
        foreach (UrbanAgent agent in value.Agents)
        {
            if (agent is null || !Text(agent.Id, 128, false) || !ids.Add(agent.Id) || !Kinds.Contains(agent.Kind) || !Modes.Contains(agent.Mode)
                || !Text(agent.Name, 128) || !Text(agent.OriginId, 180) || !Text(agent.DestinationId, 180) || !Text(agent.Purpose, 128)
                || !Text(agent.Status, 128) || !Text(agent.WaitingReason, 256) || !Position(agent.X, agent.Z, agent.Y)
                || !float.IsFinite(agent.DirectionX) || !float.IsFinite(agent.DirectionZ) || Math.Abs(agent.DirectionX) > 1.01 || Math.Abs(agent.DirectionZ) > 1.01
                || agent.Route is null || agent.Route.Count > MaxRoutePoints || agent.RouteIndex < 0 || agent.RouteIndex > agent.Route.Count
                || !float.IsFinite(agent.SegmentProgress) || agent.SegmentProgress < 0 || agent.SegmentProgress > 1
                || agent.PassengerIds is null || agent.PassengerIds.Count > MaxResidents || agent.PassengerIds.Any(id => !Text(id, 128, false))) return false;
            routePoints += agent.Route.Count;
            if (routePoints > 32_768) return false;
            foreach (TravelWaypoint point in agent.Route)
                if (point is null || !Position(point.X, point.Z, point.Y) || !Modes.Contains(point.Mode) || !Text(point.RoadId, 180)
                    || !Text(point.JunctionId, 180) || !float.IsFinite(point.SpeedMetersPerSecond) || point.SpeedMetersPerSecond <= 0 || point.SpeedMetersPerSecond > 200) return false;
        }
        error = "";
        return true;
    }

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "walk", "car", "train", "ship" };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "person", "car", "train", "ship" };

    /// <summary>
    /// 機能: 不正な文字列や制御文字を除外します。
    /// 引数: value は string 型、length は int 型の上限、empty は bool 型の空文字許可です。
    /// 返り値: bool 型の検証可否です。
    /// </summary>
    private static bool Text(string? value, int length, bool empty = true) => value is not null && value.Length <= length && (empty || value.Length > 0) && !value.Any(char.IsControl);

    /// <summary>
    /// 機能: 都市範囲内の有限な座標を検証します。
    /// 引数: x、z、y は float 型の座標です。
    /// 返り値: bool 型の検証可否です。
    /// </summary>
    private static bool Position(float x, float z, float y) => float.IsFinite(x) && float.IsFinite(z) && float.IsFinite(y)
        && Math.Abs(x) <= 10_000 && Math.Abs(z) <= 10_000 && Math.Abs(y) <= 10_000;

    // ----------------------------------------

    /// <summary>
    /// 機能: 編集時だけ交通網を再構築し、処理済み時計から最大 60 秒を追跡計算します。
    /// 引数: state は CityState 型、design は IReadOnlyList&lt;UrbanObject&gt; 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Advance(CityState state, IReadOnlyList<UrbanObject> design)
    {
        if (_compiledRevision != state.Revision) Compile(state, design);
        UrbanSimulationState runtime = state.Simulation;
        if (runtime.LastProcessedTick > state.SimulationTick) runtime.LastProcessedTick = state.SimulationTick;
        // 旧保存には住民状態がないため、過去の日数分を一度に仮計算しません。
        if (runtime.LastProcessedTick == 0 && runtime.Residents.Count == 0 && state.SimulationTick > 60)
            runtime.LastProcessedTick = state.SimulationTick - 1;
        long target = Math.Min(state.SimulationTick, runtime.LastProcessedTick + 60);
        while (runtime.LastProcessedTick < target)
        {
            runtime.LastProcessedTick++;
            OneSecond(state, runtime.LastProcessedTick);
        }
    }

    /// <summary>
    /// 機能: 確定設計から交通手段別の有向網と用途をコンパイルします。
    /// 引数: state は CityState 型、design は IReadOnlyList&lt;UrbanObject&gt; 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Compile(CityState state, IReadOnlyList<UrbanObject> design)
    {
        _objects.Clear(); _networks.Clear(); _junctions.Clear(); _homes.Clear(); _workplaces.Clear(); _visitPlaces.Clear(); _stations.Clear(); _ports.Clear();
        foreach (UrbanObject item in design)
        {
            if (item.Deleted) continue;
            _objects[item.Id] = item;
            if (item.Kind == "intersection") _junctions[item.Id] = item;
            string usage = Usage(item);
            if (item.Kind == "surface" && usage is "公園" or "park" or "public" or "公共") _visitPlaces.Add(item);
            if (item.Kind != "building") continue;
            if (usage is "housing" or "residential" or "住宅" or "共同住宅") _homes.Add(item);
            else if (usage is "office" or "commercial" or "industrial" or "civic" or "商業" or "事務所" or "職場" or "工場" or "公共") _workplaces.Add(item);
            else if (usage is "station" or "駅") _stations.Add(item);
            else if (usage is "port" or "港") _ports.Add(item);
            if (usage is "commercial" or "商業" or "公園" or "park" or "civic" or "公共") _visitPlaces.Add(item);
        }
        foreach (string mode in Modes)
            _networks[mode] = new Network(mode, design, _junctions.Values);
        _homes.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        _workplaces.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        _visitPlaces.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        foreach (UrbanResident resident in state.Simulation.Residents)
        {
            UrbanAgent? person = state.Simulation.Agents.Find(value => value.Id == resident.Id);
            if (!_workplaces.Any(value => value.Id == resident.WorkId))
            {
                // 旧保存で公園を固定職場へ割り当てていた場合も、訪問先へ移行します。
                if (_visitPlaces.Any(value => value.Id == resident.WorkId) && resident.VisitDestinationId.Length == 0)
                    resident.VisitDestinationId = resident.WorkId;
                resident.WorkId = "";
            }
            if (!_visitPlaces.Any(value => value.Id == resident.VisitDestinationId)) resident.VisitDestinationId = "";
            if (resident.CurrentPlaceId.Length == 0 && person?.Route.Count == 0)
                resident.CurrentPlaceId = resident.AtHome ? resident.HomeId : person.DestinationId;
        }
        _compiledRevision = state.Revision;
        // 設計変更で経路の実在性を再検証します。撤去された道路上を走り続けません。
        if (state.Simulation.DesignRevision >= 0 && state.Simulation.DesignRevision != state.Revision)
        {
            state.Simulation.Agents.RemoveAll(agent => agent.Kind != "person");
            foreach (UrbanAgent person in state.Simulation.Agents)
            {
                person.Route.Clear(); person.RouteIndex = 0; person.SegmentProgress = 0;
                person.Status = "待機"; person.Mode = "walk"; person.WaitingReason = "都市の編集に合わせて経路を再探索します。";
            }
        }
        state.Simulation.DesignRevision = state.Revision;
    }

    /// <summary>
    /// 機能: 用途指定の交通手段部分を除いた用途を取得します。
    /// 引数: item は UrbanObject 型です。
    /// 返り値: string 型の用途です。
    /// </summary>
    private static string Usage(UrbanObject item) => item.Usage.Split(':', 2)[0].Trim().ToLowerInvariant();

    /// <summary>
    /// 機能: 一秒ごとの段階入居、目的地探索、交通移動を実行します。
    /// 引数: state は CityState 型、tick は long 型の確定秒です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void OneSecond(CityState state, long tick)
    {
        UrbanSimulationState runtime = state.Simulation;
        if (tick % 5 == 1) Admit(runtime, tick);
        Dictionary<string, UrbanAgent> people = runtime.Agents.Where(value => value.Kind == "person").ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (UrbanResident resident in runtime.Residents)
        {
            if (!people.TryGetValue(resident.Id, out UrbanAgent? person)) continue;
            if (person.RouteIndex < person.Route.Count)
            {
                Move(state, person, tick);
                if (person.RouteIndex >= person.Route.Count) Arrive(state, resident, person, tick);
                continue;
            }
            if (tick < resident.ReadyAtSeconds) continue;
            if (person.Status == "待機" && tick % 5 != 0) continue;
            PlanTrip(state, resident, person, tick);
        }
        foreach (UrbanAgent vehicle in runtime.Agents.Where(value => value.Kind != "person").ToArray())
        {
            UrbanAgent? passenger = vehicle.PassengerIds.Count > 0 ? people.GetValueOrDefault(vehicle.PassengerIds[0]) : null;
            if (passenger is null || passenger.Mode != vehicle.Kind || passenger.RouteIndex >= passenger.Route.Count)
            {
                runtime.Agents.Remove(vehicle);
                continue;
            }
            vehicle.X = passenger.X; vehicle.Z = passenger.Z; vehicle.Y = passenger.Y;
            vehicle.DirectionX = passenger.DirectionX; vehicle.DirectionZ = passenger.DirectionZ;
            vehicle.Status = passenger.WaitingReason.Length > 0 ? "停車中" : "移動中";
            vehicle.WaitingReason = passenger.WaitingReason;
            vehicle.RouteIndex = passenger.RouteIndex; vehicle.SegmentProgress = passenger.SegmentProgress;
        }
    }

    /// <summary>
    /// 機能: 明示住宅の床面積による定員へ、最大 8 人ずつ段階的に入居させます。
    /// 引数: runtime は UrbanSimulationState 型、tick は long 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Admit(UrbanSimulationState runtime, long tick)
    {
        Dictionary<string, int> occupancy = runtime.Residents.GroupBy(value => value.HomeId).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        int admitted = 0;
        foreach (UrbanObject home in _homes)
        {
            if (runtime.Residents.Count >= MaxResidents) { runtime.Limitation = "個別交通の試作上限 512 人に達しました。建物の設計定員とは異なります。"; break; }
            double floorArea = UrbanModel.FloorAreaSquareMeters(home);
            int capacity = (int)Math.Clamp(Math.Floor(floorArea / 35), 0, 100_000);
            if (occupancy.GetValueOrDefault(home.Id) >= capacity) continue;
            string id = "resident-" + runtime.NextResidentId++;
            string mode = home.Usage.Contains(':') ? home.Usage.Split(':', 2)[1].ToLowerInvariant() : "walk";
            if (!Modes.Contains(mode)) mode = "walk";
            UrbanResident resident = new() { Id = id, Name = "住民 " + (runtime.NextResidentId - 1), HomeId = home.Id, CurrentPlaceId = home.Id, PreferredMode = mode, ReadyAtSeconds = tick + 2 };
            runtime.Residents.Add(resident);
            runtime.Agents.Add(new UrbanAgent { Id = id, Name = resident.Name, OriginId = home.Id, X = home.X, Z = home.Z, Y = home.Y, Status = "在宅", Purpose = "生活" });
            if (++admitted >= 8) break;
        }
    }

    /// <summary>
    /// 機能: 実在する住居・職場を結ぶ経路を探索し、通行不能な場合は理由を保持します。
    /// 引数: state は CityState 型、resident は UrbanResident 型、person は UrbanAgent 型、tick は long 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void PlanTrip(CityState state, UrbanResident resident, UrbanAgent person, long tick)
    {
        if (!_objects.TryGetValue(resident.HomeId, out UrbanObject? home)) { Wait(person, "住居が撤去されています。新しい住居の割当が必要です。"); return; }
        UrbanObject? destination;
        string purpose = "帰宅";
        if (!resident.AtHome) destination = home;
        else
        {
            UrbanObject? workplace = _objects.GetValueOrDefault(resident.WorkId);
            if (workplace is null || !_workplaces.Contains(workplace))
            {
                workplace = _workplaces.Where(value => state.Simulation.Residents.Count(other => other.Id != resident.Id && other.WorkId == value.Id)
                        < Math.Floor(UrbanModel.FloorAreaSquareMeters(value) / 20))
                    .OrderBy(value => Distance(home.X, home.Z, value.X, value.Z)).ThenBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault();
                resident.WorkId = workplace?.Id ?? "";
            }
            UrbanObject? visit = _objects.GetValueOrDefault(resident.VisitDestinationId);
            if (visit is null || !_visitPlaces.Contains(visit))
                visit = _visitPlaces.OrderBy(value => Distance(home.X, home.Z, value.X, value.Z)).ThenBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault();
            resident.VisitDestinationId = visit?.Id ?? "";
            bool visiting = visit is not null && (resident.NextActivity == "visit" || workplace is null);
            destination = visiting ? visit : workplace;
            purpose = visiting ? Usage(visit!) is "commercial" or "商業" ? "買物" : "余暇" : "通勤";
        }
        if (destination is null)
        {
            Wait(person, _workplaces.Count > 0 ? "職場の定員に空きがなく、買物や余暇の行き先もありません。" : "用途が指定された職場、店舗、公園などの行き先がありません。");
            return;
        }
        person.OriginId = resident.CurrentPlaceId.Length > 0 ? resident.CurrentPlaceId : person.OriginId;
        person.DestinationId = destination.Id;
        person.Purpose = purpose;
        // 道路が変わっても現在位置から再探索し、出発地点へ瞬間移動させません。
        UrbanObject origin = new() { X = person.X, Z = person.Z, Y = person.Y, WidthMeters = home.WidthMeters, DepthMeters = home.DepthMeters };
        List<TravelWaypoint>? route;
        string reason;
        if (resident.PreferredMode is "train" or "ship")
            route = TransitRoute(origin, destination, resident.PreferredMode, out reason);
        else route = _networks[resident.PreferredMode].Route(origin, destination, out reason);
        if (route is null || route.Count == 0) { Wait(person, reason); return; }
        if (state.Simulation.Agents.Sum(value => value.Route.Count) + route.Count * 2 > 32_768)
        {
            Wait(person, "同時計算できる交通経路の上限に達したため出発を待っています。");
            return;
        }
        person.Route = route; person.RouteIndex = 0; person.SegmentProgress = 0;
        person.Status = "移動中"; person.WaitingReason = "";
        resident.CurrentPlaceId = "";
        resident.ReadyAtSeconds = tick;
    }

    /// <summary>
    /// 機能: 徒歩による乗降施設への移動と専用線上の移動を一つの目的経路へ連結します。
    /// 引数: origin、destination は UrbanObject 型、mode は string 型、reason は string 型の出力です。
    /// 返り値: List&lt;TravelWaypoint&gt; 型の経路、接続不能の場合は null です。
    /// </summary>
    private List<TravelWaypoint>? TransitRoute(UrbanObject origin, UrbanObject destination, string mode, out string reason)
    {
        List<UrbanObject> stops = mode == "train" ? _stations : _ports;
        reason = mode == "train" ? "駅を 2 か所以上と接続した線路が必要です。" : "港を 2 か所以上と接続した航路が必要です。";
        if (stops.Count < 2) return null;
        UrbanObject start = stops.OrderBy(value => Distance(origin.X, origin.Z, value.X, value.Z)).First();
        UrbanObject end = stops.Where(value => value.Id != start.Id).OrderBy(value => Distance(destination.X, destination.Z, value.X, value.Z)).First();
        List<TravelWaypoint>? first = _networks["walk"].Route(origin, start, out reason);
        if (first is null) { reason = "乗り場へ歩いて到達できません。" + reason; return null; }
        List<TravelWaypoint>? transit = _networks[mode].Route(start, end, out reason);
        if (transit is null) return null;
        List<TravelWaypoint>? last = _networks["walk"].Route(end, destination, out reason);
        if (last is null) { reason = "降り場と目的地を結ぶ歩道がありません。" + reason; return null; }
        if (first.Count + transit.Count + last.Count > MaxRoutePoints) { reason = "交通経路が長すぎます。"; return null; }
        first.AddRange(transit); first.AddRange(last);
        reason = "";
        return first;
    }

    /// <summary>
    /// 機能: 待機状態と具体的な理由を設定します。
    /// 引数: person は UrbanAgent 型、reason は string 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void Wait(UrbanAgent person, string reason)
    {
        person.Status = "待機"; person.WaitingReason = reason; person.Mode = "walk";
    }

    /// <summary>
    /// 機能: 制限速度、赤信号、先行車との間隔を守って一秒分の経路を進めます。
    /// 引数: state は CityState 型、person は UrbanAgent 型、tick は long 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private void Move(CityState state, UrbanAgent person, long tick)
    {
        double remaining = 1;
        person.WaitingReason = "";
        int traversed = 0;
        while (remaining > 0.00001 && person.RouteIndex < person.Route.Count && traversed++ < 64)
        {
            TravelWaypoint target = person.Route[person.RouteIndex];
            person.Mode = target.Mode;
            float distance = Distance(person.X, person.Z, target.X, target.Z) * 10;
            if (target.Mode == "car")
            {
                if (distance <= target.SpeedMetersPerSecond * remaining + 5 && target.JunctionId.Length > 0 && _junctions.TryGetValue(target.JunctionId, out UrbanObject? junction)
                    && UrbanModel.SignalState(junction, junction, tick).Aspect != "off")
                {
                    UrbanSignalState signal = UrbanModel.SignalState(junction, junction, tick);
                    if (signal.Aspect != "green" || !signal.GreenRoadIds.Contains(target.RoadId))
                    {
                        float approach = Math.Min((float)(remaining * target.SpeedMetersPerSecond), Math.Max(0, distance - 3));
                        if (distance > 0.001f)
                        {
                            person.X += (target.X - person.X) * approach / distance;
                            person.Z += (target.Z - person.Z) * approach / distance;
                        }
                        person.WaitingReason = "交差点の赤信号で停車中です。";
                        break;
                    }
                }
                bool blocked = state.Simulation.Agents.Any(other => other.Kind == "person" && other.Id != person.Id && other.Mode == "car"
                    && other.RouteIndex < other.Route.Count && other.Route[other.RouteIndex].RoadId == target.RoadId
                    && Distance(person.X, person.Z, other.X, other.Z) < 0.45f
                    && (other.X - person.X) * (target.X - person.X) + (other.Z - person.Z) * (target.Z - person.Z) > 0.001f);
                if (blocked) { person.WaitingReason = "前方の車両を待っています。"; break; }
            }
            float speed = Math.Max(0.1f, target.SpeedMetersPerSecond);
            double duration = distance / speed;
            if (distance < 0.001f || duration <= remaining)
            {
                if (distance > 0.001f) { person.DirectionX = (target.X - person.X) * 10 / distance; person.DirectionZ = (target.Z - person.Z) * 10 / distance; }
                person.X = target.X; person.Z = target.Z; person.Y = target.Y;
                person.RouteIndex++; person.SegmentProgress = 0;
                remaining -= duration;
            }
            else
            {
                float fraction = (float)(remaining / duration);
                person.DirectionX = (target.X - person.X) * 10 / distance; person.DirectionZ = (target.Z - person.Z) * 10 / distance;
                person.X += (target.X - person.X) * fraction; person.Z += (target.Z - person.Z) * fraction; person.Y += (target.Y - person.Y) * fraction;
                person.SegmentProgress += (1 - person.SegmentProgress) * fraction;
                remaining = 0;
            }
        }
        person.Status = person.Mode == "walk" ? "移動中" : "乗車中";
        if (person.Mode != "walk" && person.RouteIndex < person.Route.Count)
        {
            string vehicleId = "vehicle-" + person.Id;
            UrbanAgent? vehicle = state.Simulation.Agents.Find(value => value.Id == vehicleId);
            if (vehicle is null)
            {
                state.Simulation.Agents.Add(new UrbanAgent
                {
                    Id = vehicleId, Kind = person.Mode, Mode = person.Mode,
                    Name = person.Mode switch { "car" => "乗用車", "train" => "列車", _ => "旅客船" },
                    OriginId = person.OriginId, DestinationId = person.DestinationId, Purpose = person.Purpose,
                    PassengerIds = new() { person.Id }, Route = person.Route.Select(point => point.DeepClone()).ToList(),
                    X = person.X, Z = person.Z, Y = person.Y
                });
            }
        }
    }

    /// <summary>
    /// 機能: 到着を実績に計上して在宅・就業状態に切り替えます。
    /// 引数: state は CityState 型、resident は UrbanResident 型、person は UrbanAgent 型、tick は long 型です。
    /// 返り値: なし（void 型）。
    /// </summary>
    private static void Arrive(CityState state, UrbanResident resident, UrbanAgent person, long tick)
    {
        resident.AtHome = person.DestinationId == resident.HomeId;
        bool work = person.Purpose == "通勤";
        bool visit = person.Purpose is "買物" or "余暇" or "外出";
        string arrivalPurpose = person.Purpose;
        resident.CurrentPlaceId = person.DestinationId;
        if (!resident.AtHome) resident.NextActivity = work ? "visit" : "work";
        person.Status = resident.AtHome ? "在宅" : work ? "就業中" : "滞在中";
        person.Purpose = resident.AtHome ? "生活" : work ? "就業" : arrivalPurpose;
        person.Mode = "walk"; person.WaitingReason = "";
        resident.ReadyAtSeconds = tick + (resident.AtHome ? 15 : 30);
        person.Route.Clear(); person.RouteIndex = 0; person.SegmentProgress = 0;
        state.Simulation.CompletedTrips++;
        if (!resident.AtHome && work) state.Simulation.WorkArrivals++;
        if (!resident.AtHome && visit) state.Simulation.VisitArrivals++;
    }

    /// <summary>
    /// 機能: 二点間の都市座標距離を計算します。
    /// 引数: ax、az、bx、bz は float 型です。
    /// 返り値: float 型の平面距離です。
    /// </summary>
    private static float Distance(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    // ----------------------------------------

    /// <summary>
    /// 道路折線を結ぶ交通手段別の有向グラフです。別階層の交差は接続しません。
    /// </summary>
    private sealed class Network
    {
        private sealed record Vertex(float X, float Z, float Y, string JunctionId);
        private sealed record Edge(int To, float Length, UrbanObject Road);
        private sealed record Segment(int A, int B, UrbanObject Road);
        private sealed record Attachment(Segment Segment, float T, float X, float Z, float Distance);
        private readonly string _mode;
        private readonly List<Vertex> _vertices = new();
        private readonly List<List<Edge>> _edges = new();
        private readonly List<Segment> _segments = new();
        private readonly Dictionary<(int X, int Z, int Level, int Y), int> _nodeIds = new();
        private readonly Dictionary<(int X, int Z), List<Segment>> _spatial = new();
        private readonly Dictionary<string, UrbanObject> _roadObjects = new(StringComparer.Ordinal);

        /// <summary>
        /// 機能: 通行属性と明示された交差点を用いて交通網を構築します。
        /// 引数: mode は string 型、design と junctions は IEnumerable&lt;UrbanObject&gt; 型です。
        /// 返り値: なし（コンストラクター）。
        /// </summary>
        public Network(string mode, IEnumerable<UrbanObject> design, IEnumerable<UrbanObject> junctions)
        {
            _mode = mode;
            Dictionary<string, List<UrbanObject>> joins = new(StringComparer.Ordinal);
            foreach (UrbanObject junction in junctions)
                foreach (string roadId in junction.ConnectedRoadIds)
                {
                    if (!joins.TryGetValue(roadId, out List<UrbanObject>? values)) joins[roadId] = values = new();
                    values.Add(junction);
                }
            foreach (UrbanObject road in design)
            {
                if (road.Deleted || road.Kind != "road" || road.Path.Count < 2 || !Supports(road, mode)) continue;
                _roadObjects[road.Id] = road;
                for (int index = 1; index < road.Path.Count; index++)
                {
                    UrbanPoint a = road.Path[index - 1], b = road.Path[index];
                    List<(float T, float X, float Z, string Junction)> split = new() { (0, a.X, a.Z, ""), (1, b.X, b.Z, "") };
                    if (joins.TryGetValue(road.Id, out List<UrbanObject>? connections))
                        foreach (UrbanObject junction in connections)
                        {
                            if (junction.Level != road.Level) continue;
                            (float t, float x, float z) = Project(junction.X, junction.Z, a.X, a.Z, b.X, b.Z);
                            if (Distance(x, z, junction.X, junction.Z) <= Math.Max(0.5f, road.WidthMeters / 20))
                                split.Add((t, junction.X, junction.Z, junction.Id));
                        }
                    split.Sort((left, right) => left.T.CompareTo(right.T));
                    for (int part = 1; part < split.Count; part++)
                    {
                        var p = split[part - 1]; var q = split[part];
                        int from = Node(p.X, p.Z, road, p.Junction), to = Node(q.X, q.Z, road, q.Junction);
                        if (from == to) continue;
                        float length = Distance(p.X, p.Z, q.X, q.Z) * 10;
                        bool both = mode == "walk";
                        if (both || road.LanesForward > 0) _edges[from].Add(new(to, length, road));
                        if (both || road.LanesBackward > 0) _edges[to].Add(new(from, length, road));
                        Segment segment = new(from, to, road); _segments.Add(segment);
                        // 斜めの長い道路でも矩形全域へ登録せず、折線が通る格子だけを使います。
                        int steps = Math.Max(1, (int)Math.Ceiling(length / 50));
                        HashSet<(int X, int Z)> cells = new();
                        for (int sample = 0; sample <= steps; sample++)
                        {
                            float fraction = (float)sample / steps;
                            int x = (int)Math.Floor((p.X + (q.X - p.X) * fraction) / 10);
                            int z = (int)Math.Floor((p.Z + (q.Z - p.Z) * fraction) / 10);
                            if (!cells.Add((x, z))) continue;
                            if (!_spatial.TryGetValue((x, z), out List<Segment>? values)) _spatial[(x, z)] = values = new();
                            values.Add(segment);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 機能: 用途と通行設備から交通手段の利用可否を判定します。
        /// 引数: road は UrbanObject 型、mode は string 型です。
        /// 返り値: bool 型の通行可否です。
        /// </summary>
        private static bool Supports(UrbanObject road, string mode)
        {
            if (mode != "walk" && road.SpeedKph <= 0) return false;
            string usage = Usage(road);
            bool rail = usage is "rail" or "railway" or "鉄道";
            bool water = usage is "waterway" or "航路";
            return mode switch { "train" => rail, "ship" => water, "walk" => !rail && !water && road.SidewalkWidthMeters > 0, _ => !rail && !water && road.LanesForward + road.LanesBackward > 0 };
        }

        /// <summary>
        /// 機能: 座標と高さが一致する経路頂点を共有します。
        /// 引数: x、z は float 型、road は UrbanObject 型、junction は string 型です。
        /// 返り値: int 型の頂点番号です。
        /// </summary>
        private int Node(float x, float z, UrbanObject road, string junction)
        {
            var key = ((int)MathF.Round(x * 100), (int)MathF.Round(z * 100), road.Level, (int)MathF.Round(road.Y * 10));
            if (_nodeIds.TryGetValue(key, out int existing))
            {
                if (junction.Length > 0 && _vertices[existing].JunctionId.Length == 0) _vertices[existing] = _vertices[existing] with { JunctionId = junction };
                return existing;
            }
            int index = _vertices.Count;
            _vertices.Add(new(x, z, road.Y, junction)); _edges.Add(new()); _nodeIds[key] = index;
            return index;
        }

        /// <summary>
        /// 機能: 建物の接続点間を最短距離で探索し、実道路折線に沿う通過点を生成します。
        /// 引数: origin、destination は UrbanObject 型、reason は string 型の出力です。
        /// 返り値: List&lt;TravelWaypoint&gt; 型の経路、通行不能は null です。
        /// </summary>
        public List<TravelWaypoint>? Route(UrbanObject origin, UrbanObject destination, out string reason)
        {
            reason = _mode switch { "walk" => "歩道のある道路への接続がありません。", "car" => "自動車が通行できる道路への接続がありません。", "train" => "駅に接続した線路がありません。", _ => "港に接続した航路がありません。" };
            Attachment? start = Attach(origin), end = Attach(destination);
            if (start is null || end is null) return null;
            float[] distances = Enumerable.Repeat(float.PositiveInfinity, _vertices.Count).ToArray();
            (int From, UrbanObject? Road)[] previous = new (int, UrbanObject?)[_vertices.Count];
            Array.Fill(previous, (-1, null));
            PriorityQueue<int, float> queue = new();
            bool forwardStart = _mode == "walk" || start.Segment.Road.LanesForward > 0;
            bool backwardStart = _mode == "walk" || start.Segment.Road.LanesBackward > 0;
            if (forwardStart) Seed(start.Segment.B, start, distances, queue);
            if (backwardStart) Seed(start.Segment.A, start, distances, queue);
            bool endForward = _mode == "walk" || end.Segment.Road.LanesForward > 0;
            bool endBackward = _mode == "walk" || end.Segment.Road.LanesBackward > 0;
            int goal = -1;
            float best = float.PositiveInfinity;
            bool direct = start.Segment == end.Segment && ((start.T <= end.T && forwardStart) || (start.T >= end.T && backwardStart));
            if (direct) best = Distance(start.X, start.Z, end.X, end.Z) * 10;
            while (queue.TryDequeue(out int node, out float priority))
            {
                if (priority > distances[node] || priority > best) continue;
                if (node == end.Segment.A && endForward || node == end.Segment.B && endBackward)
                {
                    Vertex v = _vertices[node];
                    float total = priority + Distance(v.X, v.Z, end.X, end.Z) * 10;
                    if (total < best) { goal = node; best = total; direct = false; }
                }
                foreach (Edge edge in _edges[node])
                {
                    float next = priority + edge.Length;
                    if (next >= distances[edge.To]) continue;
                    distances[edge.To] = next; previous[edge.To] = (node, edge.Road); queue.Enqueue(edge.To, next);
                }
            }
            if (goal < 0 && !direct) { reason = "起点と目的地の交通網が分断されているか、一方通行で到達できません。"; return null; }
            List<TravelWaypoint> route = new();
            // 建物と直近の道路接続だけがアクセス区間です。途中の地表へ自由経路を生成しません。
            route.Add(Point(start.X, start.Z, start.Segment.Road.Y, start.Segment.Road, "", AccessMode()));
            if (!direct)
            {
                List<(int Node, UrbanObject Road)> reversed = new();
                int current = goal;
                while (current >= 0)
                {
                    var step = previous[current];
                    reversed.Add((current, step.Road ?? start.Segment.Road));
                    current = step.From;
                    if (reversed.Count > MaxRoutePoints - 3) { reason = "交通経路が長すぎます。"; return null; }
                }
                reversed.Reverse();
                for (int index = 0; index < reversed.Count; index++)
                {
                    var step = reversed[index]; Vertex v = _vertices[step.Node];
                    // 交差点へ進入する道路を記録し、信号がこの進入方向を許可するまで停止します。
                    UrbanObject arrivingRoad = index == 0 ? start.Segment.Road : step.Road;
                    route.Add(Point(v.X, v.Z, v.Y, arrivingRoad, v.JunctionId, _mode));
                }
            }
            route.Add(Point(end.X, end.Z, end.Segment.Road.Y, end.Segment.Road, "", _mode));
            route.Add(Point(destination.X, destination.Z, destination.Y, end.Segment.Road, "", AccessMode()));
            OffsetLanes(route);
            reason = "";
            return route;
        }

        /// <summary>
        /// 機能: 人を歩道側、車を左側通行の車線へ配置し、同一中心線の重なりを避けます。
        /// 引数: route は List&lt;TravelWaypoint&gt; 型の経路です。
        /// 返り値: なし（void 型）。建物への最後のアクセス点は変更しません。
        /// </summary>
        private void OffsetLanes(List<TravelWaypoint> route)
        {
            if (_mode is not ("walk" or "car") || route.Count < 2) return;
            TravelWaypoint[] original = route.Select(point => point.DeepClone()).ToArray();
            for (int index = 0; index < route.Count - 1; index++)
            {
                TravelWaypoint point = route[index];
                if (!_roadObjects.TryGetValue(point.RoadId, out UrbanObject? road)) continue;
                TravelWaypoint from = index == 0 ? original[index] : original[index - 1];
                TravelWaypoint to = index == 0 ? original[index + 1] : original[index];
                float length = Distance(from.X, from.Z, to.X, to.Z);
                if (length < 0.001f) continue;
                float offset = _mode == "walk" ? (road.LanesForward + road.LanesBackward) * road.LaneWidthMeters / 2 + road.SidewalkWidthMeters / 2 : road.LaneWidthMeters / 2;
                point.X += (to.Z - from.Z) / length * offset / 10;
                point.Z -= (to.X - from.X) / length * offset / 10;
            }
        }

        /// <summary>
        /// 機能: 交通施設と網の短い接続区間で利用する手段を取得します。
        /// 引数: なし。
        /// 返り値: string 型の移動手段です。
        /// </summary>
        private string AccessMode() => _mode is "train" or "ship" ? _mode : "walk";

        /// <summary>
        /// 機能: 出発投影点から有向網の探索開始頂点を登録します。
        /// 引数: node は int 型、start は Attachment 型、distances は float[] 型、queue は PriorityQueue&lt;int,float&gt; 型です。
        /// 返り値: なし（void 型）。
        /// </summary>
        private void Seed(int node, Attachment start, float[] distances, PriorityQueue<int, float> queue)
        {
            Vertex v = _vertices[node]; float distance = Distance(start.X, start.Z, v.X, v.Z) * 10;
            distances[node] = distance; queue.Enqueue(node, distance);
        }

        /// <summary>
        /// 機能: 建物の近傍にある同一階層の通行線へアクセス点を投影します。
        /// 引数: building は UrbanObject 型です。
        /// 返り値: Attachment 型の接続情報、範囲外の場合は null です。
        /// </summary>
        private Attachment? Attach(UrbanObject building)
        {
            Attachment? nearest = null;
            int cx = (int)Math.Floor(building.X / 10), cz = (int)Math.Floor(building.Z / 10);
            HashSet<Segment> checkedSegments = new();
            float maximum = Math.Min(12, Math.Max(building.WidthMeters, building.DepthMeters) / 20 + 4);
            for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++)
            {
                if (!_spatial.TryGetValue((cx + dx, cz + dz), out List<Segment>? segments)) continue;
                foreach (Segment segment in segments)
                {
                    if (!checkedSegments.Add(segment) || segment.Road.Level != building.Level) continue;
                    Vertex a = _vertices[segment.A], b = _vertices[segment.B];
                    var projection = Project(building.X, building.Z, a.X, a.Z, b.X, b.Z);
                    float distance = Distance(building.X, building.Z, projection.X, projection.Z);
                    if (distance > maximum || nearest is not null && distance >= nearest.Distance) continue;
                    nearest = new(segment, projection.T, projection.X, projection.Z, distance);
                }
            }
            return nearest;
        }

        /// <summary>
        /// 機能: 通過点に通行速度と道路情報を付与します。
        /// 引数: x、z、y は float 型、road は UrbanObject 型、junction と mode は string 型です。
        /// 返り値: TravelWaypoint 型の通過点です。
        /// </summary>
        private static TravelWaypoint Point(float x, float z, float y, UrbanObject road, string junction, string mode) => new()
        {
            X = x, Z = z, Y = y, Mode = mode, RoadId = road.Id, JunctionId = junction,
            SpeedMetersPerSecond = mode switch { "walk" => 1.4f, "ship" => Math.Min(12, road.SpeedKph / 3.6f), "train" => Math.Min(45, road.SpeedKph / 3.6f), _ => Math.Min(36, road.SpeedKph / 3.6f) }
        };

        /// <summary>
        /// 機能: 線分上への最近点と位置比率を取得します。
        /// 引数: x、z、ax、az、bx、bz は float 型です。
        /// 返り値: float 型の T、X、Z を含むタプルです。
        /// </summary>
        private static (float T, float X, float Z) Project(float x, float z, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az, length = dx * dx + dz * dz;
            float t = length <= 0.000001f ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / length, 0, 1);
            return (t, ax + dx * t, az + dz * t);
        }
    }
}
