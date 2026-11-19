using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Splines;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.Splines.ExtrusionShapes;
using WorldFlow;

namespace TileVoxUnity.Editor
{
    [InitializeOnLoad]
    [CustomEditor(typeof(SplineExtruder))]
    public class SplineNormalOffsetExtrudeEditor : UnityEditor.Editor
    {
        private Type[] shapeTypes;
        private string[] shapeNames;

        static SplineNormalOffsetExtrudeEditor()
        {
            EditorSplineUtility.AfterSplineWasModified += OnSplineModified;
            SplineContainer.SplineAdded += OnSplineListChanged;
            SplineContainer.SplineRemoved += OnSplineListChanged;
        }

        private static void OnSplineModified(Spline spline)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            RebuildAffected(spline, null);
        }

        private static void OnSplineListChanged(SplineContainer container, int index)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            RebuildAffected(null, container);
        }

        private static void RebuildAffected(Spline spline, SplineContainer container)
        {
            foreach (var extrude in Resources.FindObjectsOfTypeAll<SplineExtruder>())
            {
                if (!extrude.isActiveAndEnabled || !extrude.gameObject.scene.IsValid() ||
                    !extrude.RebuildOnSplineChange || extrude.Container == null)
                    continue;

                if (container != null)
                {
                    if (extrude.Container == container) extrude.Rebuild();
                    continue;
                }

                if (extrude.Shape is SplineShape shape && shape.Spline == spline)
                {
                    extrude.Rebuild();
                    continue;
                }

                foreach (var source in extrude.Container.Splines)
                    if (source == spline)
                    {
                        extrude.Rebuild();
                        break;
                    }
            }
        }

        private void OnEnable()
        {
            shapeTypes = TypeCache.GetTypesDerivedFrom<IExtrudeShape>()
                .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters &&
                    !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                    type.IsSerializable && type.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ThenBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            shapeNames = shapeTypes.Select(type => type == typeof(SplineShape)
                    ? L10n.Tr("Spline Profile")
                    : ObjectNames.NicifyVariableName(type.Name))
                .ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Container"), new GUIContent("Source Spline Container"));

            var shape = serializedObject.FindProperty("m_Shape");
            int selected = Array.IndexOf(shapeTypes, shape.managedReferenceValue?.GetType());
            int next = EditorGUILayout.Popup("Shape Type", selected, shapeNames);
            if (next >= 0 && next != selected)
                shape.managedReferenceValue = Activator.CreateInstance(shapeTypes[next]);
            if (shape.managedReferenceValue != null)
                EditorGUILayout.PropertyField(shape, new GUIContent("Shape Settings"), true);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_NormalOffset"), new GUIContent("Normal Offset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Radius"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_SegmentsPerUnit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Range"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Capped"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_FlipNormals"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RebuildOnSplineChange"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RebuildFrequency"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_UpdateColliders"));
            var targetMesh = serializedObject.FindProperty("m_TargetMesh");
            EditorGUILayout.PropertyField(targetMesh);
            bool meshCreated = false;
            if (targetMesh.objectReferenceValue == null && GUILayout.Button("Create Mesh Asset"))
            {
                string path = EditorUtility.SaveFilePanelInProject("Create Extruded Mesh", target.name + " Mesh", "asset", "Choose where to save the generated mesh.");
                if (!string.IsNullOrEmpty(path))
                {
                    var mesh = new Mesh { name = target.name + " Mesh" };
                    AssetDatabase.CreateAsset(mesh, path);
                    targetMesh.objectReferenceValue = mesh;
                    meshCreated = true;
                }
            }

            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            var extrude = (SplineExtruder)target;
            if (changed || meshCreated) extrude.Rebuild();
            if (GUILayout.Button("Rebuild")) extrude.Rebuild();
        }
    }
}
