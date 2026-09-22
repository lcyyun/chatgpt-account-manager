using Tomlyn;

namespace GptPlusManager.Core.Codex;

/// <summary>
/// 生成结果的 TOML 兜底校验。
///
/// 一道语法错误的 <c>config.toml</c> 会让 Codex 完全无法启动，用户看到的是
/// "os error" 之类的天书。写入前用真正的 TOML 解析器过一遍，坏文件在应用内就被拦住。
/// </summary>
internal static class TomlValidation
{
    public static void EnsureParsable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"待校验的配置文件不存在：{path}");
        }

        var text = File.ReadAllText(path);
        var syntax = Toml.Parse(text);
        if (!syntax.HasErrors)
        {
            return;
        }

        var first = syntax.Diagnostics.FirstOrDefault();
        var detail = first is null ? "未知语法错误" : $"{first.Message}";
        throw new InvalidOperationException($"生成的 config.toml 不是合法 TOML，已中止写入：{detail}");
    }
}
