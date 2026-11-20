using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// PSS-24: строк зберігання <c>sec.LoginAttempt</c> — налаштування (400 днів за замовчуванням, змінюється без
/// перевидання), підлога/стеля, і захист від переписування спроб (<c>TR_LoginAttempt_NoUpdate</c>).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати <c>Math.Clamp</c> у <c>LoginAttemptRetention.Resolve</c> — червоніють тести підлоги й
/// стелі; читати налаштування не з бази, а лише з константи — червоніє тест «людина змінила строк»; прибрати
/// <c>WHERE AttemptedAt &lt; …</c> з прибирання — червоніє тест «свіжі спроби не чіпаються».
/// </remarks>
[Collection("SqlServer")]
public sealed class LoginAttemptRetentionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, 400)]
    [InlineData("", 400)]
    [InlineData("не-число", 400)]
    [InlineData("200", 200)]
    [InlineData("30", 30)]
    [InlineData("1", 30)] // підлога: свіжі спроби не стираються
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("99999", 36500)] // стеля
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public void Строк_береться_з_налаштування_інакше_400_і_завжди_в_межах(string? stored, int expected)
        => Assert.Equal(expected, LoginAttemptRetention.Resolve(stored));

    [Theory]
    [InlineData(null, 120)]
    [InlineData("не-число", 120)]
    [InlineData("240", 240)]
    [InlineData("119", 120)] // підлога 120 місяців (рішення людини 20.11 п. 7)
    [InlineData("0", 120)]
    [InlineData("-1", 120)]
    [InlineData("1201", 1200)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-24")]
    public void Глибина_аудиту_не_менше_120_місяців_і_не_більше_1200(string? stored, int expected)
        => Assert.Equal(expected, Ecr.Application.Security.SessionSettings.ResolveAuditRetentionMonths(stored));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task За_замовчуванням_прибираються_спроби_старші_за_400_днів_а_свіжі_лишаються()
    {
        await ClearSettingAsync();
        var tag = Tag();
        await SeedAsync(tag, ageDays: 500, "old");
        await SeedAsync(tag, ageDays: 399, "kept399");
        await SeedAsync(tag, ageDays: 1, "fresh");

        var purged = await PurgeAsync();

        Assert.True(purged >= 1);
        Assert.Equal(["fresh", "kept399"], await SurvivorsAsync(tag));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Людина_змінила_строк_у_налаштуванні_і_наступний_прохід_діє_без_перевидання()
    {
        var tag = Tag();
        await SeedAsync(tag, ageDays: 500, "old");
        await SeedAsync(tag, ageDays: 100, "mid");
        await SeedAsync(tag, ageDays: 1, "fresh");

        try
        {
            await SetSettingAsync("60");
            await PurgeAsync();
            Assert.Equal(["fresh"], await SurvivorsAsync(tag));

            // Збільшення строку не повертає стерте, але й не стирає більше: 1000 днів — свіжі лишаються.
            await SetSettingAsync("1000");
            await PurgeAsync();
            Assert.Equal(["fresh"], await SurvivorsAsync(tag));
        }
        finally
        {
            await ClearSettingAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Рядок_налаштування_з_нулем_не_стирає_свіжих_спроб_підлога_30_днів()
    {
        var tag = Tag();
        await SeedAsync(tag, ageDays: 31, "older");
        await SeedAsync(tag, ageDays: 29, "recent");

        try
        {
            await SetSettingAsync("0");
            await PurgeAsync();
            Assert.Equal(["recent"], await SurvivorsAsync(tag));
        }
        finally
        {
            await ClearSettingAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task UPDATE_спроби_входу_відхиляється_тригером_а_рядок_лишається_цілим_DELETE_за_строком_дозволений()
    {
        var tag = Tag();
        await SeedAsync(tag, ageDays: 1, "row");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(
            connection, $"UPDATE sec.LoginAttempt SET IpAddress = N'6.6.6.6' WHERE UserName = N'{tag}-row'"));
        Assert.Equal(50060, error.Number);
        Assert.Equal(["row"], await SurvivorsAsync(tag));

        // DELETE дозволений — його робить прибирання за строком.
        await ExecAsync(connection, $"DELETE FROM sec.LoginAttempt WHERE UserName = N'{tag}-row'");
        Assert.Empty(await SurvivorsAsync(tag));

        // Тригер створений і ввімкнений.
        await using var check = connection.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_LoginAttempt_NoUpdate' AND is_disabled = 0 "
            + "AND parent_id = OBJECT_ID(N'sec.LoginAttempt')";
        Assert.Equal(1, (int)(await check.ExecuteScalarAsync())!);
    }

    private static string Tag() => "ret" + Guid.NewGuid().ToString("N")[..12];

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<int> PurgeAsync()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        await using var db = NewDb();
        return await new LoginAttemptPurger(db, clock).PurgeAsync(CancellationToken.None);
    }

    private async Task SeedAsync(string tag, int ageDays, string label)
    {
        await using var db = NewDb();
        db.LoginAttempts.Add(new LoginAttempt(
            $"{tag}-{label}", AuthProvider.Local, false, Now.AddDays(-ageDays), "203.0.113.9", "BadPassword"));
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> SurvivorsAsync(string tag)
    {
        await using var db = NewDb();
        var prefix = tag + "-";
        return (await db.LoginAttempts.Where(a => a.UserName.StartsWith(prefix)).Select(a => a.UserName).ToListAsync())
            .Select(n => n[prefix.Length..]).Order(StringComparer.Ordinal).ToList();
    }

    private async Task SetSettingAsync(string value)
    {
        await ClearSettingAsync();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO sys_ecr.SystemSetting ([Key], Value, Description, ModifiedAt, ModifiedByUserId) "
            + "VALUES (@k, @v, N'test', SYSUTCDATETIME(), 0)";
        command.Parameters.AddWithValue("@k", LoginAttemptRetention.SettingKey);
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ClearSettingAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sys_ecr.SystemSetting WHERE [Key] = @k";
        command.Parameters.AddWithValue("@k", LoginAttemptRetention.SettingKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(SqlConnection connection, string statement)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}
