using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZM.ZMAsset;

[Serializable]
public class BuildBundleWindow : BundleBehaviour
{
    public override void DrawBuildButtons()
    {
        GUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(78)))
        {
            GUILayout.Space(34);
            GUILayout.Label("输出目录", ZMBuildStyles.StatusLabel, GUILayout.Width(66), GUILayout.Height(48));
            Rect outputRect = GUILayoutUtility.GetRect(270, 38, GUILayout.Width(270), GUILayout.Height(38));
            outputRect.y += 5;
            GUI.Box(outputRect, GUIContent.none, ZMBuildStyles.FieldBox);
            GUI.Label(outputRect, $"AssetBundle/{EditorUserBuildSettings.activeBuildTarget}", ZMBuildStyles.FlatField);
            Rect folderRect = new Rect(outputRect.xMax - 35, outputRect.y + 1, 34, outputRect.height - 2);
            EditorGUI.DrawRect(new Rect(folderRect.x, folderRect.y, 1, folderRect.height), ZMBuildStyles.Border);
            Texture fieldFolder = EditorGUIUtility.IconContent("Folder Icon").image;
            if (fieldFolder != null) GUI.DrawTexture(new Rect(folderRect.x + 8, folderRect.y + 8, 19, 19), fieldFolder, ScaleMode.ScaleToFit);
            EditorGUIUtility.AddCursorRect(folderRect, MouseCursor.Link);
            if (GUI.Button(folderRect, GUIContent.none, GUIStyle.none)) BuildBundleCompiler.OpenAssetBundleFolder();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(SelectedCount == 0))
            {
                if (GUILayout.Button("内嵌到 StreamingAssets", ZMBuildStyles.SecondaryButton, GUILayout.Width(205)))
                    CopyBundleToStreamingAssetsPath();
                GUILayout.Space(12);
                if (GUILayout.Button("开始构建资源", ZMBuildStyles.PrimaryButton, GUILayout.Width(205)))
                    BuildBundle();
            }
            GUILayout.Space(34);
        }
    }

    public override void BuildBundle()
    {
        var jobs = new List<(string name, Func<System.Collections.IEnumerator> action)>();
        foreach (BundleModuleData item in moduleDataList)
            if (item.isBuild)
            {
                BundleModuleData module = item;
                jobs.Add((module.moduleName, () => BuildBundleCompiler.BuildAssetBundleStaged(module)));
            }
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "AssetBundle"));
        ZMBuildProgress.RunStaged("资源构建", jobs, output);
    }

    public void CopyBundleToStreamingAssetsPath()
    {
        var jobs = new List<(string name, Action action)>();
        foreach (BundleModuleData item in moduleDataList)
            if (item.isBuild)
            {
                BundleModuleData module = item;
                jobs.Add((module.moduleName, () => BuildBundleCompiler.CopyBundleToStramingAssets(module, false)));
            }
        ZMBuildProgress.Run("内嵌资源", jobs, Application.streamingAssetsPath);
    }
}
