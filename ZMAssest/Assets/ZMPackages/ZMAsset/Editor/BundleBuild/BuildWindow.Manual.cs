using UnityEditor;
using UnityEngine;

/// <summary>
/// ZMAsset 构建中心的使用手册页面。
/// 页面内容与构建业务解耦，后续可独立维护文档结构和引导文案。
/// </summary>
public partial class BuildWindows
{
    private const string ZMAssetDocumentationUrl = "https://www.zm-doc.com/ZMAsset/";
    [SerializeField] private Vector2 manualScroll;
    [System.NonSerialized] private static GUIStyle manualStepCardStyle;

    private void DrawManual()
    {
        GUILayout.Space(28);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(34);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
            {
                DrawManualHeader();
                GUILayout.Space(16);
                using (var scroll = new EditorGUILayout.ScrollViewScope(manualScroll))
                {
                    manualScroll = scroll.scrollPosition;
                    DrawManualQuickStart();
                    GUILayout.Space(12);
                    DrawManualFeatureCards();
                    GUILayout.Space(18);
                }
            }
            GUILayout.Space(34);
        }
    }

    private static void DrawManualHeader()
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(54)))
        {
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Label("使用手册", ZMBuildStyles.Heading, GUILayout.Height(31));
                GUILayout.Label("从资源模块配置到热更补丁发布的完整工作流程",
                    ZMBuildStyles.Subtitle, GUILayout.Height(20));
            }
            GUILayout.FlexibleSpace();
            GUILayout.Space(12);
            if (GUILayout.Button("打开 API 文档", ZMBuildStyles.CompactPrimaryButton,
                    GUILayout.Width(146), GUILayout.Height(38)))
                Application.OpenURL(ZMAssetDocumentationUrl);
        }
    }

    private static void DrawManualQuickStart()
    {
        using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
        {
            GUILayout.Label("快速开始", ZMBuildStyles.SettingsSectionTitle, GUILayout.Height(24));
            GUILayout.Label("推荐首次接入时按以下顺序完成配置与构建",
                ZMBuildStyles.SettingsHint, GUILayout.Height(18));
            GUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawManualStep("01", "配置模块", "创建模块并设置资源打包规则");
                GUILayout.Space(10);
                DrawManualStep("02", "设置参数", "确认平台、压缩、加载与热更模式");
                GUILayout.Space(10);
                DrawManualStep("03", "构建资源", "选择模块并生成完整 Bundle");
                GUILayout.Space(10);
                DrawManualStep("04", "发布补丁", "填写版本信息并生成热更补丁");
            }
        }
    }

    private void DrawManualFeatureCards()
    {
        // 两列统一使用固定计算宽度，避免不同正文长度影响 GUILayout 的自动分配。
        float cardWidth = Mathf.Max(280f, (position.width - 286f) * .5f);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawManualCard("资源构建",
                "• 勾选需要参与构建的资源模块。\n\n" +
                "• 点击模块卡片可选中；右键可以编辑模块配置。\n\n" +
                "• 输出目录应位于稳定、可写且便于版本归档的位置。\n\n" +
                "• 开始构建前确认目标平台和压缩格式正确。\n\n" +
                "• 构建完成后检查输出目录与 AssetBundle 配置清单。\n\n" +
                "• 内置资源：随安装包发布，适合启动必需资源，但会增加首包体积。",
                cardWidth);
            GUILayout.Space(12);
            DrawManualCard("热更补丁",
                "• 生效应用版本必须与客户端发布版本保持一致。\n\n" +
                "• 补丁版本使用非负整数，并确保每次发布递增。\n\n" +
                "• 热更公告支持在独立编辑窗口中输入长文本。\n\n" +
                "• 仅选择本次需要生成补丁的资源模块。\n\n" +
                "• 上传前应核对补丁路径、版本和文件完整性。",
                cardWidth);
        }
        GUILayout.Space(12);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawManualCard("Bundle 设置",
                "• 下载地址用于运行时远程资源访问，正式环境建议使用 HTTPS/CDN。\n\n" +
                "• 推荐使用 LZ4，在加载速度和文件体积之间取得平衡。\n\n" +
                "• 加载模式和热更模式必须与当前项目发布策略一致。\n\n" +
                "• 下载线程数建议保持在 3–8，移动网络不宜设置过高。\n\n" +
                "• 加密密钥属于敏感配置，禁止提交到公开仓库。",
                cardWidth);
            GUILayout.Space(12);
            DrawManualCard("模块打包规则",
                "预制体包：以 Prefab 为入口收集依赖并生成 Bundle。\n\n" +
                "文件夹子包：按指定目录的子文件夹分别生成 Bundle。\n\n" +
                "文件夹包：将指定文件夹内容合并到一个 Bundle。\n\n" +
                "源文件配置：不打包为 Bundle，直接复制原文件，适用于 mp3、mp4 等。\n\n" +
                "修改规则后务必保存配置，再重新执行资源构建。",
                cardWidth);
        }
    }

    private static void DrawManualStep(string number, string title, string description)
    {
        // 步骤卡片使用更宽的内容内边距，避免文字紧贴左侧圆角和边框。
        manualStepCardStyle ??= new GUIStyle(ZMBuildStyles.BadgeBox)
        {
            padding = new RectOffset(18, 14, 11, 10)
        };
        using (new EditorGUILayout.VerticalScope(manualStepCardStyle,
                   GUILayout.Height(84), GUILayout.ExpandWidth(true)))
        {
            GUILayout.Label(number, new GUIStyle(ZMBuildStyles.SettingsSectionTitle)
            {
                fontSize = 15,
                normal = { textColor = ZMBuildStyles.Accent }
            }, GUILayout.Height(20));
            GUILayout.Label(title, ZMBuildStyles.SettingsLabel, GUILayout.Height(20));
            GUILayout.Label(description, new GUIStyle(ZMBuildStyles.SettingsHint)
            {
                wordWrap = true
            }, GUILayout.Height(32));
        }
    }

    private static void DrawManualCard(string title, string content, float width)
    {
        using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard,
                   GUILayout.Width(width), GUILayout.MinHeight(250)))
        {
            GUILayout.Label(title, ZMBuildStyles.SettingsSectionTitle, GUILayout.Height(24));
            GUILayout.Space(8);
            GUILayout.Label(WrapManualText(content, 50), new GUIStyle(ZMBuildStyles.SettingsHint)
            {
                fontSize = 12,
                wordWrap = true,
                richText = false
            }, GUILayout.ExpandHeight(true));
        }
    }

    /// <summary>
    /// 按字符数量预先整理手册正文，避免窗口较宽时出现过长的单行内容。
    /// 显式空行会被保留，便于维持段落层级。
    /// </summary>
    private static string WrapManualText(string content, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(content) || maximumCharacters <= 0)
            return content;

        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder(content.Length + lines.Length * 2);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            for (int offset = 0; offset < line.Length; offset += maximumCharacters)
            {
                int length = Mathf.Min(maximumCharacters, line.Length - offset);
                if (offset > 0)
                    builder.Append('\n');
                builder.Append(line, offset, length);
            }
            if (lineIndex < lines.Length - 1)
                builder.Append('\n');
        }
        return builder.ToString();
    }
}
