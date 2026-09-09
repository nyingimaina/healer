using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class TelegramFieldValidatorTests
{
    [Theory]
    [InlineData("123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ")]
    [InlineData("7000000000:AAExampleToken-With_Underscores123")]
    public void ValidateBotToken_AcceptsKnownGoodShapes(string token)
    {
        var (valid, reason) = TelegramFieldValidator.ValidateBotToken(token);

        Assert.True(valid);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("123456789")]
    [InlineData(":missingdigits")]
    public void ValidateBotToken_RejectsKnownBadShapes(string token)
    {
        var (valid, reason) = TelegramFieldValidator.ValidateBotToken(token);

        Assert.False(valid);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("-1001234567890")]
    public void ValidateChatId_AcceptsPositiveAndNegativeNumericIds(string chatId)
    {
        var (valid, _) = TelegramFieldValidator.ValidateChatId(chatId);

        Assert.True(valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    public void ValidateChatId_RejectsNonNumericInput(string chatId)
    {
        var (valid, reason) = TelegramFieldValidator.ValidateChatId(chatId);

        Assert.False(valid);
        Assert.NotNull(reason);
    }
}
