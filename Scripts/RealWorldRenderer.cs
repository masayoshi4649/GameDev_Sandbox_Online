using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// 実地形とPLATEAU建築物を、元の座標と写真を維持して順次描画します。
/// 大容量アセットは自動インポートに依存せず、バイナリから読み込みます。
/// </summary>
public partial class RealWorldRenderer : Node3D
{
    public event Action<int, int>? ProgressChanged;
    public event Action? Completed;
    public event Action<string>? Failed;
    public bool IsComplete { get; private set; }
    public int LoadedBuildings { get; private set; }
    public int LoadedTiles { get; private set; }
    public int TotalTiles { get; private set; }

    private const double FrameBudgetMilliseconds = 10;
    private readonly Queue<RealRaster> _groundQueue = new();
    private readonly Queue<RealTile> _buildingQueue = new();
    private readonly Dictionary<ulong, Texture2D> _mipmappedTextures = new();
    private readonly HashSet<ulong> _preparedMaterials = new();
    private readonly Stopwatch _loadingTimer = new();
    private GltfDocument? _pendingDocument;
    private GltfState? _pendingState;
    private RealTile? _pendingTile;
    private string _activeDataId = "初期化";
    private bool _initialized;
    private bool _failed;

    /// <summary>
    /// 初期視点に近い地表、建物の順で読み込むキューを作成します。
    /// 引数: data（RealWorldData）実地図、initialFocus（Vector2）初期視点のX・Z座標。
    /// 返り値: void。進行状況は地表と建物を合わせたタイル数で通知します。
    /// </summary>
    public void Initialize(RealWorldData data, Vector2 initialFocus)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_initialized) throw new InvalidOperationException("実地図の描画は初期化済みです。");
        _initialized = true;
        _loadingTimer.Start();
        IsComplete = false;
        LoadedBuildings = 0;
        LoadedTiles = 0;
        TotalTiles = data.Rasters.Count + data.Tiles.Count;

        // ----------------------------------------

        if (DisplayServer.GetName() == "headless" && InteractionTest.IsRequested)
        {
            CompleteLoading(true);
            return;
        }
        foreach (var raster in data.Rasters.OrderBy(item => item.Bounds.GetCenter().DistanceSquaredTo(initialFocus)))
            _groundQueue.Enqueue(raster);
        foreach (var tile in data.Tiles.OrderBy(item =>
        {
            var center = item.Bounds.GetCenter();
            return new Vector2(center.X, center.Z).DistanceSquaredTo(initialFocus);
        })) _buildingQueue.Enqueue(tile);
        SetProcess(true);
        ProgressChanged?.Invoke(0, TotalTiles);
        if (TotalTiles == 0) Fail("描画する実地図のタイルがありません。");
    }

    /// <summary>
    /// フレームごとに1タイルを読み、画像のGPU転送を分散します。
    /// 引数: delta（double）前フレームからの秒数。
    /// 返り値: void。不可分なネイティブGLB解析は10msを超える場合があります。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_initialized || _failed || IsComplete) return;
        var timer = Stopwatch.StartNew();
        try
        {
            if (_groundQueue.TryDequeue(out var raster))
            {
                _activeDataId = raster.TerrainPath;
                AddChild(CreateGround(raster));
                LoadedTiles++;
            }
            else
            {
                if (_pendingTile is null && _buildingQueue.TryDequeue(out var nextTile))
                {
                    _activeDataId = nextTile.Id;
                    BeginBuildingTile(nextTile);
                    if (timer.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds) return;
                }
                var tile = _pendingTile ?? throw new InvalidOperationException("読み込み中の建築物タイルがありません。");
                AddChild(CompleteBuildingTile());
                LoadedTiles++;
                LoadedBuildings += tile.BuildingCount;
            }
            ProgressChanged?.Invoke(LoadedTiles, TotalTiles);

            // ----------------------------------------

            if (_groundQueue.Count == 0 && _buildingQueue.Count == 0 && _pendingTile is null)
            {
                CompleteLoading();
            }
            // 同じフレームで次のタイルのGPU転送を始めず、描画へ処理を返します。
        }
        catch (Exception error)
        {
            Fail($"実地図の読み込みに失敗しました。{error.Message}");
        }
    }

    // ----------------------------------------

    /// <summary>
    /// 実標高の頂点と写真から、影を落とさない地表メッシュを生成します。
    /// 引数: raster（RealRaster）写真と実地形JSONの組。
    /// 返り値: MeshInstance3D 写真を持つ実地形。
    /// </summary>
    private MeshInstance3D CreateGround(RealRaster raster)
    {
        if (!Godot.FileAccess.FileExists(raster.Path)) throw new InvalidOperationException($"地表写真がありません: {raster.Path}");
        using var document = RealWorldData.ReadJson(raster.TerrainPath);
        var root = document.RootElement;
        var sourceVertices = root.GetProperty("terrainVertices");
        var sourceIndices = root.GetProperty("terrainIndices");
        var vertices = new Vector3[sourceVertices.GetArrayLength()];
        var normals = new Vector3[vertices.Length];
        var coordinates = new Vector2[vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var value = sourceVertices[index];
            if (value.GetArrayLength() != 5) throw new InvalidOperationException($"地表の頂点形式が不正です: {raster.TerrainPath}");
            vertices[index] = new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
            normals[index] = Vector3.Up;
            coordinates[index] = new Vector2(value[3].GetSingle(), value[4].GetSingle());
        }
        var indices = sourceIndices.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(index => index < 0 || index >= vertices.Length))
            throw new InvalidOperationException($"地表の三角形が不正です: {raster.TerrainPath}");

        // ----------------------------------------

        using var image = Image.LoadFromFile(ProjectSettings.GlobalizePath(raster.Path));
        if (image is null || image.IsEmpty()) throw new InvalidOperationException($"地表写真を開けません: {raster.Path}");
        if (!image.HasMipmaps() && image.GenerateMipmaps() != Error.Ok)
            throw new InvalidOperationException($"地表写真の縮小画像を生成できません: {raster.Path}");
        var material = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Metallic = 0,
            Roughness = 1
        };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = coordinates;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return new MeshInstance3D
        {
            Name = $"実地形_{LoadedTiles}",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }

    /// <summary>
    /// GLBの生バイトをGodot標準ローダーに渡し、シーン生成を次の工程へ分けます。
    /// 引数: tile（RealTile）GLBと建築件数を持つタイル。
    /// 返り値: void。解析済みの状態は描画工程まで保持します。
    /// </summary>
    private void BeginBuildingTile(RealTile tile)
    {
        using var file = Godot.FileAccess.Open(tile.Path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"建築物のGLBがありません: {tile.Path}");
        var bytes = file.GetBuffer((long)file.GetLength());
        var document = new GltfDocument();
        var state = new GltfState();
        var result = document.AppendFromBuffer(bytes, "res://Assets/RealWorld/Plateau/", state);
        if (result != Error.Ok)
        {
            document.Dispose();
            state.Dispose();
            throw new InvalidOperationException($"建築物のGLBを読めません: {tile.Id}（{result}）");
        }
        _pendingDocument = document;
        _pendingState = state;
        _pendingTile = tile;
    }

    /// <summary>
    /// 解析済みのGLBから元の形状と材質を持つシーンを生成します。
    /// 引数: なし。初期解析で保持したGltfStateを使用します。
    /// 返り値: Node3D 焼込済み座標の建物シーン。
    /// </summary>
    private Node3D CompleteBuildingTile()
    {
        try
        {
            var generated = _pendingDocument!.GenerateScene(_pendingState!);
            if (generated is not Node3D scene || _pendingState!.GetMeshes().Count == 0)
            {
                generated?.Free();
                throw new InvalidOperationException($"建築物のメッシュがありません: {_pendingTile!.Id}");
            }
            scene.Name = $"実建築_{_pendingTile!.Id}";
            try { PrepareBuildingMaterials(scene); }
            catch { scene.Free(); throw; }
            return scene;
        }
        finally { ReleasePendingTile(); }
    }

    /// <summary>
    /// 元の写真材質を維持し、建築物の反射と縮小時のちらつきを抑えます。
    /// 引数: scene（Node）GLBから生成したノード。
    /// 返り値: void。形状、面の表裏、法線、座標には変更を加えません。
    /// </summary>
    private void PrepareBuildingMaterials(Node scene)
    {
        var nodes = new Stack<Node>();
        nodes.Push(scene);
        while (nodes.TryPop(out var node))
        {
            foreach (var child in node.GetChildren()) nodes.Push(child);
            if (node is not MeshInstance3D { Mesh: { } mesh } instance) continue;
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D material || !_preparedMaterials.Add(material.GetInstanceId())) continue;
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
                material.Metallic = 0;
                material.Roughness = 1;
                if (material.AlbedoTexture is { } texture) material.AlbedoTexture = WithMipmaps(texture);
                else
                {
                    var color = material.AlbedoColor;
                    var luminance = color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f;
                    if (luminance > 0.9f) material.AlbedoColor = new Color(183 / 255f, 191 / 255f, 197 / 255f, color.A);
                }
            }
        }
    }

    /// <summary>
    /// 同一写真の処理を重複させず、欠落している縮小画像だけを生成します。
    /// 引数: texture（Texture2D）元のGLB写真。
    /// 返り値: Texture2D 縮小画像を持つ元テクスチャまたは生成テクスチャ。
    /// </summary>
    private Texture2D WithMipmaps(Texture2D texture)
    {
        var identifier = texture.GetInstanceId();
        if (_mipmappedTextures.TryGetValue(identifier, out var prepared)) return prepared;
        using var image = texture.GetImage();
        if (image is null || image.IsEmpty()) throw new InvalidOperationException("建築物の写真テクスチャを取得できません。");
        if (image.HasMipmaps()) return _mipmappedTextures[identifier] = texture;
        if (image.IsCompressed() && image.Decompress() != Error.Ok)
            throw new InvalidOperationException("建築物の写真テクスチャを展開できません。");
        if (image.GenerateMipmaps() != Error.Ok)
            throw new InvalidOperationException("建築物の写真テクスチャの縮小画像を生成できません。");
        return _mipmappedTextures[identifier] = ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// 不完全な実データを成功扱いにせず、読み込みを止めて理由を通知します。
    /// 引数: message（string）失敗したデータと理由。
    /// 返り値: void。IsCompleteはfalseのまま保持します。
    /// </summary>
    private void Fail(string message)
    {
        ReleasePendingTile();
        _loadingTimer.Stop();
        _failed = true;
        IsComplete = false;
        SetProcess(false);
        var detail = $"{message}（データ: {_activeDataId}）";
        GD.PushError(detail);
        GD.Print(FormattableString.Invariant($"BAYFRONT_WORLD_FAILED elapsed_ms={_loadingTimer.Elapsed.TotalMilliseconds:F0} data_id={_activeDataId} buildings={LoadedBuildings} tiles={LoadedTiles}/{TotalTiles}"));
        Failed?.Invoke(detail);
    }

    /// <summary>
    /// 読み込み完了を記録し、実経過時間と描画済み件数を通知します。
    /// 引数: renderingSkipped（bool）対話テストで描画を省略した場合はtrue。
    /// 返り値: void。BAYFRONT_WORLD_READYログの件数は実際の描画済み件数です。
    /// </summary>
    private void CompleteLoading(bool renderingSkipped = false)
    {
        _loadingTimer.Stop();
        IsComplete = true;
        SetProcess(false);
        _mipmappedTextures.Clear();
        _preparedMaterials.Clear();
        GD.Print(FormattableString.Invariant($"BAYFRONT_WORLD_READY elapsed_ms={_loadingTimer.Elapsed.TotalMilliseconds:F0} buildings={LoadedBuildings} tiles={LoadedTiles}/{TotalTiles} rendering_skipped={renderingSkipped}"));
        Completed?.Invoke();
    }

    /// <summary>
    /// シーン生成前のGLB解析用リソースを解放します。
    /// 引数: なし。返り値: void。
    /// </summary>
    private void ReleasePendingTile()
    {
        _pendingDocument?.Dispose();
        _pendingState?.Dispose();
        _pendingDocument = null;
        _pendingState = null;
        _pendingTile = null;
    }

    /// <summary>
    /// 読み込み途中でゲームを閉じた場合も解析用リソースを解放します。
    /// 引数: なし。返り値: void。
    /// </summary>
    public override void _ExitTree() => ReleasePendingTile();
}
