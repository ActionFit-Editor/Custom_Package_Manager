#if UNITY_EDITOR
using System.IO;
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
        string content = readme != null ? readme.text : ReadFileContent(readmePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            EditorUtility.DisplayDialog("ActionFit Package Manager", $"README not found.\n{readmePath}", "OK");
            return;
        }

        var window = GetWindow<ActionFitPackageReadmeWindow>("ActionFit Package README");
        window.minSize = new Vector2(560, 520);
        window._readmePath = readmePath;
        window._content = content;
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
        else _content = ReadFileContent(_readmePath);
    }

    private static string ReadFileContent(string readmePath)
    {
        string fullPath = ResolveProjectPath(readmePath);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
    }

    private static string ResolveProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return path;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, path.Replace("/", Path.DirectorySeparatorChar.ToString()));
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
