// tests/Ecr.Infrastructure.Tests/Integration/SmtpClientCredentialsTests.cs
using System.Net;
using Ecr.Infrastructure.Integration;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>S2 (ent6): без логіна SMTP-клієнт — анонімний, облікові дані служби (NTLM) на хост не йдуть.</summary>
public sealed class SmtpClientCredentialsTests
{
    [Fact]
    [Trait("Requirement", "D-263")]
    public void Без_логіна_клієнт_анонімний_і_не_бере_облікових_даних_служби()
    {
        using var client = SmtpNotificationSender.BuildClient(
            "smtp.corp.example", 25, startTls: false, user: null, () => throw new InvalidOperationException("пароль не читається"));

        // ⛔ Мутація: повернути `client.UseDefaultCredentials = true` у гілку без логіна → обидва рядки червоні.
        Assert.False(client.UseDefaultCredentials);
        Assert.Null(client.Credentials);
    }

    [Theory]
    [Trait("Requirement", "D-263")]
    [InlineData("")]
    [InlineData("   ")]
    public void Порожній_логін_теж_анонімний(string user)
    {
        using var client = SmtpNotificationSender.BuildClient("smtp.corp.example", 25, false, user, () => "x");

        Assert.False(client.UseDefaultCredentials);
        Assert.Null(client.Credentials);
    }

    [Fact]
    [Trait("Requirement", "D-263")]
    public void З_логіном_береться_лише_явний_пароль_і_без_облікових_даних_служби()
    {
        using var client = SmtpNotificationSender.BuildClient("smtp.corp.example", 587, startTls: true, "mailer", () => "Pw-1");

        Assert.False(client.UseDefaultCredentials);
        var credential = Assert.IsType<NetworkCredential>(client.Credentials);
        Assert.Equal("mailer", credential.UserName);
        Assert.Equal("Pw-1", credential.Password);
        Assert.True(client.EnableSsl);
    }
}
