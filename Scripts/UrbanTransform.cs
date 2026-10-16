using Godot;
using System;

namespace Bayfront;

/// <summary>
/// ドラッグ移動・回転・高さ変更で地物の経路と敷地を一体として変換します。
/// </summary>
public static class UrbanTransform
{
    /// <summary>
    /// 元の設計を変更せず、平行移動・中心回転・上下移動を適用した設計案を作ります。
    /// 引数: source（UrbanObject）元設計、translation（Vector2）都市座標の移動、degrees（float）回転度、height（float）上下都市座標。
    /// 返り値: UrbanObject 変換後の独立した設計です。
    /// </summary>
    public static UrbanObject Apply(UrbanObject source, Vector2 translation, float degrees = 0, float height = 0)
    {
        var item = source.DeepClone();
        var center = new Vector2(source.X, source.Z); var angle = Mathf.DegToRad(degrees);
        item.X += translation.X; item.Z += translation.Y; item.Y += height;
        foreach (var point in item.Path)
        {
            var transformed = (new Vector2(point.X, point.Z) - center).Rotated(angle) + center + translation;
            point.X = transformed.X; point.Z = transformed.Y;
        }
        foreach (var point in item.Footprint) { point.X += translation.X; point.Z += translation.Y; }
        item.Rotation = source.Kind == "road" ? 0 : Mathf.Wrap(source.Rotation + degrees, -180, 180);
        return item;
    }
}
