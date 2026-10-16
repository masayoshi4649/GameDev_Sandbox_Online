using System;
using System.Collections.Generic;

namespace Bayfront;

/// <summary>
/// 住民や施設の実在する関連先へ移動するための表示情報です。
/// </summary>
public sealed record UrbanAgentLink(string Id, string Label, bool IsAgent);

/// <summary>
/// 個体の今回の移動と、固定の住居・職場・実際の滞在先を区別した画面情報です。
/// 所有者や定員など、シミュレーションで保持しない情報は作成しません。
/// </summary>
public sealed class UrbanAgentDetails
{
    public string Home { get; init; } = "未設定";
    public string Workplace { get; init; } = "未割当";
    public string CurrentPlace { get; init; } = "移動中";
    public string OccupantNotice { get; init; } = "";
    public bool Following { get; init; }
    public bool CanFollow { get; init; }
    public bool RouteVisible { get; init; } = true;
    public IReadOnlyList<UrbanAgentLink> Links { get; init; } = Array.Empty<UrbanAgentLink>();
    public Action<bool>? RouteChanged { get; init; }
    public Action? FollowRequested { get; init; }
}
