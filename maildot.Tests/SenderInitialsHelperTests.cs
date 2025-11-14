using maildot.Services;

namespace maildot.Tests;

public class SenderInitialsHelperTests
{
    [Theory]
    [InlineData("\"Bryan Lee\"", "aspen@subgraph-hiring.tech", "BL")]
    [InlineData("Madonna", "madonna@example.com", "M")]
    [InlineData(null, "john.doe@example.com", "JD")]
    [InlineData(null, "solo@example.com", "SO")]
    [InlineData("", "", "?")]
    public void ExtractsExpectedInitials(string? displayName, string? address, string expected)
    {
        var initials = SenderInitialsHelper.From(displayName, address);
        Assert.Equal(expected, initials);
    }
}
