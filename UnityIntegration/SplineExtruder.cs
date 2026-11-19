using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;
using UnityEngine.Splines.ExtrusionShapes;

namespace WorldFlow
{
    /// <summary>Extrudes a shape along a copy of the source spline displaced along its up vectors.</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("WorldFlow/WorldFlow Spline Extruder")]
    public class SplineExtruder : MonoBehaviour
    {
        [SerializeField, Tooltip("Spline container whose paths are copied before extrusion.")] private SplineContainer m_Container;
        [SerializeReference] private IExtrudeShape m_Shape = new Circle();
        [SerializeField] private Mesh m_TargetMesh;
        [SerializeField, Tooltip("World-space distance along the source spline's up vector. Positive values follow the spline normal.")] private float m_NormalOffset;
        [SerializeField] private float m_Radius = .25f;
        [SerializeField] private float m_SegmentsPerUnit = 4f;
        [SerializeField] private Vector2 m_Range = new Vector2(0f, 1f);
        [SerializeField] private bool m_Capped = true;
        [SerializeField] private bool m_FlipNormals;
        [SerializeField] private bool m_RebuildOnSplineChange = true;
        [SerializeField] private int m_RebuildFrequency = 30;
        [SerializeField] private bool m_UpdateColliders = true;

        private Mesh m_Mesh;
        private bool m_OwnsMesh;
        private bool m_RebuildRequested;
        private float m_NextRebuild;

        public SplineContainer Container { get => m_Container; set { m_Container = value; m_RebuildRequested = true; } }
        public IExtrudeShape Shape { get => m_Shape; set { m_Shape = value; m_RebuildRequested = true; } }
        public Mesh TargetMesh { get => m_TargetMesh; set { m_TargetMesh = value; Rebuild(); } }
        public float NormalOffset { get => m_NormalOffset; set { m_NormalOffset = value; m_RebuildRequested = true; } }
        public float Radius { get => m_Radius; set { m_Radius = value; m_RebuildRequested = true; } }
        public float SegmentsPerUnit { get => m_SegmentsPerUnit; set { m_SegmentsPerUnit = value; m_RebuildRequested = true; } }
        public Vector2 Range { get => m_Range; set { m_Range = value; m_RebuildRequested = true; } }
        public bool Capped { get => m_Capped; set { m_Capped = value; m_RebuildRequested = true; } }
        public bool FlipNormals { get => m_FlipNormals; set { m_FlipNormals = value; m_RebuildRequested = true; } }
        public bool RebuildOnSplineChange { get => m_RebuildOnSplineChange; set => m_RebuildOnSplineChange = value; }
        public int RebuildFrequency { get => m_RebuildFrequency; set => m_RebuildFrequency = value; }
        public bool UpdateColliders { get => m_UpdateColliders; set { m_UpdateColliders = value; m_RebuildRequested = true; } }

        private void Reset()
        {
            TryGetComponent(out m_Container);
            Rebuild();
        }

        private void OnEnable()
        {
            Spline.Changed += OnSplineChanged;
            SplineContainer.SplineAdded += OnSplineListChanged;
            SplineContainer.SplineRemoved += OnSplineListChanged;
            Rebuild();
        }

        private void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
            SplineContainer.SplineAdded -= OnSplineListChanged;
            SplineContainer.SplineRemoved -= OnSplineListChanged;
            ReleaseMesh();
        }

        private void OnValidate() => m_RebuildRequested = true;

        private void Update()
        {
            if (m_RebuildRequested && Time.realtimeSinceStartup >= m_NextRebuild)
                Rebuild();
        }

        private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            if (!m_RebuildOnSplineChange || m_Container == null) return;
            foreach (var source in m_Container.Splines)
                if (source == spline)
                {
                    m_RebuildRequested = true;
                    return;
                }

            if (m_Shape is SplineShape shape && shape.Spline == spline)
                m_RebuildRequested = true;
        }

        private void OnSplineListChanged(SplineContainer container, int index)
        {
            if (m_RebuildOnSplineChange && container == m_Container)
                m_RebuildRequested = true;
        }

        private void EnsureMesh()
        {
            bool needsTemporary = m_TargetMesh == null;
            if (m_Mesh != null && (m_OwnsMesh != needsTemporary || (!m_OwnsMesh && m_Mesh != m_TargetMesh)))
                ReleaseMesh();

            if (m_Mesh == null)
            {
                m_OwnsMesh = needsTemporary;
                m_Mesh = needsTemporary ? new Mesh { name = "<Spline Normal Offset Mesh>", hideFlags = HideFlags.HideAndDontSave } : m_TargetMesh;
            }

            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        }

        private void ReleaseMesh()
        {
            if (m_Mesh == null) return;
            var filter = GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh == m_Mesh) filter.sharedMesh = null;
            if (m_OwnsMesh) DestroyMesh(m_Mesh);
            m_Mesh = null;
            m_OwnsMesh = false;
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }

        private Spline CreateOffsetSpline(Spline source)
        {
            var offset = new Spline(source);
            // The copy starts with knot data but initializes tangent metadata on demand.
            offset.GetTangentMode(0);
            var sourceTransform = m_Container.transform;
            float4x4 sourceToLocal = (float4x4)(transform.worldToLocalMatrix * sourceTransform.localToWorldMatrix);

            for (int i = 0; i < source.Count; i++)
            {
                var knot = source[i];
                float3 up = !source.Closed && i == source.Count - 1
                    ? math.rotate(knot.Rotation, math.up())
                    : source.GetCurveUpVector(i, 0f);
                up = math.normalizesafe(up, math.up());
                Vector3 worldOffset = sourceTransform.TransformDirection((Vector3)up).normalized * m_NormalOffset;
                knot.Position += (float3)sourceTransform.InverseTransformVector(worldOffset);
                if (sourceTransform != transform) knot = knot.Transform(sourceToLocal);
                offset.SetKnotNoNotify(i, knot);
            }

            return offset;
        }

        private ExtrudeSettings<IExtrudeShape> GetSettings(Spline spline)
        {
            var settings = new ExtrudeSettings<IExtrudeShape>(m_Shape)
            {
                Radius = m_Radius,
                CapEnds = m_Capped,
                Range = m_Range,
                FlipNormals = m_FlipNormals,
                SegmentCount = Mathf.CeilToInt(spline.GetLength() * Mathf.Abs(m_Range.y - m_Range.x) * m_SegmentsPerUnit)
            };
            return settings;
        }

        /// <summary>Copies and offsets every source spline, then regenerates the extrusion mesh.</summary>
        public void Rebuild()
        {
            EnsureMesh();
            m_RebuildRequested = false;
            if (m_Shape == null) m_Shape = new Circle();
            if (m_Container == null) return;

            var splines = new List<Spline>();
            foreach (var source in m_Container.Splines)
                if (source != null && source.Count > 1)
                    splines.Add(CreateOffsetSpline(source));

            m_Mesh.Clear();
            if (splines.Count == 1)
            {
                SplineMesh.Extrude(splines[0], m_Mesh, GetSettings(splines[0]));
            }
            else if (splines.Count > 1)
            {
                var parts = new List<Mesh>(splines.Count);
                var combine = new List<CombineInstance>(splines.Count);
                int vertexCount = 0;
                foreach (var spline in splines)
                {
                    var part = new Mesh();
                    if (SplineMesh.Extrude(spline, part, GetSettings(spline)))
                    {
                        vertexCount += part.vertexCount;
                        parts.Add(part);
                        combine.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
                    }
                    else DestroyMesh(part);
                }

                if (combine.Count > 0)
                {
                    m_Mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    m_Mesh.CombineMeshes(combine.ToArray(), true, false);
                }
                foreach (var part in parts) DestroyMesh(part);
            }

#if UNITY_PHYSICS_MODULE
            if (m_UpdateColliders)
            {
                if (TryGetComponent<MeshCollider>(out var meshCollider))
                {
                    meshCollider.sharedMesh = null;
                    meshCollider.sharedMesh = m_Mesh;
                }
                if (TryGetComponent<BoxCollider>(out var boxCollider))
                {
                    boxCollider.center = m_Mesh.bounds.center;
                    boxCollider.size = m_Mesh.bounds.size;
                }
                if (TryGetComponent<SphereCollider>(out var sphereCollider))
                {
                    sphereCollider.center = m_Mesh.bounds.center;
                    Vector3 extents = m_Mesh.bounds.extents;
                    sphereCollider.radius = Mathf.Max(extents.x, extents.y, extents.z);
                }
            }
#endif

#if UNITY_EDITOR
            if (m_TargetMesh != null) UnityEditor.EditorUtility.SetDirty(m_TargetMesh);
#endif
            m_NextRebuild = Time.realtimeSinceStartup + 1f / Mathf.Max(1, m_RebuildFrequency);
        }
    }
}
