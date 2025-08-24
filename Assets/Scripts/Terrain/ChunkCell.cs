    using System.Collections;
    using System.Collections.Generic;
    using UnityEditor;
    using UnityEngine;
    using System.Diagnostics; // Stopwatch
    // If you want a short alias: using Stopwatch = System.Diagnostics.Stopwatch;

    public class ChunkCell : MonoBehaviour
    {
        public Vector3Int index;
        public int gridSize = 10;
        public int chunkSize = 16;
        public float isoLevel = 0.5f;

        private Voxel[] voxelData;

        public ComputeShader marchingCubesShader;
        public ComputeShader noiseShader;

        private ComputeBuffer triangleBuffer;
        private ComputeBuffer counterBuffer;
        private ComputeBuffer voxelBuffer;

        public Material[] materialInstances;
        private MeshRenderer meshRenderer;
        private MeshFilter meshFilter;
        private MeshCollider meshCollider;

        public List<TerrainNoiseProfile> terrainLayers;

        // ---------------- FOLIAGE CONFIG ----------------
        [Header("Foliage")]
        public GameObject[] foliagePrefabs;          // Assign one or more prefabs
        public float foliageMaxSlopeDeg = 25f;       // Max slope for spawning (0 = only perfectly up; 90 = any)
        public float foliageTargetsPerArea = 10f;    // Expected spawns per (gridSize * gridSize) per chunk
        [Range(0f, 360f)] public float yawJitterDeg = 360f;   // full spin randomness (set smaller if you want less)
        [Range(0f, 20f)] public float tiltJitterDeg = 4f;    // small pitch/roll wobble
        public float positionJitter = 0.05f;                 // world-space XY jitter (meters)
        public Vector2 uniformScaleRange = new Vector2(0.9f, 1.1f); // min..max
        public Transform foliageParent;              // Optional parent for hierarchy
        
        private bool foliageInitialized = false;

        void Start()
        {
            gameObject.layer = LayerMask.NameToLayer("Terrain");
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            meshRenderer.receiveShadows = true;
            meshRenderer.materials = materialInstances;

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshCollider = gameObject.AddComponent<MeshCollider>();
            
            foliageParent = transform;
            InitChunk();
        }

        public void InitChunk()
        {
            int voxelGridSize = gridSize * gridSize * gridSize;
            voxelData = new Voxel[voxelGridSize];

            if (triangleBuffer != null) triangleBuffer.Release();
            if (counterBuffer != null) counterBuffer.Release();
            if (voxelBuffer != null) voxelBuffer.Release();

            triangleBuffer = new ComputeBuffer(voxelGridSize * 5, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Triangle)), ComputeBufferType.Append);
            counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
            voxelBuffer = new ComputeBuffer(voxelGridSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Voxel)));

            foreach (TerrainNoiseProfile layer in terrainLayers)
                GenerateGPUVoxels(layer);
        }

        void OnDestroy()
        {
            if (triangleBuffer != null) { triangleBuffer.Release(); triangleBuffer = null; }
            if (counterBuffer != null) { counterBuffer.Release(); counterBuffer = null; }
            if (voxelBuffer != null) { voxelBuffer.Release(); voxelBuffer = null; }
        }

        public void GenerateGPUVoxels(TerrainNoiseProfile layer)
        {
            int kernelIndex = noiseShader.FindKernel("Density");
            noiseShader.SetVector("chunkPosition", new Vector4(transform.position.x, transform.position.y, transform.position.z));
            noiseShader.SetFloat("isoLevel", isoLevel);
            noiseShader.SetBuffer(kernelIndex, "voxels", voxelBuffer);
            noiseShader.SetInt("numPointsPerAxis", gridSize);
            noiseShader.SetFloat("chunkSize", chunkSize);
            noiseShader.SetVector("offset", new Vector4(layer.offset.x, layer.offset.y, layer.offset.z));
            noiseShader.SetInt("octaves", layer.octaves);
            noiseShader.SetFloat("lacunarity", layer.lacunarity);
            noiseShader.SetFloat("persistence", layer.persistence);
            noiseShader.SetFloat("noiseScale", layer.noiseScale);
            noiseShader.SetFloat("noiseWeight", layer.noiseWeight);
            noiseShader.SetFloat("floorOffset", layer.floorOffset);
            noiseShader.SetFloat("weightMultiplier", layer.weightMultiplier);
            noiseShader.SetFloat("hardFloor", layer.hardFloor);
            noiseShader.SetFloat("hardFloorWeight", layer.hardFloorWeight);
            noiseShader.SetInt("val", (int)layer.type);

            noiseShader.Dispatch(0, gridSize / 4, gridSize / 4, gridSize / 4);
            voxelBuffer.GetData(voxelData);
        }

        public void GenerateCPUVoxels()
        {
            float amplitude = 2f;
            float frequency = 5f * Mathf.PI / (float)(gridSize - 3);
            float spacing = chunkSize / (float)(gridSize - 3);

            for (int z = 0; z < gridSize; z++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int x = 0; x < gridSize; x++)
                    {
                        int idx = x + y * gridSize + z * gridSize * gridSize;

                        Vector3 voxelWorldPos = transform.position + new Vector3(x, y, z) * spacing + Vector3.down * 20;
                        float baseIso = voxelWorldPos.y - voxelWorldPos.x * 0.5f;
                        float sinOffset = Mathf.Sin(voxelWorldPos.x * frequency) * amplitude;
                        float isoValue = baseIso + sinOffset;

                        TerrainType type = (TerrainType)(isoValue < 0.47f ? (isoValue < 0.40f ? 2 : 1) : 0);
                        voxelData[idx] = new Voxel(type, isoValue, 0);
                    }
                }
            }

            voxelBuffer.SetData(voxelData);
        }
        
        public TerrainType GetTerrainTypeAtLocal(Vector3 localPos)
        {
            // localPos is in this chunk's LOCAL space (same space as mesh vertices):
            // x,y,z ∈ [0, chunkSize]. We map to voxel grid indices 0..gridSize-1.
            if (voxelData == null || voxelData.Length == 0) return default;

            float toGrid = (gridSize - 3f) / Mathf.Max(1f, (float)chunkSize);

            int x = Mathf.Clamp(Mathf.RoundToInt(localPos.x * toGrid), 0, gridSize - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(localPos.y * toGrid), 0, gridSize - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt(localPos.z * toGrid), 0, gridSize - 1);

            int idx = x + y * gridSize + z * gridSize * gridSize;
            return voxelData[idx].type;
        }
        
        // Back-compat: same signature you already call from elsewhere.
        public void UpdateVoxelGridWithSphere(
            Vector3 position, float radius, float strength, TerrainType terrainType,
            Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
        {
            UpdateVoxelGrid(
                BrushShape.Sphere,
                position,
                radius,
                Vector3.zero,
                Quaternion.identity,
                strength,
                terrainType,
                inventory,
                breakingProgress,
                doFallOff,
                oneBlockOnly,
                forceReplace
            );
        }

// New: oriented cube brush (halfExtents are in *grid* units, rotation in world/grid axes)
        public void UpdateVoxelGridWithCube(
            Vector3 center, Vector3 halfExtents, Quaternion rotation, float strength,
            TerrainType terrainType, Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
        {
            UpdateVoxelGrid(
                BrushShape.Wall,
                center,
                0f,
                halfExtents,
                rotation,
                strength,
                terrainType,
                inventory,
                breakingProgress,
                doFallOff,
                oneBlockOnly,
                forceReplace
            );
        }
        private void UpdateVoxelGrid(
            BrushShape shape,
            Vector3 centerGrid,                 // brush center in grid coords (same as before)
            float radiusGrid,                   // for Sphere
            Vector3 halfExtentsGrid,            // for Cube (XYZ half-sizes in grid coords)
            Quaternion rotation,                // for Cube
            float strength,
            TerrainType fillType,               // use the type the caller asked for
            Dictionary<TerrainType, int> inventory,
            float breakingProgress = 0,
            bool doFallOff = true, 
            bool oneBlockOnly = false, 
            bool forceReplace = false
            )
        {
            // todo: this is a bit hacky
        if (forceReplace)
            strength = 0;
    // Safety helpers
    float SafeDiv(float a, float b) => a / (Mathf.Abs(b) < 1e-6f ? 1e-6f : b);

    // Optional: bounding sphere for early skip when using cubes
    float cubeBoundRadiusSqr = 0f;
    if (shape == BrushShape.Wall)
    {
        float r = halfExtentsGrid.magnitude;
        cubeBoundRadiusSqr = r * r;
    }

    for (int z = 0; z < gridSize; z++)
    for (int y = 0; y < gridSize; y++)
    for (int x = 0; x < gridSize; x++)
    {
        Vector3 voxelPos = new Vector3(x, y, z);
        int idx = x + y * gridSize + z * gridSize * gridSize;

        bool inside = false;
        float falloff = 1f;

        float sqrDistance = (voxelPos - centerGrid).sqrMagnitude;
        if (shape == BrushShape.Sphere)
        {
            if (sqrDistance <= radiusGrid * radiusGrid)
            {
                if (doFallOff)
                {
                    float distance = Mathf.Sqrt(sqrDistance);
                    float normalized = Mathf.Clamp01(distance / radiusGrid); // 0 at center, 1 at surface
                    falloff = 1f - normalized;
                }
                inside = true;
            }
            
        }
        else // Cube
        {
            // Early bounding-sphere cull to avoid doing the rotation/math when far
            Vector3 d = (voxelPos - centerGrid);
            if (d.sqrMagnitude > cubeBoundRadiusSqr) goto Skip;

            // Transform into cube local space (so the cube is axis-aligned in its own space)
            // NOTE: if you ever rotate the chunk GameObject itself, consider: Quaternion inv = Quaternion.Inverse(transform.rotation * rotation);
            Quaternion inv = Quaternion.Inverse(rotation);
            Vector3 local = inv * d;

            Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

            if (a.x <= halfExtentsGrid.x && a.y <= halfExtentsGrid.y && a.z <= halfExtentsGrid.z)
            {
                if (doFallOff)
                {
                    // Normalized distance to faces (max component gives "Chebyshev radius")
                    float nx = SafeDiv(a.x, halfExtentsGrid.x);
                    float ny = SafeDiv(a.y, halfExtentsGrid.y);
                    float nz = SafeDiv(a.z, halfExtentsGrid.z);
                    float normalized = Mathf.Clamp01(Mathf.Max(nx, Mathf.Max(ny, nz))); // 0 center .. 1 faces
                    falloff = 1f - normalized;
                }
                inside = true;
                
            }
        }

        if (inside)
        {
            voxelData[idx].breakingProgress = breakingProgress * falloff;
            // Apply type on fill when crossing from "air" to "solid"
            if (strength > 0f && voxelData[idx].iso <= 0.5f)
                voxelData[idx].type = fillType;
            
            if (strength < 0f && voxelData[idx].iso < 0.5f)
                voxelData[idx].type = default;
            // Only destroy b
            if(strength < 0f && oneBlockOnly && voxelData[idx].type != fillType)
                continue;
                
            
            float oldIso = voxelData[idx].iso;
            if (voxelData[idx].type != default)
                voxelData[idx].iso = Mathf.Clamp(voxelData[idx].iso + strength * falloff, 0f, 1f);

            // Inventory bookkeeping when crossing iso threshold
            if (strength > 0f)
            {
                if (oldIso <= 0.5f && voxelData[idx].iso >= 0.5f)
                    inventory[voxelData[idx].type]--;
            }
            else
            {
                if (oldIso >= 0.5f && voxelData[idx].iso <= 0.5f)
                    inventory[voxelData[idx].type]++;
            }
        }

        Skip:; // label for early-continue
    }

    voxelBuffer.SetData(voxelData);

    // Remove foliage for the edited region
    if (shape == BrushShape.Sphere)
        RemoveFoliageInsideSphere(centerGrid, radiusGrid);
    else
        RemoveFoliageInsideBox(centerGrid, halfExtentsGrid, rotation);
}
        
        // ---- SMOOTHING (sphere) ----
// centerGrid & radiusGrid are in GRID coordinates (0..gridSize-1)
// strength in [0..1] is the blend factor toward the smoothed value.
// doFallOff: if true, stronger smoothing near center, weaker near the edge.
public void SmoothSphere(Vector3 centerGrid, float radiusGrid, float strength, bool doFallOff = true)
{
    strength = Mathf.Clamp01(strength);
    if (strength <= 1e-6f || voxelData == null || voxelData.Length == 0) return;

    int gs = gridSize;
    float r = Mathf.Max(0f, radiusGrid);
    float r2 = r * r;

    // Bounds of the sphere in grid coords
    int xmin = Mathf.Max(0, Mathf.FloorToInt(centerGrid.x - r));
    int xmax = Mathf.Min(gs - 1, Mathf.CeilToInt(centerGrid.x + r));
    int ymin = Mathf.Max(0, Mathf.FloorToInt(centerGrid.y - r));
    int ymax = Mathf.Min(gs - 1, Mathf.CeilToInt(centerGrid.y + r));
    int zmin = Mathf.Max(0, Mathf.FloorToInt(centerGrid.z - r));
    int zmax = Mathf.Min(gs - 1, Mathf.CeilToInt(centerGrid.z + r));

    // Copy iso field so we read from src and write to dst without feedback
    float[] srcIso = new float[voxelData.Length];
    for (int i = 0; i < voxelData.Length; i++) srcIso[i] = voxelData[i].iso;

    // Separable 1-2-1 kernel weights for each axis
    // Combined weight in 3D is prod of axis weights
    int[] w1 = { 1, 2, 1 }; // offsets -1,0,1  (we'll map with +1)
    int wSum1D = 1 + 2 + 1; // 4
    int wSum3D = wSum1D * wSum1D * wSum1D; // 64

    for (int z = zmin; z <= zmax; z++)
    {
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                Vector3 p = new Vector3(x, y, z);
                Vector3 d = p - centerGrid;
                float dist2 = d.sqrMagnitude;

                if (dist2 > r2) continue; // outside sphere

                // Weighted 3x3x3 neighborhood average from srcIso
                int accumW = 0;
                float accumIso = 0f;

                for (int dz = -1; dz <= 1; dz++)
                {
                    int zz = Mathf.Clamp(z + dz, 0, gs - 1);
                    int wz = w1[dz + 1];

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Mathf.Clamp(y + dy, 0, gs - 1);
                        int wy = w1[dy + 1];

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Mathf.Clamp(x + dx, 0, gs - 1);
                            int wx = w1[dx + 1];

                            int w = wx * wy * wz;
                            int nIdx = xx + yy * gs + zz * gs * gs;

                            accumW += w;
                            accumIso += srcIso[nIdx] * w;
                        }
                    }
                }

                float avgIso = (accumW > 0) ? (accumIso / accumW) : voxelData[x + y * gs + z * gs * gs].iso;

                // Distance-based falloff (stronger in center)
                float t = strength;
                if (doFallOff && r > 1e-6f)
                {
                    float dist = Mathf.Sqrt(dist2);
                    float normalized = Mathf.Clamp01(dist / r);   // 0 center .. 1 edge
                    float fall = 1f - normalized;                 // 1 center .. 0 edge
                    t *= fall;
                }

                if (t > 1e-6f)
                {
                    int idx = x + y * gs + z * gs * gs;
                    float cur = voxelData[idx].iso;
                    voxelData[idx].iso = Mathf.Lerp(cur, avgIso, t);
                }
            }
        }
    }

    voxelBuffer.SetData(voxelData);
}

        private void RemoveFoliageInsideBox(Vector3 centerGrid, Vector3 halfExtentsGrid, Quaternion rotation)
        {
            if (foliageParent == null) return;

            float scale = chunkSize / Mathf.Max(1f, (gridSize - 1f));
            Vector3 worldCenter = transform.position + centerGrid * scale;
            Vector3 worldHalf   = halfExtentsGrid * scale;

            Quaternion inv = Quaternion.Inverse(rotation);

            for (int i = foliageParent.childCount - 1; i >= 0; i--)
            {
                var child = foliageParent.GetChild(i);
                if (child.GetComponent<FoliageTag>() == null) continue;

                Vector3 local = inv * (child.position - worldCenter);
                Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

                if (a.x <= worldHalf.x && a.y <= worldHalf.y && a.z <= worldHalf.z)
                {
                    Destroy(child.gameObject);
                }
            }
        }


        // -------------------- READ + BUILD MESH + (OPTIONAL) FOLIAGE --------------------
        public void ReadVerticesFromComputeShader()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            long tStart = stopwatch.ElapsedTicks;

            int kernelIndex = marchingCubesShader.FindKernel("March");
            long tAfterFindKernel = stopwatch.ElapsedTicks;

            triangleBuffer.SetCounterValue(0);
            marchingCubesShader.SetBuffer(kernelIndex, "triangles", triangleBuffer);
            marchingCubesShader.SetBuffer(kernelIndex, "voxels", voxelBuffer);
            marchingCubesShader.SetInt("numPointsPerAxis", gridSize);
            marchingCubesShader.SetFloat("isoLevel", isoLevel);
            marchingCubesShader.SetFloat("chunkSize", chunkSize);

            long tBeforeDispatch = stopwatch.ElapsedTicks;
            marchingCubesShader.Dispatch(kernelIndex, gridSize / 4, gridSize / 4, gridSize / 4);
            long tAfterDispatch = stopwatch.ElapsedTicks;

            ComputeBuffer.CopyCount(triangleBuffer, counterBuffer, 0);
            int[] triCountArray = { 0 };
            counterBuffer.GetData(triCountArray);
            int numTris = triCountArray[0];

            long tAfterCount = stopwatch.ElapsedTicks;

            Triangle[] tris = new Triangle[numTris];
            triangleBuffer.GetData(tris, 0, 0, numTris);

            long tAfterGetTriangles = stopwatch.ElapsedTicks;

            var vertices = new Vector3[numTris * 3];
            var normals = new Vector3[numTris * 3];
            var meshTriangles = new int[numTris * 3];
            var uvs1 = new Vector3[numTris * 3];
            var uvs2 = new Vector3[numTris * 3];
            var colors = new Color[numTris * 3];

            int[] type = new int[3];
            float[] progress = new float[3];
            Color[] blend = new Color[]
            {
                new Color(1f, 0f, 0f),
                new Color(0f, 1f, 0f),
                new Color(0f, 0f, 1f)
            };

            // ---- FOLIAGE PROBABILITY + ANGLE GATE ----
            float spawnProbPerVertex = foliageTargetsPerArea / (gridSize * gridSize);
            float cosMaxSlope = Mathf.Cos(foliageMaxSlopeDeg * Mathf.Deg2Rad);

            for (int i = 0; i < numTris; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    var vert = tris[i].GetVertex(j);
                    int vi = i * 3 + j;

                    meshTriangles[vi] = vi;
                    vertices[vi] = vert.position;
                    normals[vi] = vert.normal;

                    type[j] = vert.data;
                    progress[j] = vert.breakingProgress;

                    if (!foliageInitialized && vert.data == (int)TerrainType.Grass)
                    {
                        Vector3 n = vert.normal.normalized;
                        if (Vector3.Dot(n, Vector3.up) >= cosMaxSlope)
                        {
                            var worldPos = transform.position + vert.position;
                            uint seed = Hash(worldPos);
                            if (Next01(ref seed) < spawnProbPerVertex)
                            {
                                SpawnFoliage(worldPos, ref seed);
                            }
                        }
                    }
                }

                for (int j = 0; j < 3; j++)
                {
                    int vi = i * 3 + j;
                    uvs1[vi] = new Vector3(type[0], type[1], type[2]);
                    uvs2[vi] = new Vector3(progress[0], progress[1], progress[2]);
                    colors[vi] = blend[j];
                }
            }
            foliageInitialized = true;

            long tAfterLoop = stopwatch.ElapsedTicks;

            Mesh mesh = new Mesh();
            mesh.indexFormat = (vertices.Length > 65535)
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = meshTriangles;
            mesh.colors = colors;
            mesh.SetUVs(2, new System.Collections.Generic.List<Vector3>(uvs1));
            mesh.SetUVs(3, new System.Collections.Generic.List<Vector3>(uvs2));

            meshFilter.mesh = mesh;
            meshCollider.sharedMesh = mesh;

            long tEnd = stopwatch.ElapsedTicks;
        }

        // -------------------- FOLIAGE HELPER --------------------
        private void SpawnFoliage(Vector3 worldPos, ref uint seed)
        {
            if (foliagePrefabs == null || foliagePrefabs.Length == 0) return;

            int idx = NextRange(ref seed, 0, foliagePrefabs.Length);
            var prefab = foliagePrefabs[idx];

            // --- Position jitter (XY plane, keep Z up) ---
            // small, deterministic offset so spawns don't look like a perfect grid
            float jx = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
            float jy = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
            Vector3 jitteredPos = worldPos + new Vector3(jx, 0f, jy);

            // --- Rotation jitter ---
            // Yaw: full spin by default (tunable), Tilt: gentle random lean
            float yaw  = (yawJitterDeg <= 0f)  ? 0f : Next01(ref seed) * yawJitterDeg;
            float pitch = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
            float roll  = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
            Quaternion rot = Quaternion.Euler(pitch, yaw, roll);

            // --- Scale jitter (uniform) ---
            float sMin = Mathf.Min(uniformScaleRange.x, uniformScaleRange.y);
            float sMax = Mathf.Max(uniformScaleRange.x, uniformScaleRange.y);
            float s = sMin + Next01(ref seed) * (sMax - sMin);

            var parent = foliageParent != null ? foliageParent : transform;
            var go = Instantiate(prefab, jitteredPos, rot, parent);

            // Apply uniform scale jitter on top of prefab scale
            go.transform.localScale = go.transform.localScale * s;

            // Keep your tag so we can remove these later if needed
            if (!go.TryGetComponent<FoliageTag>(out _))
                go.AddComponent<FoliageTag>();
        }


        // <<< ADDED >>> remove foliage inside edit sphere
        private void RemoveFoliageInsideSphere(Vector3 centerGrid, float radiusGrid)
        {
            if (foliageParent == null) return;

            float scale = chunkSize / Mathf.Max(1f, (gridSize - 1f));
            Vector3 worldCenter = transform.position + centerGrid * scale;
            float radiusWorld = radiusGrid * scale;
            float radiusWorldSqr = radiusWorld * radiusWorld;

            for (int i = foliageParent.childCount - 1; i >= 0; i--)
            {
                var child = foliageParent.GetChild(i);
                if (child.GetComponent<FoliageTag>() == null) continue;

                if ((child.position - worldCenter).sqrMagnitude <= radiusWorldSqr)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        // <<< ADDED >>> marker so we know what to delete
        private class FoliageTag : MonoBehaviour {}

        // ---------- DETERMINISTIC HASH + PRNG ----------
        static uint Hash(Vector3 p)
        {
            const float quant = 0.25f;
            int xi = Mathf.FloorToInt(p.x / quant);
            int yi = Mathf.FloorToInt(p.y / quant);
            int zi = Mathf.FloorToInt(p.z / quant);

            uint h = 2166136261u;
            unchecked
            {
                h = (h ^ (uint)xi) * 16777619u;
                h = (h ^ (uint)yi) * 16777619u;
                h = (h ^ (uint)zi) * 16777619u;
                h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            }
            return h;
        }

        static float Next01(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0x00FFFFFF) / 16777216f;
        }

        static int NextRange(ref uint state, int minInclusive, int maxExclusive)
        {
            float r = Next01(ref state);
            return minInclusive + Mathf.FloorToInt(r * (maxExclusive - minInclusive));
        }

        // -------------------- STRUCTS --------------------
        struct Triangle
        {
            public Vertex a;
            public Vertex b;
            public Vertex c;

            public Vertex GetVertex(int index)
            {
                return index switch
                {
                    0 => a,
                    1 => b,
                    2 => c,
                    _ => throw new System.IndexOutOfRangeException("Triangle only has 3 vertices: 0, 1, 2")
                };
            }
        }

        public struct Vertex
        {
            public Vector3 position;
            public Vector3 normal;
            public int data; // terrain type id
            public float breakingProgress;
        }

        struct Voxel
        {
            public TerrainType type;
            public float iso;
            public float breakingProgress;

            public Voxel(TerrainType type, float iso, float breakingProgress)
            {
                this.type = type;
                this.iso = iso;
                this.breakingProgress = breakingProgress;
            }
        }
    }
