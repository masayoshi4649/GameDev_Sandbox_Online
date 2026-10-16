"""国土地理院の実地形と国土交通省の写真・標高配信を範囲抽出する。"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import functools
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import time
import threading
import urllib.request
import urllib.error

import mapbox_vector_tile
import numpy as np
from PIL import Image
from pmtiles.reader import Reader
from shapely import make_valid, set_precision, transform_coordseq
from shapely.geometry import box, shape, Polygon
from shapely.ops import unary_union


# ----------------------------------------
# 取得範囲・出典・座標系
ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(__file__).parent / "cache"
OUTPUT = ROOT / "Data/RealWorld/ground.json"
ASSETS = ROOT / "Assets/RealWorld/Ground"
BOUNDS = {"west": 139.760, "east": 139.830, "south": 35.610, "north": 35.670}
ORIGIN = {"longitude": 139.795, "latitude": 35.640, "height": 0.0, "metersPerUnit": 10.0}
PMTILES_URL = "https://cyberjapandata.gsi.go.jp/xyz/optimal_bvmap-v1/optimal_bvmap-v1.pmtiles"
PHOTO_URL = "https://tile.plateauview.mlit.go.jp/tiles/plateau-ortho-2023/{z}/{x}/{y}.png"
GSI_PHOTO_URL = "https://cyberjapandata.gsi.go.jp/xyz/seamlessphoto/{z}/{x}/{y}.jpg"
DEM_URL = "https://tile.plateauview.mlit.go.jp/mapbox/{z}/{x}/{y}.png?heights=ellipsoidal"
VECTOR_ZOOM = 16
LAYOUT_ZOOM = 16
DEM_ZOOM = 15
PHOTO_PROGRESS = 0
PHOTO_PROGRESS_LOCK = threading.Lock()
WGS84_A = 6378137.0
WGS84_E2 = 6.6943799901413165e-3
ORIGIN_LON = math.radians(ORIGIN["longitude"])
ORIGIN_LAT = math.radians(ORIGIN["latitude"])


def geodetic_to_ecef(lon: object, lat: object, height: object = 0.0) -> tuple:
    """
    経緯度と楕円体高を WGS84 地心直交座標へ変換する。
    引数: lon/float|ndarray 経度、lat/float|ndarray 緯度、height/float|ndarray 楕円体高 m。
    返り値: tuple[float|ndarray, float|ndarray, float|ndarray] 地心座標 m。
    """
    lon = np.radians(lon)
    lat = np.radians(lat)
    normal = WGS84_A / np.sqrt(1.0 - WGS84_E2 * np.sin(lat) ** 2)
    return ((normal + height) * np.cos(lat) * np.cos(lon),
            (normal + height) * np.cos(lat) * np.sin(lon),
            (normal * (1.0 - WGS84_E2) + height) * np.sin(lat))


def transform(function, geometry):
    """
    図形の全頂点へ座標変換を適用する。
    引数: function/callable 座標変換、geometry/BaseGeometry 変換対象。
    返り値: BaseGeometry 変換済みの図形。
    """
    return transform_coordseq(geometry, function, interleaved=False)


ORIGIN_ECEF = geodetic_to_ecef(ORIGIN["longitude"], ORIGIN["latitude"])


def to_world(lon: object, lat: object, height: object = 37.0) -> tuple:
    """
    経緯度から原点の接平面座標へ変換する。
    引数: lon/float|ndarray 経度、lat/float|ndarray 緯度、height/float|ndarray 楕円体高 m。
    返り値: tuple[float|ndarray, float|ndarray, float|ndarray] X 東・Y 上・Z 南、1 単位 10 m。
    """
    x, y, z = geodetic_to_ecef(lon, lat, height)
    dx, dy, dz = x - ORIGIN_ECEF[0], y - ORIGIN_ECEF[1], z - ORIGIN_ECEF[2]
    east = -math.sin(ORIGIN_LON) * dx + math.cos(ORIGIN_LON) * dy
    north = (-math.sin(ORIGIN_LAT) * math.cos(ORIGIN_LON) * dx
             - math.sin(ORIGIN_LAT) * math.sin(ORIGIN_LON) * dy + math.cos(ORIGIN_LAT) * dz)
    up = (math.cos(ORIGIN_LAT) * math.cos(ORIGIN_LON) * dx
          + math.cos(ORIGIN_LAT) * math.sin(ORIGIN_LON) * dy + math.sin(ORIGIN_LAT) * dz)
    return east / 10.0, up / 10.0, -north / 10.0


def world_to_lonlat(x: object, z: object) -> tuple:
    """
    接平面の X/Z を標高参照用の経緯度に逆変換する。
    引数: x/ndarray 東方向座標、z/ndarray 南方向座標。仮の楕円体高 37 m を使用する。
    返り値: tuple[ndarray, ndarray] 経度と緯度。標高差による水平誤差は範囲内で cm 未満。
    """
    east, north, up = x * 10.0, -z * 10.0, 37.0
    ex = ORIGIN_ECEF[0] - math.sin(ORIGIN_LON) * east - math.sin(ORIGIN_LAT) * math.cos(ORIGIN_LON) * north + math.cos(ORIGIN_LAT) * math.cos(ORIGIN_LON) * up
    ey = ORIGIN_ECEF[1] + math.cos(ORIGIN_LON) * east - math.sin(ORIGIN_LAT) * math.sin(ORIGIN_LON) * north + math.cos(ORIGIN_LAT) * math.sin(ORIGIN_LON) * up
    ez = ORIGIN_ECEF[2] + math.cos(ORIGIN_LAT) * north + math.sin(ORIGIN_LAT) * up
    radial = np.sqrt(ex * ex + ey * ey)
    latitude = np.arctan2(ez, radial * (1.0 - WGS84_E2))
    for _ in range(5):
        normal = WGS84_A / np.sqrt(1.0 - WGS84_E2 * np.sin(latitude) ** 2)
        latitude = np.arctan2(ez + WGS84_E2 * normal * np.sin(latitude), radial)
    return np.degrees(np.arctan2(ey, ex)), np.degrees(latitude)


def lonlat_tile(lon: object, lat: object, zoom: int) -> tuple:
    """
    経緯度を XYZ タイルの連続座標へ変換する。
    引数: lon/float|ndarray 経度、lat/float|ndarray 緯度、zoom/int ズーム。
    返り値: tuple[float|ndarray, float|ndarray] タイル座標。
    """
    return ((np.asarray(lon) + 180.0) / 360.0 * 2**zoom,
            (1.0 - np.arcsinh(np.tan(np.radians(lat))) / math.pi) / 2.0 * 2**zoom)


def tile_lonlat(x: object, y: object, zoom: int) -> tuple:
    """
    XYZ タイルの連続座標を経緯度へ変換する。
    引数: x/float|ndarray、y/float|ndarray タイル座標、zoom/int ズーム。
    返り値: tuple[float|ndarray, float|ndarray] 経度と緯度。
    """
    return (np.asarray(x) / 2**zoom * 360.0 - 180.0,
            np.degrees(np.arctan(np.sinh(math.pi * (1.0 - 2.0 * np.asarray(y) / 2**zoom)))))


def tile_range(zoom: int, margin: int = 0) -> list:
    """
    範囲に交差する XYZ タイル番号を列挙する。
    引数: zoom/int ズーム、margin/int 外周の追加タイル数。
    返り値: list[tuple[int, int, int]] ズーム・X・Y。
    """
    west, north = lonlat_tile(BOUNDS["west"], BOUNDS["north"], zoom)
    east, south = lonlat_tile(BOUNDS["east"], BOUNDS["south"], zoom)
    return [(zoom, x, y) for x in range(int(west) - margin, int(east) + margin + 1)
            for y in range(int(north) - margin, int(south) + margin + 1)]


# ----------------------------------------
# 配信の範囲取得と再実行用のローカルキャッシュ
def fetch(url: str, cache_path: Path, headers: dict | None = None) -> bytes:
    """
    公開データを取得し、同一 URL の再取得を避ける。
    引数: url/str 公開 URL、cache_path/Path 保存先、headers/dict|None HTTP ヘッダー。
    返り値: bytes 応答内容。取得に失敗した場合は例外を送出する。
    """
    if cache_path.exists():
        return cache_path.read_bytes()
    missing_path = cache_path.with_suffix(cache_path.suffix + ".404")
    if missing_path.exists():
        raise urllib.error.HTTPError(url, 404, "未提供の応答をキャッシュから再利用", None, None)
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(4):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "BayfrontSandbox-data-import/1.0", **(headers or {})})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = response.read()
            temporary_cache = cache_path.with_name(f"{cache_path.name}.{threading.get_ident()}.tmp")
            temporary_cache.write_bytes(data)
            temporary_cache.replace(cache_path)
            return data
        except Exception as exception:
            if isinstance(exception, urllib.error.HTTPError) and exception.code == 404:
                missing_path.write_text(url, encoding="utf-8")
                raise
            if attempt == 3:
                raise
            time.sleep(1.0 + attempt)
    raise RuntimeError("データ取得を完了できませんでした。")


@functools.lru_cache(maxsize=1024)
def range_bytes(offset: int, length: int) -> bytes:
    """
    日本全国の巨大ファイルから必要なバイト範囲だけを読む。
    引数: offset/int 開始位置、length/int バイト数。
    返り値: bytes 国土地理院 PMTiles の要求範囲。
    """
    data = fetch(PMTILES_URL, CACHE / "ranges" / f"{offset}-{length}.bin",
                 {"Range": f"bytes={offset}-{offset + length - 1}"})
    if len(data) != length:
        raise ValueError("PMTiles の HTTP Range 応答が要求範囲と一致しません。")
    return data


def fetch_vector_tile(tile: tuple) -> tuple:
    """
    国土地理院の最適化ベクトルタイルを復号する。
    引数: tile/tuple[int, int, int] ズーム・X・Y。
    返り値: tuple[tuple, dict] タイル番号と MVT レイヤー。
    """
    data = Reader(range_bytes).get(*tile)
    if data is None:
        return tile, {}
    if data[:2] == b"\x1f\x8b":
        data = gzip.decompress(data)
    return tile, mapbox_vector_tile.decode(data, default_options={"y_coord_down": True})


def geometry_parts(geometry: object, kind: str) -> list:
    """
    複合ジオメトリから目的の図形だけを抽出する。
    引数: geometry/BaseGeometry 図形、kind/str Polygon または LineString。
    返り値: list[BaseGeometry] 空でない目的図形。
    """
    if geometry.is_empty:
        return []
    if geometry.geom_type == kind:
        return [geometry]
    return [part for child in getattr(geometry, "geoms", []) for part in geometry_parts(child, kind)]


def polygon_record(polygon: Polygon) -> dict:
    """
    陸域・水域ポリゴンを読み込み用の形式へ変換する。
    引数: polygon/Polygon 図形。
    返り値: dict 外周 points と内周 holes。座標単位は 10 m。
    """
    return {"points": [[round(x, 5), round(z, 5)] for x, z in polygon.exterior.coords[:-1]],
            "holes": [[[round(x, 5), round(z, 5)] for x, z in ring.coords[:-1]] for ring in polygon.interiors]}


def width_reference(properties: dict) -> float:
    """
    道路の幅員区分から判定用の参考幅員を返す。
    引数: properties/dict 国土地理院の属性。
    返り値: float 幅員 m。実測幅員ではなく区分の代表値。
    """
    rank = properties.get("vt_rnkwidth", "")
    if rank.startswith("19.5"):
        return 24.0
    if rank.startswith("13"):
        return 16.0
    if rank.startswith("5.5"):
        return 9.0
    if rank.startswith("3m-"):
        return 4.25
    if "3m" in rank:
        return 2.5
    return 6.0


def extract_vectors(workers: int) -> dict:
    """
    実水域の面形状と実道路中心線・海岸線を範囲抽出する。
    引数: workers/int 並列取得数。
    返り値: dict 陸域・水域・道路・海岸線と元データ件数。
    """
    region_geo = box(BOUNDS["west"], BOUNDS["south"], BOUNDS["east"], BOUNDS["north"])
    waters, roads, coastlines = [], [], []
    counts = {}
    tiles = tile_range(VECTOR_ZOOM)
    with ThreadPoolExecutor(max_workers=workers) as executor:
        for index, (tile, layers) in enumerate(executor.map(fetch_vector_tile, tiles)):
            zoom, tx, ty = tile
            lon0, lat1 = tile_lonlat(tx, ty, zoom)
            lon1, lat0 = tile_lonlat(tx + 1, ty + 1, zoom)
            clip = region_geo.intersection(box(lon0, lat0, lon1, lat1))
            for name in ("WA", "RdCL", "Cstline"):
                layer = layers.get(name, {})
                extent = layer.get("extent", 4096)
                for feature in layer.get("features", []):
                    counts[name] = counts.get(name, 0) + 1
                    geometry = shape(feature["geometry"])
                    def tile_to_geo(x: object, y: object, z: object = None) -> tuple:
                        """
                        タイル内部の座標を経緯度へ変換する。
                        引数: x/tuple|float、y/tuple|float 内部座標、z/None 未使用。
                        返り値: tuple 経度と緯度。
                        """
                        return tile_lonlat(tx + np.asarray(x) / extent, ty + np.asarray(y) / extent, zoom)
                    geometry = make_valid(transform(tile_to_geo, geometry)).intersection(clip)
                    def geo_to_world(lon: object, lat: object, z: object = None) -> tuple:
                        """
                        地図の経緯度を建設判定用の X/Z へ変換する。
                        引数: lon/tuple|float 経度、lat/tuple|float 緯度、z/None 未使用。
                        返り値: tuple 東方向と南方向の座標。
                        """
                        x, _, wz = to_world(lon, lat)
                        return x, wz
                    geometry = transform(geo_to_world, geometry)
                    if name == "WA":
                        waters.extend(geometry_parts(geometry, "Polygon"))
                    else:
                        for part in geometry_parts(geometry, "LineString"):
                            if part.length < 0.01:
                                continue
                            record = {"points": [[round(x, 5), round(z, 5)] for x, z in part.coords],
                                      "sourceCode": feature["properties"].get("vt_code", 0)}
                            if name == "RdCL":
                                properties = feature["properties"]
                                record.update({"widthMeters": width_reference(properties), "widthIsApproximate": True,
                                               "widthRank": properties.get("vt_rnkwidth", "不明"),
                                               "sourceWidth": properties.get("vt_width", 0),
                                               "category": properties.get("vt_rdctg", "不明"),
                                               "level": properties.get("vt_lvorder", 0),
                                               "motorway": properties.get("vt_motorway", 0) == 1})
                                roads.append(record)
                            else:
                                coastlines.append(record)
            if index % 20 == 0 or index + 1 == len(tiles):
                print(f"ベクトル取得 {index + 1}/{len(tiles)}", flush=True)
    water = set_precision(make_valid(unary_union(waters)), 0.00001)
    def region_to_world(lon: object, lat: object, z: object = None) -> tuple:
        """
        対象領域の頂点を接平面の平面座標へ写す。
        引数: lon/tuple|float、lat/tuple|float 経緯度、z/None 未使用。
        返り値: tuple X/Z。
        """
        x, _, wz = to_world(lon, lat)
        return x, wz
    region_world = transform(region_to_world, region_geo)
    land = set_precision(make_valid(region_world.difference(water)), 0.00001)
    lands = [polygon_record(part.simplify(0.005, preserve_topology=True)) for part in geometry_parts(land, "Polygon") if part.area > 0.01]
    return {"lands": lands, "waters": [polygon_record(part) for part in geometry_parts(water, "Polygon")],
            "roads": roads, "coastlines": coastlines, "vectorFeatureCounts": counts,
            "vectorTiles": len(tiles), "boundsWorld": {"x": region_world.bounds[0], "z": region_world.bounds[1],
                                                         "width": region_world.bounds[2] - region_world.bounds[0],
                                                         "height": region_world.bounds[3] - region_world.bounds[1]}}


# ----------------------------------------
# 楕円体高を持つ DEM と航空写真地表
def fetch_dem_tile(tile: tuple) -> tuple:
    """
    標高画像をデコードし、楕円体高へ戻す。
    引数: tile/tuple[int, int, int] ズーム・X・Y。
    返り値: tuple[tuple, ndarray] タイル番号と高さ m の 256 四方配列。
    """
    zoom, x, y = tile
    data = fetch(DEM_URL.format(z=zoom, x=x, y=y), CACHE / "dem" / f"{zoom}-{x}-{y}.png")
    rgba = np.asarray(Image.open(io.BytesIO(data)).convert("RGBA"), dtype=np.int32)
    height = -10000.0 + (rgba[:, :, 0] * 65536 + rgba[:, :, 1] * 256 + rgba[:, :, 2]) * 0.1
    if not np.all(rgba[:, :, 3] == 255):
        raise ValueError(f"標高欠損があるため未測定箇所を作成しません: {tile}")
    return tile, height


def dem_sampler(workers: int):
    """
    対象領域の標高画像をモザイクし、補間関数を作成する。
    引数: workers/int 並列取得数。
    返り値: callable 経緯度配列から楕円体高 m を返す双一次補間関数。
    """
    tiles = tile_range(DEM_ZOOM, margin=1)
    min_x, max_x = min(t[1] for t in tiles), max(t[1] for t in tiles)
    min_y, max_y = min(t[2] for t in tiles), max(t[2] for t in tiles)
    mosaic = np.empty(((max_y - min_y + 1) * 256, (max_x - min_x + 1) * 256), dtype=np.float32)
    with ThreadPoolExecutor(max_workers=workers) as executor:
        for tile, height in executor.map(fetch_dem_tile, tiles):
            _, x, y = tile
            mosaic[(y - min_y) * 256:(y - min_y + 1) * 256,
                   (x - min_x) * 256:(x - min_x + 1) * 256] = height
    print(f"楕円体高 DEM {len(tiles)} 枚、範囲 {mosaic.min():.1f}〜{mosaic.max():.1f} m", flush=True)
    def sample(lon: object, lat: object) -> np.ndarray:
        """
        元標高画素の中心を基準として双一次補間する。
        引数: lon/float|ndarray 経度、lat/float|ndarray 緯度。
        返り値: ndarray 楕円体高 m。
        """
        tx, ty = lonlat_tile(lon, lat, DEM_ZOOM)
        px = np.clip((tx - min_x) * 256 - 0.5, 0, mosaic.shape[1] - 1.00001)
        py = np.clip((ty - min_y) * 256 - 0.5, 0, mosaic.shape[0] - 1.00001)
        x0, y0 = px.astype(np.int32), py.astype(np.int32)
        fx, fy = px - x0, py - y0
        return ((1 - fy) * ((1 - fx) * mosaic[y0, x0] + fx * mosaic[y0, x0 + 1])
                + fy * ((1 - fx) * mosaic[y0 + 1, x0] + fx * mosaic[y0 + 1, x0 + 1]))
    return sample


def fetch_photo_tile(zoom: int, tx: int, ty: int) -> tuple:
    """
    指定した詳細度の実写真を取得し、未整備部分を同じ詳細度の国土地理院写真で補う。
    引数: zoom/int 元画像のズーム、tx/int、ty/int タイル番号。
    返り値: tuple[Image, dict] 実画像と元 URL・原本 SHA-256・補完元。
    """
    url = PHOTO_URL.format(z=zoom, x=tx, y=ty)
    fallback_url = GSI_PHOTO_URL.format(z=zoom, x=tx, y=ty)
    source_notes = []
    try:
        data = fetch(url, CACHE / "ortho" / f"{zoom}-{tx}-{ty}.png")
        photo = Image.open(io.BytesIO(data)).convert("RGBA")
        if photo.getextrema()[3][0] < 255:
            base = fetch(fallback_url, CACHE / "gsi-photo" / f"{zoom}-{tx}-{ty}.jpg")
            photo = Image.alpha_composite(Image.open(io.BytesIO(base)).convert("RGBA"), photo)
            source_notes.append({"sourceUrl": fallback_url, "sourceSha256": hashlib.sha256(base).hexdigest(), "processing": "PLATEAU 写真の透明部分に同じズームの全国最新写真を合成"})
    except urllib.error.HTTPError as exception:
        if exception.code != 404:
            raise
        url = fallback_url
        data = fetch(url, CACHE / "gsi-photo" / f"{zoom}-{tx}-{ty}.jpg")
        photo = Image.open(io.BytesIO(data)).convert("RGBA")
    if photo.size != (256, 256) or photo.getextrema()[3][0] != 255:
        raise ValueError(f"元写真の寸法または被覆範囲が不正です: {zoom}/{tx}/{ty}")
    return photo, {"sourceUrl": url, "sourceSha256": hashlib.sha256(data).hexdigest(),
                   "zoom": zoom, "tileX": tx, "tileY": ty, "additionalSources": source_notes}


def build_photo_atlas(tile: tuple, source_zoom: int) -> tuple:
    """
    配置用タイル内の実写真を結合し、描画タイル数を増やさず解像度を上げる。
    引数: tile/tuple 配置用 z16 タイル、source_zoom/int 写真原典の取得ズーム。
    返り値: tuple[Image, list] 写真アトラスと子タイルごとの実際の出典・解像度。
    """
    global PHOTO_PROGRESS
    zoom, tx, ty = tile
    factor = 2 ** (source_zoom - zoom)
    atlas = Image.new("RGB", (256 * factor, 256 * factor))
    sources = []
    for row in range(factor):
        for col in range(factor):
            child_x, child_y = tx * factor + col, ty * factor + row
            try:
                photo, source = fetch_photo_tile(source_zoom, child_x, child_y)
            except urllib.error.HTTPError as exception:
                if exception.code != 404 or source_zoom == zoom:
                    raise
                parent_photo, parent_source = fetch_photo_tile(zoom, tx, ty)
                box_size = 256 // factor
                photo = parent_photo.crop((col * box_size, row * box_size, (col + 1) * box_size, (row + 1) * box_size)).resize((256, 256), Image.Resampling.LANCZOS)
                source = {**parent_source, "requestedZoom": source_zoom, "requestedTileX": child_x, "requestedTileY": child_y,
                          "isLowerResolutionFallback": True,
                          "processing": "PLATEAU と国土地理院の高解像写真が共に未提供のため、当該領域のみ明示的に元 z16 写真を使用。高解像データとしては扱わない。"}
            source.update({"atlasColumn": col, "atlasRow": row,
                           "pixelSpacingMetersAtOrigin": round(math.cos(ORIGIN_LAT) * 2 * math.pi * WGS84_A / 2**source["zoom"] / 256, 4)})
            sources.append(source)
            atlas.paste(photo.convert("RGB"), (col * 256, row * 256))
            with PHOTO_PROGRESS_LOCK:
                PHOTO_PROGRESS += 1
                if PHOTO_PROGRESS % 128 == 0:
                    print(f"元写真取得・結合 {PHOTO_PROGRESS} 枚", flush=True)
    return atlas, sources


def build_raster(tile: tuple, sample, mesh_size: int, photo_zoom: int) -> dict:
    """
    実航空写真と実標高を同じ接平面の地表メッシュへ変換する。
    引数: tile/tuple 配置用タイル、sample/callable 楕円体高補間、mesh_size/int 一辺の頂点数、photo_zoom/int 元写真のズーム。
    返り値: dict 写真出典・座標・頂点・索引。原写真は再取得用キャッシュへ保存する。
    """
    zoom, tx, ty = tile
    photo, source_tiles = build_photo_atlas(tile, photo_zoom)
    name = f"ortho-{zoom}-{tx}-{ty}.jpg"
    photo_path = ASSETS / name
    photo.convert("RGB").save(photo_path, quality=94, subsampling=0)
    uv = np.linspace(0, 1, mesh_size)
    u, v = np.meshgrid(uv, uv)
    lon, lat = tile_lonlat(tx + u, ty + v, zoom)
    elevation = sample(lon, lat)
    x, y, z = to_world(lon, lat, elevation)
    vertices = np.column_stack((x.ravel(), y.ravel(), z.ravel(), u.ravel(), v.ravel())).round(6).tolist()
    indices = []
    for row in range(mesh_size - 1):
        for col in range(mesh_size - 1):
            a = row * mesh_size + col
            indices.extend((a, a + 1, a + mesh_size, a + 1, a + mesh_size + 1, a + mesh_size))
    west, north = tile_lonlat(tx, ty, zoom)
    east, south = tile_lonlat(tx + 1, ty + 1, zoom)
    terrain_name = f"terrain-{zoom}-{tx}-{ty}.json"
    (ASSETS / terrain_name).write_text(json.dumps({"terrainVertices": vertices, "terrainIndices": indices}, separators=(",", ":")), encoding="utf-8")
    return {"path": f"res://Assets/RealWorld/Ground/{name}", "sourceUrl": PHOTO_URL,
            "sourceTiles": source_tiles, "atlasSha256": hashlib.sha256(photo_path.read_bytes()).hexdigest(),
            "photoSourceZoom": photo_zoom, "imageSize": list(photo.size), "zoom": zoom, "tileX": tx, "tileY": ty,
            "west": float(west), "east": float(east), "south": float(south), "north": float(north),
            "worldrect": {"x": float(x.min()), "z": float(z.min()), "width": float(x.max() - x.min()), "height": float(z.max() - z.min())},
            "meshSize": mesh_size, "terrainPath": f"res://Assets/RealWorld/Ground/{terrain_name}"}


def build_elevation_grid(bounds: dict, sample, size: int = 513) -> dict:
    """
    描画データなしでサーバーも読める均一接平面の高さ配列を作る。
    引数: bounds/dict 接平面範囲、sample/callable 元標高補間、size/int 一辺の標本数。
    返り値: dict 幅・高さ・接平面範囲と行優先 Y 配列。Y はゲーム内の座標単位。
    """
    xs = np.linspace(bounds["x"], bounds["x"] + bounds["width"], size)
    zs = np.linspace(bounds["z"], bounds["z"] + bounds["height"], size)
    x, z = np.meshgrid(xs, zs)
    lon, lat = world_to_lonlat(x, z)
    elevation = sample(lon, lat)
    _, y, _ = to_world(lon, lat, elevation)
    return {"width": size, "height": size, "boundsWorld": bounds,
            "minX": bounds["x"], "minZ": bounds["z"], "stepX": bounds["width"] / (size - 1), "stepZ": bounds["height"] / (size - 1),
            "heights": np.round(y.ravel(), 5).tolist(), "order": "行優先、X が先に増加、Z が後に増加",
            "heightUnits": "ゲーム座標、1 単位 = 10 m、WGS84 原点 ENU の Up"}


def main() -> None:
    """
    実配信データの取得・変換・検査・保存を順に実行する。
    引数: なし。コマンドラインから並列数と地表の頂点数を読み取る。
    返り値: None。欠損・不正図形・取得失敗があれば失敗終了する。
    """
    parser = argparse.ArgumentParser(description="東京湾岸の国の実データを取得します。")
    parser.add_argument("--workers", type=int, default=6, choices=range(1, 7))
    parser.add_argument("--mesh-size", type=int, default=65, choices=(17, 33, 65))
    parser.add_argument("--photo-zoom", type=int, default=18, choices=(16, 17, 18))
    parser.add_argument("--rasters-only", action="store_true", help="陸海・道路・CPU 高さを再計算せず、写真と描画地表のみ更新する。")
    args = parser.parse_args()
    ASSETS.mkdir(parents=True, exist_ok=True)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    existing = json.loads(OUTPUT.read_text(encoding="utf-8")) if args.rasters_only else None
    vector_keys = ("lands", "waters", "roads", "coastlines", "vectorFeatureCounts", "vectorTiles", "boundsWorld")
    vectors = {key: existing[key] for key in vector_keys} if existing else extract_vectors(args.workers)
    sample = dem_sampler(args.workers)
    rasters = []
    tiles = tile_range(LAYOUT_ZOOM)
    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        def raster_worker(tile: tuple) -> dict:
            """
            指定タイルの写真地表を並列生成する。
            引数: tile/tuple タイル番号。
            返り値: dict 地表の写真と実標高メッシュ。
            """
            return build_raster(tile, sample, args.mesh_size, args.photo_zoom)
        for index, raster in enumerate(executor.map(raster_worker, tiles)):
            rasters.append(raster)
            if index % 20 == 0 or index + 1 == len(tiles):
                print(f"写真地表取得 {index + 1}/{len(tiles)}", flush=True)
    cpu_grid_path = OUTPUT.parent / "elevation.json"
    if not args.rasters_only:
        grid = build_elevation_grid(vectors["boundsWorld"], sample)
        cpu_grid_path.write_text(json.dumps(grid, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    metadata = {
        "schemaVersion": 1, "generatedAtUtc": datetime.now(timezone.utc).isoformat(), "origin": ORIGIN, "bounds": BOUNDS,
        "coordinateSystem": "WGS84 経緯度・楕円体高 → ECEF → 原点 ENU、X=East/10, Y=Up/10, Z=-North/10",
        "horizontalReferenceHeightMeters": 37.0,
        "attribution": "国土地理院最適化ベクトルタイル、国土交通省 Project PLATEAU、Mapterhorn、国土地理院を加工して作成",
        "sources": [
            {"name": "国土地理院最適化ベクトルタイル", "url": PMTILES_URL, "dataDate": "2026-07-01",
             "licenseUrl": "https://www.gsi.go.jp/kikakuchousei/kikakuchousei40182.html",
             "documentation": "https://github.com/gsi-cyberjapan/optimal_bvmap",
             "processing": "対象範囲切出し、水域ポリゴン差分による陸地抽出、ENU 座標変換"},
            {"name": "国土交通省 Project PLATEAU オルソ写真 2023", "url": PHOTO_URL,
             "licenseUrl": "https://www.mlit.go.jp/plateau/site-policy/",
             "documentation": "https://docs.plateauview.mlit.go.jp/datasets/ortho/",
             "processing": "対象範囲切出し、PNG から JPEG へ圧縮、実標高地表へ投影"},
            {"name": "国土地理院 全国最新写真（シームレス）", "url": GSI_PHOTO_URL,
             "licenseUrl": "https://maps.gsi.go.jp/help/use.html",
             "documentation": "https://maps.gsi.go.jp/development/ichiran.html",
             "processing": "PLATEAU 写真の未整備・透明部分を対象範囲の国土地理院写真で補完、JPEG 化",
             "usage": "地理院タイル一覧の『基本測量成果以外で出典の記載のみで利用可能』の写真に該当"},
            {"name": "PLATEAU 楕円体高 Terrain-RGB", "url": DEM_URL,
             "tileJsonUrl": "https://tile.plateauview.mlit.go.jp/mapbox/tilejson.json?format=png",
             "catalogUrl": "https://tile.plateauview.mlit.go.jp/tiles/catalog.json",
             "licenseUrl": "https://www.mlit.go.jp/plateau/site-policy/",
             "attribution": "PLATEAU | Mapterhorn | 国土地理院",
             "upstreamAttributionUrl": "https://download.mapterhorn.com/attribution.json",
             "upstreamJapaneseDataLicense": "国土地理院コンテンツ利用規約／測量法に基づく国土地理院長承認（使用）R 8JHs 131（Mapterhorn が公開する日本 DEM 原典の承認表示）",
             "documentation": "https://docs.plateauview.mlit.go.jp/datasets/terrain/",
             "processing": "z15 の元標高画像を双一次補間し楕円体高のまま ECEF→ENU へ変換",
             "caveat": "公式説明には raster-dem 未公開の記載が残るが、取得日時の公式配信カタログと TileJSON に公開されていた現行 URL を使用。元 DEM の整備時期・標高精度は地物・地域により異なる。"}],
        "elevation": {"path": "res://Data/RealWorld/elevation.json", "datum": "WGS84 楕円体高由来の原点 ENU Up",
                      "sourceZoom": DEM_ZOOM, "sourcePixelSpacingMeters": round(math.cos(ORIGIN_LAT) * 2 * math.pi * WGS84_A / 2**DEM_ZOOM / 256, 3),
                      "encoding": "Mapbox Terrain-RGB: -10000 + (R*65536+G*256+B)*0.1 m",
                      "rasterMeshSize": args.mesh_size, "cpuGridSize": [513, 513]},
        "imagery": {"sourceZoom": args.photo_zoom, "layoutZoom": LAYOUT_ZOOM,
                    "atlasImageSize": [256 * 2**(args.photo_zoom - LAYOUT_ZOOM)] * 2,
                    "pixelSpacingMetersAtOrigin": round(math.cos(ORIGIN_LAT) * 2 * math.pi * WGS84_A / 2**args.photo_zoom / 256, 4),
                    "sourceTileCount": sum(len(raster["sourceTiles"]) for raster in rasters),
                    "lowerResolutionFallbackCount": sum(source.get("isLowerResolutionFallback", False) for raster in rasters for source in raster["sourceTiles"])},
        **vectors, "rasters": rasters,
        "limitations": ["道路中心線の参考幅員は幅員区分の代表値であり実測幅員ではない。元属性を併記。",
                        "データ整備年度の異なる建物・地表写真・標高・海岸線を重ねるため局所的な位置差がある。",
                        "土地の造成、建物の内部、交通シミュレーション、地下構造は元データから生成していない。"]}
    temporary_output = OUTPUT.with_suffix(".json.tmp")
    temporary_output.write_text(json.dumps(metadata, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    temporary_output.replace(OUTPUT)
    print(json.dumps({"陸地": len(vectors["lands"]), "道路": len(vectors["roads"]), "海岸線": len(vectors["coastlines"]),
                      "写真": len(rasters), "地表頂点": len(rasters) * args.mesh_size**2,
                      "地表JSONバイト": OUTPUT.stat().st_size, "標高JSONバイト": cpu_grid_path.stat().st_size}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
