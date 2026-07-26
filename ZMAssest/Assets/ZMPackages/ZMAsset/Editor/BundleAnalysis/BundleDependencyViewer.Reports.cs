#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZM.Editor
{
    /// <summary>
    /// BundleDependencyViewer 的独立功能页面。业务状态由主窗口持有，
    /// 本文件仅维护该页面的交互、分析调度与结果展示。
    /// </summary>
    public partial class BundleDependencyViewer
    {
        private void ExportEmbeddedResults(string srcGame)
        {
            if (_t2EmbeddedResults.Count == 0) return;
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"{srcGame}_EmbeddedCrossAssets.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出深度扫描结果", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("SourceGame,SourceBundle,EmbeddedAssetPath,MatchedGame");
                foreach (var r in _t2EmbeddedResults)
                    foreach (var (ap, mg) in r.CrossAssets)
                        sw.WriteLine($"{CsvEscape(srcGame)},{CsvEscape(r.SourceBundle)},{CsvEscape(ap)},{CsvEscape(mg)}");
            }
            else
            {
                sw.WriteLine($"=== 深度扫描：跨游戏嵌入资源报告 ===");
                sw.WriteLine($"源游戏: {srcGame}    时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"问题 Bundle 数: {_t2EmbeddedResults.Count}    嵌入资源总数: {_t2EmbeddedResults.Sum(r => r.CrossAssets.Count)}");
                sw.WriteLine(new string('-', 90));
                foreach (var r in _t2EmbeddedResults)
                {
                    sw.WriteLine($"[{r.SourceBundle}]");
                    foreach (var (ap, mg) in r.CrossAssets)
                        sw.WriteLine($"  [{mg}] ↳ {ap}");
                    sw.WriteLine();
                }
            }
            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        private void ExportTab2(string srcGame)
        {
            if (_t2Results.Count == 0) return;
            string ext    = IsCsv ? "csv" : "txt";
            string defName = $"{srcGame}_CrossGameDeps.{ext}";
            string path   = EditorUtility.SaveFilePanel("导出跨游戏依赖报告", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("SourceGame,SourceBundle,DependencyBundle");
                foreach (var r in _t2Results)
                    foreach (var dep in r.CrossDeps)
                        sw.WriteLine($"{CsvEscape(srcGame)},{CsvEscape(r.SourceBundle)},{CsvEscape(dep)}");
            }
            else
            {
                sw.WriteLine($"=== 跨游戏依赖检测报告 ===");
                sw.WriteLine($"源游戏: {srcGame}    时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine($"问题 Bundle 数: {_t2Results.Count}    冗余引用总数: {_t2Results.Sum(r => r.CrossDeps.Length)}");
                sw.WriteLine(new string('-', 80));
                foreach (var r in _t2Results)
                {
                    sw.WriteLine($"[{r.SourceBundle}]");
                    foreach (var dep in r.CrossDeps)
                        sw.WriteLine($"  ↳ {dep}");
                    sw.WriteLine();
                }
            }

            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        private void ExportTab3()
        {
            if (_t3Summary.Count == 0) return;
            string ext    = IsCsv ? "csv" : "txt";
            string defName = $"ABDeps_GlobalReport_{DateTime.Now:yyyyMMdd_HHmm}.{ext}";
            string path   = EditorUtility.SaveFilePanel("导出全局依赖报告", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("GameName,TotalBundles,BundlesWithCrossDep,TotalCrossRefCount,CrossBreakdown,EmbeddedBundleCount,EmbeddedAssetCount,EmbeddedBreakdown");
                foreach (var s in _t3Summary)
                {
                    if (s.LoadFailed)
                        sw.WriteLine($"{CsvEscape(s.GameName)},,,,,,,Manifest加载失败");
                    else
                        sw.WriteLine($"{CsvEscape(s.GameName)},{s.TotalBundles},{s.BundlesWithCross},{s.TotalCrossCount},{CsvEscape(s.CrossBreakdown)},{s.EmbeddedBundleCount},{s.EmbeddedAssetCount},{CsvEscape(s.EmbeddedBreakdown)}");
                }
            }
            else
            {
                sw.WriteLine($"=== AB 全局依赖总览报告 ===");
                sw.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}    平台: {Platforms[_platformIdx]}");
                sw.WriteLine(new string('=', 80));
                foreach (var s in _t3Summary)
                {
                    if (s.LoadFailed)
                    {
                        sw.WriteLine($"[FAIL] {s.GameName} — Manifest 加载失败");
                        continue;
                    }
                    bool hasProblem = s.BundlesWithCross > 0 || s.EmbeddedBundleCount > 0;
                    string status = hasProblem ? "⚠ 存在冗余" : "✅ 干净";
                    sw.WriteLine($"[{status}] {s.GameName}");
                    sw.WriteLine($"  总Bundle: {s.TotalBundles}  含跨游戏Bundle: {s.BundlesWithCross}  冗余引用数: {s.TotalCrossCount}");
                    if (!string.IsNullOrEmpty(s.CrossBreakdown))
                        sw.WriteLine($"  Manifest冗余分布: {s.CrossBreakdown}");
                    if (s.EmbeddedBundleCount > 0)
                    {
                        sw.WriteLine($"  🔬 嵌入跨游戏资源Bundle: {s.EmbeddedBundleCount}  嵌入资源路径数: {s.EmbeddedAssetCount}");
                        if (!string.IsNullOrEmpty(s.EmbeddedBreakdown))
                            sw.WriteLine($"  嵌入分布: {s.EmbeddedBreakdown}");
                    }
                    sw.WriteLine();
                }
            }

            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }


    }
}
#endif
