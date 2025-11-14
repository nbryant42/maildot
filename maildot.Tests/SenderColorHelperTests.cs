using maildot.Services;

namespace maildot.Tests;

public class SenderColorHelperTests
{
    [Fact]
    public void SameSenderProducesSameColor()
    {
        var first = SenderColorHelper.GetColor("\"Nathan Bryant\"", "nbryant@optonline.net");
        var second = SenderColorHelper.GetColor("\"Nathan Bryant\"", "nbryant@optonline.net");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSendersWithSameInitialsProduceDifferentColors()
    {
        var first = SenderColorHelper.GetColor("\"Nathan Bryant\"", "nbryant@optonline.net");
        var second = SenderColorHelper.GetColor("\"Noah Benson\"", "nbenson@example.com");

        Assert.NotEqual(first, second);
    }
}
