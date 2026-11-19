using g4;
using System;
using System.Collections.Generic;
using TileVox.Meshing;

namespace WorldFlow
{
    /// <summary>Creates the indexed triangle mesh, optionally welding and smoothing compatible vertices.</summary>
    public class ModifierWelder
    {
        private struct CellKey : IEquatable<CellKey>
        {
            public long x, y, z;

            public CellKey(Vector3d position, double size)
            {
                x = (long)Math.Floor(position.x / size);
                y = (long)Math.Floor(position.y / size);
                z = (long)Math.Floor(position.z / size);
            }

            public CellKey(long x, long y, long z) { this.x = x; this.y = y; this.z = z; }
            public bool Equals(CellKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = x.GetHashCode();
                    hash = hash * 31 + y.GetHashCode();
                    return hash * 31 + z.GetHashCode();
                }
            }
        }

        private class VertexEntry
        {
            public int id;
            public Vector3d position, normal, normalSum;
            public Vector2f uv;
            public uint color;
        }

        public bool enable;
        public double weldDistance = 0.01;
        public double normalFilter = 0.5;
        public IList<EdgeSelection> edgeSets;

        /// <summary>Always builds a DMesh3. Enabled welding also merges nearby compatible vertices and smooths normals.</summary>
        public StructureMesherResult3D Apply(StructureMesherResult3D result)
            => new BuildState(enable, weldDistance, normalFilter, edgeSets).Build(result);

        private class BuildState
        {
            private DMesh3 mesh;
            private Dictionary<uint, int> groups;
            private Dictionary<CellKey, List<int>> buckets;
            private List<VertexEntry> vertices;
            private List<byte> vertexAlpha;
            private double cellSize;
            private readonly bool enable;
            private readonly double weldDistance, normalFilter;
            private readonly IList<EdgeSelection> edgeSets;

            public BuildState(bool enable, double weldDistance, double normalFilter, IList<EdgeSelection> edgeSets)
            {
                this.enable = enable;
                this.weldDistance = weldDistance;
                this.normalFilter = normalFilter;
                this.edgeSets = edgeSets;
            }

            public StructureMesherResult3D Build(StructureMesherResult3D result)
            {
                if (result == null) throw new ArgumentNullException(nameof(result));
                mesh = new DMesh3(true, true, true, true);
                groups = new Dictionary<uint, int>();
                buckets = new Dictionary<CellKey, List<int>>();
                vertices = new List<VertexEntry>();
                vertexAlpha = new List<byte>();
                cellSize = enable ? Math.Max(0.000001, weldDistance) : 1;
                if (enable && edgeSets != null)
                    foreach (var set in edgeSets) cellSize = Math.Max(cellSize, set.parameter);
    
                if (result.Quads != null)
                    foreach (var face in result.Quads)
                        AddFace(face.a, face.b, face.c, face.d, true, face.color, face.material);
                if (result.Triangles != null)
                    foreach (var face in result.Triangles)
                        AddFace(face.a, face.b, face.c, Vector3d.Zero, false, face.color, face.material);
                if (result.Ngons != null)
                    foreach (var polygon in result.Ngons)
                        foreach (var face in polygon.Triangulate())
                            AddFace(face.a, face.b, face.c, Vector3d.Zero, false,
                                face.color, face.material);
    
                foreach (var entry in vertices)
                {
                    Vector3d normal = entry.normalSum;
                    double distance = GetWeldDistance(entry.position);
                    if (enable && distance > 0)
                    {
                        CellKey cell = new CellKey(entry.position, cellSize);
                        for (int z = -1; z <= 1; z++)
                            for (int y = -1; y <= 1; y++)
                                for (int x = -1; x <= 1; x++)
                                    if (buckets.TryGetValue(new CellKey(cell.x + x, cell.y + y, cell.z + z), out var candidates))
                                        foreach (int index in candidates)
                                        {
                                            var other = vertices[index];
                                            if (other.id != entry.id &&
                                                (entry.position - other.position).LengthSquared <= distance * distance &&
                                                Vector3d.Dot(entry.normal, other.normal) >= normalFilter)
                                                normal += other.normalSum;
                                        }
                    }
                    normal = normal.Normalized;
                    mesh.SetVertexNormal(entry.id, new Vector3f((float)normal.x, (float)normal.y, (float)normal.z));
                }
    
                result.Mesh = mesh;
                result.MaterialGroups = groups;
                result.VertexAlpha = vertexAlpha;
                return result;
            }

            private void AddFace(Vector3d a, Vector3d b, Vector3d c, Vector3d d,
                bool quad, uint color, uint material)
            {
                Vector3d normal = Vector3d.Cross(b - a, c - a).Normalized;
                if (normal.LengthSquared < 1e-16) return;
                if (quad)
                {
                    Vector3d secondNormal = Vector3d.Cross(c - a, d - a).Normalized;
                    if (Vector3d.Dot(normal, secondNormal) < 0.999999)
                    {
                        AddFace(a, b, c, Vector3d.Zero, false, color, material);
                        AddFace(a, c, d, Vector3d.Zero, false, color, material);
                        return;
                    }
                }
                if (!groups.TryGetValue(material, out int group))
                {
                    group = groups.Count;
                    groups.Add(material, group);
                }
                int first = AddVertex(a, normal, color);
                int second = AddVertex(b, normal, color);
                int third = AddVertex(c, normal, color);
                AppendFaceTriangle(first, second, third, a, b, c, normal, color, group);
                if (quad)
                {
                    int fourth = AddVertex(d, normal, color);
                    AppendFaceTriangle(first, third, fourth, a, c, d, normal, color, group);
                }
            }

            private void AppendFaceTriangle(int first, int second, int third,
                Vector3d a, Vector3d b, Vector3d c, Vector3d normal, uint color, int group)
            {
                if (first == second || first == third || second == third ||
                    mesh.AppendTriangle(first, second, third, group) < 0)
                {
                    first = AddIsolated(a, normal, color);
                    second = AddIsolated(b, normal, color);
                    third = AddIsolated(c, normal, color);
                    mesh.AppendTriangle(first, second, third, group);
                }
            }
    
            private int AddVertex(Vector3d position, Vector3d normal, uint color)
            {
                Vector2f uv = GetUV(position, normal);
                double distance = GetWeldDistance(position);
                CellKey cell = new CellKey(position, cellSize);
                int radius = enable && distance > 0 ? 1 : 0;
                for (int z = -radius; z <= radius; z++)
                    for (int y = -radius; y <= radius; y++)
                        for (int x = -radius; x <= radius; x++)
                            if (buckets.TryGetValue(new CellKey(cell.x + x, cell.y + y, cell.z + z), out var candidates))
                                foreach (int index in candidates)
                                {
                                    var candidate = vertices[index];
                                    if (candidate.color != color || candidate.uv.x != uv.x || candidate.uv.y != uv.y)
                                        continue;
                                    double squared = (candidate.position - position).LengthSquared;
                                    if (squared < 1e-20 && Vector3d.Dot(candidate.normal, normal) > 0.999999 ||
                                        enable && distance > 0 && squared <= distance * distance &&
                                        Vector3d.Dot(candidate.normal, normal) >= normalFilter)
                                    {
                                        candidate.normalSum += normal;
                                        return candidate.id;
                                    }
                                }
    
                int id = Append(position, normal, color, uv);
                var entry = new VertexEntry { id = id, position = position, normal = normal,
                    normalSum = normal, uv = uv, color = color };
                if (!buckets.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    buckets.Add(cell, list);
                }
                list.Add(vertices.Count);
                vertices.Add(entry);
                return id;
            }
    
            private int AddIsolated(Vector3d position, Vector3d normal, uint color) =>
                Append(position, normal, color, GetUV(position, normal));
    
            private int Append(Vector3d position, Vector3d normal, uint color, Vector2f uv)
            {
                var rgb = new Vector3f(((color >> 24) & 255) / 255f,
                    ((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f);
                int id = mesh.AppendVertex(new NewVertexInfo(position,
                    new Vector3f((float)normal.x, (float)normal.y, (float)normal.z), rgb, uv));
                while (vertexAlpha.Count <= id) vertexAlpha.Add(255);
                vertexAlpha[id] = (byte)color;
                return id;
            }
    
            private static Vector2f GetUV(Vector3d point, Vector3d normal)
            {
                double x = Math.Abs(normal.x), y = Math.Abs(normal.y), z = Math.Abs(normal.z);
                return x >= y && x >= z ? new Vector2f((float)point.y, (float)point.z)
                    : y >= z ? new Vector2f((float)point.z, (float)point.x)
                    : new Vector2f((float)point.x, (float)point.y);
            }
    
            private double GetWeldDistance(Vector3d point)
            {
                if (!enable) return 0;
                if (edgeSets == null || edgeSets.Count == 0) return Math.Max(0, weldDistance);
                double distance = 0;
                foreach (var set in edgeSets)
                    if (set.edges != null && set.parameter > distance)
                        foreach (var edge in set.edges)
                        {
                            Vector3d line = edge.b - edge.a;
                            double lengthSquared = line.LengthSquared;
                            if (lengthSquared < 1e-20) continue;
                            double t = Math.Max(0, Math.Min(1, Vector3d.Dot(point - edge.a, line) / lengthSquared));
                            if ((point - (edge.a + line * t)).LengthSquared <= set.parameter * set.parameter)
                            {
                                distance = set.parameter;
                                break;
                            }
                        }
                return distance;
            }
        }
    }
}
