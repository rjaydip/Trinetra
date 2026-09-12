using System.Reflection;
using Shouldly;
using Trinetra.Federation.Api.Endpoints;

namespace Trinetra.UnitTests;

/// <summary>
/// Finding 6-M5: <c>email</c> had no format check. This covers
/// <c>UserEndpoints.TryValidateEmail</c> directly — the uniqueness half is a database
/// constraint (v1.14.sql), covered at the integration level instead.
/// </summary>
public sealed class UserEmailValidationTests
{
    private static readonly MethodInfo Method = typeof(UserEndpoints)
        .GetMethod("TryValidateEmail", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(UserEndpoints), "TryValidateEmail");

    private static (bool Ok, string? Normalized, string? Error) Invoke(string? email)
    {
        var args = new object?[] { email, null, null };
        var ok = (bool)Method.Invoke(null, args)!;
        return (ok, (string?)args[1], (string?)args[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_IsValid_ClearsTheField(string? email)
    {
        var (ok, normalized, error) = Invoke(email);

        ok.ShouldBeTrue();
        normalized.ShouldBeNull();
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.example.co.in")]
    public void WellFormedAddress_IsValid(string email)
    {
        var (ok, normalized, error) = Invoke(email);

        ok.ShouldBeTrue();
        normalized.ShouldBe(email);
        error.ShouldBeNull();
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmed()
    {
        var (ok, normalized, _) = Invoke("  user@example.com  ");

        ok.ShouldBeTrue();
        normalized.ShouldBe("user@example.com");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local.com")]
    public void MalformedAddress_IsRejected(string email)
    {
        var (ok, normalized, error) = Invoke(email);

        ok.ShouldBeFalse();
        normalized.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// BA review: <c>MailAddress.TryCreate</c> happily parses RFC 5322 "Display Name &lt;addr&gt;"
    /// syntax — real mail-client syntax, but a silent data-quality trap for an admin field a
    /// pasted "From:" line lands in unnoticed. Rejected outright rather than stripped down to
    /// the bare address, so the admin sees the correction to make instead of the system quietly
    /// deciding what they meant.
    /// </summary>
    [Theory]
    [InlineData("John Doe <john@example.com>")]
    [InlineData("\"John Doe\" <john@example.com>")]
    [InlineData("a b@example.com")] // MailAddress parses this as display-name "a", address "b@example.com"
    public void DisplayNameSyntax_IsRejected_NotSilentlyStrippedToBareAddress(string email)
    {
        var (ok, normalized, error) = Invoke(email);

        ok.ShouldBeFalse();
        normalized.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void OverLongAddress_IsRejected()
    {
        var longLocal = new string('a', 250);
        var (ok, normalized, error) = Invoke($"{longLocal}@example.com");

        ok.ShouldBeFalse();
        normalized.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }
}
