using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using ZM.Asset;

public partial class BuildWindows
{
    private static readonly PassType[] ShaderVariantPassTypes =
        (PassType[])Enum.GetValues(typeof(PassType));
    private static readonly string[] ShaderVariantPassTypeLabels =
        Enum.GetNames(typeof(PassType));
    private static readonly ShaderVariantAuditStage[] ShaderVariantStages =
        (ShaderVariantAuditStage[])Enum.GetValues(typeof(ShaderVariantAuditStage));
    private static readonly string[] ShaderVariantStageLabels =
        Enum.GetNames(typeof(ShaderVariantAuditStage));
    private static readonly ShaderVariantStrippingMode[] ShaderVariantStrippingModes =
        (ShaderVariantStrippingMode[])Enum.GetValues(typeof(ShaderVariantStrippingMode));
    private static readonly string[] ShaderVariantStrippingModeLabels =
    {
        "仅审计（不剔除）",
        "生成 Allowlist（安全剔除）"
    };

    [NonSerialized] private ShaderVariantAuditPageState shaderVariantPageState;

    private void DrawShaderVariants()
    {
        EnsureShaderVariantPageState();

        GUILayout.Space(28);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(34);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
            {
                GUILayout.Label("Shader 变体", ZMBuildStyles.Heading);
                GUILayout.Label("审计构建输入、管理显式规则，并保存 Editor 当前已记录的变体集合", ZMBuildStyles.Subtitle);
                GUILayout.Space(18);

                using (var scroll = new EditorGUILayout.ScrollViewScope(shaderVariantPageState.Scroll))
                {
                    shaderVariantPageState.Scroll = scroll.scrollPosition;
                    DrawShaderVariantConfiguration();
                    DrawShaderVariantRecording();
                    DrawShaderVariantExplicitRules();
                    DrawShaderVariantReports();
                    DrawShaderVariantConfigurationActions();
                    GUILayout.Space(18);
                }
            }
            GUILayout.Space(34);
        }
    }

    private void DrawShaderVariantConfiguration()
    {
        EditorGUI.BeginChangeCheck();
        DrawSettingsSection("审计配置", "设置会保存在 ProjectSettings，不进入运行时或 AssetBundle", () =>
        {
            bool strippingRequiresAudit =
                shaderVariantPageState.StrippingMode == ShaderVariantStrippingMode.GeneratedAllowlist;
            if (strippingRequiresAudit) shaderVariantPageState.IsEnabled = true;
            using (new EditorGUI.DisabledScope(strippingRequiresAudit))
            {
                shaderVariantPageState.IsEnabled = DrawSettingsSwitch(
                    "启用构建审计",
                    shaderVariantPageState.IsEnabled,
                    shaderVariantPageState.IsEnabled
                        ? "AssetBundle 构建时记录处理前后候选，并按所选策略执行末端处理"
                        : "已关闭；AssetBundle 构建流程不创建 Shader 审计会话");
            }
            shaderVariantPageState.ReportDirectory = DrawSettingsTextField(
                "报告目录",
                shaderVariantPageState.ReportDirectory,
                "工程内相对路径，禁止越出工程目录");
            shaderVariantPageState.DetailedVariantLimitText = DrawSettingsTextField(
                "详细项上限",
                shaderVariantPageState.DetailedVariantLimitText,
                $"最小 {ShaderVariantAuditSettings.MinimumDetailedVariantLimit:N0}；超出后仍保留总数并标记截断");
            shaderVariantPageState.ActiveProfileName = DrawSettingsTextField(
                "活动配置档",
                shaderVariantPageState.ActiveProfileName,
                "例如 Default、Low、High；审计、SVC 生成和 Bundle 注入必须一致");
            int strippingModeIndex = Mathf.Max(
                0,
                Array.IndexOf(ShaderVariantStrippingModes, shaderVariantPageState.StrippingMode));
            strippingModeIndex = DrawShaderVariantPopup(
                "ShaderVariants.StrippingMode",
                "剔除策略",
                strippingModeIndex,
                ShaderVariantStrippingModeLabels);
            shaderVariantPageState.StrippingMode = ShaderVariantStrippingModes[Mathf.Clamp(
                strippingModeIndex,
                0,
                ShaderVariantStrippingModes.Length - 1)];
            bool generatedStripping =
                shaderVariantPageState.StrippingMode == ShaderVariantStrippingMode.GeneratedAllowlist;
            if (generatedStripping)
            {
                shaderVariantPageState.IsEnabled = true;
                shaderVariantPageState.IncludeGeneratedCollections = true;
                shaderVariantPageState.RequireUpToDateCollection = true;
            }

            using (new EditorGUI.DisabledScope(generatedStripping))
            {
                shaderVariantPageState.IncludeGeneratedCollections = DrawSettingsSwitch(
                    "注入生成 SVC",
                    shaderVariantPageState.IncludeGeneratedCollections,
                    "只注入目标平台、模块、配置档和来源哈希均匹配的生成集合");
                if (!shaderVariantPageState.IncludeGeneratedCollections)
                    shaderVariantPageState.RequireUpToDateCollection = false;
                using (new EditorGUI.DisabledScope(!shaderVariantPageState.IncludeGeneratedCollections))
                {
                    shaderVariantPageState.RequireUpToDateCollection = DrawSettingsSwitch(
                        "严格新鲜度门禁",
                        shaderVariantPageState.RequireUpToDateCollection,
                        shaderVariantPageState.RequireUpToDateCollection
                            ? "缺失、跨平台或来源过期都会在 BuildPipeline 前阻止发布"
                            : "缺失或过期时记录警告并继续构建，不注入旧集合");
                }
            }
            if (generatedStripping)
                GUILayout.Label(
                    "安全剔除已启用：SVC、allowlist、Unity 版本或 Shader 依赖任一不一致都会中止构建；未知 Shader/Pass 保留全部。",
                    ZMBuildStyles.SettingsFieldHint);
        });
        if (EditorGUI.EndChangeCheck()) shaderVariantPageState.IsDirty = true;
    }

    private void DrawShaderVariantRecording()
    {
        DrawSettingsSection("当前变体录制", "在 Editor 中实际遍历场景、角色与特效后，将已编译变体保存为 SVC 资产", () =>
        {
            string countText = shaderVariantPageState.RecordingCountsAvailable
                ? $"已记录 {shaderVariantPageState.RecordedShaderCount:N0} 个 Shader · {shaderVariantPageState.RecordedVariantCount:N0} 个变体"
                : "当前记录统计不可用";
            GUILayout.Label(countText, ZMBuildStyles.SettingsSectionTitle);
            GUILayout.Label(ShaderVariantRecordingService.CompatibilityMessage, ZMBuildStyles.SettingsHint);
            GUILayout.Space(10);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开 Graphics 设置", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(138)))
                    SettingsService.OpenProjectSettings("Project/Graphics");
                GUILayout.Space(8);
                using (new EditorGUI.DisabledScope(!ShaderVariantRecordingService.IsAvailable))
                {
                    if (GUILayout.Button("刷新统计", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(96)))
                        RefreshShaderVariantRecordingCounts();
                    GUILayout.Space(8);
                    if (GUILayout.Button("保存为 SVC", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(108)))
                        SaveCurrentShaderVariantCollection();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("清空记录", ZMBuildStyles.CompactDangerButton, GUILayout.Width(96)))
                        ClearCurrentShaderVariantCollection();
                }
            }
            GUILayout.Space(5);
            GUILayout.Label(
                "建议从空记录开始，在目标平台质量设置下自动遍历关键玩法，再保存集合；仅打开工程并不能覆盖运行时动态关键字。",
                ZMBuildStyles.SettingsFieldHint);
        });
    }

    private void DrawShaderVariantExplicitRules()
    {
        EditorGUI.BeginChangeCheck();
        DrawSettingsSection("显式变体规则", "补充材质静态扫描无法发现的运行时关键字组合；规则参与审计清单，不会隐式修改 SVC", () =>
        {
            DrawShaderVariantRuleToolbar();
            if (shaderVariantPageState.Rules.Count == 0)
            {
                GUILayout.Space(8);
                GUILayout.Label("当前没有显式规则。动态 EnableKeyword、全局关键字或脚本驱动 Pass 应在这里声明。", ZMBuildStyles.SettingsHint);
                return;
            }

            shaderVariantPageState.ActiveRuleIndex = Mathf.Clamp(
                shaderVariantPageState.ActiveRuleIndex,
                0,
                shaderVariantPageState.Rules.Count - 1);
            ShaderVariantExplicitRuleDraft rule =
                shaderVariantPageState.Rules[shaderVariantPageState.ActiveRuleIndex];
            GUILayout.Space(12);
            rule.IsEnabled = DrawSettingsSwitch(
                "启用规则",
                rule.IsEnabled,
                rule.IsEnabled ? "构建审计会解析并写入规范清单" : "保留配置，但本规则不会进入构建报告");
            DrawShaderAssetSelector(rule);
            rule.BuiltInShaderName = DrawSettingsTextField(
                "内置 Shader",
                rule.BuiltInShaderName,
                "资源 GUID 无法解析时使用，例如 UI/Default");
            rule.ModuleName = DrawSettingsTextField(
                "模块",
                rule.ModuleName,
                "可选；用于区分 GameOne、GameTwo 等业务模块");
            rule.ProfileName = DrawSettingsTextField(
                "配置档",
                rule.ProfileName,
                "可选；例如 Low、High、Android、iOS");

            int passIndex = Mathf.Max(0, Array.IndexOf(ShaderVariantPassTypes, rule.PassType));
            passIndex = DrawShaderVariantPopup(
                $"ShaderVariants.PassType.{shaderVariantPageState.ActiveRuleIndex}",
                "Pass Type",
                passIndex,
                ShaderVariantPassTypeLabels);
            rule.PassType = ShaderVariantPassTypes[Mathf.Clamp(passIndex, 0, ShaderVariantPassTypes.Length - 1)];
            rule.PassName = DrawSettingsTextField(
                "Pass 名称",
                rule.PassName,
                "可选；用于审计定位，不参与 Unity 编译 API 调用");

            int stageIndex = Mathf.Max(0, Array.IndexOf(ShaderVariantStages, rule.ShaderStage));
            stageIndex = DrawShaderVariantPopup(
                $"ShaderVariants.Stage.{shaderVariantPageState.ActiveRuleIndex}",
                "Shader Stage",
                stageIndex,
                ShaderVariantStageLabels);
            rule.ShaderStage = ShaderVariantStages[Mathf.Clamp(stageIndex, 0, ShaderVariantStages.Length - 1)];
            rule.KeywordsText = DrawSettingsTextField(
                "关键字",
                rule.KeywordsText,
                "空格、逗号、分号或换行分隔；保存时去重并稳定排序");
            rule.Reason = DrawShaderVariantTextArea(
                "规则说明",
                rule.Reason,
                "记录来源、触发脚本和验证场景，方便代码审查");

            if (rule.IsEnabled &&
                string.IsNullOrWhiteSpace(rule.ShaderGuid) &&
                string.IsNullOrWhiteSpace(rule.BuiltInShaderName))
            {
                GUILayout.Label("此规则尚未指定 Shader，保存时会被拒绝。", ZMBuildStyles.BuildFailureDetail);
            }
        });
        if (EditorGUI.EndChangeCheck()) shaderVariantPageState.IsDirty = true;
    }

    private void DrawShaderVariantRuleToolbar()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (shaderVariantPageState.Rules.Count > 0)
            {
                string[] labels = BuildShaderVariantRuleLabels();
                Rect selectorRect = GUILayoutUtility.GetRect(180, 34, GUILayout.ExpandWidth(true));
                GUI.Box(selectorRect, GUIContent.none, ZMBuildStyles.FieldBox);
                int activeIndex = Mathf.Clamp(
                    shaderVariantPageState.ActiveRuleIndex,
                    0,
                    labels.Length - 1);
                if (GUI.Button(selectorRect, labels[activeIndex], ZMBuildStyles.SettingsPopup))
                {
                    Rect screenRect = GUIUtility.GUIToScreenRect(selectorRect);
                    DarkDropdownWindow.Show(screenRect, labels, activeIndex, index =>
                    {
                        shaderVariantPageState.ActiveRuleIndex = index;
                        Repaint();
                    });
                }
                DrawPopupArrow(selectorRect);
                GUILayout.Space(8);
            }

            if (GUILayout.Button("添加规则", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(96)))
            {
                shaderVariantPageState.Rules.Add(new ShaderVariantExplicitRuleDraft());
                shaderVariantPageState.ActiveRuleIndex = shaderVariantPageState.Rules.Count - 1;
                shaderVariantPageState.IsDirty = true;
                GUI.FocusControl(null);
            }

            if (shaderVariantPageState.Rules.Count > 0)
            {
                GUILayout.Space(8);
                if (GUILayout.Button("删除", ZMBuildStyles.CompactDangerButton, GUILayout.Width(76)) &&
                    EditorUtility.DisplayDialog(
                        "删除显式 Shader 规则",
                        $"确认删除规则 {shaderVariantPageState.ActiveRuleIndex + 1}？未保存的修改无法恢复。",
                        "删除",
                        "取消"))
                {
                    shaderVariantPageState.Rules.RemoveAt(shaderVariantPageState.ActiveRuleIndex);
                    shaderVariantPageState.ActiveRuleIndex = Mathf.Clamp(
                        shaderVariantPageState.ActiveRuleIndex,
                        0,
                        shaderVariantPageState.Rules.Count - 1);
                    shaderVariantPageState.IsDirty = true;
                    GUI.FocusControl(null);
                }
            }
        }
    }

    private void DrawShaderAssetSelector(ShaderVariantExplicitRuleDraft rule)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(72)))
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
            {
                GUILayout.Space(3);
                GUILayout.Label("Shader 资源", ZMBuildStyles.SettingsLabel);
                GUILayout.Label("拖入资源或使用当前选择", ZMBuildStyles.SettingsFieldHint);
            }
            GUILayout.Space(12);
            using (new EditorGUILayout.VerticalScope())
            {
                string assetPath = string.IsNullOrWhiteSpace(rule.ShaderGuid)
                    ? string.Empty
                    : AssetDatabase.GUIDToAssetPath(rule.ShaderGuid);
                string display = !string.IsNullOrWhiteSpace(assetPath)
                    ? assetPath
                    : string.IsNullOrWhiteSpace(rule.ShaderGuid)
                        ? "拖入 Shader 资源到这里"
                        : $"资源缺失 · GUID {rule.ShaderGuid}";
                Rect assetRect = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
                GUI.Box(assetRect, GUIContent.none, ZMBuildStyles.FieldBox);
                GUI.Label(assetRect, display, ZMBuildStyles.FlatField);
                HandleShaderDragAndDrop(assetRect, rule);

                GUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("使用当前选择", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(108), GUILayout.Height(24)))
                    {
                        Shader selectedShader = Selection.activeObject as Shader;
                        if (selectedShader == null)
                            SetShaderVariantMessage("当前 Project 选择不是 Shader 资源。", true);
                        else
                            AssignShaderAsset(rule, selectedShader);
                    }
                    GUILayout.Space(6);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(rule.ShaderGuid)))
                    {
                        if (GUILayout.Button("清除", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(62), GUILayout.Height(24)))
                            rule.ShaderGuid = string.Empty;
                    }
                }
            }
        }
    }

    private void HandleShaderDragAndDrop(Rect rect, ShaderVariantExplicitRuleDraft rule)
    {
        Event current = Event.current;
        if (!rect.Contains(current.mousePosition) ||
            (current.type != EventType.DragUpdated && current.type != EventType.DragPerform))
            return;

        Shader shader = null;
        foreach (UnityEngine.Object candidate in DragAndDrop.objectReferences)
        {
            shader = candidate as Shader;
            if (shader != null) break;
        }
        if (shader == null) return;

        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (current.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            AssignShaderAsset(rule, shader);
        }
        current.Use();
    }

    private void AssignShaderAsset(ShaderVariantExplicitRuleDraft rule, Shader shader)
    {
        string assetPath = shader == null ? string.Empty : AssetDatabase.GetAssetPath(shader);
        string shaderGuid = string.IsNullOrWhiteSpace(assetPath)
            ? string.Empty
            : AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrWhiteSpace(shaderGuid))
        {
            SetShaderVariantMessage("所选 Shader 不是可持久化的工程资源；内置 Shader 请填写名称。", true);
            return;
        }

        rule.ShaderGuid = shaderGuid;
        shaderVariantPageState.IsDirty = true;
        SetShaderVariantMessage($"已选择 Shader：{assetPath}", false);
    }

    private int DrawShaderVariantPopup(string key, string label, int selected, string[] labels)
    {
        if (popupSelections.TryGetValue(key, out int pendingSelection))
        {
            popupSelections.Remove(key);
            selected = Mathf.Clamp(pendingSelection, 0, labels.Length - 1);
        }

        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(48)))
        {
            GUILayout.Label(label, ZMBuildStyles.SettingsLabel, GUILayout.Width(150));
            GUILayout.Space(12);
            Rect rect = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, ZMBuildStyles.FieldBox);
            selected = Mathf.Clamp(selected, 0, labels.Length - 1);
            if (GUI.Button(rect, labels[selected], ZMBuildStyles.SettingsPopup))
            {
                Rect screenRect = GUIUtility.GUIToScreenRect(rect);
                DarkDropdownWindow.Show(screenRect, labels, selected, index =>
                {
                    popupSelections[key] = index;
                    shaderVariantPageState.IsDirty = true;
                    Repaint();
                });
            }
            DrawPopupArrow(rect);
            return selected;
        }
    }

    private static string DrawShaderVariantTextArea(string label, string value, string hint)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(78)))
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
            {
                GUILayout.Space(3);
                GUILayout.Label(label, ZMBuildStyles.SettingsLabel);
                GUILayout.Label(hint, ZMBuildStyles.SettingsFieldHint);
            }
            GUILayout.Space(12);
            Rect rect = GUILayoutUtility.GetRect(100, 68, GUILayout.ExpandWidth(true));
            return ZMBuildStyles.DrawTextArea(rect, value, ZMBuildStyles.TextArea);
        }
    }

    private string[] BuildShaderVariantRuleLabels()
    {
        string[] labels = new string[shaderVariantPageState.Rules.Count];
        for (int index = 0; index < labels.Length; index++)
        {
            ShaderVariantExplicitRuleDraft rule = shaderVariantPageState.Rules[index];
            string shaderName = ResolveShaderRuleDisplayName(rule);
            labels[index] = $"规则 {index + 1:00} · {shaderName}";
        }
        return labels;
    }

    private static string ResolveShaderRuleDisplayName(ShaderVariantExplicitRuleDraft rule)
    {
        if (rule == null) return "无效规则";
        if (!string.IsNullOrWhiteSpace(rule.ShaderGuid))
        {
            string path = AssetDatabase.GUIDToAssetPath(rule.ShaderGuid);
            if (!string.IsNullOrWhiteSpace(path)) return Path.GetFileNameWithoutExtension(path);
        }
        return string.IsNullOrWhiteSpace(rule.BuiltInShaderName)
            ? "未指定 Shader"
            : rule.BuiltInShaderName.Trim();
    }

    private void DrawShaderVariantReports()
    {
        DrawSettingsSection("最近构建报告", "AssetBundle 构建完成后生成 Markdown、审计 JSON 与规范清单", () =>
        {
            ShaderVariantAuditArtifactLocator.Result artifacts = shaderVariantPageState.Artifacts;
            if (!string.IsNullOrEmpty(shaderVariantPageState.ArtifactError))
                GUILayout.Label(shaderVariantPageState.ArtifactError, ZMBuildStyles.BuildFailureDetail);
            else if (artifacts == null || !artifacts.HasAnyArtifact)
                GUILayout.Label("报告目录中尚无审计产物；完成一次启用审计的 AssetBundle 构建后即可查看。", ZMBuildStyles.SettingsHint);
            else
            {
                DrawShaderVariantArtifactRow("审计报告", artifacts.MarkdownPath);
                DrawShaderVariantArtifactRow("报告 JSON", artifacts.ReportJsonPath);
                DrawShaderVariantArtifactRow("规范清单", artifacts.ManifestJsonPath);
            }

            GUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("刷新报告", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(96)))
                    RefreshShaderVariantArtifacts();
                GUILayout.Space(8);
                using (new EditorGUI.DisabledScope(
                           artifacts == null || string.IsNullOrWhiteSpace(artifacts.ReportJsonPath)))
                {
                    if (GUILayout.Button("从报告生成 SVC", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(132)))
                        GenerateShaderVariantCollections();
                }
                GUILayout.Space(8);
                using (new EditorGUI.DisabledScope(
                           artifacts == null || !Directory.Exists(artifacts.DirectoryPath)))
                {
                    if (GUILayout.Button("打开报告目录", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(120)))
                        EditorUtility.RevealInFinder(artifacts.DirectoryPath);
                }
            }
        });
    }

    private static void DrawShaderVariantArtifactRow(string label, string path)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(34)))
        {
            GUILayout.Label(label, ZMBuildStyles.SettingsLabel, GUILayout.Width(90));
            string display = string.IsNullOrWhiteSpace(path) ? "未生成" : Path.GetFileName(path);
            GUILayout.Label(display, ZMBuildStyles.StatusLabel, GUILayout.ExpandWidth(true));
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            {
                if (GUILayout.Button("打开", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(68)))
                    EditorUtility.OpenWithDefaultApp(path);
            }
        }
    }

    private void DrawShaderVariantConfigurationActions()
    {
        using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
        {
            if (!string.IsNullOrWhiteSpace(shaderVariantPageState.Message))
            {
                GUILayout.Label(
                    shaderVariantPageState.Message,
                    shaderVariantPageState.MessageIsError
                        ? ZMBuildStyles.BuildFailureDetail
                        : ZMBuildStyles.SettingsHint);
                GUILayout.Space(8);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(
                    shaderVariantPageState.IsDirty ? "有未保存修改" : "配置已同步",
                    ZMBuildStyles.StatusLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("重新载入", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(96)))
                    ReloadShaderVariantConfiguration();
                GUILayout.Space(8);
                if (GUILayout.Button("保存配置", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(108)))
                    SaveShaderVariantConfiguration();
            }
        }
    }

    private void EnsureShaderVariantPageState()
    {
        if (shaderVariantPageState != null) return;
        shaderVariantPageState = new ShaderVariantAuditPageState();
        ReloadShaderVariantConfiguration();
        RefreshShaderVariantRecordingCounts();
    }

    private void ReloadShaderVariantConfiguration()
    {
        ShaderVariantAuditEditorConfiguration configuration =
            ShaderVariantAuditSettings.instance.CreateEditorConfiguration();
        shaderVariantPageState.IsEnabled = configuration.IsEnabled;
        shaderVariantPageState.ReportDirectory = configuration.ReportDirectory;
        shaderVariantPageState.DetailedVariantLimitText = configuration.MaxDetailedVariants.ToString();
        shaderVariantPageState.ActiveProfileName = configuration.ActiveProfileName;
        shaderVariantPageState.IncludeGeneratedCollections = configuration.IncludeGeneratedCollections;
        shaderVariantPageState.RequireUpToDateCollection = configuration.RequireUpToDateCollection;
        shaderVariantPageState.StrippingMode = configuration.StrippingMode;
        shaderVariantPageState.Rules = configuration.ExplicitRules;
        shaderVariantPageState.ActiveRuleIndex = Mathf.Clamp(
            shaderVariantPageState.ActiveRuleIndex,
            0,
            shaderVariantPageState.Rules.Count - 1);
        shaderVariantPageState.IsDirty = false;
        shaderVariantPageState.Message = "已从 ProjectSettings 重新载入 Shader 变体配置。";
        shaderVariantPageState.MessageIsError = false;
        RefreshShaderVariantArtifacts();
    }

    private void SaveShaderVariantConfiguration()
    {
        if (!int.TryParse(
                shaderVariantPageState.DetailedVariantLimitText,
                out int detailedVariantLimit))
        {
            SetShaderVariantMessage("详细项上限必须是有效整数。", true);
            return;
        }

        if (!ShaderVariantAuditSettings.instance.TryApplyEditorConfiguration(
                shaderVariantPageState.IsEnabled,
                shaderVariantPageState.ReportDirectory,
                detailedVariantLimit,
                shaderVariantPageState.ActiveProfileName,
                shaderVariantPageState.IncludeGeneratedCollections,
                shaderVariantPageState.RequireUpToDateCollection,
                shaderVariantPageState.StrippingMode,
                shaderVariantPageState.Rules,
                out string error))
        {
            SetShaderVariantMessage(error, true);
            return;
        }

        ShaderVariantAuditEditorConfiguration configuration =
            ShaderVariantAuditSettings.instance.CreateEditorConfiguration();
        shaderVariantPageState.ReportDirectory = configuration.ReportDirectory;
        shaderVariantPageState.DetailedVariantLimitText = configuration.MaxDetailedVariants.ToString();
        shaderVariantPageState.ActiveProfileName = configuration.ActiveProfileName;
        shaderVariantPageState.IncludeGeneratedCollections = configuration.IncludeGeneratedCollections;
        shaderVariantPageState.RequireUpToDateCollection = configuration.RequireUpToDateCollection;
        shaderVariantPageState.StrippingMode = configuration.StrippingMode;
        shaderVariantPageState.Rules = configuration.ExplicitRules;
        shaderVariantPageState.ActiveRuleIndex = Mathf.Clamp(
            shaderVariantPageState.ActiveRuleIndex,
            0,
            shaderVariantPageState.Rules.Count - 1);
        shaderVariantPageState.IsDirty = false;
        SetShaderVariantMessage("Shader 变体配置已保存到 ProjectSettings。", false);
        RefreshShaderVariantArtifacts();
    }

    private void RefreshShaderVariantRecordingCounts()
    {
        shaderVariantPageState.RecordingCountsAvailable =
            ShaderVariantRecordingService.TryGetCounts(
                out int shaderCount,
                out int variantCount,
                out string error);
        shaderVariantPageState.RecordedShaderCount = shaderCount;
        shaderVariantPageState.RecordedVariantCount = variantCount;
        if (!shaderVariantPageState.RecordingCountsAvailable)
            SetShaderVariantMessage(error, true);
        Repaint();
    }

    private void ClearCurrentShaderVariantCollection()
    {
        if (!EditorUtility.DisplayDialog(
                "清空当前 Shader 变体记录",
                "此操作会清除 Editor 本次会话已记录、但尚未保存的 Shader 变体。确认继续？",
                "清空记录",
                "取消"))
            return;

        if (!ShaderVariantRecordingService.TryClear(out string error))
        {
            SetShaderVariantMessage(error, true);
            return;
        }

        RefreshShaderVariantRecordingCounts();
        SetShaderVariantMessage("当前 Shader 变体记录已清空。", false);
    }

    private void SaveCurrentShaderVariantCollection()
    {
        string assetPath = EditorUtility.SaveFilePanelInProject(
            "保存当前 Shader 变体集合",
            "ZMAsset_RecordedVariants",
            "shadervariants",
            "请选择 Assets 下的保存位置。已存在的资源需要在系统确认后才会覆盖。");
        if (string.IsNullOrWhiteSpace(assetPath)) return;

        if (!ShaderVariantRecordingService.TrySave(assetPath, out string error))
        {
            SetShaderVariantMessage(error, true);
            return;
        }

        SetShaderVariantMessage($"当前 Shader 变体已保存：{assetPath}", false);
        RefreshShaderVariantRecordingCounts();
    }

    private void GenerateShaderVariantCollections()
    {
        try
        {
            if (shaderVariantPageState.IsDirty)
                throw new InvalidOperationException("请先保存当前 Shader 变体配置，再从报告生成 SVC。");
            ShaderVariantCollectionGenerationResult result =
                ShaderVariantCollectionGenerator.GenerateLatest(
                    shaderVariantPageState.ReportDirectory,
                    shaderVariantPageState.ActiveProfileName,
                    EditorUserBuildSettings.activeBuildTarget);
            string warningSuffix = result.Warnings.Count == 0
                ? string.Empty
                : $"；跳过/合并警告 {result.Warnings.Count} 条，详见 Console";
            foreach (string warning in result.Warnings)
                Debug.LogWarning($"Shader SVC 生成：{warning}");
            SetShaderVariantMessage(
                $"SVC 生成完成：{result.ModuleCount} 个模块，{result.ShaderCount:N0} 个 Shader，" +
                $"{result.VariantCount:N0} 个变体，{result.StripManifestPaths.Count:N0} 份剔除 allowlist" +
                $"{warningSuffix}。下一次构建将执行来源新鲜度检查。",
                false);
            if (result.ManifestAssetPaths.Count > 0)
            {
                UnityEngine.Object manifest = AssetDatabase.LoadMainAssetAtPath(result.ManifestAssetPaths[0]);
                if (manifest != null) EditorGUIUtility.PingObject(manifest);
            }
        }
        catch (Exception exception)
        {
            SetShaderVariantMessage(
                $"SVC 生成失败：{exception.GetType().Name}：{exception.Message}",
                true);
        }
    }

    private void RefreshShaderVariantArtifacts()
    {
        if (shaderVariantPageState == null) return;
        if (!ShaderVariantAuditArtifactLocator.TryFindLatest(
                shaderVariantPageState.ReportDirectory,
                out ShaderVariantAuditArtifactLocator.Result artifacts,
                out string error))
        {
            shaderVariantPageState.Artifacts = null;
            shaderVariantPageState.ArtifactError = error;
            return;
        }

        shaderVariantPageState.Artifacts = artifacts;
        shaderVariantPageState.ArtifactError = string.Empty;
    }

    private void SetShaderVariantMessage(string message, bool isError)
    {
        shaderVariantPageState.Message = message ?? string.Empty;
        shaderVariantPageState.MessageIsError = isError;
        Repaint();
    }

    private sealed class ShaderVariantAuditPageState
    {
        internal Vector2 Scroll;
        internal bool IsEnabled;
        internal string ReportDirectory = ShaderVariantAuditSettings.DefaultReportDirectory;
        internal string DetailedVariantLimitText =
            ShaderVariantAuditSettings.DefaultDetailedVariantLimit.ToString();
        internal string ActiveProfileName = ShaderVariantPrewarmPaths.DefaultProfileName;
        internal bool IncludeGeneratedCollections = true;
        internal bool RequireUpToDateCollection;
        internal ShaderVariantStrippingMode StrippingMode = ShaderVariantStrippingMode.AuditOnly;
        internal List<ShaderVariantExplicitRuleDraft> Rules =
            new List<ShaderVariantExplicitRuleDraft>();
        internal int ActiveRuleIndex;
        internal bool IsDirty;
        internal bool RecordingCountsAvailable;
        internal int RecordedShaderCount;
        internal int RecordedVariantCount;
        internal ShaderVariantAuditArtifactLocator.Result Artifacts;
        internal string ArtifactError = string.Empty;
        internal string Message = string.Empty;
        internal bool MessageIsError;
    }
}
