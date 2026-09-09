using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class ServerNameValidatorTests
{
    [Theory]
    [InlineData("prod-api-1")]
    [InlineData("ip-10-0-1-23")]
    public void Validate_AcceptsNonEmptyNames(string name)
    {
        var (valid, reason) = ServerNameValidator.Validate(name);

        Assert.True(valid);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankNames(string name)
    {
        var (valid, reason) = ServerNameValidator.Validate(name);

        Assert.False(valid);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Validate_RejectsNamesLongerThanMaxLength()
    {
        var (valid, reason) = ServerNameValidator.Validate(new string('a', ServerNameValidator.MaxLength + 1));

        Assert.False(valid);
        Assert.NotNull(reason);
    }
}
