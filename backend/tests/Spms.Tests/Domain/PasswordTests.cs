using Spms.Host.Identity;
using Xunit;

namespace Spms.Tests.Domain;

public class PasswordTests
{
    [Fact]
    public void A_hash_verifies_its_password_and_nothing_else()
    {
        var hash = Passwords.Hash("correct horse battery", iterations: 20_000);
        Assert.StartsWith("pbkdf2-sha256$20000$", hash);
        Assert.True(Passwords.Verify("correct horse battery", hash));
        Assert.False(Passwords.Verify("correct horse batterY", hash));
        Assert.NotEqual(hash, Passwords.Hash("correct horse battery", iterations: 20_000)); // salted
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pbkdf2-sha256$abc$x$y")]
    [InlineData("md5$1$2$3")]
    [InlineData("pbkdf2-sha256$20000$not-base64$also-not")]
    public void A_malformed_hash_never_verifies(string? stored) => Assert.False(Passwords.Verify("anything", stored));

    [Theory]
    [InlineData("short", false)]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("owais-secret-1", false)] // contains the email name
    [InlineData("Plenty-of-variety-9", true)]
    public void The_policy(string password, bool ok) => Assert.Equal(ok, Passwords.Problem(password, "owais@aarfid.com") is null);

    [Fact]
    public void A_temporary_password_passes_the_policy_and_has_no_look_alikes()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = Passwords.Temporary();
            Assert.Equal(16, t.Length);
            Assert.Null(Passwords.Problem(t));
            Assert.DoesNotContain(t, c => "0O1lI".Contains(c));
        }
    }

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData("  Owais@Aarfid.com ", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("@start.com", false)]
    [InlineData("end@", false)]
    [InlineData("two words@x.com", false)]
    public void Emails(string email, bool ok) => Assert.Equal(ok, Passwords.LooksLikeEmail(email));

    [Fact]
    public void The_subject_is_the_trimmed_lower_cased_email() => Assert.Equal("owais@aarfid.com", Passwords.Subject("  Owais@AARFID.com "));
}
