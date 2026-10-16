using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bayfront;

/// <summary>
/// PLATEAUの元形状を三角形と建物識別子で関連付けます。
/// glTF importerの頂点再配列に依存せず、取込済みGLBのaccessor/indexを直接読みます。
/// </summary>
public sealed class UrbanSourceMeshes
{
    public sealed record Primitive(Vector3[] Vertices, Vector3[] Normals, Vector2[] Coordinates, Color[] Colors, Material Material);
    public sealed record FeaturePart(Primitive Source, int[] Indices);
    private readonly Dictionary<string, List<FeaturePart>> _features = new(StringComparer.Ordinal);
    public int FeatureCount => _features.Count;
    public int TriangleCount { get; private set; }
    public static int TextureSizeLimit { get; } = ReadTextureSize();

    /// <summary>
    /// 指定した建物に属する原典三角形を取得します。
    /// 引数: id（string）建物識別子、parts（out IReadOnlyList）材質別の三角形。
    /// 返り値: bool 元形状が存在する場合はtrue。
    /// </summary>
    public bool TryGet(string id, out IReadOnlyList<FeaturePart> parts)
    {
        if (_features.TryGetValue(id, out var values)) { parts = values; return true; }
        parts = Array.Empty<FeaturePart>(); return false;
    }

    /// <summary>
    /// 既に読み込んだ原典三角形と視線を交差させ、張り出し・段差・凹部を描画と同じ形で選択します。
    /// 引数: id（string）建物識別子、origin/direction（Vector3）原典座標系の視線、limit（float）探索距離、distance（out float）最短距離。
    /// 返り値: bool 視線の前方で指定距離以内の原典三角面に当たる場合はtrue。
    /// </summary>
    public bool IntersectRay(string id, Vector3 origin, Vector3 direction, float limit, out float distance)
    {
        distance = float.PositiveInfinity;
        if (!_features.TryGetValue(id, out var parts)) return false;
        foreach (var part in parts)
        {
            var vertices = part.Source.Vertices; var indices = part.Indices;
            for (var index = 0; index + 2 < indices.Length; index += 3)
                if (UrbanCatalog.RayTriangle(origin, direction, vertices[indices[index]], vertices[indices[index + 1]], vertices[indices[index + 2]], out var hit)
                    && hit <= limit && hit < distance) distance = hit;
        }
        return float.IsFinite(distance);
    }

    /// <summary>
    /// 一つのGLBと建物対応表を検証し、材質を保持した編集用原典メッシュへ分解します。
    /// 引数: tile（RealTile）取込済みタイル。
    /// 返り値: void。SHA、三角形数、頂点範囲が不一致なら例外にします。
    /// </summary>
    public void LoadTile(RealTile tile)
    {
        var mappingPath = tile.Path[..^4] + ".features.json";
        if (!Godot.FileAccess.FileExists(mappingPath))
            throw new InvalidOperationException($"編集用建物対応表がありません。import-world.ps1を再実行してください: {mappingPath}");
        using var glbFile = Godot.FileAccess.Open(tile.Path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"元形状を開けません: {tile.Path}");
        var bytes = glbFile.GetBuffer((long)glbFile.GetLength());
        using var mapping = RealWorldData.ReadJson(mappingPath);
        var expectedHash = mapping.RootElement.GetProperty("glbSha256").GetString();
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (actualHash != expectedHash) throw new InvalidOperationException($"建物対応表とGLBのSHA256が一致しません: {tile.Id}");
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            throw new InvalidOperationException($"GLBヘッダーが不正です: {tile.Id}");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var binaryOffset = 28 + jsonLength;
        var binary = bytes.AsMemory(binaryOffset);
        var root = document.RootElement;
        var featureIds = mapping.RootElement.GetProperty("featureIds").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var primitiveMaps = mapping.RootElement.GetProperty("primitives").EnumerateArray().ToArray();
        var primitives = root.GetProperty("meshes").EnumerateArray().SelectMany(value => value.GetProperty("primitives").EnumerateArray()).ToArray();
        if (primitiveMaps.Length != primitives.Length) throw new InvalidOperationException($"描画面と建物対応表の数が一致しません: {tile.Id}");
        var materials = ReadMaterials(root, binary, actualHash);
        var defaultMaterial = new StandardMaterial3D { AlbedoColor = new Color("#b6c1c4"), Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var tileTriangles = 0;
        for (var number = 0; number < primitives.Length; number++)
        {
            var primitive = primitives[number];
            var attributes = primitive.GetProperty("attributes");
            var positionValues = ReadFloats(root, binary, attributes.GetProperty("POSITION").GetInt32(), 3);
            var positions = Enumerable.Range(0, positionValues.Length / 3).Select(index => new Vector3(positionValues[index * 3], positionValues[index * 3 + 1], positionValues[index * 3 + 2])).ToArray();
            var normals = new Vector3[positions.Length];
            if (attributes.TryGetProperty("NORMAL", out var normalAccessor))
            {
                var values = ReadFloats(root, binary, normalAccessor.GetInt32(), 3);
                for (var i = 0; i < normals.Length; i++) normals[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            }
            var coordinates = new Vector2[positions.Length];
            if (attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor))
            {
                var values = ReadFloats(root, binary, uvAccessor.GetInt32(), 2);
                for (var i = 0; i < coordinates.Length; i++) coordinates[i] = new Vector2(values[i * 2], values[i * 2 + 1]);
            }
            var colors = Enumerable.Repeat(Colors.White, positions.Length).ToArray();
            if (attributes.TryGetProperty("COLOR_0", out var colorAccessor))
            {
                var accessor = root.GetProperty("accessors")[colorAccessor.GetInt32()];
                var components = accessor.GetProperty("type").GetString() == "VEC3" ? 3 : 4;
                var values = ReadFloats(root, binary, colorAccessor.GetInt32(), components);
                for (var i = 0; i < colors.Length; i++) colors[i] = new Color(values[i * components], values[i * components + 1], values[i * components + 2], components == 4 ? values[i * components + 3] : 1);
            }
            var indices = ReadIndices(root, binary, primitive.GetProperty("indices").GetInt32());
            var features = primitiveMaps[number].GetProperty("triangleFeatures").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (indices.Length % 3 != 0 || features.Length != indices.Length / 3) throw new InvalidOperationException($"三角形対応数が一致しません: {tile.Id}/{number}");
            var material = primitive.TryGetProperty("material", out var materialIndex) ? materials[materialIndex.GetInt32()] : defaultMaterial;
            var source = new Primitive(positions, normals, coordinates, colors, material);
            var groups = new Dictionary<int, List<int>>();
            for (var triangle = 0; triangle < features.Length; triangle++)
            {
                var feature = features[triangle];
                if (feature < 0 || feature >= featureIds.Length) throw new InvalidOperationException("建物番号が対応表の範囲外です。");
                if (!groups.TryGetValue(feature, out var group)) groups[feature] = group = new List<int>();
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = indices[triangle * 3 + corner];
                    if (index < 0 || index >= positions.Length) throw new InvalidOperationException("頂点番号が範囲外です。");
                    group.Add(index);
                }
            }
            foreach (var group in groups)
            {
                var id = featureIds[group.Key];
                if (!_features.TryGetValue(id, out var parts)) _features[id] = parts = new List<FeaturePart>();
                parts.Add(new FeaturePart(source, group.Value.ToArray()));
            }
            tileTriangles += features.Length;
        }
        if (tileTriangles != tile.TriangleCount) throw new InvalidOperationException($"原典三角形数が一致しません: {tile.Id}");
        TriangleCount += tileTriangles;
    }

    // ----------------------------------------

    /// <summary>
    /// 原典のbaseColor、埋込み写真、透過設定をGodot材質へ復元します。
    /// 引数: root（JsonElement）GLTF、binary（ReadOnlyMemory&lt;byte&gt;）BIN、sourceHash（string）原典SHA256。
    /// 返り値: Material[] 原典と同じ順序の材質。
    /// </summary>
    private static Material[] ReadMaterials(JsonElement root, ReadOnlyMemory<byte> binary, string sourceHash)
    {
        if (!root.TryGetProperty("materials", out var array)) return Array.Empty<Material>();
        var textures = new Dictionary<int, Texture2D>();
        return array.EnumerateArray().Select(value =>
        {
            var material = new StandardMaterial3D { Roughness = 1, Metallic = 0,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic };
            var hasPhoto = false;
            if (value.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var factor)) material.AlbedoColor = new Color(factor[0].GetSingle(), factor[1].GetSingle(), factor[2].GetSingle(), factor[3].GetSingle());
                if (pbr.TryGetProperty("baseColorTexture", out var source))
                {
                    var textureIndex = source.GetProperty("index").GetInt32();
                    if (!textures.TryGetValue(textureIndex, out var texture))
                    {
                        var textureSource = root.GetProperty("textures")[textureIndex];
                        int imageIndex;
                        if (textureSource.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("EXT_texture_webp", out var webp)) imageIndex = webp.GetProperty("source").GetInt32();
                        else imageIndex = textureSource.GetProperty("source").GetInt32();
                        var imageSource = root.GetProperty("images")[imageIndex];
                        var view = root.GetProperty("bufferViews")[imageSource.GetProperty("bufferView").GetInt32()];
                        var offset = view.TryGetProperty("byteOffset", out var start) ? start.GetInt32() : 0;
                        using var image = new Image();
                        var mime = imageSource.GetProperty("mimeType").GetString();
                        var cacheRoot = ProjectSettings.GlobalizePath($"user://cache/urban-textures/v1-{TextureSizeLimit}");
                        var cacheBase = System.IO.Path.Combine(cacheRoot, $"{sourceHash}-{imageIndex}");
                        var cached = System.IO.File.Exists(cacheBase + ".jpg") ? cacheBase + ".jpg" : System.IO.File.Exists(cacheBase + ".png") ? cacheBase + ".png" : "";
                        Error result;
                        if (cached.Length > 0) result = image.Load(cached);
                        else
                        {
                            var bytes = binary.Slice(offset, view.GetProperty("byteLength").GetInt32()).ToArray();
                            result = mime switch { "image/webp" => image.LoadWebpFromBuffer(bytes), "image/png" => image.LoadPngFromBuffer(bytes), "image/jpeg" => image.LoadJpgFromBuffer(bytes), _ => Error.FileUnrecognized };
                        }
                        if (result != Error.Ok) throw new InvalidOperationException($"原典写真を展開できません: {mime}/{result}");
                        var largest = Math.Max(image.GetWidth(), image.GetHeight());
                        if (largest > TextureSizeLimit)
                        {
                            var ratio = (float)TextureSizeLimit / largest;
                            image.Resize(Math.Max(1, (int)(image.GetWidth() * ratio)), Math.Max(1, (int)(image.GetHeight() * ratio)), Image.Interpolation.Lanczos);
                        }
                        if (image.DetectAlpha() == Image.AlphaMode.None) image.Convert(Image.Format.Rgb8);
                        if (cached.Length == 0)
                        {
                            System.IO.Directory.CreateDirectory(cacheRoot);
                            var hasTransparency = image.DetectAlpha() != Image.AlphaMode.None;
                            var destination = cacheBase + (hasTransparency ? ".png" : ".jpg");
                            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            var saved = hasTransparency ? image.SavePng(temporary) : image.SaveJpg(temporary, 0.96f);
                            if (saved == Error.Ok) System.IO.File.Move(temporary, destination, true);
                            else if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
                        }
                        image.GenerateMipmaps(); texture = ImageTexture.CreateFromImage(image); textures[textureIndex] = texture;
                    }
                    material.AlbedoTexture = texture; hasPhoto = true;
                }
            }
            if (!hasPhoto && material.AlbedoColor.R > 0.9f && material.AlbedoColor.G > 0.9f && material.AlbedoColor.B > 0.9f)
                material.AlbedoColor = new Color(0.72f, 0.75f, 0.77f, material.AlbedoColor.A);
            if (value.TryGetProperty("alphaMode", out var alpha))
            {
                if (alpha.GetString() == "BLEND") material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                if (alpha.GetString() == "MASK") { material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; material.AlphaScissorThreshold = value.TryGetProperty("alphaCutoff", out var cutoff) ? cutoff.GetSingle() : 0.5f; }
            }
            return (Material)material;
        }).ToArray();
    }

    /// <summary>
    /// 浮動小数点accessorのstrideを解釈して属性を取得します。
    /// 引数: root（JsonElement）GLTF、binary（ReadOnlyMemory）BIN、index/components（int）accessor番号と要素数。
    /// 返り値: float[] 未変形の原典頂点属性。
    /// </summary>
    private static float[] ReadFloats(JsonElement root, ReadOnlyMemory<byte> binary, int index, int components)
    {
        var accessor = root.GetProperty("accessors")[index];
        if (accessor.GetProperty("componentType").GetInt32() != 5126) throw new InvalidOperationException("浮動小数点以外の頂点属性です。");
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var offset = (view.TryGetProperty("byteOffset", out var v) ? v.GetInt32() : 0) + (accessor.TryGetProperty("byteOffset", out var a) ? a.GetInt32() : 0);
        var stride = view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : components * 4;
        var count = accessor.GetProperty("count").GetInt32();
        var values = new float[count * components];
        for (var item = 0; item < count; item++)
            for (var component = 0; component < components; component++)
                values[item * components + component] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(binary.Span.Slice(offset + item * stride + component * 4, 4)));
        return values;
    }

    /// <summary>
    /// unsigned accessorを元の三角形頂点番号へ復元します。
    /// 引数: root（JsonElement）GLTF、binary（ReadOnlyMemory）BIN、index（int）accessor番号。
    /// 返り値: int[] 頂点番号。元の順序を変えません。
    /// </summary>
    private static int[] ReadIndices(JsonElement root, ReadOnlyMemory<byte> binary, int index)
    {
        var accessor = root.GetProperty("accessors")[index];
        var type = accessor.GetProperty("componentType").GetInt32();
        var size = type == 5125 ? 4 : type == 5123 ? 2 : 1;
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var offset = (view.TryGetProperty("byteOffset", out var v) ? v.GetInt32() : 0) + (accessor.TryGetProperty("byteOffset", out var a) ? a.GetInt32() : 0);
        var values = new int[accessor.GetProperty("count").GetInt32()];
        for (var i = 0; i < values.Length; i++) values[i] = size switch
        {
            4 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(binary.Span.Slice(offset + i * 4, 4))),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(binary.Span.Slice(offset + i * 2, 2)),
            _ => binary.Span[offset + i]
        };
        return values;
    }

    /// <summary>
    /// クライアントの写真描画上限を起動引数から読みます。原典形状や計算データには影響しません。
    /// 引数: なし。返り値: int 写真の長辺の上限画素数。既定は2048です。
    /// </summary>
    private static int ReadTextureSize()
    {
        var argument = OS.GetCmdlineUserArgs().FirstOrDefault(value => value.StartsWith("--urban-texture-size=", StringComparison.Ordinal));
        return argument is not null && int.TryParse(argument.Split('=')[1], out var size) ? Math.Clamp(size, 256, 8192) : 2048;
    }
}
