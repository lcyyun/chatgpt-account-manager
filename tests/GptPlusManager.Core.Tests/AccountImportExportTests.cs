using GptPlusManager.Core.Models;
using GptPlusManager.Core.Services;

namespace GptPlusManager.Core.Tests;

public sealed class AccountImportExportTests
{
    [Fact]
    public void Parse_ReadsDelimitedTextRows()
    {
        var content = "a@example.com | pass1 | JBSWY3DPEHPK3PXP\r\nb@example.com | pass2\r\n";
        var records = AccountImportExport.Parse(content, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(2, records.Count);
        Assert.Equal("a@example.com", records[0].Email);
        Assert.Equal("pass1", records[0].Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", records[0].Secret);
        Assert.Equal("pass2", records[1].Password);
        Assert.Equal(string.Empty, records[1].Secret);
    }

    [Fact]
    public void Parse_ReadsJsonBackup()
    {
        var json = """
        [
          { "Email": "x@example.com", "Password": "px", "Secret": "SECRETX", "Note": "n" }
        ]
        """;
        var records = AccountImportExport.Parse(json, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Single(records);
        Assert.Equal("px", records[0].Password);
        Assert.Equal("n", records[0].Note);
    }

    [Fact]
    public void Parse_SkipsInvalidRowsAndIgnoresComments()
    {
        var content = "# comment\nonlyonecolumn\nok@example.com | p\n";
        var records = AccountImportExport.Parse(content, out var skipped);

        Assert.Equal(1, skipped);
        Assert.Single(records);
    }

    [Fact]
    public void Parse_ReturnsEmptyForGarbage()
    {
        var records = AccountImportExport.Parse("not a recognised payload", out var skipped);
        Assert.Empty(records);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void Merge_AddsNewAndUpdatesExistingByEmailCaseInsensitively()
    {
        var existing = new List<AccountRecord>
        {
            new() { Email = "keep@example.com", Password = "old", Secret = "OLDSECRET", Note = "old-note" }
        };
        var incoming = new List<AccountRecord>
        {
            new() { Email = "KEEP@example.com", Password = "new" },
            new() { Email = "fresh@example.com", Password = "p", Secret = "S" }
        };

        var summary = AccountImportExport.Merge(existing, incoming);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Updated);
        Assert.Equal(2, existing.Count);
        var updated = existing.Single(x => x.Email.Equals("keep@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("new", updated.Password);
        Assert.Equal("old-note", updated.Note);
    }

    [Fact]
    public void Merge_DoesNotClearExistingValuesWithEmptyIncomingFields()
    {
        var existing = new List<AccountRecord>
        {
            new() { Email = "a@example.com", Password = "pw", Secret = "SEC", PurchasedAt = "2026-01-01", Note = "keep" }
        };
        var incoming = new List<AccountRecord> { new() { Email = "a@example.com" } };

        AccountImportExport.Merge(existing, incoming);

        var target = existing[0];
        Assert.Equal("pw", target.Password);
        Assert.Equal("SEC", target.Secret);
        Assert.Equal("2026-01-01", target.PurchasedAt);
        Assert.Equal("keep", target.Note);
    }

    [Fact]
    public void Merge_CountsRowsWithoutEmailAsSkipped()
    {
        var existing = new List<AccountRecord>();
        var incoming = new List<AccountRecord> { new() { Email = "  " }, new() { Email = "ok@example.com" } };

        var summary = AccountImportExport.Merge(existing, incoming);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Skipped);
    }
}
