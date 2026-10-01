// tests/Ecr.Application.Tests/Notifications/SmtpFailureClassifierTests.cs
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using Ecr.Application.Notifications;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Notifications;

/// <summary>Категорія відмови проби SMTP (ФВ-12.4a): адміністратор бачить, що лагодити — DNS, порт, TLS, логін, relay.</summary>
public sealed class SmtpFailureClassifierTests
{
    private static SmtpException Wrapped(Exception inner)
        => new("Failure sending mail.", inner);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.4a")]
    [InlineData(SocketError.HostNotFound, SmtpFailureClassifier.Dns)]
    [InlineData(SocketError.TryAgain, SmtpFailureClassifier.Dns)]
    [InlineData(SocketError.ConnectionRefused, SmtpFailureClassifier.Connect)]
    [InlineData(SocketError.NetworkUnreachable, SmtpFailureClassifier.Connect)]
    [InlineData(SocketError.TimedOut, SmtpFailureClassifier.Timeout)]
    public void Помилка_сокета_під_обгорткою_SmtpException_дає_dns_connect_або_timeout(SocketError code, string expected)
        => Assert.Equal(expected, SmtpFailureClassifier.MessageKeyOf(Wrapped(new SocketException((int)code))));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.4a")]
    public void Помилка_TLS_і_обовязковий_STARTTLS_дають_tls()
    {
        Assert.Equal(
            SmtpFailureClassifier.Tls,
            SmtpFailureClassifier.MessageKeyOf(Wrapped(new AuthenticationException("The remote certificate is invalid."))));
        Assert.Equal(
            SmtpFailureClassifier.Tls,
            SmtpFailureClassifier.MessageKeyOf(new SmtpException(SmtpStatusCode.MustIssueStartTlsFirst)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.4a")]
    public void Відмова_логіну_і_relay_розрізняються_за_кодом_відповіді()
    {
        Assert.Equal(
            SmtpFailureClassifier.Auth,
            SmtpFailureClassifier.MessageKeyOf(new SmtpException(SmtpStatusCode.ClientNotPermitted, "535 rejected")));
        Assert.Equal(
            SmtpFailureClassifier.Relay,
            SmtpFailureClassifier.MessageKeyOf(new SmtpFailedRecipientException(SmtpStatusCode.MailboxUnavailable, "ops@corp.example")));
        Assert.Equal(
            SmtpFailureClassifier.Relay,
            SmtpFailureClassifier.MessageKeyOf(new SmtpException(SmtpStatusCode.MailboxUnavailable, "550 relay denied")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.4a")]
    public void Тайм_аут_і_скасування_дають_timeout_а_нерозпізнане_unknown()
    {
        Assert.Equal(SmtpFailureClassifier.Timeout, SmtpFailureClassifier.MessageKeyOf(Wrapped(new TimeoutException())));
        Assert.Equal(SmtpFailureClassifier.Unknown, SmtpFailureClassifier.MessageKeyOf(new InvalidOperationException("x")));
        Assert.Equal(SmtpFailureClassifier.Unknown, SmtpFailureClassifier.MessageKeyOf(new SmtpException("mystery")));
    }
}
