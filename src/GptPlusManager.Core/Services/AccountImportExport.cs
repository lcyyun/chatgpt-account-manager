using System.Text.Json;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Persistence;

namespace GptPlusManager.Core.Services;

/// <summary>摘要：导入新增、更新与跳过的条目数量。</summary>
public sealed record ImportSummary(int Added, int Updated, int Skipped)
{
    public int Total => Added + Updated;
}

/// <summary>
/// 导出文件解析与合并。支持程序导出的 JSON 备份和 <c>邮箱 | 密码 | 密钥</c> 文本格式。
/// 合并以邮箱为唯一键，不覆盖导入文件中的空值，避免误删已有密码或 2FA 密钥。
/// </summary>
public static class AccountImportExport
{
    private static readonly char[] RowSeparators = ['|', '\t'];

    /// <summary>解析 UTF-8 文本内容。自动识别 JSON 数组或分隔符文本。</summary>
    public static IReadOnlyList<AccountRecord> Parse(string content, out int skipped)
    {
        skipped = 0;
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            return ParseJson(content, out skipped);
        }

        return ParseDelimited(content, out skipped);
    }

    private static IReadOnlyList<AccountRecord> ParseJson(string content, out int skipped)
    {
        skipped = 0;
        List<AccountRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<AccountRecord>>(content, JsonDefaults.Compact);
        }
        catch (JsonException)
        {
            // 兼容仅含单个对象的导出文件。
            try
            {
                var single = JsonSerializer.Deserialize<AccountRecord>(content, JsonDefaults.Compact);
                records = single is null ? null : [single];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        if (records is null)
        {
            return [];
        }

        var result = new List<AccountRecord>(records.Count);
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.Email))
            {
                skipped++;
                continue;
            }
            record.NormalizeStrings();
            result.Add(record);
        }
        return result;
    }

    private static IReadOnlyList<AccountRecord> ParseDelimited(string content, out int skipped)
    {
        skipped = 0;
        var result = new List<AccountRecord>();
        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(RowSeparators);
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                skipped++;
                continue;
            }

            result.Add(new AccountRecord
            {
                Email = parts[0],
                Password = parts.Length > 1 ? parts[1] : string.Empty,
                Secret = parts.Length > 2 ? parts[2] : string.Empty
            });
        }
        return result;
    }

    /// <summary>
    /// 将导入项合并进现有集合。邮箱已存在时更新非空字段，否则追加为新账号。
    /// 返回修改后的集合，调用方负责持久化。
    /// </summary>
    public static ImportSummary Merge(IList<AccountRecord> existing, IEnumerable<AccountRecord> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        var added = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var record in incoming)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.Email))
            {
                skipped++;
                continue;
            }

            var email = record.Email.Trim();
            var target = existing.FirstOrDefault(x =>
                string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                record.Email = email;
                existing.Add(record);
                added++;
                continue;
            }

            // 仅覆盖导入文件中确实提供了值的字段。
            if (!string.IsNullOrEmpty(record.Password)) target.Password = record.Password;
            if (!string.IsNullOrEmpty(record.Secret)) target.Secret = record.Secret;
            if (!string.IsNullOrEmpty(record.PurchasedAt)) target.PurchasedAt = record.PurchasedAt;
            if (!string.IsNullOrEmpty(record.Note)) target.Note = record.Note;
            updated++;
        }

        return new ImportSummary(added, updated, skipped);
    }
}
