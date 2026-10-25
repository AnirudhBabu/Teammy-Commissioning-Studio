using Xunit;

namespace Teammy.Presentation.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void NoArgumentsSelectsAnExplicitlyLocalDemo()
    {
        var options = LaunchOptions.Parse([]);
        Assert.Null(options.ApiAddress);
        Assert.False(options.DemoAuthentication);
    }

    [Fact]
    public void ApiDemoIsAnExplicitMode()
    {
        var options = LaunchOptions.Parse(["--api-demo", "http://localhost:5080"]);
        Assert.True(options.DemoAuthentication);
        Assert.Equal("localhost", options.ApiAddress?.Host);
    }

    [Fact]
    public void ScreenshotSmokeTestCannotConnectToARemoteService() =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--api", "https://example.test", "--smoke-test", "test.png"]));

    [Theory]
    [InlineData("--api")]
    [InlineData("--api-demo")]
    [InlineData("--smoke-test")]
    [InlineData("--unknown")]
    public void IncompleteAndUnknownArgumentsAreRejected(string argument) =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse([argument]));

    [Fact]
    public void MultipleApiModesAreRejected() =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--api", "https://example.test", "--api-demo", "http://localhost"]));

    [Fact]
    public void DemoAndApiCannotBeCombined() =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--demo", "--api", "https://example.test"]));
}
