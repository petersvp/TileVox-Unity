using g4;
using System;
using System.Collections.Generic;
using TileVox;
using UnityEngine;
using UnityEngine.Rendering;

namespace TileVoxUnity
{
    /// <summary>Converts TileVox mesher output into Unity meshes.</summary>
    public static class MeshUtils
    {
        /// <summary>Converts the geometry and its material groups into Unity submeshes, using triangles when a modifier supplies them.</summary>
        public static Mesh ToUnityMesh(MesherResult3D result) => ToUnityMesh(result, out _);

        /// <summary>Converts the geometry and returns each submesh's numeric material ID in submesh order.</summary>
        public static Mesh ToUnityMesh(MesherResult3D result, out uint[] materialIds)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.MeshQuads.Count > 0) return ToUnityTriangleMesh(result, out materialIds);

            DMesh3 source = result.Mesh;
            var vertices = new List<Vector3>(source.VertexCount);
            var normals = source.HasVertexNormals ? new List<Vector3>(source.VertexCount) : null;
            var colors = source.HasVertexColors ? new List<Color>(source.VertexCount) : null;
            var uvs = source.HasVertexUVs ? new List<Vector2>(source.VertexCount) : null;
            var tangents = result.VertexTangents.Count > 0 ? new List<Vector4>(source.VertexCount) : null;
            var vertexIndices = new Dictionary<int, int>(source.VertexCount);

            foreach (int vertexId in source.VertexIndices())
            {
                vertexIndices.Add(vertexId, vertices.Count);
                Vector3d position = source.GetVertex(vertexId);
                vertices.Add(new Vector3((float)position.x, (float)position.y, (float)position.z));

                if (normals != null)
                {
                    Vector3f normal = source.GetVertexNormal(vertexId);
                    normals.Add(new Vector3(normal.x, normal.y, normal.z));
                }
                if (colors != null)
                {
                    Vector3f color = source.GetVertexColor(vertexId);
                    float alpha = vertexId < result.VertexAlpha.Count ? result.VertexAlpha[vertexId] / 255f : 1f;
                    colors.Add(new Color(color.x, color.y, color.z, alpha));
                }
                if (uvs != null)
                {
                    Vector2f uv = source.GetVertexUV(vertexId);
                    uvs.Add(new Vector2(uv.x, uv.y));
                }
                if (tangents != null)
                {
                    if (vertexId >= result.VertexTangents.Count) tangents = null;
                    else
                    {
                        Vector4f tangent = result.VertexTangents[vertexId];
                        tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, tangent.w));
                    }
                }
            }

            var materialByGroup = new Dictionary<int, uint>(result.MaterialGroups.Count);
            foreach (var pair in result.MaterialGroups)
                materialByGroup[pair.Value] = pair.Key;

            bool useQuads = result.Quads.Count > 0 && source.TriangleCount == 0;
            var submeshByGroup = new Dictionary<int, int>();
            var submeshIndices = new List<List<int>>();
            var submeshMaterials = new List<uint>();
            if (useQuads)
            {
                foreach (var quad in result.Quads)
                {
                    if (!submeshByGroup.TryGetValue(quad.group, out int submesh))
                    {
                        submesh = submeshIndices.Count;
                        submeshByGroup.Add(quad.group, submesh);
                        submeshIndices.Add(new List<int>());
                        submeshMaterials.Add(materialByGroup.TryGetValue(quad.group, out uint materialId) ? materialId : 0);
                    }

                    var indices = submeshIndices[submesh];
                    indices.Add(vertexIndices[quad.a]);
                    indices.Add(vertexIndices[quad.b]);
                    indices.Add(vertexIndices[quad.c]);
                    indices.Add(vertexIndices[quad.d]);
                }
            }
            else
            {
                foreach (int triangleId in source.TriangleIndices())
                {
                    int group = source.HasTriangleGroups ? source.GetTriangleGroup(triangleId) : 0;
                    if (!submeshByGroup.TryGetValue(group, out int submesh))
                    {
                        submesh = submeshIndices.Count;
                        submeshByGroup.Add(group, submesh);
                        submeshIndices.Add(new List<int>());
                        submeshMaterials.Add(materialByGroup.TryGetValue(group, out uint materialId) ? materialId : 0);
                    }

                    Index3i triangle = source.GetTriangle(triangleId);
                    var indices = submeshIndices[submesh];
                    indices.Add(vertexIndices[triangle.a]);
                    indices.Add(vertexIndices[triangle.b]);
                    indices.Add(vertexIndices[triangle.c]);
                }
            }

            var mesh = new Mesh
            {
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            if (colors != null) mesh.SetColors(colors);
            if (uvs != null) mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshIndices.Count;
            MeshTopology topology = useQuads ? MeshTopology.Quads : MeshTopology.Triangles;
            for (int i = 0; i < submeshIndices.Count; i++)
                mesh.SetIndices(submeshIndices[i].ToArray(), topology, i, false);
            if (normals != null) mesh.SetNormals(normals);
            else mesh.RecalculateNormals();
            if (tangents != null) mesh.SetTangents(tangents);
            mesh.RecalculateBounds();

            materialIds = submeshMaterials.ToArray();
            return mesh;
        }

        /// <summary>Triangulates position-based block quads without welding their vertices.</summary>
        public static Mesh ToUnityTriangleMesh(MesherResult3D result) => ToUnityTriangleMesh(result, out _);

        /// <summary>Triangulates block quads and returns numeric material IDs in submesh order.</summary>
        public static Mesh ToUnityTriangleMesh(MesherResult3D result, out uint[] materialIds)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var vertices = new List<Vector3>(result.MeshQuads.Count * 4);
            var normals = new List<Vector3>(result.MeshQuads.Count * 4);
            var tangents = new List<Vector4>(result.MeshQuads.Count * 4);
            var colors = new List<Color32>(result.MeshQuads.Count * 4);
            var uvs = new List<Vector2>(result.MeshQuads.Count * 4);
            var submeshes = new Dictionary<uint, List<int>>();

            foreach (var quad in result.MeshQuads)
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
            int submesh = 0;
            foreach (var entry in submeshes)
            {
                materialIds[submesh] = entry.Key;
                mesh.SetIndices(entry.Value, MeshTopology.Triangles, submesh, false);
                submesh++;
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
