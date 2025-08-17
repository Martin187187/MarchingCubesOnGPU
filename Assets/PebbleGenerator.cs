using System.Collections.Generic;
using UnityEngine;
using System.Linq;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class PebbleGenerator : MonoBehaviour
{
    [SerializeField] int pointCount = 50;
    [SerializeField] float radius = 0.5f;
    [SerializeField] TerrainType type = TerrainType.Dirt;

    void Start()
    {
        var mesh = GeneratePebbleMesh(pointCount, radius);
        GetComponent<MeshFilter>().mesh = mesh;
        GetComponent<MeshCollider>().sharedMesh = mesh;
    }

    Mesh GeneratePebbleMesh(int count, float radius)
    {
        var points = new List<Vector3>();
        for (int i = 0; i < count; i++)
        {
            points.Add(Random.insideUnitSphere * radius * Random.Range(0.7f, 1.0f));
        }

        var hullFaces = QuickHull3D.Build(points);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var vertexMap = new Dictionary<Vector3, int>();
        var uvs1 = new List<Vector3>();

        foreach (var face in hullFaces)
        {
            for (int i = 0; i < 3; i++)
            {
                var v = face[i];
                if (!vertexMap.ContainsKey(v))
                {
                    vertexMap[v] = vertices.Count;
                    vertices.Add(v);
                    uvs1.Add(Vector3.one * (int)type);
                }
                triangles.Add(vertexMap[v]);
            }
        }

        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.SetUVs(2, new System.Collections.Generic.List<Vector3>(uvs1));
        return mesh;
    }

    class Face
    {
        public Vector3[] Vertices = new Vector3[3];
        public Vector3 Normal;

        public Face(Vector3 a, Vector3 b, Vector3 c)
        {
            Vertices[0] = a;
            Vertices[1] = b;
            Vertices[2] = c;
            Normal = Vector3.Cross(b - a, c - a).normalized;
        }

        public bool CanSee(Vector3 point)
        {
            return Vector3.Dot(Normal, point - Vertices[0]) > 1e-5f;
        }

        public Vector3 this[int i] => Vertices[i];
    }

    static class QuickHull3D
    {
        public static List<Face> Build(List<Vector3> points)
        {
            // Step 1: Find extreme points
            var minX = points.OrderBy(p => p.x).First();
            var maxX = points.OrderByDescending(p => p.x).First();

            var baseLine = maxX - minX;
            var maxDist = 0f;
            Vector3 third = Vector3.zero;

            foreach (var p in points)
            {
                var dist = Vector3.Cross(baseLine, p - minX).magnitude;
                if (dist > maxDist)
                {
                    maxDist = dist;
                    third = p;
                }
            }

            var planeNormal = Vector3.Cross(maxX - minX, third - minX).normalized;
            var fourth = points.OrderByDescending(p => Mathf.Abs(Vector3.Dot(planeNormal, p - minX))).First();

            var faces = new List<Face>();

            AddFace(faces, minX, maxX, third);
            AddFace(faces, minX, third, fourth);
            AddFace(faces, minX, fourth, maxX);
            AddFace(faces, maxX, fourth, third);

            var pending = new List<(Face, List<Vector3>)>();

            foreach (var face in faces.ToList())
            {
                var outside = points.Where(p => face.CanSee(p)).ToList();
                if (outside.Count > 0)
                    pending.Add((face, outside));
            }

            while (pending.Count > 0)
            {
                var (face, outsidePoints) = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);

                var eyePoint = outsidePoints.OrderByDescending(p => Vector3.Dot(face.Normal, p - face[0])).First();
                var visibleFaces = faces.Where(f => f.CanSee(eyePoint)).ToList();

                var borderEdges = FindHorizon(visibleFaces, eyePoint);

                foreach (var f in visibleFaces)
                    faces.Remove(f);

                foreach (var (a, b) in borderEdges)
                {
                    var newFace = new Face(a, b, eyePoint);
                    faces.Add(newFace);

                    var newOutside = outsidePoints.Where(p => newFace.CanSee(p) && p != eyePoint).ToList();
                    if (newOutside.Count > 0)
                        pending.Add((newFace, newOutside));
                }
            }

            return faces;
        }

        static void AddFace(List<Face> faces, Vector3 a, Vector3 b, Vector3 c)
        {
            var face = new Face(a, b, c);
            if (face.CanSee(Vector3.zero)) // Flip if needed
                face = new Face(a, c, b);
            faces.Add(face);
        }

        static List<(Vector3, Vector3)> FindHorizon(List<Face> visibleFaces, Vector3 eyePoint)
        {
            var edgeCount = new Dictionary<(Vector3, Vector3), int>();

            foreach (var face in visibleFaces)
            {
                for (int i = 0; i < 3; i++)
                {
                    var edge = (face[i], face[(i + 1) % 3]);
                    var reversed = (edge.Item2, edge.Item1);
                    if (edgeCount.ContainsKey(reversed))
                        edgeCount[reversed]++;
                    else if (edgeCount.ContainsKey(edge))
                        edgeCount[edge]++;
                    else
                        edgeCount[edge] = 1;
                }
            }

            return edgeCount.Where(e => e.Value == 1)
                            .Select(e => e.Key)
                            .ToList();
        }
    }
}
