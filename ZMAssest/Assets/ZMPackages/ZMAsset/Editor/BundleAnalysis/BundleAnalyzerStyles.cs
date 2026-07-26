#if UNITY_EDITOR
using UnityEngine;

namespace ZM.Editor
{
    /// <summary>
    /// AssetBundle 分析器的表现层主题。
    /// 复用 ZMAsset 已验收的色板与九宫纹理，分析器仅声明自身需要的文字层级。
    /// </summary>
    internal static class BundleAnalyzerStyles
    {
        internal static GUIStyle HeaderTitle { get; private set; }
        internal static GUIStyle HeaderSubtitle { get; private set; }
        internal static GUIStyle ToolbarLabel { get; private set; }
        internal static GUIStyle ToolbarValue { get; private set; }
        internal static GUIStyle Tab { get; private set; }
        internal static GUIStyle TabSelected { get; private set; }
        internal static GUIStyle PageTitle { get; private set; }
        internal static GUIStyle PageSubtitle { get; private set; }
        internal static GUIStyle InfoBox { get; private set; }
        internal static GUIStyle SidebarItem { get; private set; }
        internal static GUIStyle SidebarItemSelected { get; private set; }
        internal static GUIStyle MetricCard { get; private set; }
        internal static GUIStyle MetricLabel { get; private set; }
        internal static GUIStyle MetricValue { get; private set; }
        internal static GUIStyle ListRow { get; private set; }
        internal static GUIStyle ListRowSelected { get; private set; }
        internal static GUIStyle TableHeader { get; private set; }
        internal static GUIStyle TableHeaderLabel { get; private set; }
        internal static GUIStyle TableRow { get; private set; }
        internal static GUIStyle TableCell { get; private set; }
        internal static GUIStyle TableCellRight { get; private set; }

        internal static void Ensure()
        {
            ZMBuildStyles.Ensure();
            if (HeaderTitle != null) return;

            HeaderTitle = Label(20, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            HeaderSubtitle = Label(11, FontStyle.Normal, ZMBuildStyles.Muted, TextAnchor.MiddleLeft);
            ToolbarLabel = Label(10, FontStyle.Normal, ZMBuildStyles.Muted, TextAnchor.MiddleLeft);
            ToolbarValue = Label(12, FontStyle.Bold, new Color32(220, 225, 232, 255), TextAnchor.MiddleLeft);
            ToolbarValue.padding = new RectOffset(10, 24, 0, 0);
            Tab = new GUIStyle(ZMBuildStyles.Segment) { fixedHeight = 36, fontSize = 12 };
            TabSelected = new GUIStyle(ZMBuildStyles.SegmentSelected) { fixedHeight = 36, fontSize = 12 };
            PageTitle = Label(24, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            PageSubtitle = Label(12, FontStyle.Normal, ZMBuildStyles.Muted, TextAnchor.MiddleLeft);
            InfoBox = new GUIStyle(ZMBuildStyles.BadgeBox)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                padding = new RectOffset(12, 12, 7, 7),
                normal = { textColor = new Color32(170, 181, 193, 255) }
            };
            SidebarItem = new GUIStyle(ZMBuildStyles.Segment)
            {
                fixedHeight = 48,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(48, 12, 0, 0),
                fontSize = 13
            };
            SidebarItemSelected = new GUIStyle(ZMBuildStyles.SegmentSelected)
            {
                fixedHeight = 48,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(48, 12, 0, 0),
                fontSize = 13
            };
            MetricCard = new GUIStyle(ZMBuildStyles.CardBox) { padding = new RectOffset(16, 16, 12, 12) };
            MetricLabel = Label(11, FontStyle.Normal, ZMBuildStyles.Muted, TextAnchor.MiddleLeft);
            MetricValue = Label(22, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            ListRow = new GUIStyle(ZMBuildStyles.BadgeBox) { padding = new RectOffset(12, 12, 8, 8) };
            ListRowSelected = new GUIStyle(ZMBuildStyles.CardSelectedBox) { padding = new RectOffset(12, 12, 8, 8) };
            TableHeader = new GUIStyle(ZMBuildStyles.BadgeBox)
            {
                padding = new RectOffset(14, 14, 0, 0),
                fixedHeight = 36
            };
            TableHeaderLabel = Label(11, FontStyle.Bold, new Color32(151, 160, 172, 255), TextAnchor.MiddleLeft);
            TableRow = new GUIStyle(ZMBuildStyles.CardBox)
            {
                padding = new RectOffset(14, 14, 0, 0),
                fixedHeight = 46
            };
            TableCell = Label(12, FontStyle.Normal, new Color32(218, 224, 232, 255), TextAnchor.MiddleLeft);
            TableCellRight = new GUIStyle(TableCell) { alignment = TextAnchor.MiddleRight };
        }

        private static GUIStyle Label(int size, FontStyle font, Color color, TextAnchor alignment)
        {
            return new GUIStyle(UnityEditor.EditorStyles.label)
            {
                fontSize = size,
                fontStyle = font,
                alignment = alignment,
                normal = { textColor = color }
            };
        }
    }
}
#endif
