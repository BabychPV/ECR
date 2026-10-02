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
    [InlineData(SocketError.ConnectionRefused, SmtpFailureClassifier.ProbeFailed)]
    [InlineData(SocketError.NetworkUnreachable, SmtpFailureClassifier.ProbeFailed)]
    [InlineData(SocketError.HostUnreachable, SmtpFailureClassifier.ProbeFailed)]
    [InlineData(SocketError.TimedOut, SmtpFailureClassifier.ProbeFailed)]
    public void Помилка_сокета_під_обгорткою_SmtpException_дає_dns_або_єдину_категорію_недосяжності(SocketError code, string expected)
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
    public void Тайм_аут_скасування_і_нерозпізнане_зливаються_з_відмовою_з_єднання_в_одну_категорію()
    {
        // ⛔ S4 (ent6): connect/timeout/unknown — оракул сканування портів; усі троє мусять бути однаковими.
        // Мутація: повернути `Timeout`/`Connect`/`Unknown` в будь-якій гілці → відповідний рядок червоніє.
        Assert.Equal(SmtpFailureClassifier.ProbeFailed, SmtpFailureClassifier.MessageKeyOf(Wrapped(new TimeoutException())));
        Assert.Equal(SmtpFailureClassifier.ProbeFailed, SmtpFailureClassifier.MessageKeyOf(new OperationCanceledException()));
        Assert.Equal(SmtpFailureClassifier.ProbeFailed, SmtpFailureClassifier.MessageKeyOf(new InvalidOperationException("x")));
        Assert.Equal(SmtpFailureClassifier.ProbeFailed, SmtpFailureClassifier.MessageKeyOf(new SmtpException("mystery")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public void Відмова_політики_напрямку_має_власну_категорію_що_не_залежить_від_мережі()
        => Assert.Equal(
            SmtpFailureClassifier.EndpointForbidden,
            SmtpFailureClassifier.MessageKeyOf(Wrapped(new SmtpEndpointForbiddenException())));
}
