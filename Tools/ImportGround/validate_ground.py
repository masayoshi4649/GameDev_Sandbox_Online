"""取得データの位置・標高・メッシュ整合性を検査する。"""
import json
import numpy as np
from PIL import Image
from shapely.geometry import Point, Polygon
from shapely.ops import unary_union

import import_ground as ground


def main() -> None:
    """
    代表地点、図形の妥当性、地形の索引、画像を検査する。
    引数: なし。生成済みの ground.json と地表アセットを読む。
    返り値: None。不整合時は例外を送出し、成功時は件数と代表標高を表示する。
    """
    data = json.loads(ground.OUTPUT.read_text(encoding="utf-8"))
    lands = [Polygon(record["points"], record["holes"]) for record in data["lands"]]
    assert all(polygon.is_valid for polygon in lands), "陸地に不正なポリゴンがあります。"
    land = unary_union(lands)
    places = [
        ("台場・フジテレビ付近", 139.774, 35.6267, True),
        ("有明・東京ビッグサイト付近", 139.7974, 35.6296, True),
        ("東雲・辰巳運河西岸", 139.8099, 35.6456, True),
        ("豊洲・豊洲駅付近", 139.7968, 35.6549, True),
        ("東京湾・有明南の海上", 139.8000, 35.6190, False),
        ("東京湾・台場北側の海上", 139.7800, 35.6350, False),
    ]
    for name, longitude, latitude, expected_land in places:
        x, _, z = ground.to_world(longitude, latitude)
        actual_land = land.covers(Point(x, z))
        assert actual_land == expected_land, f"代表地点の陸海が不一致: {name}"
        print(name, "陸地" if actual_land else "水域")

    # ----------------------------------------
    # 地表頂点・テクスチャ範囲・三角形索引の対応
    tiles = []
    for raster in data["rasters"]:
        asset = ground.ROOT / raster["path"].replace("res://", "")
        assert asset.exists(), f"写真がありません: {asset}"
        assert Image.open(asset).size == tuple(raster.get("imageSize", [256, 256]))
        if "atlasSha256" in raster:
            import hashlib
            assert hashlib.sha256(asset.read_bytes()).hexdigest() == raster["atlasSha256"]
        terrain_path = ground.ROOT / raster["terrainPath"].replace("res://", "")
        terrain = json.loads(terrain_path.read_text(encoding="utf-8"))
        vertices = np.asarray(terrain["terrainVertices"])
        indices = np.asarray(terrain["terrainIndices"])
        mesh_size = raster["meshSize"]
        assert vertices.shape == (mesh_size**2, 5)
        assert len(indices) == (mesh_size - 1)**2 * 6
        assert indices.min() == 0 and indices.max() == mesh_size**2 - 1
        assert np.isfinite(vertices).all()
        assert vertices[:, 3:5].min() == 0 and vertices[:, 3:5].max() == 1
        assert np.all((vertices[:, 1] > -100) & (vertices[:, 1] < 100))
        tiles.append((raster["tileX"], raster["tileY"], asset))
    elevation = json.loads((ground.ROOT / "Data/RealWorld/elevation.json").read_text(encoding="utf-8"))
    assert len(elevation["heights"]) == elevation["width"] * elevation["height"]
    assert np.isfinite(elevation["heights"]).all()

    # ----------------------------------------
    # 取得範囲全体の位置確認用コンタクトシート
    min_x, min_y = min(tile[0] for tile in tiles), min(tile[1] for tile in tiles)
    max_x, max_y = max(tile[0] for tile in tiles), max(tile[1] for tile in tiles)
    contact = Image.new("RGB", ((max_x - min_x + 1) * 128, (max_y - min_y + 1) * 128))
    for tx, ty, asset in tiles:
        contact.paste(Image.open(asset).resize((128, 128)), ((tx - min_x) * 128, (ty - min_y) * 128))
    output = ground.ROOT / "artifacts/real-world-ground-contact.jpg"
    output.parent.mkdir(parents=True, exist_ok=True)
    contact.save(output, quality=92)
    print(json.dumps({"陸域面積平方km": round(land.area * 100 / 1_000_000, 3),
                      "道路中心線km": round(sum(np.linalg.norm(np.diff(np.asarray(road["points"]), axis=0), axis=1).sum() for road in data["roads"]) * 10 / 1000, 3),
                      "写真数": len(tiles), "高さ配列最小Y": min(elevation["heights"]), "高さ配列最大Y": max(elevation["heights"]),
                      "確認画像": str(output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
