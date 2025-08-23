#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TerrainToolPreset))]
public class TerrainToolPresetEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var opProp    = serializedObject.FindProperty("operation");
        var shapeProp = serializedObject.FindProperty("shape");

        Draw("displayName");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Operation", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(opProp);
        var op = (ToolOperation)opProp.enumValueIndex;

        // Strengths
        if (op == ToolOperation.Smooth) Draw("smoothStrength");
        else if (op == ToolOperation.Break || op == ToolOperation.Build) Draw("strength");

        if (op != ToolOperation.None)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shape + Base Size", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(shapeProp);
            var shape = (BrushShape)shapeProp.enumValueIndex;

            if (shape == BrushShape.Sphere)
            {
                Draw("sphereRadius");
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Runtime Scale Limits", EditorStyles.boldLabel);
                Draw("sphereRadiusRange");
            }
            else
            {
                Draw("boxSize");
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Runtime Scale Limits", EditorStyles.boldLabel);
                Draw("boxSizeMin");
                Draw("boxSizeMax");
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Placement / Anchoring", EditorStyles.boldLabel);
            Draw("anchored");
            if (shape != BrushShape.Sphere) Draw("lockYawWhileActive");
            Draw("hideBrush");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Snapping permissions", EditorStyles.boldLabel);
            Draw("allowPositionSnap");
            Draw("allowYawSnap");

            if (op == ToolOperation.Break)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Type anchor (Break only)", EditorStyles.boldLabel);
                Draw("allowTypeAnchor");
            }

            if (op == ToolOperation.Build)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Build material (Build only)", EditorStyles.boldLabel);
                Draw("fillType");
            }
        }
        else
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("No operation: this tool does nothing and shows no indicator.", MessageType.Info);
        }

        serializedObject.ApplyModifiedProperties();
    }

    void Draw(string name) => EditorGUILayout.PropertyField(serializedObject.FindProperty(name));
}
#endif
