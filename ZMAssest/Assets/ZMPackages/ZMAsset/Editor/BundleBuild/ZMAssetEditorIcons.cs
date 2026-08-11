using UnityEditor;
using UnityEngine;

internal enum ZMAssetEditorIcon
{
    ShaderVariant = 0
}

/// <summary>
/// Reusable code-vector icons for ZMAsset editor tools. Drawing is DPI-aware through IMGUI coordinates and
/// keeps its aspect ratio by fitting every icon into a centered square.
/// </summary>
internal static class ZMAssetEditorIcons
{
    internal static void Draw(Rect bounds, ZMAssetEditorIcon icon, Color color)
    {
        float size = Mathf.Min(bounds.width, bounds.height);
        Rect rect = new Rect(
            bounds.center.x - size * .5f,
            bounds.center.y - size * .5f,
            size,
            size);

        Handles.BeginGUI();
        Color previousColor = Handles.color;
        Matrix4x4 previousMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = color;

        switch (icon)
        {
            case ZMAssetEditorIcon.ShaderVariant:
                DrawShaderVariant(rect);
                break;
        }

        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
        Handles.EndGUI();
    }

    private static void DrawShaderVariant(Rect rect)
    {
        Vector2 center = rect.center;
        float outerRadius = rect.width * .31f;
        ZMBuildStyles.DrawGuiCircle(center, outerRadius, 1.8f, 24);
        ZMBuildStyles.DrawGuiCircle(center, rect.width * .12f, 1.8f, 18);

        Handles.DrawAAPolyLine(
            1.8f,
            new Vector3(rect.x + rect.width * .08f, center.y),
            new Vector3(center.x - outerRadius, center.y));
        Handles.DrawAAPolyLine(
            1.8f,
            new Vector3(center.x + outerRadius, center.y),
            new Vector3(rect.xMax - rect.width * .08f, center.y));
        Handles.DrawAAPolyLine(
            1.8f,
            new Vector3(center.x, rect.y + rect.height * .08f),
            new Vector3(center.x, center.y - outerRadius));
        Handles.DrawAAPolyLine(
            1.8f,
            new Vector3(center.x, center.y + outerRadius),
            new Vector3(center.x, rect.yMax - rect.height * .08f));
    }
}
