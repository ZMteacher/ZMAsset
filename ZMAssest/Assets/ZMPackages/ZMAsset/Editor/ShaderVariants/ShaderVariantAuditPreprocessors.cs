using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Records candidates before ordinary project preprocessors. It deliberately leaves the list untouched.
    /// </summary>
    internal sealed class ShaderVariantAuditBeforePreprocessor : IPreprocessShaders
    {
        internal const int Order = -1000000;
        public int callbackOrder => Order;

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            ShaderVariantAuditBuildCoordinator.RecordBefore(shader, snippet, data);
        }
    }

    /// <summary>
    /// Records candidates after ordinary project preprocessors, then applies the immutable per-build policy.
    /// AuditOnly remains the default and leaves the list untouched.
    /// </summary>
    internal sealed class ShaderVariantAuditAfterPreprocessor : IPreprocessShaders
    {
        internal const int Order = 1000000;
        public int callbackOrder => Order;

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            ShaderVariantAuditBuildCoordinator.ProcessAfter(shader, snippet, data);
        }
    }
}
