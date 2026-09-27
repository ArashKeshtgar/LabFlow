using LabFlow.Api.Domain;

namespace LabFlow.Api.Tests;

public class HealthCardTests
{
    [Theory]
    [InlineData("9876543217")]
    [InlineData("1234567897")]
    [InlineData("1111111116")]
    public void Accepts_numbers_with_a_valid_check_digit(string hcn) =>
        Assert.True(HealthCard.IsValidNumber(hcn));

    [Theory]
    [InlineData("9876543210")]   // wrong check digit
    [InlineData("1234567898")]
    [InlineData("123456789")]    // 9 digits
    [InlineData("12345678901")]  // 11 digits
    [InlineData("12345678A7")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_invalid_numbers(string? hcn) =>
        Assert.False(HealthCard.IsValidNumber(hcn));

    [Fact]
    public void Normalize_strips_spaces_and_dashes() =>
        Assert.Equal("1234567897", HealthCard.Normalize(" 1234-567-897 "));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("A", true)]
    [InlineData("AB", true)]
    [InlineData("ab", false)]
    [InlineData("ABC", false)]
    [InlineData("A1", false)]
    public void Validates_version_code(string? version, bool expected) =>
        Assert.Equal(expected, HealthCard.IsValidVersion(version));
}
