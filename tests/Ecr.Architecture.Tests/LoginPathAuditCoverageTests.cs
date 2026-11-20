using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// PSS-24: усе, що пише шлях входу, прикрите тригером незмінності АБО строком зберігання, кожна спроба входу лишає слід
/// у журналі безпеки, а адреса клієнта береться тим самим визначником, що й межа частоти.
/// </summary>
/// <remarks>
/// ⛔ Це сторож, а не доказ поведінки (її доводять <c>LoginAuditApiTests</c>, <c>LoginAttemptRetentionTests</c>): новий
/// шлях запису спроби без журналу, нова таблиця без охорони чи повернення сирої адреси сокета падають тут, а не
/// мовчать до розслідування.
/// </remarks>
public sealed class LoginPathAuditCoverageTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts]));

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Журнал_безпеки_незмінний_а_оперативна_таблиця_спроб_має_заборону_UPDATE_і_строк_зберігання()
    {
        var sql = Flat(Read("src", "Ecr.Infrastructure", "Persistence", "Sql", "11-audit-tables.sql"));

        // Куди йдуть події входу — незмінна `aud.SecurityEvent`.
        Assert.Contains(
            "CREATE OR ALTER TRIGGER aud.TR_SecurityEvent_Immutable ON aud.SecurityEvent AFTER UPDATE, DELETE",
            sql, StringComparison.Ordinal);

        // Оперативна `sec.LoginAttempt`: UPDATE заборонений, DELETE — лише прибирання за строком.
        Assert.Contains("CREATE OR ALTER TRIGGER sec.TR_LoginAttempt_NoUpdate ON sec.LoginAttempt AFTER UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'sec.LoginAttempt', N'U') IS NOT NULL", sql, StringComparison.Ordinal);

        var retention = Flat(Read("src", "Ecr.Infrastructure", "Security", "LoginAttemptRetention.cs"));
        Assert.Contains("DELETE TOP ({Batch}) FROM sec.LoginAttempt WHERE AttemptedAt < {cutoff}", retention, StringComparison.Ordinal);
        var settings = Flat(Read("src", "Ecr.Application", "Security", "SessionSettingsHandlers.cs"));
        Assert.Contains("LoginAttemptRetentionKey = \"Security.LoginAttemptRetentionDays\"", settings, StringComparison.Ordinal);
        Assert.Contains("DefaultLoginAttemptRetentionDays = 400", settings, StringComparison.Ordinal);
        Assert.Contains("MinLoginAttemptRetentionDays = 30", settings, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_запис_спроби_входу_в_LoginHandler_супроводжується_записом_у_журнал_безпеки()
    {
        var lines = Read("src", "Ecr.Application", "Security", "LoginHandler.cs").Split('\n');
        var writers = lines.Select((line, index) => (line, index)).Where(x => x.line.Contains("users.RecordAttempt(", StringComparison.Ordinal)).ToList();

        Assert.True(writers.Count >= 3, "парсинг зламався: очікується ≥ 3 записи спроби (успіх локальний, успіх Windows, відмова)");

        foreach (var (_, index) in writers)
        {
            var following = string.Join('\n', lines.Skip(index).Take(14));
            Assert.True(
                following.Contains("JournalAsync(", StringComparison.Ordinal),
                $"LoginHandler.cs:{index + 1}: запис спроби входу без запису в журнал безпеки (JournalAsync)");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Адреса_для_журналу_входів_береться_з_визначника_межі_а_не_з_сирого_сокета()
    {
        var controller = Read("src", "Ecr.Api", "Controllers", "AuthController.cs");

        Assert.Contains("clientAddress.AddressOf(HttpContext)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection.RemoteIpAddress?.ToString()", controller, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LoginSucceeded")]
    [InlineData("LoginFailed")]
    [InlineData("AccountLocked")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тип_події_входу_має_підписи_en_ru_kz_у_сіді(string eventType)
    {
        var seed = Read("src", "Ecr.Infrastructure", "Persistence", "Sql", "seed", "features", "SEC-login-audit-events.sql");

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            Assert.Contains($"audit.eventLabel.{eventType}', N'{lang}'", seed, StringComparison.Ordinal);
        }
    }
}
