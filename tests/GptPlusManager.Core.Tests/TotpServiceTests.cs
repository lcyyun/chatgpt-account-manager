using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

public sealed class TotpServiceTests
{
    private const string RfcSha1Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    public static TheoryData<long, string> Rfc6238Sha1Vectors => new()
    {
        { 59, "94287082" },
        { 1_111_111_109, "07081804" },
        { 1_111_111_111, "14050471" },
        { 1_234_567_890, "89005924" },
        { 2_000_000_000, "69279037" },
        { 20_000_000_000, "65353130" }
    };

    [Theory]
    [MemberData(nameof(Rfc6238Sha1Vectors))]
    public void GenerateCode_MatchesRfc6238Sha1Vectors(long unixSeconds, string expected)
    {
        var service = new TotpService();

        var actual = service.GenerateCode(RfcSha1Secret, unixSeconds, digits: 8, periodSeconds: 30);

        Assert.Equal(expected, actual);
    }
}
