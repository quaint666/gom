using Gom.Web.Auth;

namespace Gom.Tests;

public class GomIdTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("me_2")]
    [InlineData("a1234567890123456789")] // 20 characters
    public void Accepts_lowercase_letters_digits_and_underscore_from_3_to_20(string id) =>
        Assert.True(GomId.IsValid(id));

    [Theory]
    [InlineData("ab")]                      // too short
    [InlineData("a12345678901234567890")]   // 21 characters
    [InlineData("Abc")]                     // uppercase
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("mẹ_ơi")]                   // non-ASCII
    [InlineData("")]
    public void Rejects_everything_else(string id) =>
        Assert.False(GomId.IsValid(id));

    [Theory]
    [InlineData("admin")]
    [InlineData("ops")]
    [InlineData("gom")]
    public void Rejects_reserved_words(string id) =>
        Assert.False(GomId.IsValid(id));
}

public class PasswordPolicyTests
{
    [Fact]
    public void Accepts_a_long_enough_password_with_spaces() =>
        Assert.Equal(PasswordProblem.None, PasswordPolicy.Check("bánh mì trứng 2026"));

    [Fact]
    public void Rejects_fewer_than_10_characters() =>
        Assert.Equal(PasswordProblem.TooShort, PasswordPolicy.Check("abcdefghi"));

    [Fact]
    public void Counts_characters_not_bytes() =>
        // 9 Vietnamese letters are 9 characters even though they take more than 9 bytes.
        Assert.Equal(PasswordProblem.TooShort, PasswordPolicy.Check("ặặặặặặặặặ"));

    [Fact]
    public void Rejects_more_than_128_characters() =>
        Assert.Equal(PasswordProblem.TooLong, PasswordPolicy.Check(new string('x', 129)));

    [Fact]
    public void Accepts_exactly_128_characters() =>
        Assert.Equal(PasswordProblem.None, PasswordPolicy.Check(new string('x', 128)));

    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]
    [InlineData("1234567890")]
    [InlineData("qwertyuiop")]
    public void Rejects_very_common_passwords_in_any_case(string password) =>
        Assert.Equal(PasswordProblem.TooCommon, PasswordPolicy.Check(password));
}

public class PasswordServiceTests
{
    // Few iterations keep the tests fast; the real count comes from configuration.
    private static PasswordService Service(int iterations = 1_000) => new(iterations);

    [Fact]
    public void A_hashed_password_verifies()
    {
        var hash = Service().Hash("correct horse battery");
        Assert.Equal(PasswordCheck.Ok, Service().Verify(hash, "correct horse battery"));
    }

    [Fact]
    public void A_wrong_password_fails()
    {
        var hash = Service().Hash("correct horse battery");
        Assert.Equal(PasswordCheck.Wrong, Service().Verify(hash, "correct horse batterY"));
    }

    [Fact]
    public void The_same_password_gets_a_different_hash_each_time() =>
        Assert.NotEqual(Service().Hash("correct horse battery"), Service().Hash("correct horse battery"));

    [Fact]
    public void A_hash_made_with_fewer_iterations_is_flagged_for_rehash()
    {
        var old = Service(1_000).Hash("correct horse battery");
        Assert.Equal(PasswordCheck.OkRehash, Service(2_000).Verify(old, "correct horse battery"));
    }

    [Fact]
    public void Burning_time_for_a_missing_user_takes_a_real_checks_work()
    {
        // Used when the Gom ID does not exist, so timing does not reveal it.
        var service = new PasswordService(200_000);
        var hash = service.Hash("x-correct-password");
        service.BurnTime("warm-up");

        var real = System.Diagnostics.Stopwatch.StartNew();
        service.Verify(hash, "other-password");
        real.Stop();

        var burn = System.Diagnostics.Stopwatch.StartNew();
        service.BurnTime("other-password");
        burn.Stop();

        Assert.True(burn.Elapsed > real.Elapsed / 2, $"burn {burn.Elapsed.TotalMilliseconds} ms, real check {real.Elapsed.TotalMilliseconds} ms");
    }
}

public class TokenTests
{
    [Fact]
    public void A_token_is_43_url_safe_characters_and_never_repeats()
    {
        var a = Tokens.New();
        var b = Tokens.New();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Token);
        Assert.NotEqual(a.Token, b.Token);
    }

    [Fact]
    public void The_stored_hash_is_32_bytes_and_matches_the_token()
    {
        var t = Tokens.New();
        Assert.Equal(32, t.Hash.Length);
        Assert.Equal(t.Hash, Tokens.Hash(t.Token));
    }

    [Fact]
    public void Different_tokens_have_different_hashes() =>
        Assert.NotEqual(Tokens.Hash("a"), Tokens.Hash("b"));
}
