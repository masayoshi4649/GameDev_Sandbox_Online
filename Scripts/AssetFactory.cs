using Godot;
using System;
using System.Collections.Generic;

namespace Bayfront;

/// <summary>
/// 市販素材を使わず、共有メッシュと共有素材から湾岸の低ポリ景観を生成します。
/// 全建築は原点を敷地中央とし、上方向をY正として配置します。
/// </summary>
public static class AssetFactory
{
    private static readonly Dictionary<string, StandardMaterial3D> Materials = new();
    private static readonly Dictionary<string, Mesh> Meshes = new();
    private static readonly Color Concrete = new("#E4E3D5");
    private static readonly Color Pale = new("#F1EEE0");
    private static readonly Color Glass = new("#46768A");
    private static readonly Color DarkGlass = new("#254857");
    private static readonly Color Roof = new("#879C9C");
    private static readonly Color Green = new("#6E9D6C");
    private static readonly Color Path = new("#DECFAD");

    /**
     * 機能: 住宅、業務、公園、公共施設の手続き型モデルを生成します。
     * 引数: kindはstring型の建築種別、variantはint型の形状番号。
     * 返り値: Node3D型のモデル。位置と回転は呼び出し側で設定します。
     */
    public static Node3D CreateBuilding(string kind, int variant = 0)
    {
        variant = (variant % 7 + 7) % 7;
        var root = new Node3D { Name = "街区アセット" };
        switch (kind)
        {
            case "park": BuildPark(root, variant); break;
            case "office": BuildOffice(root, variant); break;
            case "civic": BuildCivic(root, variant); break;
            default: BuildHousing(root, variant); break;
        }
        return root;
    }

    /**
     * 機能: 実在施設の雰囲気を参考にしたオリジナルの記念建築を生成します。
     * 引数: kindはstring型。observatory、exhibition、market、towerを指定します。
     * 返り値: Node3D型のランドマーク。
     */
    public static Node3D CreateLandmark(string kind)
    {
        var root = new Node3D { Name = "湾岸ランドマーク" };
        switch (kind)
        {
            case "exhibition": BuildExhibition(root); break;
            case "market": BuildMarket(root); break;
            case "tower": BuildTower(root); break;
            default: BuildObservatory(root); break;
        }
        return root;
    }

    // ----------------------------------------

    /**
     * 機能: 縁石と破線を含む道路を二地点の間に生成します。海上区間は橋として使えます。
     * 引数: startとendはVector3型の端点、widthはfloat型の道路幅。
     * 返り値: MeshInstance3D型の道路。端点を含むワールド座標に配置済みです。
     */
    public static MeshInstance3D CreateRoad(Vector3 start, Vector3 end, float width = 2.4f)
    {
        var delta = end - start;
        var length = new Vector2(delta.X, delta.Z).Length();
        var root = Box(new Vector3(width, 0.18f, length), new Color("#59656A"),
            (start + end) * 0.5f + new Vector3(0, 0.13f, 0));
        root.Name = "湾岸道路";
        root.Rotation = new Vector3(0, Mathf.Atan2(delta.X, delta.Z), 0);
        AddBox(root, new Vector3(0.22f, 0.25f, length), new Vector3(-width / 2 - 0.11f, 0, 0), Concrete);
        AddBox(root, new Vector3(0.22f, 0.25f, length), new Vector3(width / 2 + 0.11f, 0, 0), Concrete);
        for (var offset = -length / 2 + 2; offset < length / 2 - 1; offset += 6)
            AddBox(root, new Vector3(0.12f, 0.025f, Math.Min(2.5f, length / 2 - offset)),
                new Vector3(0, 0.105f, offset), new Color("#E7E4CF"));
        for (var offset = -length / 2 + 9; offset < length / 2 - 4; offset += 18)
            AddBox(root, new Vector3(width * 0.6f, 2.4f, 1.2f), new Vector3(0, -1.3f, offset), new Color("#8E9F9C"));
        return root;
    }

    /**
     * 機能: 岸壁付きの薄い埋立地メッシュを三角形分割して生成します。
     * 引数: polygonはVector2[]型のX/Z輪郭、colorはColor型の地表色。
     * 返り値: MeshInstance3D型の陸地。上面Y=0、底面Y=-2です。
     */
    public static MeshInstance3D CreateLand(Vector2[] polygon, Color color)
    {
        var indices = Geometry2D.TriangulatePolygon(polygon);
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var index in indices)
        {
            surface.SetNormal(Vector3.Up);
            surface.AddVertex(new Vector3(polygon[index].X, 0, polygon[index].Y));
        }
        var root = new MeshInstance3D { Mesh = surface.Commit(), MaterialOverride = Material(color), Name = "埋立地" };
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var edge = b - a;
            var midpoint = (a + b) / 2;
            var quay = Box(new Vector3(0.55f, 2.1f, edge.Length()), new Color("#87968B"),
                new Vector3(midpoint.X, -0.95f, midpoint.Y));
            quay.Rotation = new Vector3(0, Mathf.Atan2(edge.X, edge.Y), 0);
            root.AddChild(quay);
            var promenade = Box(new Vector3(1.5f, 0.12f, edge.Length()), new Color("#E3D7BA"),
                new Vector3(midpoint.X, 0.08f, midpoint.Y));
            promenade.Rotation = quay.Rotation;
            root.AddChild(promenade);
        }
        return root;
    }

    // ----------------------------------------

    /**
     * 機能: バルコニーと屋上設備を持つ淡色の集合住宅を組み立てます。
     * 引数: rootはNode3D型の親、variantはint型の形状番号。
     * 返り値: void型。
     */
    private static void BuildHousing(Node3D root, int variant)
    {
        var height = 9.5f + variant * 2.2f;
        var width = 6.5f + (variant % 2) * 1.2f;
        var depth = 6.8f;
        var colors = new[] { new Color("#DFDED2"), new Color("#DDD3BD"), new Color("#CDD7D3") };
        var shell = colors[variant % colors.Length];
        AddBox(root, new Vector3(10.2f, 0.45f, 10.2f), new Vector3(0, 0.25f, 0), new Color("#C8C9B6"));
        AddBox(root, new Vector3(width, height, depth), new Vector3(0, height / 2 + 0.5f, 0), shell);
        AddBox(root, new Vector3(width - 0.8f, height - 1.4f, depth + 0.08f), new Vector3(0, height / 2 + 0.3f, 0), Glass);
        for (var level = 1.2f; level < height; level += 1.8f)
            AddBox(root, new Vector3(width + 0.6f, 0.28f, depth + 0.65f), new Vector3(0, level + 0.6f, 0), shell);
        for (var side = -1; side <= 1; side += 2)
            AddBox(root, new Vector3(0.65f, height + 0.3f, depth + 0.4f), new Vector3(side * width / 2, height / 2 + 0.6f, 0), shell);
        AddBox(root, new Vector3(width + 0.4f, 0.5f, depth + 0.5f), new Vector3(0, height + 0.7f, 0), Pale);
        AddBox(root, new Vector3(2.3f, 1.1f, 2.8f), new Vector3(-0.7f, height + 1.5f, 0), Roof);
        AddBox(root, new Vector3(2, 1.6f, 0.2f), new Vector3(0, 1.1f, depth / 2 + 0.1f), DarkGlass);
        AddTree(root, new Vector3(-4.5f, 0.5f, 3.5f), 0.8f, variant);
    }

    /**
     * 機能: ガラス外壁と垂直フレームを持つ業務ビルを組み立てます。
     * 引数: rootはNode3D型の親、variantはint型の形状番号。
     * 返り値: void型。
     */
    private static void BuildOffice(Node3D root, int variant)
    {
        var height = 13f + variant * 3.0f;
        var width = 8.0f;
        var depth = 7.0f + variant % 2;
        var glazing = variant % 2 == 0 ? Glass : new Color("#648F99");
        AddBox(root, new Vector3(12, 0.45f, 12), new Vector3(0, 0.25f, 0), new Color("#C5C6B9"));
        AddBox(root, new Vector3(10.5f, 2.1f, 9.5f), new Vector3(0, 1.5f, 0), Concrete);
        AddBox(root, new Vector3(width, height, depth), new Vector3(0, height / 2 + 1.1f, 0), glazing);
        for (var level = 3f; level < height; level += 3.0f)
            AddBox(root, new Vector3(width + 0.2f, 0.18f, depth + 0.2f), new Vector3(0, level + 1.1f, 0), new Color("#9CB5B5"));
        for (var x = -width / 2; x <= width / 2; x += width / 2)
            AddBox(root, new Vector3(0.26f, height + 0.2f, depth + 0.2f), new Vector3(x, height / 2 + 1.1f, 0), Concrete);
        AddBox(root, new Vector3(width + 0.4f, 0.5f, depth + 0.4f), new Vector3(0, height + 1.35f, 0), Roof);
        AddBox(root, new Vector3(3.0f, 1.4f, 3.5f), new Vector3(0, height + 2.3f, 0), Concrete);
        AddBox(root, new Vector3(4.2f, 0.25f, 2.2f), new Vector3(0, 2.7f, depth / 2 + 1.7f), new Color("#C5DDD7"));
    }

    /**
     * 機能: 芝生、園路、複数の樹木を持つ水辺向けの公園を組み立てます。
     * 引数: rootはNode3D型の親、variantはint型の植栽番号。
     * 返り値: void型。
     */
    private static void BuildPark(Node3D root, int variant)
    {
        AddBox(root, new Vector3(13, 0.35f, 13), new Vector3(0, 0.25f, 0), new Color("#83AD76"));
        AddBox(root, new Vector3(1.6f, 0.08f, 13), new Vector3(0.6f, 0.47f, 0), Path);
        AddBox(root, new Vector3(13, 0.08f, 1.5f), new Vector3(0, 0.48f, 1), Path);
        var points = new[] { new Vector3(-4, 0.45f, -4), new Vector3(4, 0.45f, -3.5f),
            new Vector3(-4, 0.45f, 4.2f), new Vector3(3.6f, 0.45f, 4.3f), new Vector3(-2.2f, 0.45f, -0.8f) };
        for (var i = 0; i < points.Length; i++) AddTree(root, points[i], 0.9f + (variant + i) % 3 * 0.12f, variant + i);
        AddBox(root, new Vector3(2.0f, 0.3f, 0.65f), new Vector3(2.6f, 0.9f, 2.2f), new Color("#AF8559"));
    }

    /**
     * 機能: 公共施設の低層キャンパスと緑色の庇を組み立てます。
     * 引数: rootはNode3D型の親、variantはint型の配色番号。
     * 返り値: void型。
     */
    private static void BuildCivic(Node3D root, int variant)
    {
        AddBox(root, new Vector3(15, 0.4f, 14), new Vector3(0, 0.25f, 0), new Color("#C9CEBA"));
        AddBox(root, new Vector3(11, 4.6f, 8), new Vector3(0, 2.8f, -1), Pale);
        AddBox(root, new Vector3(10.5f, 1.4f, 8.15f), new Vector3(0, 3.3f, -1), Glass);
        AddBox(root, new Vector3(11.8f, 0.55f, 8.8f), new Vector3(0, 5.35f, -1), new Color("#68998E"));
        AddBox(root, new Vector3(5, 0.4f, 3), new Vector3(0, 2.9f, 4), new Color("#87B5A5"));
        AddBox(root, new Vector3(3.2f, 2.4f, 0.15f), new Vector3(0, 1.7f, 3.1f), DarkGlass);
        AddBox(root, new Vector3(2, 1, 1.6f), new Vector3(-2, 6.1f, -1), Roof);
        AddTree(root, new Vector3(-5.7f, 0.5f, 4.4f), 0.9f, variant);
        AddTree(root, new Vector3(5.7f, 0.5f, 4.4f), 0.9f, variant + 1);
    }

    // ----------------------------------------

    /**
     * 機能: 逆ピラミッドの展示・交流施設を独自の寸法で組み立てます。
     * 引数: rootはNode3D型の親。
     * 返り値: void型。
     */
    private static void BuildExhibition(Node3D root)
    {
        AddBox(root, new Vector3(30, 0.35f, 28), new Vector3(0, 0.3f, 0), new Color("#D5CCB6"));
        AddBox(root, new Vector3(21, 1.8f, 18), new Vector3(0, 1.4f, 1), Concrete);
        foreach (var x in new[] { -5.1f, 5.1f })
            foreach (var z in new[] { -4.6f, 4.6f })
            {
                AddBox(root, new Vector3(1.4f, 6.0f, 1.4f), new Vector3(x, 5.0f, z), new Color("#8DA4A7"));
                var inverted = new MeshInstance3D
                {
                    Mesh = Frustum(1.7f, 8.8f, 4.8f), MaterialOverride = Material(new Color("#B9CBD0")),
                    Position = new Vector3(x, 10.4f, z)
                };
                root.AddChild(inverted);
                AddBox(root, new Vector3(9.1f, 0.3f, 9.1f), new Vector3(x, 12.95f, z), Pale);
            }
        AddBox(root, new Vector3(11.7f, 0.55f, 2), new Vector3(0, 10.7f, 0), DarkGlass);
        AddBox(root, new Vector3(6, 0.4f, 7), new Vector3(0, 2.5f, 12), new Color("#88A9A5"));
    }

    /**
     * 機能: 二棟を球体展望室で結ぶ湾岸の記念建築を組み立てます。
     * 引数: rootはNode3D型の親。
     * 返り値: void型。
     */
    private static void BuildObservatory(Node3D root)
    {
        AddBox(root, new Vector3(25, 0.5f, 19), new Vector3(0, 0.4f, 0), new Color("#D1D3C3"));
        foreach (var x in new[] { -7.5f, 7.5f })
        {
            AddBox(root, new Vector3(6, 14, 7), new Vector3(x, 7.7f, 0), Concrete);
            AddBox(root, new Vector3(6.15f, 11.5f, 5), new Vector3(x, 7.8f, 0), Glass);
            for (var y = 3f; y < 14; y += 2.0f)
                AddBox(root, new Vector3(6.5f, 0.23f, 7.4f), new Vector3(x, y, 0), Pale);
        }
        AddBox(root, new Vector3(21, 1.0f, 3), new Vector3(0, 12, 0), new Color("#A6B9B8"));
        AddSphere(root, new Vector3(0, 14.2f, 0), 4.3f, new Color("#BCCBCA"));
        AddCylinder(root, new Vector3(0, 14.2f, 0), 4.36f, 0.75f, Glass);
        AddBox(root, new Vector3(20, 2.8f, 11), new Vector3(0, 2.1f, 0), Concrete);
    }

    /**
     * 機能: 連続した低い屋根を持つ市場と搬入棟を組み立てます。
     * 引数: rootはNode3D型の親。
     * 返り値: void型。
     */
    private static void BuildMarket(Node3D root)
    {
        AddBox(root, new Vector3(32, 0.4f, 24), new Vector3(0, 0.3f, 0), new Color("#BCC7B7"));
        AddBox(root, new Vector3(27, 3.5f, 18), new Vector3(0, 2.2f, 0), Pale);
        for (var x = -10f; x <= 10; x += 5)
        {
            AddBox(root, new Vector3(4.5f, 0.8f, 18.6f), new Vector3(x, 4.4f, 0), new Color("#8BA9A2"));
            AddBox(root, new Vector3(3.5f, 0.3f, 12), new Vector3(x, 4.98f, 0), new Color("#BBD4D0"));
            AddBox(root, new Vector3(3.0f, 1.8f, 0.2f), new Vector3(x, 1.7f, 9.1f), Glass);
        }
        AddBox(root, new Vector3(29, 0.35f, 3.6f), new Vector3(0, 3.0f, 10.5f), new Color("#4B8C85"));
        for (var x = -10; x <= 10; x += 10)
            AddBox(root, new Vector3(0.3f, 2.5f, 0.3f), new Vector3(x, 1.6f, 12), Concrete);
    }

    /**
     * 機能: 段状の外形と青緑の窓を持つ東雲の高層建築を組み立てます。
     * 引数: rootはNode3D型の親。
     * 返り値: void型。
     */
    private static void BuildTower(Node3D root)
    {
        AddBox(root, new Vector3(18, 0.5f, 18), new Vector3(0, 0.4f, 0), new Color("#D0D1C0"));
        AddBox(root, new Vector3(13, 4, 12), new Vector3(0, 2.5f, 0), Concrete);
        AddBox(root, new Vector3(9, 30, 9), new Vector3(0, 18, 0), new Color("#83AAA9"));
        for (var y = 5; y <= 32; y += 3)
            AddBox(root, new Vector3(9.6f, 0.3f, 9.6f), new Vector3(0, y, 0), Pale);
        foreach (var x in new[] { -4.3f, 4.3f })
            AddBox(root, new Vector3(0.65f, 30, 9.5f), new Vector3(x, 18, 0), Concrete);
        AddBox(root, new Vector3(6.5f, 6, 6.5f), new Vector3(0, 35, 0), Glass);
        AddBox(root, new Vector3(7, 0.6f, 7), new Vector3(0, 38.3f, 0), Concrete);
        AddCylinder(root, new Vector3(0, 40, 0), 0.15f, 3.5f, Roof);
    }

    // ----------------------------------------

    /**
     * 機能: 幹と低ポリの樹冠を持つ樹木を生成します。
     * 引数: rootはNode3D型の親、positionはVector3型の根元、scaleはfloat型の倍率、variantはint型の配色番号。
     * 返り値: void型。
     */
    private static void AddTree(Node3D root, Vector3 position, float scale, int variant)
    {
        AddCylinder(root, position + new Vector3(0, 1.1f * scale, 0), 0.18f * scale, 2.2f * scale, new Color("#957352"));
        var leaf = variant % 3 == 0 ? new Color("#5A8D6B") : variant % 3 == 1 ? Green : new Color("#8AAC6D");
        AddSphere(root, position + new Vector3(0, 2.8f * scale, 0), 1.5f * scale, leaf);
    }

    /**
     * 機能: 共有する箱メッシュを親ノードへ追加します。
     * 引数: rootはNode3D型の親、sizeとpositionはVector3型、colorはColor型。
     * 返り値: void型。
     */
    private static void AddBox(Node3D root, Vector3 size, Vector3 position, Color color) => root.AddChild(Box(size, color, position));

    /**
     * 機能: 共有メッシュと素材から箱ノードを生成します。
     * 引数: sizeはVector3型の寸法、colorはColor型、positionはVector3型の位置。
     * 返り値: MeshInstance3D型の箱。
     */
    private static MeshInstance3D Box(Vector3 size, Color color, Vector3 position)
    {
        var key = $"箱:{size}";
        if (!Meshes.TryGetValue(key, out var mesh)) Meshes[key] = mesh = new BoxMesh { Size = size };
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color), Position = position };
    }

    /**
     * 機能: 共有する低ポリ球を親ノードへ追加します。
     * 引数: rootはNode3D型の親、positionはVector3型の位置、radiusはfloat型の半径、colorはColor型。
     * 返り値: void型。
     */
    private static void AddSphere(Node3D root, Vector3 position, float radius, Color color)
    {
        var key = $"球:{radius}";
        if (!Meshes.TryGetValue(key, out var mesh)) Meshes[key] = mesh = new SphereMesh
            { Radius = radius, Height = radius * 2, RadialSegments = 10, Rings = 5 };
        root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color), Position = position });
    }

    /**
     * 機能: 共有する円柱を親ノードへ追加します。
     * 引数: rootはNode3D型の親、positionはVector3型の位置、radiusとheightはfloat型の寸法、colorはColor型。
     * 返り値: void型。
     */
    private static void AddCylinder(Node3D root, Vector3 position, float radius, float height, Color color)
    {
        var key = $"円柱:{radius}:{height}";
        if (!Meshes.TryGetValue(key, out var mesh)) Meshes[key] = mesh = new CylinderMesh
            { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = 8 };
        root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color), Position = position });
    }

    /**
     * 機能: 表裏が表示される粗い質感の素材を色ごとに共有します。
     * 引数: colorはColor型の基調色。
     * 返り値: StandardMaterial3D型の共有素材。
     */
    private static StandardMaterial3D Material(Color color)
    {
        var key = color.ToHtml();
        if (!Materials.TryGetValue(key, out var material))
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = color, Roughness = 0.82f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            Materials[key] = material;
        }
        return material;
    }

    /**
     * 機能: 正方形の上下寸法が異なる四角錐台を共有メッシュとして生成します。
     * 引数: bottomとtopはfloat型の底辺と上辺、heightはfloat型の高さ。
     * 返り値: Mesh型の四角錐台。
     */
    private static Mesh Frustum(float bottom, float top, float height)
    {
        var key = $"錐台:{bottom}:{top}:{height}";
        if (Meshes.TryGetValue(key, out var cached)) return cached;
        var b = bottom / 2;
        var t = top / 2;
        var h = height / 2;
        var vertices = new[]
        {
            new Vector3(-b, -h, -b), new Vector3(b, -h, -b), new Vector3(b, -h, b), new Vector3(-b, -h, b),
            new Vector3(-t, h, -t), new Vector3(t, h, -t), new Vector3(t, h, t), new Vector3(-t, h, t)
        };
        var faces = new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7, 4, 5, 6, 4, 6, 7, 0, 3, 2, 0, 2, 1 };
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < faces.Length; i += 3)
        {
            var a = vertices[faces[i]];
            var c = vertices[faces[i + 1]];
            var d = vertices[faces[i + 2]];
            var normal = (d - a).Cross(c - a).Normalized();
            for (var j = 0; j < 3; j++)
            {
                surface.SetNormal(normal);
                surface.AddVertex(vertices[faces[i + j]]);
            }
        }
        return Meshes[key] = surface.Commit();
    }
}
