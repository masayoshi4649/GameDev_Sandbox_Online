using Godot;
using System;
using System.Collections.Generic;

namespace Bayfront;

/// <summary>
/// 都市を俯瞰するカメラの移動、回転、拡大を管理します。
/// </summary>
public partial class OrbitCamera : Node3D
{
    public event Action? ManualPanRequested;
    public Camera3D Camera { get; private set; } = null!;
    private Vector3 _focus = new(0, 0, -20);
    private float _yaw = 0.15f;
    private float _pitch = 0.88f;
    private float _distance = 510;
    private bool _dragging;
    private bool _orbiting;
    private bool _perspective;
    private Func<float, float, float> _groundHeight = (_, _) => 0;
    private float? _trackedHeight;

    /// <summary>
    /// カメラを生成します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _Ready()
    {
        Camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = _distance, Fov = 48, Near = 0.1f, Far = 3500, Current = true };
        AddChild(Camera);
        UpdateTransform();
    }

    /// <summary>
    /// キー入力から視点を移動します。
    /// 引数: delta（double）経過秒数。返り値: void。
    /// </summary>
    public override void _Process(double delta)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        var axis = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) axis.Y -= 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) axis.Y += 1;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) axis.X -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) axis.X += 1;
        var movement = new Vector3(axis.X, 0, axis.Y).Rotated(Vector3.Up, _yaw);
        if (axis != Vector2.Zero) { _trackedHeight = null; ManualPanRequested?.Invoke(); }
        _focus += movement * (float)delta * _distance * 0.45f;
        _focus.X = Mathf.Clamp(_focus.X, -400, 400);
        _focus.Z = Mathf.Clamp(_focus.Z, -400, 400);
        if (Input.IsPhysicalKeyPressed(Key.Q)) _yaw += (float)delta * 0.7f;
        if (Input.IsPhysicalKeyPressed(Key.E)) _yaw -= (float)delta * 0.7f;
        UpdateTransform();
    }

    /// <summary>
    /// マウスのドラッグとホイールを処理します。
    /// 引数: input（InputEvent）入力イベント。返り値: void。
    /// </summary>
    public override void _UnhandledInput(InputEvent input)
    {
        if (input is InputEventMouseButton mouse)
        {
            if (mouse.ButtonIndex == MouseButton.Middle) _dragging = mouse.Pressed;
            if (mouse.ButtonIndex == MouseButton.Right) _orbiting = mouse.Pressed;
            if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelUp) _distance = Mathf.Max(_perspective ? 3 : 8, _distance * 0.9f);
            if (mouse.Pressed && mouse.ButtonIndex == MouseButton.WheelDown) _distance = Mathf.Min(1050, _distance * 1.1f);
        }
        if (input is InputEventMouseMotion motion)
        {
            if (_dragging)
            {
                if (motion.Relative != Vector2.Zero) { _trackedHeight = null; ManualPanRequested?.Invoke(); }
                var amount = _distance / GetViewport().GetVisibleRect().Size.Y;
                _focus += new Vector3(-motion.Relative.X, 0, -motion.Relative.Y / Mathf.Sin(_pitch)).Rotated(Vector3.Up, _yaw) * amount;
            }
            if (_orbiting)
            {
                _yaw -= motion.Relative.X * 0.005f;
                _pitch = Mathf.Clamp(_pitch + motion.Relative.Y * 0.003f, 0.40f, 1.35f);
            }
        }
        UpdateTransform();
    }

    /// <summary>
    /// UI上でボタンを離した場合もドラッグ状態を解除します。
    /// 引数: input（InputEvent）入力イベント。返り値: void。
    /// </summary>
    public override void _Input(InputEvent input)
    {
        if (input is not InputEventMouseButton { Pressed: false } mouse) return;
        if (mouse.ButtonIndex == MouseButton.Middle) _dragging = false;
        if (mouse.ButtonIndex == MouseButton.Right) _orbiting = false;
    }

    // ----------------------------------------

    /// <summary>
    /// 指定地区へ視点を合わせます。
    /// 引数: center（Vector2）地区中心、distance（float）表示範囲。返り値: void。
    /// </summary>
    public void FocusOn(Vector2 center, float distance = 245)
    {
        _trackedHeight = null;
        _focus = new Vector3(center.X, _groundHeight(center.X, center.Y), center.Y);
        _distance = distance;
        UpdateTransform();
    }

    /// <summary>
    /// 地表以外の高さを持つ個体へ、指定された表示高度で視点を合わせます。
    /// 引数: center（Vector3）注視する三次元位置、distance（float）表示範囲。
    /// 返り値: void。橋上や海上の個体を地表へ押し戻しません。
    /// </summary>
    public void FocusOn(Vector3 center, float distance = 245)
    {
        _focus = center; _trackedHeight = center.Y; _distance = distance;
        UpdateTransform();
    }

    /// <summary>
    /// 対象の長手方向を横から見て、全ての立体頂点を編集画面の中央領域へ収めます。
    /// 引数: points（IReadOnlyList&lt;Vector3&gt;）対象の立体範囲、along（Vector2）長手方向。
    /// 返り値: void。固定距離で遠方や画面下端へ対象を追いやらない初期視点を設定します。
    /// </summary>
    public void FramePoints(IReadOnlyList<Vector3> points, Vector2 along)
    {
        if (points.Count == 0) return;
        var minimum = points[0]; var maximum = points[0];
        foreach (var point in points) { minimum = minimum.Min(point); maximum = maximum.Max(point); }
        var direction = along.LengthSquared() > 0.0001f ? along.Normalized() : Vector2.Right;
        var side = new Vector2(-direction.Y, direction.X);
        _yaw = Mathf.Atan2(side.X, side.Y); _pitch = 0.78f;
        _focus = (minimum + maximum) * 0.5f; _trackedHeight = _focus.Y;
        _distance = 10; UpdateTransform();
        var projection = Camera.GetCameraProjection();
        var horizontal = Math.Abs(projection.X.X); var vertical = Math.Abs(projection.Y.Y);
        var distance = 3f;
        foreach (var point in points)
        {
            var relative = point - _focus;
            var x = Math.Abs(Camera.GlobalBasis.X.Dot(relative));
            var y = Math.Abs(Camera.GlobalBasis.Y.Dot(relative));
            var z = Camera.GlobalBasis.Z.Dot(relative);
            distance = Math.Max(distance, _perspective
                ? z + Math.Max(x * horizontal / (2 * 0.42f), y * vertical / (2 * 0.29f))
                : Math.Max(x * 10 * horizontal / (2 * 0.42f), y * 10 * vertical / (2 * 0.29f)));
        }
        _distance = distance * 1.08f;
        UpdateTransform();
    }

    /// <summary>
    /// 拡大率と向きを保って移動個体を注視します。
    /// 引数: center（Vector2）追跡する都市座標。返り値: void。
    /// </summary>
    public void Follow(Vector2 center) { _trackedHeight = null; _focus.X = center.X; _focus.Z = center.Y; UpdateTransform(); }

    /// <summary>
    /// 拡大率と向きを保ち、補間済み個体の三次元位置を注視します。
    /// 引数: center（Vector3）描画中の個体位置。
    /// 返り値: void。回転と拡縮を行っても追跡を継続できます。
    /// </summary>
    public void Follow(Vector3 center) { _focus = center; _trackedHeight = center.Y; UpdateTransform(); }

    /// <summary>
    /// 画面座標を地表の座標へ変換します。
    /// 引数: screen（Vector2）画面位置。返り値: Vector3 地表の位置。
    /// </summary>
    public Vector3 GroundPosition(Vector2 screen)
    {
        var origin = Camera.ProjectRayOrigin(screen);
        var direction = Camera.ProjectRayNormal(screen);
        if (_groundHeight.Target is UrbanTerrain terrain)
        {
            if (terrain.Raycast(origin, direction, out var hit)) return hit;
            if (Math.Abs(direction.Y) < 0.0001f) return _focus;
            var waterDistance = (terrain.SeaLevel - origin.Y) / direction.Y;
            return waterDistance > 0 ? origin + direction * waterDistance : _focus;
        }
        if (RaycastHeight(origin, direction, out var sampleHit)) return sampleHit;
        return _focus;
    }

    /// <summary>
    /// 旧地図を含む高さ関数と視線の交点を、区間探索と二分探索で求めます。
    /// 引数: origin/direction（Vector3）視線、hit（out Vector3）取得した交点。
    /// 返り値: bool 前方5,000ゲーム単位以内で交差する場合はtrue。
    /// </summary>
    private bool RaycastHeight(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        hit = _focus;
        var previousDistance = 0f;
        var previous = origin.Y - _groundHeight(origin.X, origin.Z);
        var horizontal = new Vector2(direction.X, direction.Z).Length();
        var step = horizontal < 0.0001f ? 1 : Math.Min(8, 0.5f / horizontal);
        for (var distance = step; distance <= 5000; distance += step)
        {
            var point = origin + direction * distance;
            var difference = point.Y - _groundHeight(point.X, point.Z);
            if ((previous > 0) != (difference > 0) || Math.Abs(difference) < 0.00002f)
            {
                var left = previousDistance;
                var right = distance;
                for (var index = 0; index < 24; index++)
                {
                    var middle = (left + right) * 0.5f;
                    var candidate = origin + direction * middle;
                    var delta = candidate.Y - _groundHeight(candidate.X, candidate.Z);
                    if ((delta > 0) == (previous > 0)) left = middle; else right = middle;
                }
                hit = origin + direction * ((left + right) * 0.5f);
                return true;
            }
            previous = difference;
            previousDistance = distance;
        }
        return false;
    }

    /// <summary>
    /// 実地形の高さと透視投影を設定します。
    /// 引数: height（Func&lt;float,float,float&gt;）地表高、perspective（bool）透視投影の指定。返り値: void。
    /// </summary>
    public void ConfigureTerrain(Func<float, float, float> height, bool perspective)
    {
        _groundHeight = height;
        _perspective = perspective;
        if (perspective) { _pitch = 0.68f; _yaw = 0.35f; }
        Camera.Projection = perspective ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal;
        UpdateTransform();
    }

    /// <summary>
    /// カメラの位置と注視点を更新します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void UpdateTransform()
    {
        if (Camera is null) return;
        Camera.Size = _distance;
        _focus.Y = _trackedHeight ?? _groundHeight(_focus.X, _focus.Z);
        Camera.Position = _focus + new Vector3(0, Mathf.Sin(_pitch), Mathf.Cos(_pitch)).Rotated(Vector3.Up, _yaw) * (_perspective ? _distance : 700);
        Camera.LookAt(_focus, Vector3.Up);
    }
}
