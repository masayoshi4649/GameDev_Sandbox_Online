using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 設計定員とは別に保存する、実際に入居した住民です。
/// </summary>
public sealed class UrbanResident
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string HomeId { get; set; } = "";
    public string WorkId { get; set; } = "";
    public string VisitDestinationId { get; set; } = "";
    public string CurrentPlaceId { get; set; } = "";
    public string NextActivity { get; set; } = "work";
    public string PreferredMode { get; set; } = "walk";
    public double ReadyAtSeconds { get; set; }
    public bool AtHome { get; set; } = true;

    /// <summary>
    /// 機能: 住民情報を独立した値へ複製します。
    /// 引数: なし。
    /// 返り値: UrbanResident 型の複製です。
    /// </summary>
    public UrbanResident DeepClone() => (UrbanResident)MemberwiseClone();
}

// ----------------------------------------

/// <summary>
/// 道路や専用交通網上の通過点です。Mode はこの点へ向かう区間の交通手段です。
/// </summary>
public sealed class TravelWaypoint
{
    public float X { get; set; }
    public float Z { get; set; }
    public float Y { get; set; }
    public string Mode { get; set; } = "walk";
    public string RoadId { get; set; } = "";
    public string JunctionId { get; set; } = "";
    public float SpeedMetersPerSecond { get; set; } = 1.4f;

    /// <summary>
    /// 機能: 経路点を独立した値へ複製します。
    /// 引数: なし。
    /// 返り値: TravelWaypoint 型の複製です。
    /// </summary>
    public TravelWaypoint DeepClone() => (TravelWaypoint)MemberwiseClone();
}

// ----------------------------------------

/// <summary>
/// 選択可能な住民および乗り物の実状態です。座標と Y は都市座標、Y は地形からの加算です。
/// </summary>
public sealed class UrbanAgent
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "person";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "walk";
    public string OriginId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Status { get; set; } = "待機";
    public string WaitingReason { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float Y { get; set; }
    public float DirectionX { get; set; }
    public float DirectionZ { get; set; } = 1;
    public List<TravelWaypoint> Route { get; set; } = new();
    public int RouteIndex { get; set; }
    public float SegmentProgress { get; set; }
    public List<string> PassengerIds { get; set; } = new();

    /// <summary>
    /// 機能: 経路と乗客を含めて移動個体を深く複製します。
    /// 引数: なし。
    /// 返り値: UrbanAgent 型の複製です。
    /// </summary>
    public UrbanAgent DeepClone()
    {
        UrbanAgent clone = (UrbanAgent)MemberwiseClone();
        clone.Route = Route.Select(point => point.DeepClone()).ToList();
        clone.PassengerIds = new(PassengerIds);
        return clone;
    }
}

// ----------------------------------------

/// <summary>
/// ホストが計算し保存・同期する都市の稼働状態です。現在は最大 512 人の個別移動を計算します。
/// </summary>
public sealed class UrbanSimulationState
{
    public int Version { get; set; } = 1;
    public long LastProcessedTick { get; set; }
    public long DesignRevision { get; set; } = -1;
    public long NextResidentId { get; set; } = 1;
    public long CompletedTrips { get; set; }
    public long WorkArrivals { get; set; }
    public long VisitArrivals { get; set; }
    public string Limitation { get; set; } = "";
    public List<UrbanResident> Residents { get; set; } = new();
    public List<UrbanAgent> Agents { get; set; } = new();
    public int Population => Residents.Count;

    /// <summary>
    /// 機能: 実住民と移動個体を含めて稼働状態を複製します。
    /// 引数: なし。
    /// 返り値: UrbanSimulationState 型の複製です。
    /// </summary>
    public UrbanSimulationState DeepClone() => new()
    {
        Version = Version, LastProcessedTick = LastProcessedTick, DesignRevision = DesignRevision,
        NextResidentId = NextResidentId, CompletedTrips = CompletedTrips, WorkArrivals = WorkArrivals, VisitArrivals = VisitArrivals,
        Limitation = Limitation, Residents = Residents.Select(value => value.DeepClone()).ToList(),
        Agents = Agents.Select(value => value.DeepClone()).ToList()
    };
}
