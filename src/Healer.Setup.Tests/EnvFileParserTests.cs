using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class EnvFileParserTests
{
    [Fact]
    public void TryGetValue_FindsAMatchingKey()
    {
        var content = "TELEGRAM_BOT_TOKEN=abc123\nTELEGRAM_CHAT_ID=456\n";

        Assert.Equal("abc123", EnvFileParser.TryGetValue(content, "TELEGRAM_BOT_TOKEN"));
        Assert.Equal("456", EnvFileParser.TryGetValue(content, "TELEGRAM_CHAT_ID"));
    }

    [Fact]
    public void TryGetValue_ReturnsNullForAMissingKey()
    {
        var content = "TELEGRAM_BOT_TOKEN=abc123\n";

        Assert.Null(EnvFileParser.TryGetValue(content, "SOME_OTHER_VAR"));
    }

    [Fact]
    public void TryGetValue_HandlesAValueContainingAnEqualsSign()
    {
        var content = "TELEGRAM_BOT_TOKEN=abc=123==\n";

        Assert.Equal("abc=123==", EnvFileParser.TryGetValue(content, "TELEGRAM_BOT_TOKEN"));
    }

    [Fact]
    public void TryGetValue_IgnoresBlankLines()
    {
        var content = "\nTELEGRAM_BOT_TOKEN=abc123\n\n";

        Assert.Equal("abc123", EnvFileParser.TryGetValue(content, "TELEGRAM_BOT_TOKEN"));
    }
}
