using System;

namespace ZM.ZMAsset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// 校验资源模块名称，保证模块生命周期和热更新门面使用相同的参数约束及错误信息。
        /// </summary>
        internal static void ValidateModuleName(string moduleName, string parameterName = "moduleName")
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("资源模块名称不能为空。", parameterName);

            if (char.IsWhiteSpace(moduleName[0]) || char.IsWhiteSpace(moduleName[moduleName.Length - 1]))
                throw new ArgumentException($"资源模块名称不能包含首尾空白字符：[{moduleName}]。", parameterName);

            if (string.Equals(moduleName, BundleModuleName.None, StringComparison.Ordinal))
                throw new ArgumentException("必须指定真实资源模块，不能使用 None。", parameterName);

            if (moduleName.IndexOf('/') >= 0 ||
                moduleName.IndexOf('\\') >= 0 ||
                moduleName.Contains("..") ||
                string.Equals(moduleName, ".", StringComparison.Ordinal))
                throw new ArgumentException($"资源模块名称不能包含路径字符：[{moduleName}]。", parameterName);

            for (int index = 0; index < moduleName.Length; index++)
            {
                char character = moduleName[index];
                if (char.IsControl(character) ||
                    character == ':' ||
                    character == '*' ||
                    character == '?' ||
                    character == '"' ||
                    character == '<' ||
                    character == '>' ||
                    character == '|')
                {
                    throw new ArgumentException($"资源模块名称包含文件系统非法字符：[{moduleName}]。", parameterName);
                }
            }
        }
        
        /// <summary>
        /// 统一校验资源路径，避免格式错误一路传到底层后变成难定位的“资源不存在”。
        /// 校验始终开启，确保测试环境与正式环境使用完全相同的 CRC 输入规则。
        /// 性能概述：只在资源 API 调用时执行一次， 复杂度为 O(n)， 不创建临时字符串、数组或集合， 不运行在每帧逻辑、下载循环或资源解包循环中
        /// 相比实际的 Bundle 查找、磁盘读取和资源加载，这部分开销可以忽略。
        /// </summary>
        /// <remarks>
        /// 资源路径使用 Unity 约定的相对路径和正斜杠。这里选择拒绝异常格式而不是静默修正，
        /// 因为路径会参与 CRC 计算；如果调用方传入的原始路径与构建清单不一致，自动修正会掩盖真正的配置错误。
        /// </summary>
        public static void ValidateAssetPath(string path, string parameterName = "path")
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("资源路径不能为空。", parameterName);

            if (char.IsWhiteSpace(path[0]) || char.IsWhiteSpace(path[^1]))
                throw new ArgumentException($"资源路径不能包含首尾空白字符：[{path}]。", parameterName);

            if (path.IndexOf('\\') >= 0)
                throw new ArgumentException($"资源路径必须使用正斜杠 '/'，不能包含反斜杠：[{path}]。", parameterName);

            if (path.IndexOf("//", StringComparison.Ordinal) >= 0)
                throw new ArgumentException($"资源路径不能包含连续的斜杠：[{path}]。", parameterName);

            if (path.StartsWith("/", StringComparison.Ordinal) ||
                path.EndsWith("/", StringComparison.Ordinal) ||
                (path.Length >= 2 && path[1] == ':'))
                throw new ArgumentException($"资源路径必须是非空的相对资源路径：[{path}]。", parameterName);

            for (int index = 0; index < path.Length; index++)
            {
                if (char.IsControl(path[index]))
                    throw new ArgumentException($"资源路径不能包含控制字符：[{path}]。", parameterName);
            }

            int segmentStart = 0;
            for (int index = 0; index <= path.Length; index++)
            {
                if (index != path.Length && path[index] != '/')
                    continue;

                int segmentLength = index - segmentStart;
                if ((segmentLength == 1 && path[segmentStart] == '.') ||
                    (segmentLength == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.'))
                    throw new ArgumentException($"资源路径不能包含 '.' 或 '..' 路径段：[{path}]。", parameterName);

                segmentStart = index + 1;
            }
        }
    }
}
