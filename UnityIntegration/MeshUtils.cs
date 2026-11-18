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
            var vertices = new List<Vector3>(result.Quads.Count * 4);
            var normals = new List<Vector3>(result.Quads.Count * 4);
            var tangents = new List<Vector4>(result.Quads.Count * 4);
            var colors = new List<Color32>(result.Quads.Count * 4);
            var uvs = new List<Vector2>(result.Quads.Count * 4);
            var submeshes = new Dictionary<uint, List<int>>();

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
    }
}
