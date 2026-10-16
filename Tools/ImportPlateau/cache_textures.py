"""原典写真を描画用解像度へ事前変換し、初回起動の画像縮小処理を省きます。"""

import argparse
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")


def main():
    """
    機能: GLB内の画像を実行時と同じSHA256付きキャッシュへ変換します。
    引数: なし。コマンド行からcache-dir（str）とsize（int）を受け取ります。
    返り値: None。原典GLBと元の画像を変更しません。
    """
    parser = argparse.ArgumentParser(description="PLATEAU写真の描画キャッシュを準備します。")
    parser.add_argument("--cache-dir", type=Path, required=True)
    parser.add_argument("--size", type=int, default=2048)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    manifest = json.loads((root / "Data/RealWorld/plateau-manifest.json").read_text(encoding="utf-8"))
    output = args.cache_dir / f"v1-{args.size}"
    output.mkdir(parents=True, exist_ok=True)
    count = 0

    # ----------------------------------------

    for number, tile in enumerate(manifest["tiles"]):
        source = (root / tile["path"].removeprefix("res://")).read_bytes()
        sha = hashlib.sha256(source).hexdigest()
        if sha != tile["sha256"]:
            raise ValueError(f"原典のSHA256が不一致です: {tile['id']}")
        size = struct.unpack_from("<I", source, 12)[0]
        gltf = json.loads(source[20:20 + size])
        binary_offset = 28 + size
        for image_index, image in enumerate(gltf.get("images", [])):
            prefix = output / f"{sha}-{image_index}"
            if prefix.with_suffix(".jpg").exists() or prefix.with_suffix(".png").exists():
                continue
            view = gltf["bufferViews"][image["bufferView"]]
            begin = binary_offset + view.get("byteOffset", 0)
            content = source[begin:begin + view["byteLength"]]
            with Image.open(io.BytesIO(content)) as decoded:
                decoded.thumbnail((args.size, args.size), Image.Resampling.LANCZOS)
                alpha = "A" in decoded.getbands() and decoded.getchannel("A").getextrema()[0] != 255
                destination = prefix.with_suffix(".png" if alpha else ".jpg")
                temporary = destination.with_suffix(destination.suffix + ".tmp")
                if alpha:
                    decoded.save(temporary, "PNG")
                else:
                    decoded.convert("RGB").save(temporary, "JPEG", quality=96, subsampling=0)
                temporary.replace(destination)
                count += 1
        if (number + 1) % 32 == 0:
            print(f"写真キャッシュ {number + 1}/{len(manifest['tiles'])} タイル", flush=True)
    print(f"写真キャッシュ準備完了: 新規{count}枚、長辺上限{args.size}画素、{output}")


if __name__ == "__main__":
    main()
