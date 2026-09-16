using GptPlusManager.Core.Persistence;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

public sealed class LegacyTokenStoreTests
{
    [Theory]
    [InlineData("", "-811c9dc5.token")]
    [InlineData("a", "a-e40c292c.token")]
    [InlineData("foobar", "foobar-bf9cf968.token")]
    public void GetTokenPath_PreservesLegacyFnv1aFilename(string email, string expectedFileName)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        using var store = new LegacyTokenStore(paths);

        var path = store.GetTokenPath(email);

        Assert.Equal(Path.Combine(paths.TokensDirectory, expectedFileName), path);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(paths.TokensDirectory));
    }

    [Fact]
    public void GetTokenPath_TruncatesLegacySafeNameToFortyCharacters()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        using var store = new LegacyTokenStore(paths);
        const string email = "abcdefghijklmnopqrstuvwxyz1234567890ABCDEFGHIJ@example.test";

        var fileName = Path.GetFileName(store.GetTokenPath(email));

        Assert.StartsWith(email[..40] + "-", fileName, StringComparison.Ordinal);
        Assert.Matches("^[^-]{40}-[0-9a-f]{8}\\.token$", fileName);
        Assert.False(Directory.Exists(paths.TokensDirectory));
    }
}
