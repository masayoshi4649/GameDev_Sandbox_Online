import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import draco3d from 'draco3dgltf';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(directory, '../..');
const cache = path.join(directory, 'cache');
const output = path.join(root, 'Assets/RealWorld/Plateau');
const dataDirectory = path.join(root, 'Data/RealWorld');
const bounds = { west: 139.760, east: 139.830, south: 35.610, north: 35.670 };
const origin = { longitude: 139.795, latitude: 35.640, ellipsoidHeight: 0, metersPerUnit: 10 };
const degrees = Math.PI / 180;
const lon0 = origin.longitude * degrees;
const lat0 = origin.latitude * degrees;
const flattening = 1 / 298.257223563;
const eccentricity = flattening * (2 - flattening);
const radius = 6378137 / Math.sqrt(1 - eccentricity * Math.sin(lat0) ** 2);
const originEcef = [radius * Math.cos(lat0) * Math.cos(lon0), radius * Math.cos(lat0) * Math.sin(lon0), radius * (1 - eccentricity) * Math.sin(lat0)];
const decoderModule = await draco3d.createDecoderModule({});
const allBuildings = new Map();
const datasetAudits = {};
const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

/**
 * HTTPデータをキャッシュし、同一URLの再取得を省く。
 * @param {string} url - 取得する公式配信URL。
 * @returns {Promise<Buffer>} 配信元のバイナリ。
 */
async function download(url) {
  const destination = path.join(cache, crypto.createHash('sha256').update(url).digest('hex'));
  try { return await fs.readFile(destination); } catch {}
  for (let retry = 0; retry < 4; retry++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(120000) });
      if (!response.ok) throw new Error(`HTTP ${response.status}: ${url}`);
      const value = Buffer.from(await response.arrayBuffer());
      await fs.writeFile(destination, value);
      return value;
    } catch (error) { if (retry === 3) throw error; }
  }
}

/**
 * 3D Tilesの列優先行列同士を積算する。
 * @param {number[]} a - 親変換行列。
 * @param {number[]} b - 子変換行列。
 * @returns {number[]} 列優先4行4列の合成変換行列。
 */
function multiply(a, b) {
  const result = Array(16).fill(0);
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) for (let k = 0; k < 4; k++) result[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k];
  return result;
}

/**
 * タイルの地域境界が対象範囲と交差するか判定する。
 * @param {object} volume - 3D Tilesの境界領域。
 * @returns {boolean} 対象範囲を含む可能性がある場合は真。
 */
function intersects(volume) {
  const region = volume?.region;
  if (!region) return true;
  return region[0] / degrees <= bounds.east && region[2] / degrees >= bounds.west && region[1] / degrees <= bounds.north && region[3] / degrees >= bounds.south;
}

/**
 * 最高詳細度の末端タイルのみを抽出して親子の二重描画を防ぐ。
 * @param {object} node - 現在の3D Tilesノード。
 * @param {string} url - 現在のtileset.jsonのURL。
 * @param {number[]} parent - 親ノードの合成変換。
 * @returns {Promise<object[]>} ダウンロードする末端タイル一覧。
 */
async function selectTiles(node, url, parent = identity) {
  if (!intersects(node.boundingVolume)) return [];
  const transform = multiply(parent, node.transform ?? identity);
  const children = node.children ?? [];
  if (children.length) return (await Promise.all(children.map(child => selectTiles(child, url, transform)))).flat();
  const reference = node.content?.uri ?? node.content?.url;
  if (!reference) return [];
  const contentUrl = new URL(reference, url).href;
  if (contentUrl.endsWith('.json')) {
    const nested = JSON.parse((await download(contentUrl)).toString('utf8'));
    return selectTiles(nested.root, contentUrl, transform);
  }
  return [{ url: contentUrl, transform, region: node.boundingVolume?.region }];
}

/**
 * ECEFからゲームの東・上・南軸へ倍精度で変換する。
 * @param {number[]} p - ECEFの位置または方向ベクトル。
 * @param {boolean} position - 位置の場合のみ原点を減算する。
 * @returns {number[]} ゲーム座標。位置の場合は1単位10m。
 */
function ecefToGame(p, position = true) {
  const [x, y, z] = p.map((value, i) => value - (position ? originEcef[i] : 0));
  const east = -Math.sin(lon0) * x + Math.cos(lon0) * y;
  const north = -Math.sin(lat0) * Math.cos(lon0) * x - Math.sin(lat0) * Math.sin(lon0) * y + Math.cos(lat0) * z;
  const up = Math.cos(lat0) * Math.cos(lon0) * x + Math.cos(lat0) * Math.sin(lon0) * y + Math.sin(lat0) * z;
  const scale = position ? 1 / origin.metersPerUnit : 1;
  return [east * scale, up * scale, -north * scale];
}

/**
 * glTFのY-upから3D TilesのZ-upへ回転し、RTCと親タイル変換を適用する。
 * @param {number[]} v - glTFの位置または法線。
 * @param {number[]} rtc - 元GLBのCESIUM_RTC原点。
 * @param {number[]} transform - 親子合成済みタイル変換行列。
 * @param {boolean} position - 位置の場合はRTCおよび平行移動を適用する。
 * @returns {number[]} ゲーム座標の位置または正規化した法線。
 */
function convertVector(v, rtc, transform, position = true) {
  const q = [v[0], -v[2], v[1]].map((value, i) => value + (position ? rtc[i] : 0));
  const transformed = [0, 1, 2].map(i => transform[i] * q[0] + transform[i + 4] * q[1] + transform[i + 8] * q[2] + (position ? transform[i + 12] : 0));
  const result = ecefToGame(transformed, position);
  if (!position) { const magnitude = Math.hypot(...result); return result.map(value => value / magnitude); }
  return result;
}

/**
 * Draco圧縮された三角形メッシュを元の頂点属性へ展開する。
 * @param {object} primitive - 元glTFのプリミティブ。
 * @param {object} model - 元glTF文書。
 * @param {Buffer} binary - 元glTFのBINチャンク。
 * @returns {object} 展開した属性配列と三角形インデックス。
 */
function decodePrimitive(primitive, model, binary) {
  const extension = primitive.extensions?.KHR_draco_mesh_compression;
  if (!extension) throw new Error('未対応の非Dracoプリミティブです。原典を確認してください。');
  const view = model.bufferViews[extension.bufferView];
  const raw = binary.subarray(view.byteOffset ?? 0, (view.byteOffset ?? 0) + view.byteLength);
  const decoder = new decoderModule.Decoder();
  const buffer = new decoderModule.DecoderBuffer();
  buffer.Init(new Int8Array(raw), raw.length);
  const mesh = new decoderModule.Mesh();
  const status = decoder.DecodeBufferToMesh(buffer, mesh);
  if (!status.ok() || !mesh.ptr) throw new Error(`Dracoの展開失敗: ${status.error_msg()}`);
  const attributes = {};
  for (const [semantic, uniqueId] of Object.entries(extension.attributes)) {
    const attribute = decoder.GetAttributeByUniqueId(mesh, uniqueId);
    const values = new decoderModule.DracoFloat32Array();
    decoder.GetAttributeFloatForAllPoints(mesh, attribute, values);
    const result = new Float32Array(values.size());
    for (let i = 0; i < result.length; i++) result[i] = values.GetValue(i);
    attributes[semantic] = { data: result, components: attribute.num_components() };
    decoderModule.destroy(values);
  }
  const indices = new Uint32Array(mesh.num_faces() * 3);
  const face = new decoderModule.DracoInt32Array();
  for (let i = 0; i < mesh.num_faces(); i++) {
    decoder.GetFaceFromMesh(mesh, i, face);
    for (let j = 0; j < 3; j++) indices[i * 3 + j] = face.GetValue(j);
  }
  decoderModule.destroy(face);
  decoderModule.destroy(mesh);
  decoderModule.destroy(buffer);
  decoderModule.destroy(decoder);
  return { attributes, indices, material: primitive.material };
}

/**
 * 属性点群から水平面の凸包を計算する。
 * @param {number[][]} points - ゲーム座標の東・南点群。
 * @returns {number[][]} 反時計回りの凸包。単位はゲーム単位。
 */
function convexHull(points) {
  const unique = [...new Map(points.map(p => [p.map(v => Math.round(v * 1000)).join(','), p])).values()];
  unique.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (unique.length < 3) return unique;
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lower = [], upper = [];
  for (const p of unique) { while (lower.length > 1 && cross(lower.at(-2), lower.at(-1), p) <= 0) lower.pop(); lower.push(p); }
  for (const p of unique.reverse()) { while (upper.length > 1 && cross(upper.at(-2), upper.at(-1), p) <= 0) upper.pop(); upper.push(p); }
  lower.pop(); upper.pop();
  return lower.concat(upper).map(p => p.map(v => Math.round(v * 10000) / 10000));
}

// ----------------------------------------

/**
 * 1つのb3dmを標準GLBへ変換し、原典由来の固定地物も抽出する。
 * @param {Buffer} bytes - 公式配信b3dm。
 * @param {object} tile - 出典と変換行列を含む対象タイル。
 * @param {object} dataset - 公式データカタログのレコード。
 * @returns {Promise<object|null>} マニフェスト用のタイル情報。範囲内に地物がない場合はnull。
 */
async function convertTile(bytes, tile, dataset) {
  if (bytes.toString('ascii', 0, 4) !== 'b3dm') throw new Error('b3dm以外の形式です。');
  const featureJsonSize = bytes.readUInt32LE(12), featureBinarySize = bytes.readUInt32LE(16);
  const batchJsonSize = bytes.readUInt32LE(20), batchBinarySize = bytes.readUInt32LE(24);
  const feature = JSON.parse(bytes.toString('utf8', 28, 28 + featureJsonSize).trim());
  const batchOffset = 28 + featureJsonSize + featureBinarySize;
  const batch = batchJsonSize ? JSON.parse(bytes.toString('utf8', batchOffset, batchOffset + batchJsonSize).trim()) : {};
  const batchBinary = bytes.subarray(batchOffset + batchJsonSize, batchOffset + batchJsonSize + batchBinarySize);
  const binaryTypes = { BYTE: ['readInt8', 1], UNSIGNED_BYTE: ['readUInt8', 1], SHORT: ['readInt16LE', 2], UNSIGNED_SHORT: ['readUInt16LE', 2], INT: ['readInt32LE', 4], UNSIGNED_INT: ['readUInt32LE', 4], FLOAT: ['readFloatLE', 4], DOUBLE: ['readDoubleLE', 8] };
  for (const [key, descriptor] of Object.entries(batch)) {
    if (!descriptor || Array.isArray(descriptor) || descriptor.byteOffset === undefined) continue;
    if (descriptor.type !== 'SCALAR' || !binaryTypes[descriptor.componentType]) throw new Error(`未対応のバッチ属性型: ${key}`);
    const [reader, size] = binaryTypes[descriptor.componentType];
    batch[key] = Array.from({ length: feature.BATCH_LENGTH }, (_, i) => batchBinary[reader](descriptor.byteOffset + i * size));
  }
  const glbOffset = batchOffset + batchJsonSize + batchBinarySize;
  const jsonSize = bytes.readUInt32LE(glbOffset + 12);
  const model = JSON.parse(bytes.toString('utf8', glbOffset + 20, glbOffset + 20 + jsonSize).trim());
  const binary = bytes.subarray(glbOffset + 28 + jsonSize, glbOffset + bytes.readUInt32LE(glbOffset + 8));
  const rtc = model.extensions?.CESIUM_RTC?.center ?? feature.RTC_CENTER ?? [0, 0, 0];
  for (const node of model.nodes ?? []) {
    if (node.matrix || node.translation || node.rotation || node.scale) throw new Error('未検証のglTFノード変換を検出しました。');
  }
  const selected = new Map();
  const audit = datasetAudits[dataset.id];
  audit.sourceFeaturesInIntersectingTiles += feature.BATCH_LENGTH;
  for (let i = 0; i < feature.BATCH_LENGTH; i++) {
    if (batch._xmax?.[i] < bounds.west || batch._xmin?.[i] > bounds.east || batch._ymax?.[i] < bounds.south || batch._ymin?.[i] > bounds.north) continue;
    audit.featuresIntersectingRequestedBounds++;
    if (dataset.lod === '1' && allBuildings.has(batch.gml_id?.[i])) { audit.featuresAlreadyPresentAtHigherPriority++; continue; }
    selected.set(i, { minimumY: Infinity, maximumY: -Infinity, points: [], lod: Number(batch._lod?.[i] ?? dataset.lod) });
  }
  if (!selected.size) return null;
  const decoded = (model.meshes ?? []).flatMap(mesh => mesh.primitives.map(p => decodePrimitive(p, model, binary)));
  for (const primitive of decoded) {
    const positions = primitive.attributes.POSITION.data;
    const batches = primitive.attributes._BATCHID?.data;
    if (!batches) throw new Error('固定地物識別子_BATCHIDがありません。');
    for (let i = 0; i < positions.length / 3; i++) {
      const p = convertVector(Array.from(positions.subarray(i * 3, i * 3 + 3)), rtc, tile.transform);
      positions.set(p, i * 3);
      const building = selected.get(Math.round(batches[i]));
      if (building) {
        building.minimumY = Math.min(building.minimumY, p[1]);
        building.maximumY = Math.max(building.maximumY, p[1]);
        building.points.push([p[0], p[2]]);
      }
    }
    const normals = primitive.attributes.NORMAL?.data;
    if (normals) for (let i = 0; i < normals.length / 3; i++) normals.set(convertVector(Array.from(normals.subarray(i * 3, i * 3 + 3)), rtc, tile.transform, false), i * 3);
  }
  const id = `${dataset.city_code}-${dataset.lod === '1' ? 'lod1-' : ''}${path.basename(new URL(tile.url).pathname, '.b3dm')}`;
  const lodCounts = {};
  let buildingCount = 0;
  for (const [index, building] of selected) {
    if (!Number.isFinite(building.minimumY)) continue;
    const sourceMinimum = batch._zmin?.[index] === undefined ? null : Number(batch._zmin[index]);
    const identifier = batch.gml_id?.[index] ?? `${id}-${index}`;
    const record = {
      id: identifier,
      tileId: id,
      cityCode: dataset.city_code,
      name: batch['gml:name']?.[index] ?? '',
      usage: batch['bldg:usage']?.[index] ?? '',
      measuredHeightMeters: Number(batch['bldg:measuredHeight']?.[index]) || (building.maximumY - building.minimumY) * origin.metersPerUnit,
      groundAltitudeMeters: sourceMinimum,
      minY: building.minimumY,
      maxY: building.maximumY,
      lod: building.lod,
      footprint: convexHull(building.points),
      bounds: { min: [Math.min(...building.points.map(p => p[0])), building.minimumY, Math.min(...building.points.map(p => p[1]))], max: [Math.max(...building.points.map(p => p[0])), building.maximumY, Math.max(...building.points.map(p => p[1]))] },
      geographicBounds: [batch._xmin?.[index], batch._ymin?.[index], batch._xmax?.[index], batch._ymax?.[index]],
      sourceUrl: tile.url
    };
    allBuildings.set(identifier, record);
    audit.importedFeatureCount++;
    lodCounts[building.lod] = (lodCounts[building.lod] ?? 0) + 1;
    buildingCount++;
  }
  const gltf = { asset: { version: '2.0', generator: '湾岸PLATEAU実形状取込 1.0' }, scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0 }], meshes: [{ primitives: [] }], accessors: [], bufferViews: [], buffers: [{ byteLength: 0 }] };
  for (const key of ['extensionsUsed', 'extensionsRequired']) {
    const remaining = model[key]?.filter(extension => !['CESIUM_RTC', 'KHR_draco_mesh_compression'].includes(extension));
    if (remaining?.length) gltf[key] = remaining;
  }
  for (const key of ['materials', 'textures', 'samplers']) if (model[key]) gltf[key] = structuredClone(model[key]);
  const chunks = [];
  let binaryLength = 0;
  /**
   * GLBバッファへ4バイト整列したデータを追加する。
   * @param {Buffer|TypedArray} value - 追加するバイナリ。
   * @param {number|undefined} target - glTFの描画用途。
   * @returns {number} 追加したbufferView番号。
   */
  function append(value, target) {
    const data = Buffer.isBuffer(value) ? value : Buffer.from(value.buffer, value.byteOffset, value.byteLength);
    const index = gltf.bufferViews.length;
    gltf.bufferViews.push({ buffer: 0, byteOffset: binaryLength, byteLength: data.length, ...(target ? { target } : {}) });
    chunks.push(data);
    const padding = (4 - data.length % 4) % 4;
    if (padding) chunks.push(Buffer.alloc(padding));
    binaryLength += data.length + padding;
    return index;
  }
  if (model.images) gltf.images = model.images.map(image => {
    if (image.bufferView === undefined) throw new Error('外部テクスチャは未対応です。');
    const view = model.bufferViews[image.bufferView];
    return { ...image, bufferView: append(binary.subarray(view.byteOffset ?? 0, (view.byteOffset ?? 0) + view.byteLength)) };
  });
  const minimum = [Infinity, Infinity, Infinity], maximum = [-Infinity, -Infinity, -Infinity];
  let triangleCount = 0;
  const featureIds = [...selected].filter(([, building]) => Number.isFinite(building.minimumY))
    .map(([index]) => batch.gml_id?.[index] ?? `${id}-${index}`);
  const featureNumbers = new Map(featureIds.map((identifier, index) => [identifier, index]));
  const featurePrimitives = [];
  for (const primitive of decoded) {
    const batches = primitive.attributes._BATCHID.data;
    const validIndices = [];
    const triangleFeatures = [];
    for (let i = 0; i < primitive.indices.length; i += 3) {
      const batchId = Math.round(batches[primitive.indices[i]]);
      if (!selected.has(batchId)) continue;
      if (Math.round(batches[primitive.indices[i + 1]]) !== batchId || Math.round(batches[primitive.indices[i + 2]]) !== batchId)
        throw new Error('三角形の頂点が複数の建物識別子にまたがっています。');
      validIndices.push(primitive.indices[i], primitive.indices[i + 1], primitive.indices[i + 2]);
      triangleFeatures.push(featureNumbers.get(batch.gml_id?.[batchId] ?? `${id}-${batchId}`));
    }
    if (!validIndices.length) continue;
    const remap = new Map();
    for (const index of validIndices) if (!remap.has(index)) remap.set(index, remap.size);
    const result = { attributes: {}, indices: gltf.accessors.length, mode: 4, ...(primitive.material === undefined ? {} : { material: primitive.material }) };
    const indices = Uint32Array.from(validIndices, index => remap.get(index));
    gltf.accessors.push({ bufferView: append(indices, 34963), componentType: 5125, count: indices.length, type: 'SCALAR' });
    triangleCount += indices.length / 3;
    for (const [semantic, attribute] of Object.entries(primitive.attributes)) {
      if (semantic === '_BATCHID') continue;
      const values = new Float32Array(remap.size * attribute.components);
      for (const [oldIndex, newIndex] of remap) {
        const data = Array.from(attribute.data.subarray(oldIndex * attribute.components, (oldIndex + 1) * attribute.components));
        if (semantic === 'POSITION') {
          for (let axis = 0; axis < 3; axis++) { minimum[axis] = Math.min(minimum[axis], data[axis]); maximum[axis] = Math.max(maximum[axis], data[axis]); }
        }
        values.set(data, newIndex * attribute.components);
      }
      const accessor = { bufferView: append(values, 34962), componentType: 5126, count: remap.size, type: `VEC${attribute.components}` };
      if (semantic === 'POSITION') { accessor.min = [...minimum]; accessor.max = [...maximum]; }
      result.attributes[semantic] = gltf.accessors.length;
      gltf.accessors.push(accessor);
    }
    gltf.meshes[0].primitives.push(result);
    featurePrimitives.push({ primitive: featurePrimitives.length, triangleFeatures });
  }
  if (!triangleCount) return null;
  gltf.buffers[0].byteLength = binaryLength;
  const json = Buffer.from(JSON.stringify(gltf));
  const jsonChunk = Buffer.concat([json, Buffer.alloc((4 - json.length % 4) % 4, 0x20)]);
  const header = Buffer.alloc(20); header.write('glTF'); header.writeUInt32LE(2, 4); header.writeUInt32LE(28 + jsonChunk.length + binaryLength, 8); header.writeUInt32LE(jsonChunk.length, 12); header.writeUInt32LE(0x4e4f534a, 16);
  const binaryHeader = Buffer.alloc(8); binaryHeader.writeUInt32LE(binaryLength); binaryHeader.writeUInt32LE(0x004e4942, 4);
  const destination = path.join(output, `${id}.glb`);
  const resultBuffer = Buffer.concat([header, jsonChunk, binaryHeader, ...chunks]);
  await fs.writeFile(destination, resultBuffer);
  const featureBuffer = Buffer.from(JSON.stringify({ schemaVersion: 1, tileId: id,
    glbSha256: crypto.createHash('sha256').update(resultBuffer).digest('hex'),
    featureIds, primitives: featurePrimitives }) + '\n');
  await fs.writeFile(path.join(output, `${id}.features.json`), featureBuffer);
  return { id, path: `res://Assets/RealWorld/Plateau/${id}.glb`,
    featurePath: `res://Assets/RealWorld/Plateau/${id}.features.json`,
    featureSha256: crypto.createHash('sha256').update(featureBuffer).digest('hex'),
    buildingCount, triangleCount, byteLength: resultBuffer.length, bounds: { min: minimum, max: maximum }, transform: identity, sourceUrl: tile.url, sourceDataset: dataset.id, lodCounts, texture: Boolean(model.images?.length), sha256: crypto.createHash('sha256').update(resultBuffer).digest('hex') };
}

// ----------------------------------------

await Promise.all([fs.mkdir(cache, { recursive: true }), fs.mkdir(output, { recursive: true }), fs.mkdir(dataDirectory, { recursive: true })]);
const catalogUrl = 'https://api.plateauview.mlit.go.jp/datacatalog/plateau-datasets';
const catalog = JSON.parse((await download(catalogUrl)).toString('utf8'));
const datasets = catalog.datasets.filter(d => ['13102', '13103', '13108', '13109'].includes(d.city_code) && d.type_en === 'bldg' && d.year === 2025 && ['1', '2'].includes(d.lod) && d.texture === true).sort((a, b) => Number(b.lod) - Number(a.lod));
if (datasets.length !== 8) throw new Error('想定した2025年度の中央区・港区・江東区・品川区LOD1・LOD2データが揃っていません。');
const tiles = [];
for (const dataset of datasets) {
  const tileset = JSON.parse((await download(dataset.url)).toString('utf8'));
  const selected = await selectTiles(tileset.root, dataset.url);
  datasetAudits[dataset.id] = { requestedLeafTileCount: selected.length, sourceFeaturesInIntersectingTiles: 0, featuresIntersectingRequestedBounds: 0, featuresAlreadyPresentAtHigherPriority: 0, importedFeatureCount: 0 };
  console.log(`${dataset.city} LOD${dataset.lod}：対象タイル ${selected.length}`);
  for (let offset = 0; offset < selected.length; offset += 3) {
    const group = selected.slice(offset, offset + 3);
    const bytes = await Promise.all(group.map(tile => download(tile.url)));
    for (let i = 0; i < group.length; i++) {
      const result = await convertTile(bytes[i], group[i], dataset);
      if (result) { tiles.push(result); console.log(`${result.id}：建築物 ${result.buildingCount}、三角形 ${result.triangleCount}、${(result.byteLength / 1048576).toFixed(2)} MiB`); }
    }
  }
}
const buildings = [...allBuildings.values()];
const statistics = { tileCount: tiles.length, buildingCount: buildings.length, triangleCount: tiles.reduce((sum, tile) => sum + tile.triangleCount, 0), byteLength: tiles.reduce((sum, tile) => sum + tile.byteLength, 0), texturedTileCount: tiles.filter(t => t.texture).length, lodCounts: {}, minY: Math.min(...buildings.map(b => b.minY)), maxY: Math.max(...buildings.map(b => b.maxY)) };
for (const building of buildings) statistics.lodCounts[building.lod] = (statistics.lodCounts[building.lod] ?? 0) + 1;
statistics.uniqueBuildingsIntersectingRequestedBounds = buildings.length;
statistics.renderedBuildingInstances = tiles.reduce((sum, tile) => sum + tile.buildingCount, 0);
statistics.datasetAudits = datasetAudits;
const metadata = { schemaVersion: 1, title: '東京湾岸の実建築物（PLATEAU 2025）', generatedAt: new Date().toISOString(), origin, geographicBounds: bounds, coordinates: { x: '東', y: '上（楕円体高を含むENU）', z: '南', metersPerUnit: 10, horizontalProjection: 'WGS84 ECEFから原点の局地ENUへ変換', verticalCorrection: 'なし。glTFのY-upをZ-upに回転後、CESIUM_RTC・tileTransformを適用し倍精度のECEFからENUへ変換する。標高基準への置換や建物の個別平坦化は行わない。' }, sources: datasets.map(d => ({ id: d.id, name: d.name, year: d.year, url: d.url, lod: d.lod, texture: d.texture })), attribution: '3D都市モデル（Project PLATEAU）東京都2025年度（国土交通省・東京都）を加工して作成', license: { name: '公共データ利用規約（第1.0版、PDL1.0）。CC BY 4.0と互換。個別データの権利表記を優先。', url: 'https://www.mlit.go.jp/plateau/site-policy/' }, statistics };
await fs.writeFile(path.join(dataDirectory, 'plateau-manifest.json'), JSON.stringify({ ...metadata, tiles }, null, 2) + '\n', 'utf8');
await fs.writeFile(path.join(dataDirectory, 'buildings.json'), JSON.stringify({ schemaVersion: 1, origin, attribution: metadata.attribution, buildings }) + '\n', 'utf8');
const activeFiles = new Set(tiles.map(tile => path.basename(tile.path)));
for (const entry of await fs.readdir(output)) if (entry.endsWith('.glb') && !activeFiles.has(entry)) await fs.unlink(path.join(output, entry));
console.log(JSON.stringify(statistics, null, 2));
