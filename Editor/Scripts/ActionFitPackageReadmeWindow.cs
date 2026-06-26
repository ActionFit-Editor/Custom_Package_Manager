#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class ActionFitPackageReadmeWindow : EditorWindow
{
    private string _readmePath;
    private string _content;
    private Vector2 _scroll;

    public static void Open(string readmePath)
    {
        var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(readmePath);
        if (readme == null)
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"README not found.\n{readmePath}", "OK");
            return;
        }

        var window = GetWindow<ActionFitPackageReadmeWindow>("ActionFit Package README");
        window.minSize = new Vector2(560, 520);
        window._readmePath = readmePath;
        window._content = readme.text;
        window.Show();
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField(_readmePath ?? "", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70))) Reload();
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextArea(_content ?? "", ReadmeStyle, GUILayout.ExpandHeight(true));
        }

        EditorGUILayout.EndScrollView();
    }

    private void Reload()
    {
        var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(_readmePath);
        if (readme != null) _content = readme.text;
    }

    private static GUIStyle ReadmeStyle
    {
        get
        {
            var style = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                richText = false,
                padding = new RectOffset(10, 10, 10, 10)
            };
            return style;
        }
    }
}
#endif
