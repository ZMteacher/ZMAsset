#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ZM.Editor
{
    /// <summary>
    /// 分析器专用代码矢量图标库。使用 Handles 抗锯齿绘制，
    /// 避免依赖不同 Unity 版本中名称和尺寸不稳定的内置图标。
    /// </summary>
    internal static class BundleAnalyzerIcons
    {
        internal enum Icon
        {
            Analyzer,
            Refresh,
            Clear,
            Cache,
            Settings,
            Back,
            Compare,
            Dependency,
            Overview,
            Size,
            Browser,
            Bundle
        }

        internal static void Draw(Rect rect, Icon icon, Color color, float width = 1.7f)
        {
            if (Event.current.type != EventType.Repaint) return;
            Handles.BeginGUI();
            Color previous = Handles.color;
            Handles.color = color;
            switch (icon)
            {
                case Icon.Analyzer:
                    DrawCube(rect, width);
                    Line(width, P(rect, .18f, .80f), P(rect, .38f, .60f), P(rect, .54f, .69f), P(rect, .82f, .34f));
                    break;
                case Icon.Refresh:
                    Arc(rect.center, rect.width * .34f, 35, 315, width);
                    Line(width, P(rect, .72f, .17f), P(rect, .86f, .34f), P(rect, .65f, .37f));
                    break;
                case Icon.Clear:
                    Line(width, P(rect, .28f, .30f), P(rect, .34f, .86f), P(rect, .68f, .86f), P(rect, .74f, .30f));
                    Line(width, P(rect, .22f, .25f), P(rect, .80f, .25f));
                    Line(width, P(rect, .40f, .16f), P(rect, .62f, .16f));
                    break;
                case Icon.Cache:
                    Ellipse(rect, .50f, .25f, .34f, .13f, width);
                    Arc(new Vector2(rect.center.x, rect.y + rect.height * .48f), rect.width * .34f, 0, 180, width);
                    Arc(new Vector2(rect.center.x, rect.y + rect.height * .72f), rect.width * .34f, 0, 180, width);
                    Line(width, P(rect, .16f, .25f), P(rect, .16f, .72f));
                    Line(width, P(rect, .84f, .25f), P(rect, .84f, .72f));
                    break;
                case Icon.Settings:
                    Circle(rect.center, rect.width * .17f, width);
                    Circle(rect.center, rect.width * .38f, width);
                    break;
                case Icon.Back:
                    Line(width, P(rect, .68f, .18f), P(rect, .34f, .50f), P(rect, .68f, .82f));
                    break;
                case Icon.Compare:
                    Line(width, P(rect, .14f, .32f), P(rect, .76f, .32f));
                    Line(width, P(rect, .64f, .20f), P(rect, .78f, .32f), P(rect, .64f, .44f));
                    Line(width, P(rect, .86f, .68f), P(rect, .24f, .68f));
                    Line(width, P(rect, .36f, .56f), P(rect, .22f, .68f), P(rect, .36f, .80f));
                    break;
                case Icon.Dependency:
                    Circle(P(rect, .20f, .25f), rect.width * .10f, width);
                    Circle(P(rect, .80f, .50f), rect.width * .10f, width);
                    Circle(P(rect, .20f, .75f), rect.width * .10f, width);
                    Line(width, P(rect, .30f, .28f), P(rect, .69f, .46f));
                    Line(width, P(rect, .30f, .72f), P(rect, .69f, .54f));
                    break;
                case Icon.Overview:
                    Circle(rect.center, rect.width * .36f, width);
                    Circle(rect.center, rect.width * .10f, width);
                    Line(width, P(rect, .50f, .14f), P(rect, .50f, .39f));
                    Line(width, P(rect, .50f, .61f), P(rect, .50f, .86f));
                    Line(width, P(rect, .14f, .50f), P(rect, .39f, .50f));
                    Line(width, P(rect, .61f, .50f), P(rect, .86f, .50f));
                    break;
                case Icon.Size:
                    Line(width, P(rect, .16f, .84f), P(rect, .16f, .58f),
                        P(rect, .34f, .58f), P(rect, .34f, .84f));
                    Line(width, P(rect, .42f, .84f), P(rect, .42f, .38f),
                        P(rect, .60f, .38f), P(rect, .60f, .84f));
                    Line(width, P(rect, .68f, .84f), P(rect, .68f, .18f),
                        P(rect, .86f, .18f), P(rect, .86f, .84f));
                    break;
                case Icon.Browser:
                    Line(width, P(rect, .12f, .30f), P(rect, .38f, .30f),
                        P(rect, .46f, .20f), P(rect, .88f, .20f),
                        P(rect, .88f, .78f), P(rect, .12f, .78f), P(rect, .12f, .30f));
                    Line(width, P(rect, .12f, .38f), P(rect, .88f, .38f));
                    break;
                case Icon.Bundle:
                    DrawCube(rect, width);
                    break;
            }
            Handles.color = previous;
            Handles.EndGUI();
        }

        private static void DrawCube(Rect r, float width)
        {
            Line(width, P(r, .50f, .08f), P(r, .86f, .28f), P(r, .50f, .48f), P(r, .14f, .28f), P(r, .50f, .08f));
            Line(width, P(r, .14f, .28f), P(r, .14f, .62f), P(r, .50f, .82f), P(r, .86f, .62f), P(r, .86f, .28f));
            Line(width, P(r, .50f, .48f), P(r, .50f, .82f));
        }

        private static void Circle(Vector2 c, float radius, float width) => Arc(c, radius, 0, 360, width);

        private static void Ellipse(Rect r, float x, float y, float rx, float ry, float width)
        {
            const int count = 24;
            var points = new Vector3[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                points[i] = new Vector2(r.x + r.width * (x + Mathf.Cos(a) * rx),
                    r.y + r.height * (y + Mathf.Sin(a) * ry));
            }
            Handles.DrawAAPolyLine(width, points);
        }

        private static void Arc(Vector2 center, float radius, float start, float end, float width)
        {
            const int count = 28;
            var points = new Vector3[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float a = Mathf.Lerp(start, end, i / (float)count) * Mathf.Deg2Rad;
                points[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
            Handles.DrawAAPolyLine(width, points);
        }

        private static void Line(float width, params Vector2[] points)
        {
            var values = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++) values[i] = points[i];
            Handles.DrawAAPolyLine(width, values);
        }

        private static Vector2 P(Rect r, float x, float y) =>
            new Vector2(r.x + r.width * x, r.y + r.height * y);
    }
}
#endif
