using Godot;
using System;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 道路・交差点・信号の連動編集を描画なしで検証します。
/// </summary>
public static class UrbanGeometryTest
{
    /// <summary>
    /// 同階層の交点、立体交差、交差点の移動、道路撤去の参照解除を検証します。
    /// 引数: catalog（UrbanCatalog）原典の編集カタログ。
    /// 返り値: void。不整合があれば例外、成功時は試験識別子を出力します。
    /// </summary>
    public static void Run(UrbanCatalog catalog)
    {
        var state = new CityState { Edits = new UrbanEdits { IncludeSource = false } };
        var horizontal = Road("geometry-horizontal", -10, 0, 10, 0);
        Apply(catalog, state, new UrbanCommand { Object = horizontal });
        var vertical = Road("geometry-vertical", 0, -10, 0, 10);
        var changes = catalog.GetRoadConnections(vertical, state).ToList();
        Check(changes.Count == 3, "平面交差で道路2本と交差点1個を変更すること");
        Apply(catalog, state, new UrbanCommand { Operation = "batch", Objects = changes });
        var junction = catalog.GetEffective(state).Single(item => item.Kind == "intersection");
        Check(junction.ConnectedRoadIds.Count == 2, "交差点の接続道路を確定すること");
        Check(catalog.Find(horizontal.Id, state)!.Path.Count == 3 && catalog.Find(vertical.Id, state)!.Path.Count == 3, "両方の道路を交点で分割すること");

        // ----------------------------------------

        var elevated = Road("geometry-elevated", 3, -10, 3, 10); elevated.Level = 1; elevated.Y = 0.6f;
        var overpass = catalog.GetRoadConnections(elevated, state);
        Check(overpass.Count == 1 && overpass[0].Kind == "road", "異なる階層の道路を平面接続しないこと");
        var signal = new UrbanObject
        {
            Id = "geometry-signal", Kind = "signal", X = 1, Z = 1,
            ParentId = junction.Id, JunctionId = junction.Id, ApproachRoadId = horizontal.Id,
            ConnectedRoadIds = new() { horizontal.Id }, WidthMeters = 1, DepthMeters = 1,
            HeightMeters = 5.5f, Control = "signals", Phases = new()
            {
                new SignalPhase { Name = "東西方向", DurationSeconds = 17, GreenRoadIds = new() { horizontal.Id } }
            }
        };
        Apply(catalog, state, new UrbanCommand { Object = signal });
        var moved = junction.DeepClone(); moved.X = 2; moved.Z = 3;
        var move = catalog.GetObjectChanges(moved, state).ToList();
        Check(move.Count == 4, "交差点・両道路・所属信号を一度に移動すること");
        Apply(catalog, state, new UrbanCommand { Operation = "batch", Objects = move });
        foreach (var id in new[] { horizontal.Id, vertical.Id })
            Check(catalog.Find(id, state)!.Path.Any(point => Math.Abs(point.X - 2) < 0.001f && Math.Abs(point.Z - 3) < 0.001f), "接続道路の交点が移動先に一致すること");
        var movedSignal = catalog.Find(signal.Id, state)!;
        Check(Math.Abs(movedSignal.X - 3) < 0.001f && Math.Abs(movedSignal.Z - 4) < 0.001f, "信号の交差点に対する相対位置を保持すること");
        Check(movedSignal.Phases.Count == 1, "移動で信号現示の設定を破壊しないこと");

        // ----------------------------------------

        var removal = catalog.GetRemovalChanges(horizontal.Id, state).ToList();
        Check(removal.Count == 3, "道路撤去と交差点・信号の参照解除を一度に作ること");
        Apply(catalog, state, new UrbanCommand { Operation = "batch", Objects = removal });
        Check(catalog.Find(horizontal.Id, state) is null, "道路の表示と有効地物から撤去すること");
        Check(!catalog.Find(junction.Id, state)!.ConnectedRoadIds.Contains(horizontal.Id), "交差点に撤去道路の参照を残さないこと");
        var disconnected = catalog.Find(signal.Id, state)!;
        Check(disconnected.ApproachRoadId.Length == 0 && disconnected.Phases.Count == 0 && disconnected.Control == "unconfigured", "進入道路を失った信号を未設定へ戻すこと");
        Apply(catalog, state, new UrbanCommand { Operation = "batch", Objects = catalog.GetRemovalChanges(junction.Id, state).ToList() });
        Check(catalog.Find(signal.Id, state)!.ParentId.Length == 0 && catalog.Find(signal.Id, state)!.JunctionId.Length == 0, "交差点撤去で所属信号の参照も解除すること");
        GD.Print("BAYFRONT_URBAN_TOPOLOGY_PASS 道路交差・立体交差・交差点移動・信号追従・撤去参照解除");
    }

    /// <summary>
    /// 検証用道路を生成します。
    /// 引数: id（string）識別子、ax/az/bx/bz（float）両端の座標。
    /// 返り値: UrbanObject 二車線道路。
    /// </summary>
    private static UrbanObject Road(string id, float ax, float az, float bx, float bz) => new()
    {
        Id = id, Kind = "road", X = (ax + bx) / 2, Z = (az + bz) / 2,
        Path = new() { new UrbanPoint(ax, az), new UrbanPoint(bx, bz) },
        WidthMeters = 12, DepthMeters = 1, HeightMeters = 0.12f
    };

    /// <summary>
    /// 最新版へ命令を適用し、エラーを試験失敗として伝えます。
    /// 引数: catalog（UrbanCatalog）原典、state（CityState）都市、command（UrbanCommand）命令。
    /// 返り値: void。
    /// </summary>
    private static void Apply(UrbanCatalog catalog, CityState state, UrbanCommand command)
    {
        command.ExpectedRevision = state.Revision;
        if (!UrbanModel.TryApply(state, command, out var error, catalog.SourceExists, catalog.SourceObject))
            throw new InvalidOperationException($"接続編集の検証失敗: {error}");
    }

    /// <summary>
    /// 検証条件が真であることを確認します。
    /// 引数: condition（bool）条件、message（string）説明。返り値: void。
    /// </summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
