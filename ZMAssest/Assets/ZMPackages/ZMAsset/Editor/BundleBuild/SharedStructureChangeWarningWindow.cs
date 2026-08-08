using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZM.Editor;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 00 Shared 严重结构变化专用模态确认窗口，统一使用 ZMAsset 暗色主题和代码矢量图标。
    /// </summary>
    internal sealed class SharedStructureChangeWarningWindow : EditorWindow
    {
        private const float WindowWidth = 680f;
        private const float WindowHeight = 540f;
        private string mModuleName;
        private List<string> mDetails;
        private List<string> mPossibleConsumers;
        private List<string> mSelectedModules;
        private Vector2 mScrollPosition;
        private ConfirmationResult mResult;
        private bool mDragging;
        private Vector2 mDragOffset;

        /// <summary>
        /// 00 无边框 Popup 不能同步阻塞调用栈，因此用独立结果对象让构建枚举器逐帧等待用户选择。
        /// </summary>
        internal sealed class ConfirmationResult
        {
            internal bool IsResolved { get; private set; }
            internal bool Confirmed { get; private set; }

            internal void Resolve(bool confirmed)
            {
                if (IsResolved) return;
                Confirmed = confirmed;
                IsResolved = true;
            }
        }

        /// <summary>
        /// 00 显示无原生标题栏的 Popup；构建枚举器通过返回结果逐帧等待，关闭窗口或 Esc 均视为取消。
        /// </summary>
        internal static ConfirmationResult Show(
            string moduleName,
            IReadOnlyList<string> details,
            IReadOnlyList<string> possibleConsumers,
            IReadOnlyList<string> selectedModules)
        {
            //00 创建 Popup 前先保存当前焦点窗口；ShowPopup 后 focusedWindow 会变成提示窗口自身。
            EditorWindow ownerWindow = focusedWindow;
            SharedStructureChangeWarningWindow window = CreateInstance<SharedStructureChangeWarningWindow>();
            ConfirmationResult result = new ConfirmationResult();
            window.mResult = result;
            window.mModuleName = moduleName ?? string.Empty;
            window.mDetails = details?.ToList() ?? new List<string>();
            window.mPossibleConsumers = possibleConsumers?.ToList() ?? new List<string>();
            window.mSelectedModules = selectedModules?.ToList() ?? new List<string>();
            window.minSize = new Vector2(WindowWidth, WindowHeight);
            window.maxSize = window.minSize;
            if (ownerWindow != null)
            {
                //00 默认相对 AssetBundle Hub/打包窗口居中，而不是固定放在主显示器中央。
                Rect ownerRect = ownerWindow.position;
                window.position = new Rect(
                    ownerRect.x + (ownerRect.width - WindowWidth) * .5f,
                    ownerRect.y + (ownerRect.height - WindowHeight) * .5f,
                    WindowWidth,
                    WindowHeight);
            }
            else
            {
                //00 极少数无焦点窗口的调用场景回退到当前主显示器中心。
                Resolution resolution = Screen.currentResolution;
                window.position = new Rect(
                    Mathf.Max(0f, (resolution.width - WindowWidth) * .5f),
                    Mathf.Max(0f, (resolution.height - WindowHeight) * .5f),
                    WindowWidth,
                    WindowHeight);
            }
            //00 ShowPopup 不绘制 Unity 原生白色标题栏，窗口头部、关闭按钮和边框全部由 ZMAsset 自己绘制。
            window.ShowPopup();
            window.Focus();
            return result;
        }

        private void OnGUI()
        {
            ZMBuildStyles.Ensure();
            DrawBackground();
            DrawHeader();
            DrawSummary();
            DrawChangeList();
            DrawImpactNotice();
            DrawFooter();
            HandleWindowDrag(new Rect(0, 0, position.width - 54, 66));
            HandleKeyboard();
        }

        private void DrawBackground()
        {
            GUI.Box(new Rect(0, 0, position.width, position.height), GUIContent.none, ZMBuildStyles.PopupWindowBox);
            GUI.Box(new Rect(2, 2, position.width - 4, 64), GUIContent.none, ZMBuildStyles.PopupHeaderBox);
            EditorGUI.DrawRect(new Rect(2, 65, position.width - 4, 1), ZMBuildStyles.Border);
        }

        private void DrawHeader()
        {
            Color warningColor = new Color32(244, 183, 64, 255);
            Rect iconRect = new Rect(22, 17, 30, 30);
            BundleAnalyzerIcons.Draw(iconRect, BundleAnalyzerIcons.Icon.Warning, warningColor, 2.2f);
            GUIStyle titleStyle = new GUIStyle(ZMBuildStyles.CardTitle) { fontSize = 17 };
            GUI.Label(new Rect(66, 11, position.width - 112, 28), "检测到 Shared 严重结构变化", titleStyle);
            GUI.Label(
                new Rect(66, 38, position.width - 112, 18),
                $"模块：{mModuleName} · 请确认本次补丁发布策略",
                ZMBuildStyles.SettingsHint);
            if (GUI.Button(new Rect(position.width - 43, 17, 28, 28), "×", ZMBuildStyles.CloseButton)) Close();
        }

        private void DrawSummary()
        {
            Rect card = new Rect(22, 82, position.width - 44, 62);
            GUI.Box(card, GUIContent.none, ZMBuildStyles.SettingsCard);
            GUI.Label(new Rect(card.x + 16, card.y + 10, card.width - 32, 20), "变化摘要", ZMBuildStyles.SettingsLabel);
            GUIStyle hint = new GUIStyle(ZMBuildStyles.SettingsFieldHint)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = new Color32(202, 207, 215, 255) }
            };
            GUI.Label(
                new Rect(card.x + 16, card.y + 32, card.width - 32, 22),
                $"共检测到 {mDetails.Count} 项可能破坏旧资源身份的结构变化。框架不会自动加入其他模块。",
                hint);
        }

        private void DrawChangeList()
        {
            GUI.Label(new Rect(24, 158, 180, 21), "结构变化明细", ZMBuildStyles.SettingsSectionTitle);
            Rect panel = new Rect(22, 184, position.width - 44, 190);
            GUI.Box(panel, GUIContent.none, ZMBuildStyles.FieldBox);
            Rect viewport = new Rect(panel.x + 10, panel.y + 9, panel.width - 20, panel.height - 18);
            float rowHeight = 30f;
            float contentHeight = Mathf.Max(viewport.height, mDetails.Count * rowHeight + 4);
            mScrollPosition = GUI.BeginScrollView(
                viewport,
                mScrollPosition,
                new Rect(0, 0, viewport.width - 14, contentHeight));
            GUIStyle detailStyle = new GUIStyle(ZMBuildStyles.SettingsHint)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            for (int index = 0; index < mDetails.Count; index++)
            {
                Rect row = new Rect(0, index * rowHeight, viewport.width - 18, rowHeight - 2);
                if ((index & 1) == 1) EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, .018f));
                GUI.Label(new Rect(row.x + 8, row.y, 18, row.height), "•", ZMBuildStyles.SettingsHint);
                GUI.Label(new Rect(row.x + 25, row.y, row.width - 30, row.height),
                    new GUIContent(mDetails[index], mDetails[index]), detailStyle);
            }
            GUI.EndScrollView();
        }

        private void DrawImpactNotice()
        {
            Rect card = new Rect(22, 389, position.width - 44, 82);
            GUI.Box(card, GUIContent.none, ZMBuildStyles.SettingsCard);
            string consumers = mPossibleConsumers.Count == 0
                ? "当前工程未检测到直接消费者"
                : "当前工程可能受影响：" + string.Join("、", mPossibleConsumers);
            string selected = mSelectedModules.Count == 0 ? "无" : string.Join("、", mSelectedModules);
            GUI.Label(new Rect(card.x + 16, card.y + 9, card.width - 32, 20), consumers, ZMBuildStyles.SettingsLabel);
            GUIStyle noticeStyle = new GUIStyle(ZMBuildStyles.SettingsFieldHint) { fontSize = 11, wordWrap = true };
            GUI.Label(
                new Rect(card.x + 16, card.y + 32, card.width - 32, 42),
                $"本次只构建显式选择的模块：{selected}。影响范围仅依据当前工程分析，线上版本兼容关系由开发者决定。",
                noticeStyle);
        }

        private void DrawFooter()
        {
            float footerY = position.height - 52;
            GUI.Label(new Rect(22, footerY + 7, 250, 20), "Esc 或关闭窗口将取消本次构建", ZMBuildStyles.SettingsHint);
            Rect continueRect = new Rect(position.width - 238, footerY, 216, 34);
            Rect cancelRect = new Rect(continueRect.x - 100, footerY, 88, 34);
            if (GUI.Button(cancelRect, "取消构建", ZMBuildStyles.CompactSecondaryButton)) Close();
            if (GUI.Button(continueRect, "仍然只构建已选模块", ZMBuildStyles.CompactPrimaryButton))
            {
                mResult?.Resolve(true);
                Close();
            }
        }

        private void HandleKeyboard()
        {
            if (Event.current.type != EventType.KeyDown || Event.current.keyCode != KeyCode.Escape) return;
            Event.current.Use();
            Close();
        }

        /// <summary>
        /// 00 无原生标题栏 Popup 使用自绘头部拖动；关闭按钮区域被排除，避免点击关闭时误触移动。
        /// </summary>
        private void HandleWindowDrag(Rect dragArea)
        {
            Event current = Event.current;
            if (current.button != 0) return;
            if (current.type == EventType.MouseDown && dragArea.Contains(current.mousePosition))
            {
                mDragging = true;
                mDragOffset = GUIUtility.GUIToScreenPoint(current.mousePosition) - position.position;
                current.Use();
                return;
            }
            if (current.type == EventType.MouseDrag && mDragging)
            {
                Vector2 screenMouse = GUIUtility.GUIToScreenPoint(current.mousePosition);
                Rect movedPosition = position;
                movedPosition.position = screenMouse - mDragOffset;
                position = movedPosition;
                current.Use();
                Repaint();
                return;
            }
            if (current.type == EventType.MouseUp && mDragging)
            {
                mDragging = false;
                current.Use();
            }
        }

        private void OnDisable()
        {
            //00 点击外部、Esc、右上角关闭或程序集重载都会进入这里；未明确继续时统一视为取消。
            mResult?.Resolve(false);
        }
    }
}
