import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const manifest = JSON.parse(await fs.readFile(path.join(root, 'Data/RealWorld/plateau-manifest.json'), 'utf8'));
const buildings = JSON.parse(await fs.readFile(path.join(root, 'Data/RealWorld/buildings.json'), 'utf8')).buildings;
const byId = new Map(buildings.map(building => [building.id, building]));
const seen = new Set();
let triangles = 0, maximumOutsideMeters = 0;

/**
 * GLBのaccessorから未変形の数値を読み取ります。
 * @param {object} gltf - GLTF文書。
 * @param {Buffer} binary - BINチャンク。
 * @param {number} index - accessorの番号。
 * @param {number} item - 頂点またはインデックス番号。
 * @param {number} component - 座標軸。
 * @returns {number} 元の数値。
 */
function read(gltf, binary, index, item, component = 0) {
  const accessor = gltf.accessors[index], view = gltf.bufferViews[accessor.bufferView];
  const components = accessor.type === 'SCALAR' ? 1 : Number(accessor.type.slice(3));
  const size = accessor.componentType === 5125 || accessor.componentType === 5126 ? 4 : 2;
  const offset = (view.byteOffset ?? 0) + (accessor.byteOffset ?? 0) + item * (view.byteStride ?? components * size) + component * size;
  return accessor.componentType === 5126 ? binary.readFloatLE(offset) : accessor.componentType === 5125 ? binary.readUInt32LE(offset) : binary.readUInt16LE(offset);
}

// ----------------------------------------

for (const tile of manifest.tiles) {
  const bytes = await fs.readFile(path.join(root, tile.path.replace('res://', '')));
  const mappingBytes = await fs.readFile(path.join(root, tile.featurePath.replace('res://', '')));
  const mapping = JSON.parse(mappingBytes.toString('utf8'));
  if (crypto.createHash('sha256').update(bytes).digest('hex') !== mapping.glbSha256
      || crypto.createHash('sha256').update(mappingBytes).digest('hex') !== tile.featureSha256)
    throw new Error(`SHA256不一致: ${tile.id}`);
  const jsonLength = bytes.readUInt32LE(12);
  const gltf = JSON.parse(bytes.toString('utf8', 20, 20 + jsonLength).trim());
  const binary = bytes.subarray(28 + jsonLength);
  const primitives = gltf.meshes.flatMap(mesh => mesh.primitives);
  if (primitives.length !== mapping.primitives.length) throw new Error(`描画面数不一致: ${tile.id}`);
  let tileTriangles = 0;
  for (let p = 0; p < primitives.length; p++) {
    const primitive = primitives[p], features = mapping.primitives[p].triangleFeatures;
    if (features.length * 3 !== gltf.accessors[primitive.indices].count) throw new Error(`三角形数不一致: ${tile.id}`);
    for (let t = 0; t < features.length; t++) {
      const id = mapping.featureIds[features[t]], building = byId.get(id);
      if (!building) throw new Error(`不明な建物識別子: ${id}`);
      seen.add(id);
      for (let corner = 0; corner < 3; corner++) {
        const index = read(gltf, binary, primitive.indices, t * 3 + corner);
        for (let axis = 0; axis < 3; axis++) {
          const value = read(gltf, binary, primitive.attributes.POSITION, index, axis);
          const outside = Math.max(0, building.bounds.min[axis] - value, value - building.bounds.max[axis]) * 10;
          maximumOutsideMeters = Math.max(maximumOutsideMeters, outside);
          if (outside > 0.01) throw new Error(`元建物境界から外れた三角形: ${tile.id}/${id} ${outside}m`);
        }
      }
    }
    tileTriangles += features.length;
  }
  if (tileTriangles !== tile.triangleCount) throw new Error(`タイル三角形数不一致: ${tile.id}`);
  triangles += tileTriangles;
}
if (seen.size !== buildings.length || triangles !== manifest.statistics.triangleCount)
  throw new Error('全棟または全三角形が建物に対応していません。');
const result = { tileCount: manifest.tiles.length, editableSourceBuildings: seen.size,
  mappedTriangles: triangles, missingBuildings: buildings.length - seen.size,
  maximumBoundsRoundingMeters: maximumOutsideMeters,
  accessorOrdering: 'GLBの元accessorとindexを直接参照。Godotの頂点再配列は使用しない。' };
await fs.mkdir(path.join(root, 'artifacts'), { recursive: true });
await fs.writeFile(path.join(root, 'artifacts/editable-source-validation.json'), JSON.stringify(result, null, 2) + '\n', 'utf8');
console.log(JSON.stringify(result, null, 2));
