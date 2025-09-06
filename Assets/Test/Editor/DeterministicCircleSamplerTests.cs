using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class DeterministicCircleSamplerTests
{
    [Test]
    public void SameInputs_ProduceIdenticalOutputs()
    {
        var c = new Vector3(5f, 2f, -3f);
        float r = 7.5f;
        float chunkSize = 0.4f;
        int seed = 42;
        int pointsPerChunk = 16;

        var a = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, pointsPerChunk);
        var b = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, pointsPerChunk);

        Assert.AreEqual(a.Count, b.Count, "Counts differ for identical inputs.");

        for (int i = 0; i < a.Count; i++)
        {
            Assert.AreEqual(a[i], b[i], $"Point {i} differs.");
        }
    }

    [Test]
    public void DifferentSeed_UsuallyChangesOutput()
    {
        var c = Vector3.zero;
        float r = 5f;
        float chunkSize = 0.5f;

        var a = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, 1, 16);
        var b = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, 2, 16);

        // Not guaranteed, but very likely different
        if (a.Count != b.Count)
        {
            Assert.AreNotEqual(a.Count, b.Count, "Counts unexpectedly equal for different seeds (rare).");
        }
        else
        {
            bool anyDifferent = false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) { anyDifferent = true; break; }
            }
            Assert.IsTrue(anyDifferent, "All positions equal for different seeds (very unlikely).");
        }
    }

    [Test]
    public void AllPoints_AreInsideCircle()
    {
        var c = new Vector3(0f, 0f, 0f);
        float r = 10f;
        float chunkSize = 0.8f;
        int seed = 17;

        var pts = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, 16);
        foreach (var p in pts)
        {
            var d2 = (p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z);
            Assert.LessOrEqual(d2, r * r + 1e-5f, "Point outside circle.");
        }
    }

    [Test]
    public void Count_ScalesWithRadius()
    {
        var c = Vector3.zero;
        float chunkSize = 0.75f;
        int seed = 999;

        var small = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, 4f, chunkSize, seed, 16);
        var large = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, 8f, chunkSize, seed, 16);

        Assert.GreaterOrEqual(large.Count, small.Count, "Larger radius should not yield fewer points.");
    }

    [Test]
    public void PointsPerChunk_ControlsCountApproximately()
    {
        var c = Vector3.zero;
        float r = 12f;          // big enough to include many chunks
        float chunkSize = 1.0f;
        int seed = 2024;

        var few = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, 4);
        var many = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, 16);

        // Expect roughly 4x points; allow generous tolerance due to edge rejection near circle boundary.
        float ratio = (few.Count == 0) ? float.PositiveInfinity : (float)many.Count / few.Count;
        Assert.Greater(ratio, 2.5f, $"Expected noticeably more points when pointsPerChunk increases. Ratio was {ratio:F2}.");
    }

    [Test]
    public void ZeroPointsPerChunk_YieldsNoPoints()
    {
        var c = Vector3.zero;
        float r = 10f;
        float chunkSize = 1.0f;
        int seed = 7;

        var pts = DeterministicCircleSampler.GeneratePointsInCircleXZ(c, r, chunkSize, seed, 0);
        Assert.AreEqual(0, pts.Count, "pointsPerChunk = 0 should yield zero points.");
    }

    [Test]
    public void OverlappingCircles_HaveSamePointsInIntersection()
    {
        float r = 5f;
        float chunkSize = 0.75f;
        int seed = 1234;
        int pointsPerChunk = 16;

        var circleA = new Vector3(0f, 0f, 0f);
        var circleB = new Vector3(3f, 0f, 0f); // shifted, overlapping with A

        var ptsA = DeterministicCircleSampler.GeneratePointsInCircleXZ(circleA, r, chunkSize, seed, pointsPerChunk);
        var ptsB = DeterministicCircleSampler.GeneratePointsInCircleXZ(circleB, r, chunkSize, seed, pointsPerChunk);

        // Collect points from A that also lie inside B
        var intersectionA = new HashSet<Vector3>();
        foreach (var p in ptsA)
        {
            float dB = (p.x - circleB.x) * (p.x - circleB.x) +
                       (p.z - circleB.z) * (p.z - circleB.z);
            if (dB <= r * r + 1e-5f)
            {
                intersectionA.Add(p);
            }
        }

        // Check they appear in B’s set too
        foreach (var p in intersectionA)
        {
            Assert.IsTrue(ptsB.Contains(p),
                $"Point {p} from circle A intersection missing in circle B");
        }

        // And symmetric
        foreach (var p in ptsB)
        {
            float dA = (p.x - circleA.x) * (p.x - circleA.x) +
                       (p.z - circleA.z) * (p.z - circleA.z);
            if (dA <= r * r + 1e-5f)
            {
                Assert.IsTrue(ptsA.Contains(p),
                    $"Point {p} from circle B intersection missing in circle A");
            }
        }
    }
}
