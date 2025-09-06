using System;
using System.Collections.Generic;
using UnityEngine;

public static class DeterministicCircleSampler
{
    /// <summary>
    /// Deterministically generate points inside a circle on the XZ plane using a world-aligned chunk grid.
    /// - Chunk is just a size unit (chunkSize x chunkSize).
    /// - Expected points per fully-covered chunk is pointsPerChunk (can be fractional, e.g. 0.3).
    /// - Uses N = ceil(pointsPerChunk) candidate positions per chunk, each kept with prob p = pointsPerChunk / N.
    /// - Candidates are uniform within the chunk; points outside the circle are rejected.
    /// - Stateless per-candidate hashing (seed, chunkX, chunkZ, candidateIndex) => stable overlaps.
    /// </summary>
    /// <param name="center">Circle center (Y is preserved on returned points).</param>
    /// <param name="radius">Circle radius.</param>
    /// <param name="chunkSize">Chunk size (must be > 0).</param>
    /// <param name="seed">Deterministic seed.</param>
    /// <param name="pointsPerChunk">Expected points per fully covered chunk (can be fractional).</param>
    public static List<Vector3> GeneratePointsInCircleXZ(
        Vector3 center,
        float radius,
        float chunkSize,
        int seed,
        float pointsPerChunk = 16f)
    {
        if (radius <= 0f) throw new ArgumentOutOfRangeException(nameof(radius), "radius must be > 0");
        if (chunkSize <= 0f) throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be > 0");
        if (pointsPerChunk < 0f) throw new ArgumentOutOfRangeException(nameof(pointsPerChunk), "pointsPerChunk must be >= 0");

        var points = new List<Vector3>();
        if (pointsPerChunk == 0f) return points; // quick out

        // World-space bounds
        float minX = center.x - radius;
        float maxX = center.x + radius;
        float minZ = center.z - radius;
        float maxZ = center.z + radius;

        // world-aligned chunk indices
        int startChunkX = Mathf.FloorToInt(minX / chunkSize);
        int startChunkZ = Mathf.FloorToInt(minZ / chunkSize);
        float startX = startChunkX * chunkSize;
        float startZ = startChunkZ * chunkSize;

        int chunksX = Mathf.CeilToInt((maxX - startX) / chunkSize);
        int chunksZ = Mathf.CeilToInt((maxZ - startZ) / chunkSize);

        float halfDiag = 0.5f * chunkSize * 1.41421356237f; // chunk half diagonal

        // Candidate setup for fractional expected counts
        int candidatesPerChunk = Mathf.CeilToInt(pointsPerChunk);
        float keepProb = pointsPerChunk / candidatesPerChunk; // in (0,1], unless pointsPerChunk==0 (already handled)

        for (int iz = 0; iz <= chunksZ; iz++)
        {
            int czIndex = startChunkZ + iz;
            float z0 = startZ + iz * chunkSize;   // chunk min z
            float zC = z0 + 0.5f * chunkSize;     // chunk center z

            for (int ix = 0; ix <= chunksX; ix++)
            {
                int cxIndex = startChunkX + ix;
                float x0 = startX + ix * chunkSize;   // chunk min x
                float xC = x0 + 0.5f * chunkSize;     // chunk center x

                // quick overlap test: if chunk center is too far from the circle, skip
                float dx = xC - center.x;
                float dz = zC - center.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > radius + halfDiag) continue;

                // Stateless "RNG" base for this chunk
                int chunkKey = HashInts(seed, cxIndex, czIndex);

                // Generate N candidates; keep with probability keepProb if inside the circle
                for (int i = 0; i < candidatesPerChunk; i++)
                {
                    // independent hashed values for x, z, and gate
                    float rx = HashTo01(HashInts(chunkKey, i, 0x0A1B2C3D));
                    float rz = HashTo01(HashInts(chunkKey, i, 0x1F2E3D4C));
                    float gate = HashTo01(HashInts(chunkKey, i, 0x13579BDF)); // keep threshold

                    float px = x0 + rx * chunkSize;
                    float pz = z0 + rz * chunkSize;

                    // inside the circle?
                    float pdx = px - center.x;
                    float pdz = pz - center.z;
                    bool inside = (pdx * pdx + pdz * pdz) <= radius * radius + 1e-6f;

                    if (inside && gate < keepProb)
                    {
                        points.Add(new Vector3(px, center.y, pz));
                    }
                }
            }
        }

        return points;
    }

    /// <summary>Mix (seed, a, b) into a 32-bit value.</summary>
    private static int HashInts(int seed, int a, int b)
    {
        unchecked
        {
            int h = unchecked((int)0x9E3779B9);
            h ^= seed + unchecked((int)0x85EBCA6B) + (h << 6) + (h >> 2);
            h ^= a * unchecked((int)0x27D4EB2D) + (h << 6) + (h >> 2);
            h ^= b * unchecked((int)0x165667B1) + (h << 6) + (h >> 2);
            return h; // full 32-bit range
        }
    }

    /// <summary>Map 32-bit int to [0,1) as a float, deterministically.</summary>
    private static float HashTo01(int h)
    {
        uint u = unchecked((uint)h);
        // 1/2^32 = 2.3283064365386963e-10f
        return u * 2.3283064365386963e-10f; // in [0,1)
    }
}
