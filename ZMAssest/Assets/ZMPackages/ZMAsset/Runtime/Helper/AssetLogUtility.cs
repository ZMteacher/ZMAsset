using System;
using System.IO;

namespace ZM.Asset
{
    /// <summary>
    /// 生成可安全写入日志的资源地址，避免 CDN 查询参数、片段和 URL 用户信息泄露。
    /// </summary>
    internal static class AssetLogUtility
    {
        internal static string SanitizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "<empty>";

            string value = url.Trim();
            if (Path.IsPathRooted(value))
                return $"file:///{Path.GetFileName(value)}";

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
            {
                if (uri.IsFile)
                    return $"file:///{Path.GetFileName(uri.LocalPath)}";

                try
                {
                    // SchemeAndServer 不包含 userInfo；Path 不包含 query 和 fragment。
                    return uri.GetComponents(
                        UriComponents.SchemeAndServer | UriComponents.Path,
                        UriFormat.UriEscaped);
                }
                catch (InvalidOperationException)
                {
                    // 非层级 URI（例如自定义协议）继续使用下方的保守截断逻辑。
                }
            }

            int queryIndex = value.IndexOf('?');
            int fragmentIndex = value.IndexOf('#');
            int endIndex = value.Length;
            if (queryIndex >= 0)
                endIndex = queryIndex;
            if (fragmentIndex >= 0 && fragmentIndex < endIndex)
                endIndex = fragmentIndex;
            return value.Substring(0, endIndex);
        }
    }
}
