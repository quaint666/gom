using Gom.Web.Auth;

namespace Gom.Tests;

public class DisplayNameTests
{
    [Theory]
    [InlineData(null, "fallback_id")]
    [InlineData("", "fallback_id")]
    [InlineData("   ", "fallback_id")]
    [InlineData("  Mẹ Lan ", "Mẹ Lan")]
    public void Falls_back_to_the_ID_or_trims(string? input, string expected) =>
        Assert.Equal(expected, DisplayName.Normalize(input, "fallback_id"));

    [Fact]
    public void Accepts_exactly_60_characters() =>
        Assert.NotNull(DisplayName.Normalize(new string('x', 60), "f"));

    [Fact]
    public void Counts_what_a_person_sees_not_bytes() =>
        Assert.NotNull(DisplayName.Normalize(string.Concat(Enumerable.Repeat("ặ", 60)), "f"));

    [Fact]
    public void Rejects_more_than_60_characters() =>
        Assert.Null(DisplayName.Normalize(new string('x', 61), "f"));

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    [InlineData("bell\a")]
    [InlineData("zero​width")]
    [InlineData("right‮to-left")]
    public void Rejects_control_and_invisible_formatting_characters(string input) =>
        Assert.Null(DisplayName.Normalize(input, "f"));
}
