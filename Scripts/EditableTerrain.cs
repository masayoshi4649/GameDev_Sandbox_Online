using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bayfront;

/// <summary>
/// 公開標高を土台に、保存順の地形ブラシを連続関数として評価します。
/// GPU資源や画像を読み込まず、専用サーバーでも同じ地表高と陸水判定を使用します。
/// </summary>
public sealed class UrbanTerrain
{
    private const float IndexCell = 20;
    private readonly RealWorldData _data;
    private readonly Dictionary<(int, int), List<RealLand>> _lands = new();
    private readonly Dictionary<(int, int), bool> _uniformLandCells = new();
    private readonly Dictionary<(int, int), List<int>> _brushIndex = new();
    private readonly List<AppliedBrush> _brushes = new();
    private readonly Dictionary<Vector2, HeightSample> _heightCache = new();
    private readonly Dictionary<(int, int), float> _minimumRadius = new();
    private readonly HashSet<(float X, float Z, float Radius)> _rayShapes = new();
    private const int MaximumCachedSamples = 250000;
    private readonly float _baseMinimum, _baseMaximum;
    private float _minimumHeight, _maximumHeight;
    public float SeaLevel { get; private set; }
    public float DefaultSeaLevel { get; }
    public Rect2 Bounds => _data.Bounds;
    public int Revision { get; private set; }
    public int EditCount => _brushes.Count;
    public Rect2 ChangedBounds { get; private set; }
    public event Action<Rect2>? Changed;
    internal IReadOnlyList<AppliedBrush> Brushes => _brushes;

    /// <summary>
    /// 陸域索引と水域の代表海面高を準備します。
    /// 引数: data（RealWorldData）描画から独立した実地形データ。
    /// 返り値: UrbanTerrain 編集前の地形を評価できるインスタンス。
    /// </summary>
    public UrbanTerrain(RealWorldData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        foreach (var land in data.Lands)
        {
            var minimum = Cell(land.Bounds.Position);
            var maximum = Cell(land.Bounds.End);
            for (var z = minimum.Item2; z <= maximum.Item2; z++)
                for (var x = minimum.Item1; x <= maximum.Item1; x++)
                {
                    if (!_lands.TryGetValue((x, z), out var values)) _lands[(x, z)] = values = new();
                    values.Add(land);
                }
        }
        PrepareUniformLandCells();
        var levels = new List<float>();
        for (var z = 0; z < 24; z++)
            for (var x = 0; x < 24; x++)
            {
                var point = data.Bounds.Position + data.Bounds.Size * new Vector2((x + 0.5f) / 24, (z + 0.5f) / 24);
                if (!OriginalLand(point)) levels.Add(data.GroundHeight(point.X, point.Y));
            }
        levels.Sort();
        DefaultSeaLevel = levels.Count > 0 ? levels[levels.Count / 2] : data.GroundHeight(0, 0) - 0.2f;
        SeaLevel = DefaultSeaLevel;
        using (var elevation = RealWorldData.ReadJson("res://Data/RealWorld/elevation.json"))
        {
            var minimumHeight = float.PositiveInfinity;
            var maximumHeight = float.NegativeInfinity;
            foreach (var value in elevation.RootElement.GetProperty("heights").EnumerateArray())
            {
                minimumHeight = Math.Min(minimumHeight, value.GetSingle());
                maximumHeight = Math.Max(maximumHeight, value.GetSingle());
            }
            _baseMinimum = Math.Min(minimumHeight, DefaultSeaLevel - 0.5f);
            _baseMaximum = Math.Max(maximumHeight, DefaultSeaLevel + 0.04f);
        }
        _minimumHeight = _baseMinimum;
        _maximumHeight = _baseMaximum;
        ChangedBounds = data.Bounds;
    }

    // ----------------------------------------

    /// <summary>
    /// 地形編集の差分を取り込み、取消や保存復元の場合は索引を再構築します。
    /// 引数: edits（UrbanEdits）順序を保持した都市編集データ。
    /// 返り値: void。同じ内容の場合は何も変更せず、変更範囲をChangedで通知します。
    /// </summary>
    public void SetEdits(UrbanEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var nextSea = edits.SeaLevelMeters.HasValue ? edits.SeaLevelMeters.Value / RealWorldData.MetersPerUnit : DefaultSeaLevel;
        var seaChanged = nextSea != SeaLevel;
        SeaLevel = nextSea;
        var source = edits.Terrain;
        var common = 0;
        while (common < source.Count && common < _brushes.Count && _brushes[common].Matches(source[common])) common++;
        if (common == source.Count && common == _brushes.Count && !seaChanged) return;

        Rect2? changed = seaChanged ? Bounds : null;
        for (var index = common; index < _brushes.Count; index++) changed = Merge(changed, _brushes[index].Bounds);
        for (var index = common; index < source.Count; index++) changed = Merge(changed, BrushBounds(source[index]));
        if (common < _brushes.Count)
        {
            _brushes.Clear();
            _brushIndex.Clear();
            _heightCache.Clear();
            _minimumRadius.Clear();
            _rayShapes.Clear();
            _minimumHeight = _baseMinimum;
            _maximumHeight = _baseMaximum;
            common = 0;
        }
        for (var index = common; index < source.Count; index++)
        {
            var item = source[index];
            var target = item.TargetHeightMeters / RealWorldData.MetersPerUnit;
            if (item.Mode == "smooth") target = AverageBeforeBrush(item);
            var brush = new AppliedBrush(item.Id, item.Mode, item.X, item.Z, item.RadiusMeters,
                item.AmountMeters, item.TargetHeightMeters, target);
            var brushIndex = _brushes.Count;
            _brushes.Add(brush);
            var amount = Math.Abs(item.AmountMeters) / RealWorldData.MetersPerUnit;
            if (item.Mode == "raise") _maximumHeight += amount;
            else if (item.Mode == "lower") _minimumHeight -= amount;
            else if (item.Mode == "flatten")
            {
                _minimumHeight = Math.Min(_minimumHeight, target);
                _maximumHeight = Math.Max(_maximumHeight, target);
            }
            _rayShapes.Add((item.X, item.Z, item.RadiusMeters / RealWorldData.MetersPerUnit));
            var minimum = Cell(brush.Bounds.Position);
            var maximum = Cell(brush.Bounds.End);
            for (var z = minimum.Item2; z <= maximum.Item2; z++)
                for (var x = minimum.Item1; x <= maximum.Item1; x++)
                {
                    if (!_brushIndex.TryGetValue((x, z), out var values)) _brushIndex[(x, z)] = values = new();
                    values.Add(brushIndex);
                    _minimumRadius[(x, z)] = _minimumRadius.TryGetValue((x, z), out var oldRadius)
                        ? Math.Min(oldRadius, item.RadiusMeters / RealWorldData.MetersPerUnit)
                        : item.RadiusMeters / RealWorldData.MetersPerUnit;
                }
        }
        Revision++;
        ChangedBounds = (changed ?? Bounds).Grow(0.25f);
        Changed?.Invoke(ChangedBounds);
    }

    /// <summary>
    /// 元地形へ近傍ブラシを保存順に適用し、サブグリッド位置の高さも取得します。
    /// 引数: x/z（float）X東・Z南のゲーム座標。1ゲーム単位は10mです。
    /// 返り値: float 原点ENU Upからの地表高。単位はゲーム座標です。
    /// </summary>
    public float Height(float x, float z)
    {
        var point = new Vector2(x, z);
        if (!Bounds.Grow(0.001f).HasPoint(point)) return DefaultSeaLevel - 0.5f;
        var appliedCount = 0;
        float value;
        if (_heightCache.TryGetValue(point, out var cached))
        {
            if (cached.AppliedCount == _brushes.Count) return cached.Height;
            value = cached.Height;
            appliedCount = cached.AppliedCount;
        }
        else
        {
            var original = _data.GroundHeight(x, z);
            // 水面を地表として扱わず、元の陸水境界を保つ海底と乾いた地表に分けます。
            value = OriginalLand(point) ? Math.Max(original, DefaultSeaLevel + 0.04f) : Math.Min(original, DefaultSeaLevel - 0.3f);
        }
        if (!_brushIndex.TryGetValue(Cell(point), out var indices)) return CacheHeight(point, value);
        var first = indices.BinarySearch(appliedCount);
        if (first < 0) first = ~first;
        for (var position = first; position < indices.Count; position++)
        {
            var index = indices[position];
            var brush = _brushes[index];
            var radius = brush.RadiusMeters / RealWorldData.MetersPerUnit;
            var squared = point.DistanceSquaredTo(new Vector2(brush.X, brush.Z));
            if (squared >= radius * radius || radius <= 0) continue;
            var amount = Math.Abs(brush.AmountMeters) / RealWorldData.MetersPerUnit;
            var weight = 1 - Mathf.Sqrt(squared) / radius;
            weight = weight * weight * (3 - 2 * weight);
            value = brush.Mode switch
            {
                "raise" => value + amount * weight,
                "lower" => value - amount * weight,
                "flatten" => Mathf.Lerp(value, brush.TargetWorld, weight),
                "smooth" => value + Mathf.Clamp(brush.TargetWorld - value, -amount, amount) * weight,
                _ => value
            };
        }
        return CacheHeight(point, value);
    }

    /// <summary>
    /// 埋立や掘削を含めた現在の地表と海面から陸水を判定します。
    /// 引数: x/z（float）ゲーム座標。
    /// 返り値: bool 地図内で地表が海面より高い場合はtrue。
    /// </summary>
    public bool IsLand(float x, float z) => Bounds.HasPoint(new Vector2(x, z)) && Height(x, z) > SeaLevel + 0.005f;

    /// <summary>
    /// 視線を地形範囲内で歩進し、最初の交差区間を二分探索して取得します。
    /// 引数: origin/direction（Vector3）視線、hit（out Vector3）交点、maximumDistance（float）最大距離。
    /// 返り値: bool 地形へ交差した場合はtrue。急斜面でも固定点反復を使用しません。
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, out Vector3 hit, float maximumDistance = 5000)
    {
        hit = Vector3.Zero;
        if (!Finite(origin) || !Finite(direction) || direction.LengthSquared() < 0.000001f) return false;
        direction = direction.Normalized();
        var near = 0f;
        var far = maximumDistance;
        if (!ClipAxis(origin.X, direction.X, Bounds.Position.X, Bounds.End.X, ref near, ref far)
            || !ClipAxis(origin.Z, direction.Z, Bounds.Position.Y, Bounds.End.Y, ref near, ref far)
            || !ClipAxis(origin.Y, direction.Y, _minimumHeight - 0.1f, _maximumHeight + 0.1f, ref near, ref far)) return false;
        var horizontal = new Vector2(direction.X, direction.Z).Length();
        if (horizontal < 0.00001f)
        {
            if (Math.Abs(direction.Y) < 0.00001f) return false;
            var distance = (Height(origin.X, origin.Z) - origin.Y) / direction.Y;
            if (distance < near || distance > far) return false;
            hit = origin + direction * distance;
            return true;
        }
        var critical = RayCriticalDistances(origin, direction, near, far);
        var criticalIndex = 0;
        var at = Math.Min(far, near + 0.00001f);
        var point = origin + direction * at;
        var difference = point.Y - Height(point.X, point.Z);
        for (var iteration = 0; iteration < 250000 && at < far; iteration++)
        {
            if (Math.Abs(difference) < 0.00002f) { hit = point; return true; }
            var cell = Cell(new Vector2(point.X, point.Z));
            var step = _minimumRadius.TryGetValue(cell, out var radius) ? Math.Min(0.5f, radius / 8) : 0.5f;
            var next = Math.Min(far, at + step / horizontal);
            next = Math.Min(next, NextCellBoundary(origin, direction, cell, at));
            while (criticalIndex < critical.Count && critical[criticalIndex] <= at + 0.000005f) criticalIndex++;
            if (criticalIndex < critical.Count) next = Math.Min(next, critical[criticalIndex]);
            if (next <= at + 0.000001f) next = Math.Min(far, at + 0.00001f);
            var nextPoint = origin + direction * next;
            var nextDifference = nextPoint.Y - Height(nextPoint.X, nextPoint.Z);
            if (Math.Abs(nextDifference) < 0.00002f) { hit = nextPoint; return true; }
            if ((difference > 0) != (nextDifference > 0))
            {
                var left = at;
                var right = next;
                for (var split = 0; split < 24; split++)
                {
                    var middle = (left + right) * 0.5f;
                    var sample = origin + direction * middle;
                    var value = sample.Y - Height(sample.X, sample.Z);
                    if ((value > 0) == (difference > 0)) left = middle; else right = middle;
                }
                hit = origin + direction * ((left + right) * 0.5f);
                return true;
            }
            at = next;
            point = nextPoint;
            difference = nextDifference;
        }
        return false;
    }

    /// <summary>
    /// ブラシの円への進入・最近接・退出を、急斜面を飛び越えない標本距離として集めます。
    /// 引数: origin/direction（Vector3）正規化した視線、near/far（float）探索範囲。
    /// 返り値: List&lt;float&gt; 昇順の標本距離。同一範囲の反復ストロークは重複処理しません。
    /// </summary>
    private List<float> RayCriticalDistances(Vector3 origin, Vector3 direction, float near, float far)
    {
        var values = new List<float>();
        var divisor = (double)direction.X * direction.X + (double)direction.Z * direction.Z;
        foreach (var shape in _rayShapes)
        {
            var dx = (double)origin.X - shape.X;
            var dz = (double)origin.Z - shape.Z;
            var b = dx * direction.X + dz * direction.Z;
            var discriminant = b * b - divisor * (dx * dx + dz * dz - shape.Radius * shape.Radius);
            if (discriminant < 0) continue;
            var root = Math.Sqrt(discriminant);
            foreach (var value in new[] { (-b - root) / divisor, -b / divisor, (-b + root) / divisor })
                if (value > near && value < far) values.Add((float)value);
        }
        values.Sort();
        return values;
    }

    /// <summary>
    /// 次の200m索引セルへ入る距離を求めます。
    /// 引数: origin/direction（Vector3）視線、cell（(int,int)）現在セル、at（float）現在距離。
    /// 返り値: float セル境界の直後の距離。
    /// </summary>
    private static float NextCellBoundary(Vector3 origin, Vector3 direction, (int, int) cell, float at)
    {
        var next = float.PositiveInfinity;
        if (Math.Abs(direction.X) > 0.00001f)
        {
            var boundary = (cell.Item1 + (direction.X > 0 ? 1 : 0)) * IndexCell;
            var distance = (boundary - origin.X) / direction.X;
            if (distance > at + 0.00001f) next = Math.Min(next, distance + 0.00002f);
        }
        if (Math.Abs(direction.Z) > 0.00001f)
        {
            var boundary = (cell.Item2 + (direction.Z > 0 ? 1 : 0)) * IndexCell;
            var distance = (boundary - origin.Z) / direction.Z;
            if (distance > at + 0.00001f) next = Math.Min(next, distance + 0.00002f);
        }
        return next;
    }

    /// <summary>
    /// 視線と一軸の境界を交差させ、探索区間を絞ります。
    /// 引数: origin/direction/minimum/maximum（float）一軸の視線と境界、near/far（ref float）区間。
    /// 返り値: bool 有効な区間が残る場合はtrue。
    /// </summary>
    private static bool ClipAxis(float origin, float direction, float minimum, float maximum, ref float near, ref float far)
    {
        if (Math.Abs(direction) < 0.000001f) return origin >= minimum && origin <= maximum;
        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        if (first > second) (first, second) = (second, first);
        near = Math.Max(near, first);
        far = Math.Min(far, second);
        return near <= far;
    }

    /// <summary>
    /// ベクトルの全成分が有限値かを検査します。
    /// 引数: value（Vector3）検査対象。
    /// 返り値: bool 全成分が有限値の場合はtrue。
    /// </summary>
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// ブラシ変更に影響される範囲を都市座標で計算します。
    /// 引数: edit（TerrainEdit）メートル単位のブラシ指定。
    /// 返り値: Rect2 半径をゲーム単位へ変換した包含矩形。
    /// </summary>
    private static Rect2 BrushBounds(TerrainEdit edit) => new Rect2(new Vector2(edit.X, edit.Z), Vector2.Zero)
        .Grow(edit.RadiusMeters / RealWorldData.MetersPerUnit);

    /// <summary>
    /// 高さと適用済み履歴長を記録し、次のストロークでは追加分だけを計算します。
    /// 引数: point（Vector2）座標、height（float）現在の地表高。
    /// 返り値: float 記録した高さ。標本数には上限を設けます。
    /// </summary>
    private float CacheHeight(Vector2 point, float height)
    {
        if (_heightCache.Count >= MaximumCachedSamples) _heightCache.Clear();
        _heightCache[point] = new HeightSample(height, _brushes.Count);
        return height;
    }

    /// <summary>
    /// 平滑化直前の地表を円内の固定標本から平均します。
    /// 引数: edit（TerrainEdit）平滑化範囲。
    /// 返り値: float 直前の編集結果だけを反映した平均地表高。
    /// </summary>
    private float AverageBeforeBrush(TerrainEdit edit)
    {
        double total = 0;
        var count = 0;
        var radius = edit.RadiusMeters / RealWorldData.MetersPerUnit;
        for (var z = -3; z <= 3; z++)
            for (var x = -3; x <= 3; x++)
            {
                if (x * x + z * z > 9) continue;
                total += Height(edit.X + x * radius / 3, edit.Z + z * radius / 3);
                count++;
            }
        return (float)(total / count);
    }

    /// <summary>
    /// 空間索引で絞り込んだ原典陸域を穴付きポリゴンとして評価します。
    /// 引数: point（Vector2）X・Z座標。
    /// 返り値: bool 原典で陸域に含まれる場合はtrue。
    /// </summary>
    private bool OriginalLand(Vector2 point)
    {
        var cell = Cell(point);
        if (_uniformLandCells.TryGetValue(cell, out var uniform)) return uniform;
        if (!_lands.TryGetValue(cell, out var candidates)) return false;
        foreach (var land in candidates)
        {
            if (!land.Bounds.Grow(0.001f).HasPoint(point) || !Geometry2D.IsPointInPolygon(point, land.Points)) continue;
            var inHole = false;
            foreach (var hole in land.Holes)
                if (Geometry2D.IsPointInPolygon(point, hole)) { inHole = true; break; }
            if (!inHole) return true;
        }
        return false;
    }

    /// <summary>
    /// 海岸線や穴の境界を含まない200mセルの陸水を事前計算します。
    /// 引数: なし。
    /// 返り値: void。内部セルの大量の標高照会ではポリゴン判定を省略します。
    /// </summary>
    private void PrepareUniformLandCells()
    {
        var minimum = Cell(Bounds.Position);
        var maximum = Cell(Bounds.End);
        for (var z = minimum.Item2; z <= maximum.Item2; z++)
            for (var x = minimum.Item1; x <= maximum.Item1; x++)
            {
                var key = (x, z);
                if (!_lands.TryGetValue(key, out var candidates)) { _uniformLandCells[key] = false; continue; }
                var rect = new Rect2(new Vector2(x * IndexCell, z * IndexCell), Vector2.One * IndexCell);
                var crosses = false;
                foreach (var land in candidates)
                {
                    if (BoundaryTouches(land.Points, rect)) { crosses = true; break; }
                    foreach (var hole in land.Holes)
                        if (BoundaryTouches(hole, rect)) { crosses = true; break; }
                    if (crosses) break;
                }
                if (!crosses) _uniformLandCells[key] = OriginalLand(rect.GetCenter());
            }
    }

    /// <summary>
    /// 多角形の辺がセルを横切る可能性を、包含矩形で保守的に確認します。
    /// 引数: points（Vector2[]）閉じた輪郭、rect（Rect2）索引セル。
    /// 返り値: bool 境界がセル内に存在し得る場合はtrue。
    /// </summary>
    private static bool BoundaryTouches(Vector2[] points, Rect2 rect)
    {
        for (var index = 0; index < points.Length; index++)
        {
            var a = points[index];
            var b = points[(index + 1) % points.Length];
            if (Math.Min(a.X, b.X) <= rect.End.X && Math.Max(a.X, b.X) >= rect.Position.X
                && Math.Min(a.Y, b.Y) <= rect.End.Y && Math.Max(a.Y, b.Y) >= rect.Position.Y) return true;
        }
        return false;
    }

    /// <summary>
    /// 200m四方の索引セル座標へ変換します。
    /// 引数: point（Vector2）ゲーム座標。
    /// 返り値: (int,int) 負座標にも対応する列と行。
    /// </summary>
    private static (int, int) Cell(Vector2 point) => ((int)MathF.Floor(point.X / IndexCell), (int)MathF.Floor(point.Y / IndexCell));

    /// <summary>
    /// 未初期化の範囲を含めて変更矩形を統合します。
    /// 引数: current（Rect2?）既存範囲、next（Rect2）追加範囲。
    /// 返り値: Rect2 両方を包含する範囲。
    /// </summary>
    private static Rect2 Merge(Rect2? current, Rect2 next) => current?.Merge(next) ?? next;

    internal sealed record AppliedBrush(string Id, string Mode, float X, float Z, float RadiusMeters,
        float AmountMeters, float TargetHeightMeters, float TargetWorld)
    {
        public Rect2 Bounds => new Rect2(new Vector2(X, Z), Vector2.Zero).Grow(RadiusMeters / RealWorldData.MetersPerUnit);

        /// <summary>
        /// 取り込み済みブラシと保存データの値を比較します。
        /// 引数: item（TerrainEdit）新しい指定。
        /// 返り値: bool 全編集値が等しい場合はtrue。
        /// </summary>
        public bool Matches(TerrainEdit item) => Id == item.Id && Mode == item.Mode && X == item.X && Z == item.Z
            && RadiusMeters == item.RadiusMeters && AmountMeters == item.AmountMeters && TargetHeightMeters == item.TargetHeightMeters;
    }

    private readonly record struct HeightSample(float Height, int AppliedCount);
}

// ----------------------------------------

/// <summary>
/// 編集可能地形を局所パッチに分けて描画し、写真参照は明示操作時だけ読み込みます。
/// 都市の変更範囲だけを再生成し、上げ下げ・整地に合わせて法線と水際を更新します。
/// </summary>
public partial class EditableTerrainRenderer : Node3D
{
    private const int PatchDivisions = 16;
    private const int BaseSegments = 32;
    private const int MaximumAxisSamples = 161;
    private RealWorldData _data = null!;
    private UrbanTerrain _terrain = null!;
    private readonly Dictionary<(int, int), TerrainPatch> _patches = new();
    private readonly Queue<(int, int)> _pending = new();
    private readonly HashSet<(int, int)> _queued = new();
    private readonly Dictionary<string, PhotoPatch> _photos = new();
    private readonly Queue<RealRaster> _photoQueue = new();
    private Node3D _groundRoot = null!;
    private Node3D _photoRoot = null!;
    private StandardMaterial3D _groundMaterial = null!;
    private MeshInstance3D? _water;
    private int _lastRevision = -1;
    private bool _referencePhoto;
    private bool _initialized;
    public bool IsComplete { get; private set; }
    public int LoadedPatches => _patches.Count;
    public int TotalPatches => PatchDivisions * PatchDivisions;
    public event Action<int, int>? ProgressChanged;
    public event Action? Completed;
    public event Action<string>? Failed;

    /// <summary>
    /// 地形パッチを予約し、都市全体を覆う海面を生成します。
    /// 引数: data（RealWorldData）原典、terrain（UrbanTerrain）CPU地形。
    /// 返り値: void。初期地形は複数フレームに分散して生成します。
    /// </summary>
    public void Initialize(RealWorldData data, UrbanTerrain terrain)
    {
        if (_initialized) throw new InvalidOperationException("編集地形は初期化済みです。");
        _initialized = true;
        _data = data;
        _terrain = terrain;
        _groundRoot = new Node3D { Name = "編集地表" };
        _photoRoot = new Node3D { Name = "参照航空写真", Visible = false };
        AddChild(_groundRoot);
        AddChild(_photoRoot);
        _groundMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        if (DisplayServer.GetName() == "headless")
        {
            IsComplete = true;
            Completed?.Invoke();
            return;
        }
        _water = new MeshInstance3D
        {
            Name = "編集可能地形の海面",
            Mesh = new PlaneMesh { Size = data.Bounds.Size * 6 },
            Position = new Vector3(data.Bounds.GetCenter().X, terrain.SeaLevel, data.Bounds.GetCenter().Y),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("357d96"),
                Roughness = 0.22f,
                Metallic = 0.15f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_water);
        for (var z = 0; z < PatchDivisions; z++)
            for (var x = 0; x < PatchDivisions; x++) Enqueue((x, z));
        SetProcess(true);
    }

    /// <summary>
    /// 保存・共同編集・取消の結果をCPU地形へ適用し、変更パッチだけを予約します。
    /// 引数: state（CityState）最新の都市状態。
    /// 返り値: void。すでにCPU地形へ反映済みでも変更範囲の描画を追従させます。
    /// </summary>
    public void UpdateTerrain(CityState state)
    {
        _terrain.SetEdits(state.Edits);
        if (_lastRevision == _terrain.Revision) return;
        _lastRevision = _terrain.Revision;
        if (DisplayServer.GetName() == "headless") return;
        if (_water is not null) _water.Position = new Vector3(_water.Position.X, _terrain.SeaLevel, _water.Position.Z);
        var dirty = _terrain.ChangedBounds.Grow(0.15f);
        foreach (var key in _patches.Keys)
            if (PatchBounds(key).Intersects(dirty, true)) Enqueue(key);
        foreach (var patch in _photos.Values)
            if (patch.Bounds.Intersects(dirty, true)) RefreshPhoto(patch);
    }

    /// <summary>
    /// 航空写真の参照表示を切り替え、初回だけ画像読込を予約します。
    /// 引数: enabled（bool）写真を表示する場合はtrue。
    /// 返り値: void。通常地形の初期状態は写真なしです。
    /// </summary>
    public void SetReferencePhoto(bool enabled)
    {
        _referencePhoto = enabled;
        if (!_initialized || DisplayServer.GetName() == "headless") return;
        _groundRoot.Visible = true;
        _photoRoot.Visible = enabled;
        if (enabled && _photos.Count == 0 && _photoQueue.Count == 0)
            foreach (var raster in _data.Rasters) _photoQueue.Enqueue(raster);
    }

    /// <summary>
    /// 一度に最大4パッチを更新し、写真は1フレーム1枚だけ転送します。
    /// 引数: delta（double）前フレームからの秒数。
    /// 返り値: void。初回全パッチ完了時にCompletedを通知します。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_initialized || DisplayServer.GetName() == "headless") return;
        try
        {
            for (var count = 0; count < 4 && _pending.TryDequeue(out var key); count++)
            {
                _queued.Remove(key);
                RefreshPatch(key);
            }
            if (_referencePhoto && _photoQueue.TryDequeue(out var raster)) CreatePhoto(raster);
            if (!IsComplete)
            {
                ProgressChanged?.Invoke(LoadedPatches, TotalPatches);
                if (_pending.Count == 0)
                {
                    IsComplete = true;
                    Completed?.Invoke();
                    GD.Print($"BAYFRONT_EDITABLE_TERRAIN_READY patches={LoadedPatches} edits={_terrain.EditCount} sea_level_m={_terrain.SeaLevel * 10:0.00}");
                }
            }
        }
        catch (Exception error)
        {
            SetProcess(false);
            Failed?.Invoke($"編集地形の描画に失敗しました。{error.Message}");
            GD.PushError(error.ToString());
        }
    }

    // ----------------------------------------

    /// <summary>
    /// ブラシ中心と周辺を細分化したパッチを現在のCPU地形から生成します。
    /// 引数: key（(int,int)）パッチの列と行。
    /// 返り値: void。法線を地表高の中央差分から再計算します。
    /// </summary>
    private void RefreshPatch((int, int) key)
    {
        var bounds = PatchBounds(key);
        var xs = AxisSamples(bounds, true);
        var zs = AxisSamples(bounds, false);
        var vertices = new Vector3[xs.Length * zs.Length];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        for (var z = 0; z < zs.Length; z++)
            for (var x = 0; x < xs.Length; x++)
            {
                var index = z * xs.Length + x;
                var height = _terrain.Height(xs[x], zs[z]);
                var normal = Normal(xs[x], zs[z]);
                vertices[index] = new Vector3(xs[x], height, zs[z]);
                normals[index] = normal;
                var variation = Mathf.Sin(xs[x] * 1.13f) * Mathf.Cos(zs[z] * 0.91f) * 0.025f;
                var grass = new Color(0.34f + variation, 0.47f + variation, 0.30f + variation);
                var shore = new Color("b6aa82");
                var rock = new Color("81827b");
                var dry = shore.Lerp(grass, Mathf.Clamp((height - _terrain.SeaLevel) / 0.3f, 0, 1));
                colors[index] = dry.Lerp(rock, Mathf.Clamp((1 - normal.Y) * 2.2f, 0, 1));
            }
        var mesh = MakeMesh(vertices, normals, null, colors, GridIndices(xs.Length, zs.Length));
        mesh.SurfaceSetMaterial(0, _groundMaterial);
        if (!_patches.TryGetValue(key, out var patch))
        {
            var instance = new MeshInstance3D { Name = $"地形_{key.Item1}_{key.Item2}", CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
            _groundRoot.AddChild(instance);
            _patches[key] = patch = new TerrainPatch(instance);
        }
        var old = patch.Instance.Mesh;
        patch.Instance.Mesh = mesh;
        old?.Dispose();
    }

    /// <summary>
    /// 基本格子へブラシの中心・斜面・外縁の座標を加えます。
    /// 引数: bounds（Rect2）パッチ範囲、eastWest（bool）X軸の場合はtrue。
    /// 返り値: float[] 昇順で重複のない座標。微細ブラシも中心頂点を持ちます。
    /// </summary>
    private float[] AxisSamples(Rect2 bounds, bool eastWest)
    {
        var start = eastWest ? bounds.Position.X : bounds.Position.Y;
        var size = eastWest ? bounds.Size.X : bounds.Size.Y;
        var values = new SortedSet<float>();
        for (var index = 0; index <= BaseSegments; index++) values.Add(start + size * index / BaseSegments);
        foreach (var brush in _terrain.Brushes.Reverse())
        {
            if (!brush.Bounds.Intersects(bounds, true)) continue;
            var center = eastWest ? brush.X : brush.Z;
            var radius = brush.RadiusMeters / RealWorldData.MetersPerUnit;
            foreach (var factor in new[] { 0f, -0.5f, 0.5f, -1f, 1f })
            {
                var value = center + radius * factor;
                if (value > start + 0.0001f && value < start + size - 0.0001f) values.Add(value);
            }
            if (values.Count >= MaximumAxisSamples) break;
        }
        return values.ToArray();
    }

    /// <summary>
    /// 写真参照の頂点を読み込み、写真の道路や建物が通常編集に残らないよう分離します。
    /// 引数: raster（RealRaster）写真と原典地形タイル。
    /// 返り値: void。参照用写真にも最新の地形高を適用します。
    /// </summary>
    private void CreatePhoto(RealRaster raster)
    {
        using var document = RealWorldData.ReadJson(raster.TerrainPath);
        var points = document.RootElement.GetProperty("terrainVertices");
        var positions = new Vector3[points.GetArrayLength()];
        var uvs = new Vector2[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = new Vector3(points[index][0].GetSingle(), 0, points[index][2].GetSingle());
            uvs[index] = new Vector2(points[index][3].GetSingle(), points[index][4].GetSingle());
        }
        var indices = document.RootElement.GetProperty("terrainIndices").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        using var image = Image.LoadFromFile(ProjectSettings.GlobalizePath(raster.Path));
        if (image is null || image.IsEmpty()) throw new InvalidOperationException($"参照写真を開けません: {raster.Path}");
        image.GenerateMipmaps();
        var material = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var instance = new MeshInstance3D { Name = $"参照写真_{_photos.Count}", MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var patch = new PhotoPatch(instance, positions, uvs, indices, raster.Bounds);
        _photoRoot.AddChild(instance);
        _photos.Add(raster.Path, patch);
        RefreshPhoto(patch);
    }

    /// <summary>
    /// 写真のXY位置とUVを維持して現在の地形高へ追従させます。
    /// 引数: patch（PhotoPatch）参照用の頂点・UV・三角形。
    /// 返り値: void。通常地形とのちらつき回避のため2cmだけ上へ描画します。
    /// </summary>
    private void RefreshPhoto(PhotoPatch patch)
    {
        var vertices = new Vector3[patch.Positions.Length];
        var normals = new Vector3[vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var point = patch.Positions[index];
            vertices[index] = new Vector3(point.X, _terrain.Height(point.X, point.Z) + 0.002f, point.Z);
            normals[index] = Normal(point.X, point.Z);
        }
        var old = patch.Instance.Mesh;
        patch.Instance.Mesh = MakeMesh(vertices, normals, patch.Uvs, null, patch.Indices);
        old?.Dispose();
    }

    /// <summary>
    /// 高さ関数の中央差分から傾斜に沿った法線を計算します。
    /// 引数: x/z（float）地表座標。
    /// 返り値: Vector3 正規化された上向き法線。
    /// </summary>
    private Vector3 Normal(float x, float z)
    {
        const float delta = 0.05f;
        return new Vector3(_terrain.Height(x - delta, z) - _terrain.Height(x + delta, z), delta * 2,
            _terrain.Height(x, z - delta) - _terrain.Height(x, z + delta)).Normalized();
    }

    /// <summary>
    /// 頂点配列をGodotの描画用メッシュに変換します。
    /// 引数: vertices/normals（Vector3[]）形状、uvs（Vector2[]?）写真座標、colors（Color[]?）地表色、indices（int[]）三角形。
    /// 返り値: ArrayMesh 1サーフェスのメッシュ。
    /// </summary>
    private static ArrayMesh MakeMesh(Vector3[] vertices, Vector3[] normals, Vector2[]? uvs, Color[]? colors, int[] indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        if (uvs is not null) arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        if (colors is not null) arrays[(int)Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// 行列状の頂点を上向きの三角形へ接続します。
    /// 引数: columns/rows（int）頂点列数と行数。
    /// 返り値: int[] 各セル2三角形の頂点添字。
    /// </summary>
    private static int[] GridIndices(int columns, int rows)
    {
        var values = new int[(columns - 1) * (rows - 1) * 6];
        var index = 0;
        for (var z = 0; z < rows - 1; z++)
            for (var x = 0; x < columns - 1; x++)
            {
                var a = z * columns + x;
                values[index++] = a; values[index++] = a + 1; values[index++] = a + columns;
                values[index++] = a + 1; values[index++] = a + columns + 1; values[index++] = a + columns;
            }
        return values;
    }

    /// <summary>
    /// 全体を16分割したパッチの境界を計算します。
    /// 引数: key（(int,int)）パッチの列と行。
    /// 返り値: Rect2 元地図内のパッチ範囲。
    /// </summary>
    private Rect2 PatchBounds((int, int) key)
    {
        var size = _data.Bounds.Size / PatchDivisions;
        return new Rect2(_data.Bounds.Position + size * new Vector2(key.Item1, key.Item2), size);
    }

    /// <summary>
    /// 同じパッチを重複予約せず描画キューへ追加します。
    /// 引数: key（(int,int)）更新パッチの列と行。
    /// 返り値: void。
    /// </summary>
    private void Enqueue((int, int) key)
    {
        if (_queued.Add(key)) _pending.Enqueue(key);
    }

    private sealed record TerrainPatch(MeshInstance3D Instance);
    private sealed record PhotoPatch(MeshInstance3D Instance, Vector3[] Positions, Vector2[] Uvs, int[] Indices, Rect2 Bounds);
}
