# PLATEAU実建築物の取込

2025年度に公開された東京都の3D都市モデルを使用する。架空の建物への置換、フットプリントの矩形化、元の屋根形状の簡略化は行わない。台場・有明・東雲・豊洲と、隣接する晴海・勝どき・佃・天王洲付近を含む。

これは原典の取り込みと未変更時の描画の説明である。ゲームでは原典の建物も移動・回転・高さ変更・階数変更・輪郭編集・撤去ができ、形状変更後はその建物の元三角形だけを除いて入力した形状へ置き換える。未変更の建物は読み込み済みの原典三角形で壁と屋根をマウス選択し、CPU用の凸包だけを選択範囲にしない。操作と保存は [目的交通と編集操作](目的交通と編集操作.md) を参照。

| 項目 | 取込結果 |
| --- | --- |
| 取得範囲 | 東経139.760〜139.830度、北緯35.610〜35.670度 |
| 原典 | PLATEAU 2025年度、中央区・港区・江東区・品川区の建築物 |
| 実建築物 | 18,318棟 |
| 詳細度 | LOD2 9,229棟、原典に含まれるLOD1 9,089棟 |
| 描画用タイル | 216個 |
| 三角形 | 1,274,403枚 |
| GLB合計 | 約382 MiB |
| 元の写真テクスチャを含むタイル | 170個 |

集計の正本は `Data/RealWorld/plateau-manifest.json` の `statistics`。各棟の緯度経度境界が対象範囲と交差する場合、その建物全体を取り込む。対象範囲に交差する大きなタイルの外側建物は取り除く。

## 再取得

Node.js 22以降とnpmを使用する。ゲーム本体の.NETビルドとは独立した取込処理。

```powershell
.\Tools\ImportPlateau\import.ps1
```

Linuxでも同じ処理を実行できる。

```sh
cd Tools/ImportPlateau
npm ci --ignore-scripts
node import.mjs
```

初回は公式HTTP配信から必要なタイルだけ取得する。2回目以降は `Tools/ImportPlateau/cache/` のURLハッシュキャッシュを再利用する。キャッシュと `node_modules` はGit管理から除外する。出力GLBは再生成可能な大容量アセットとして管理する。

生成物は次のとおり。

- `Assets/RealWorld/Plateau/*.glb`：座標変換済みの標準glTF 2.0メッシュと元の写真テクスチャ。
- `Data/RealWorld/plateau-manifest.json`：原典URL、詳細度、変換条件、タイル境界、SHA-256、詳細件数。
- `Data/RealWorld/buildings.json`：元の地物ID、名前、用途、実測高さ、CPU判定用の水平凸包と3D境界。

## 詳細度と密度

公式LOD2配信には、詳細形状が整備されていない建物のLOD1も併録されている。元バッチ属性 `_lod` を読み、棟ごとの実際の詳細度を記録する。「LOD2」という配信名だけで全棟をLOD2と見なさない。

別のLOD1配信も取得し、対象範囲の全地物IDを照合する。不足するIDだけLOD1で補完する。今回の4区ではLOD2配信に対象LOD1が収録済みのため、追加不足は0棟だった。`statistics.datasetAudits` には、交差タイルに含まれる全地物数、対象範囲に交差する地物数、上位詳細度ですでに取り込んだ地物数を別々に記録する。

LOD1は原典に含まれる実際の輪郭と高さを持つ。LOD2は原典の屋根・壁面形状と、公開されている場合は元の写真テクスチャを維持する。ゲーム用に作られた内装、自由な建物分解、すべての窓や設備の形状までを持つデータではない。

## 座標と標高

WGS84楕円体を使用し、原点は東経139.795度、北緯35.640度、楕円体高0m。ゲームの1単位は10mで、X軸が東、Y軸が上、Z軸が南。

公式b3dmはDraco圧縮glTFと `CESIUM_RTC` を含む。変換は次の順序で行う。

1. 公式Dracoデコーダー `draco3dgltf 1.5.8` で実頂点・実三角形・法線・UV・地物IDを展開する。
2. glTFのY-up頂点を `[x, -z, y]` として3D TilesのZ-upへ回転する。
3. `CESIUM_RTC.center` を加算する。3D Tilesの親子 `transform` も合成して適用する。
4. 倍精度のECEFから原点の局地ENUへ変換し、`[East / 10, Up / 10, -North / 10]` をGLB頂点へ焼き込む。
5. 法線も同じ回転を適用して正規化する。最終GLBのノード変換は単位行列。

江東区の原タイル `data44.b3dm` を用いた照合では、正しい軸回転後の楕円体高は32.19463964〜114.55879910mで、公式タイル境界の32.19463929〜114.55879700mと一致した。軸回転を省くと−309〜404mとなり地形が壊れる。

建物を地表へ個別に平坦化する補正は行わない。地表も楕円体高から同じENU変換を行い、建物と同じ基準で配置する。元CityGMLの標高基準の `_zmin` は `groundAltitudeMeters` として別途残す。`minY`・`maxY` と `bounds` は描画に一致するENUのゲーム単位で、標高mとは異なる。

CPU用の `footprint` は実頂点を水平投影した凸包。屋根の張り出しなども含む保守的な占有範囲であり、凹型建物の中庭などを厳密に再現する土地境界ではない。

## 検証

Godotに直接GLBを読み込ませ、全タイルでシーンとメッシュを生成できることを確認する。

```powershell
& 'S:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe' `
  --headless --path . --script res://Tools/ImportPlateau/verify.gd
```

元データの `EXT_texture_webp` も保持する。拡張の宣言を削除すると、写真テクスチャ付きタイルをGodotで読めなくなるため、DracoとRTCだけを変換後の宣言から除く。

## 出典と利用条件

出典表示：**3D都市モデル（Project PLATEAU）東京都2025年度（国土交通省・東京都）を加工して作成**。

- [国土交通省PLATEAU公式データカタログAPI](https://api.plateauview.mlit.go.jp/datacatalog/plateau-datasets)
- [公式3D Tiles配信仕様と取得方法](https://docs.plateauview.mlit.go.jp/datasets/3d-tiles/)
- [PLATEAUサイトポリシー](https://www.mlit.go.jp/plateau/site-policy/)
- [公式Draco実装](https://github.com/google/draco)

各配信タイルの原典URLはマニフェストと地物レコードに保存する。2026年10月16日に確認した公式ポリシーでは、特記のない公開コンテンツは公共データ利用規約第1.0版（PDL1.0）に準拠し、CC BY 4.0と互換。個別データに特記された条件がある場合はその条件を優先する。加工済みデータであることも明記する。Dracoの依存ライブラリはApache License 2.0。
