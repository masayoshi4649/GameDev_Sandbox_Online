using Godot;
using System;

namespace Bayfront;

/// <summary>
/// 更地や交通街区で原典 GLB の不要な読込を避け、有効化時に続きから再開することを検証します。
/// </summary>
public partial class UrbanSourceLoadingTests : Node
{
    /// <summary>
    /// 機能: 原典の無効・有効を切り替え、実ファイル読込数と現在都市の描画完了を検査します。
    /// 引数: なし。
    /// 返り値: void。成功時は終了コード 0、失敗時は 1 で終了します。
    /// </summary>
    public override void _Ready()
    {
        try
        {
            var data = RealWorldData.Load(); var terrain = new UrbanTerrain(data); var catalog = new UrbanCatalog(data);
            var state = new CityState { MapId = "real-bayfront", Edits = new UrbanEdits { IncludeSource = false } };
            state.Edits.Objects.Add(new UrbanObject { Id = "原典待ちをしない住宅", Kind = "building", Name = "試験住宅", Floors = 1, X = 0, Z = 0 });
            var renderer = new UrbanRenderer(); AddChild(renderer); renderer.Initialize(catalog, state, terrain.Height);
            DrainCurrent(renderer);
            Require(renderer.LoadedSourceCount == 0 && renderer.PendingSourceCount == data.Tiles.Count, "原典無効の都市で不要な原典を読んでいます。");
            Require(renderer.IsComplete && renderer.TriangleCount > 0, "原典を待たず現在の街区を表示できません。");
            state.Edits.IncludeSource = true; renderer.Refresh(state); renderer._Process(0);
            Require(renderer.LoadedSourceCount == 1 && renderer.PendingSourceCount == data.Tiles.Count - 1 && !renderer.IsComplete, "原典有効化で読込を開始できません。");
            state.Edits.IncludeSource = false; renderer.Refresh(state); DrainCurrent(renderer);
            Require(renderer.LoadedSourceCount == 1 && renderer.PendingSourceCount == data.Tiles.Count - 1 && renderer.IsComplete, "原典無効化で未読タイルが休止されません。");
            state.Edits.IncludeSource = true; renderer.Refresh(state); renderer._Process(0);
            Require(renderer.LoadedSourceCount == 2 && renderer.PendingSourceCount == data.Tiles.Count - 2, "原典再有効化で最初から重複読込しています。");
            state.Edits.IncludeSource = false; renderer.Refresh(state); DrainCurrent(renderer);
            Require(renderer.LoadedSourceCount == 2 && renderer.EffectiveObjectCount == 1 && renderer.LoadedObjects == 1, "読込を休止すると新設都市の描画が失われます。");
            GD.Print($"BAYFRONT_SOURCE_LOADING_PASS 原典読込={renderer.LoadedSourceCount} 未読休止={renderer.PendingSourceCount} 新設地物={renderer.LoadedObjects}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"原典遅延読込の検査に失敗しました: {error}"); GetTree().Quit(1);
        }
    }

    /// <summary>
    /// 機能: 現在の小街区だけを一定フレーム以内に構築できることを確認します。
    /// 引数: renderer（UrbanRenderer）検査する描画ノード。
    /// 返り値: void。完了しない場合は InvalidOperationException を送出します。
    /// </summary>
    private static void DrainCurrent(UrbanRenderer renderer)
    {
        for (var frame = 0; frame < 8 && !renderer.IsComplete; frame++) renderer._Process(0);
        Require(renderer.IsComplete, "少数の新設地物を原典待ちで表示できません。");
    }

    /// <summary>
    /// 機能: 試験条件を検査します。
    /// 引数: condition（bool）検査結果、message（string）失敗理由。
    /// 返り値: void。不成立の場合は InvalidOperationException を送出します。
    /// </summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
