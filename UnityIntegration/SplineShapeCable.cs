using System;
using System.Collections.Generic;
using System.Text;

namespace TileVoxUnity
{
    using System;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.Splines;

    [Serializable]
    public class CircleOffsetShape : IExtrudeShape
    {
        [SerializeField, Min(3)]
        private int m_Sides = 16;

        [SerializeField]
        private float m_Radius = 0.5f;

        [Tooltip("Expands or shrinks the circle outward along its 2D surface normal.")]
        [SerializeField]
        private float m_RadialNormalOffset = 0.0f;

        [Tooltip("Offsets the circle center along the spline's 2D normal (X) and binormal (Y) axes.")]
        [SerializeField]
        private float2 m_PlanarOffset = float2.zero;

        public int SideCount
        {
            get => m_Sides;
            set => m_Sides = Mathf.Max(3, value);
        }

        public float Radius
        {
            get => m_Radius;
            set => m_Radius = value;
        }

        public float RadialNormalOffset
        {
            get => m_RadialNormalOffset;
            set => m_RadialNormalOffset = value;
        }

        public float2 PlanarOffset
        {
            get => m_PlanarOffset;
            set => m_PlanarOffset = value;
        }

        public void Setup(ISpline path, int segmentCount)
        {
            // Called once before extrusion begins.
            // Use this to cache spline length or segment count if needed.
        }

        public void SetSegment(int index, float t, float3 position, float3 tangent, float3 up)
        {
            // Called once per ring along the spline.
            // You can dynamically vary RadialNormalOffset or PlanarOffset based on 't' here.
        }

        public float2 GetPosition(float t, int index)
        {
            // Calculate angle around the circle (t goes from 0 to 1 around the circumference)
            float angle = t * math.PI * 2f;

            // Unit normal of the circle at this angle
            float2 normal2D = new float2(math.cos(angle), math.sin(angle));

            // Apply base radius + outward normal offset + local planar shift
            float effectiveRadius = m_Radius + m_RadialNormalOffset;
            return (normal2D * effectiveRadius) + m_PlanarOffset;
        }
    }

}
