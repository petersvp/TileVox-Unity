using g4;
using System;
using System.Collections.Generic;
using TileVox.Meshing;
using UnityEngine;
using UnityEngine.Rendering;

namespace TileVoxUnity
{
    /// <summary>Converts TileVox mesher output into Unity meshes.</summary>
    public static class MeshUtils
    {
        /// <summary>Converts block faces into a Unity triangle mesh.</summary>
        public static Mesh ToUnityMesh(StructureMesherResult3D result) => ToUnityTriangleMesh(result, out _);

        /// <summary>Converts block faces and returns material IDs in submesh order.</summary>
        public static Mesh ToUnityMesh(StructureMesherResult3D result, out uint[] materialIds) =>
            ToUnityTriangleMesh(result, out materialIds);

        /// <summary>Triangulates position-based block quads without welding their vertices.</summary>
        public static Mesh ToUnityTriangleMesh(StructureMesherResult3D result) => ToUnityTriangleMesh(result, out _);

        /// <summary>Triangulates block quads and returns numeric material IDs in submesh order.</summary>
        public static Mesh ToUnityTriangleMesh(StructureMesherResult3D result, out uint[] materialIds)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.Mesh != null) return ToUnityIndexedMesh(result, out materialIds);
            int vertexCapacity = (result.Quads?.Count ?? 0) * 4 + (result.Triangles?.Count ?? 0) * 3;
            if (result.Ngons != null)
                foreach (var polygon in result.Ngons)
                    vertexCapacity += Math.Max(0, (polygon.points?.Count ?? 0) - 2) * 3;
            var vertices = new List<Vector3>(vertexCapacity);
            var normals = new List<Vector3>(vertexCapacity);
            var tangents = new List<Vector4>(vertexCapacity);
            var colors = new List<Color32>(vertexCapacity);
            var uvs = new List<Vector2>(vertexCapacity);
            var submeshes = new Dictionary<uint, List<int>>();

            if (result.Quads != null)
            foreach (var quad in result.Quads)
            {
                var a = new Vector3((float)quad.a.x, (float)quad.a.y, (float)quad.a.z);
                var b = new Vector3((float)quad.b.x, (float)quad.b.y, (float)quad.b.z);
                var c = new Vector3((float)quad.c.x, (float)quad.c.y, (float)quad.c.z);
                var d = new Vector3((float)quad.d.x, (float)quad.d.y, (float)quad.d.z);
                Vector3 normal = Vector3.Cross(b - a, d - a).normalized;
                bool xFace = Mathf.Abs(normal.x) > 0.5f;
                bool yFace = Mathf.Abs(normal.y) > 0.5f;
                Vector3 tangent = xFace ? Vector3.up : yFace ? Vector3.forward : Vector3.right;
                float handedness = xFace ? Mathf.Sign(normal.x) : yFace ? Mathf.Sign(normal.y) : Mathf.Sign(normal.z);
                var vertexTangent = new Vector4(tangent.x, tangent.y, tangent.z, handedness);
                var color = new Color32((byte)(quad.color >> 24), (byte)(quad.color >> 16),
                    (byte)(quad.color >> 8), (byte)quad.color);
                int first = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                vertices.Add(d);
                for (int i = 0; i < 4; i++)
                {
                    normals.Add(normal);
                    tangents.Add(vertexTangent);
                    colors.Add(color);
                }
                uvs.Add(xFace ? new Vector2(a.y, a.z) : yFace ? new Vector2(a.z, a.x) : new Vector2(a.x, a.y));
                uvs.Add(xFace ? new Vector2(b.y, b.z) : yFace ? new Vector2(b.z, b.x) : new Vector2(b.x, b.y));
                uvs.Add(xFace ? new Vector2(c.y, c.z) : yFace ? new Vector2(c.z, c.x) : new Vector2(c.x, c.y));
                uvs.Add(xFace ? new Vector2(d.y, d.z) : yFace ? new Vector2(d.z, d.x) : new Vector2(d.x, d.y));

                if (!submeshes.TryGetValue(quad.material, out var indices))
                {
                    indices = new List<int>();
                    submeshes.Add(quad.material, indices);
                }
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 3);
            }

            foreach (var triangle in EnumerateTriangles(result))
                {
                    var a = new Vector3((float)triangle.a.x, (float)triangle.a.y, (float)triangle.a.z);
                    var b = new Vector3((float)triangle.b.x, (float)triangle.b.y, (float)triangle.b.z);
                    var c = new Vector3((float)triangle.c.x, (float)triangle.c.y, (float)triangle.c.z);
                    Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                    bool xFace = Mathf.Abs(normal.x) >= Mathf.Abs(normal.y) && Mathf.Abs(normal.x) >= Mathf.Abs(normal.z);
                    bool yFace = !xFace && Mathf.Abs(normal.y) >= Mathf.Abs(normal.z);
                    Vector3 tangent = xFace ? Vector3.up : yFace ? Vector3.forward : Vector3.right;
                    float handedness = xFace ? Mathf.Sign(normal.x) : yFace ? Mathf.Sign(normal.y) : Mathf.Sign(normal.z);
                    var vertexTangent = new Vector4(tangent.x, tangent.y, tangent.z, handedness);
                    var color = new Color32((byte)(triangle.color >> 24), (byte)(triangle.color >> 16),
                        (byte)(triangle.color >> 8), (byte)triangle.color);
                    int first = vertices.Count;
                    vertices.Add(a);
                    vertices.Add(b);
                    vertices.Add(c);
                    for (int i = 0; i < 3; i++)
                    {
                        normals.Add(normal);
                        tangents.Add(vertexTangent);
                        colors.Add(color);
                    }
                    uvs.Add(xFace ? new Vector2(a.y, a.z) : yFace ? new Vector2(a.z, a.x) : new Vector2(a.x, a.y));
                    uvs.Add(xFace ? new Vector2(b.y, b.z) : yFace ? new Vector2(b.z, b.x) : new Vector2(b.x, b.y));
                    uvs.Add(xFace ? new Vector2(c.y, c.z) : yFace ? new Vector2(c.z, c.x) : new Vector2(c.x, c.y));
                    if (!submeshes.TryGetValue(triangle.material, out var indices))
                    {
                        indices = new List<int>();
                        submeshes.Add(triangle.material, indices);
                    }
                    indices.Add(first);
                    indices.Add(first + 1);
                    indices.Add(first + 2);
                }

            var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            materialIds = new uint[submeshes.Count];
            submeshes.Keys.CopyTo(materialIds, 0);
            Array.Sort(materialIds);
            for (int i = 0; i < materialIds.Length; i++)
                mesh.SetIndices(submeshes[materialIds[i]], MeshTopology.Triangles, i, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static IEnumerable<TriangleFace> EnumerateTriangles(StructureMesherResult3D result)
        {
            if (result.Triangles != null)
                foreach (var face in result.Triangles) yield return face;
            if (result.Ngons != null)
                foreach (var polygon in result.Ngons)
                    foreach (var face in polygon.Triangulate()) yield return face;
        }

        private static Mesh ToUnityIndexedMesh(StructureMesherResult3D result, out uint[] materialIds)
        {
            DMesh3 source = result.Mesh;
            var indicesByVertex = new Dictionary<int, int>(source.VertexCount);
            var vertices = new List<Vector3>(source.VertexCount);
            var normals = new List<Vector3>(source.VertexCount);
            var colors = new List<Color32>(source.VertexCount);
            var uvs = new List<Vector2>(source.VertexCount);
            foreach (int id in source.VertexIndices())
            {
                indicesByVertex.Add(id, vertices.Count);
                Vector3d point = source.GetVertex(id);
                Vector3f normal = source.GetVertexNormal(id);
                Vector3f color = source.GetVertexColor(id);
                Vector2f uv = source.GetVertexUV(id);
                vertices.Add(new Vector3((float)point.x, (float)point.y, (float)point.z));
                normals.Add(new Vector3(normal.x, normal.y, normal.z));
                byte alpha = result.VertexAlpha != null && id < result.VertexAlpha.Count
                    ? result.VertexAlpha[id] : (byte)255;
                colors.Add(new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(color.x) * 255),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(color.y) * 255),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(color.z) * 255), alpha));
                uvs.Add(new Vector2(uv.x, uv.y));
            }

            var materialByGroup = new Dictionary<int, uint>();
            if (result.MaterialGroups != null)
                foreach (var entry in result.MaterialGroups) materialByGroup[entry.Value] = entry.Key;
            var submeshes = new Dictionary<uint, List<int>>();
            foreach (int id in source.TriangleIndices())
            {
                int group = source.GetTriangleGroup(id);
                uint material = materialByGroup.TryGetValue(group, out uint mapped) ? mapped : 0;
                if (!submeshes.TryGetValue(material, out var indices))
                {
                    indices = new List<int>();
                    submeshes.Add(material, indices);
                }
                Index3i triangle = source.GetTriangle(id);
                indices.Add(indicesByVertex[triangle.a]);
                indices.Add(indicesByVertex[triangle.b]);
                indices.Add(indicesByVertex[triangle.c]);
            }

            var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            materialIds = new uint[submeshes.Count];
            submeshes.Keys.CopyTo(materialIds, 0);
            Array.Sort(materialIds);
            for (int i = 0; i < materialIds.Length; i++)
                mesh.SetIndices(submeshes[materialIds[i]], MeshTopology.Triangles, i, false);
            if (vertices.Count > 0) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
