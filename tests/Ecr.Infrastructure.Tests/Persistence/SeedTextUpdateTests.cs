using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Зміна тексту наявного ключа доходить до вже розгорнутої бази — але не
/// затирає те, що адміністратор переписав сам.
/// </summary>
/// <remarks>
/// Живий випадок: заголовок <c>err.ECR-CALC-0409</c> змінено з правила чотирьох
/// очей на нейтральний, бо тим самим кодом відмовляє й видалення версії
/// методології. MERGE сіду лише вставляє, тож на розгорнутій базі лишався б
/// старий текст. Тексти — літерали, а не читання з сіду: вимога — саме ці слова.
/// </remarks>
[Collection("SqlServer")]
public sealed class SeedTextUpdateTests(SqlServerFixture sql)
{
    private const string Key = "err.ECR-CALC-0409";
    private const string OldDefault = "A second pair of eyes is required";
    private const string NewDefault = "Conflicting methodology state";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Старий_текст_за_замовчуванням_оновлюється_до_нового_і_піднімає_Revision()
    {
        await SetValueAsync(OldDefault);
        var before = await RevisionAsync();

        await SeedAsync();

        Assert.Equal(NewDefault, await ValueAsync());

        // ⚠ Без інкременту кеш `ui:{lang}:{scope}:{revision}` і ETag клієнта
        // віддавали б старий текст до першої ручної правки будь-якого ключа.
        Assert.True(await RevisionAsync() > before, "Revision не піднявся після оновлення тексту.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Текст_який_переписав_адміністратор_сід_не_чіпає()
    {
        const string adminWording = "Methodology is locked for this action";
        await SetValueAsync(adminWording);

        try
        {
            await SeedAsync();

            Assert.Equal(adminWording, await ValueAsync());
        }
        finally
        {
            await SetValueAsync(NewDefault);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Повторний_сід_після_оновлення_нічого_не_міняє()
    {
        await SetValueAsync(OldDefault);
        await SeedAsync();

        var revision = await RevisionAsync();
        await SeedAsync();

        Assert.Equal(NewDefault, await ValueAsync());
        Assert.Equal(revision, await RevisionAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.9")]
    public async Task Прибраний_ключ_із_дефолтним_текстом_видаляється_а_переписаний_лишається()
    {
        // Стара база: обидва ключі колись посіяні; другий адміністратор переписав.
        await ExecuteAsync("""
            DELETE FROM sys_ecr.UiString WHERE [Key] IN (N'campaign.laggingCount', N'registries.usageKind.data');
            INSERT sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt) VALUES
              (N'campaign.laggingCount', N'en',
               N'{lagging} of {shown} projects are not finished: no documents, not everything approved, or no snapshot yet.',
               1, SYSUTCDATETIME()),
              (N'registries.usageKind.data', N'en', @v, 1, SYSUTCDATETIME());
            """, "Values stored in documents", expectedRows: null);
        var before = await RevisionAsync();

        try
        {
            await SeedAsync();

            Assert.Equal(0, (int)(await ScalarAsync(
                "SELECT COUNT(*) FROM sys_ecr.UiString WHERE [Key] = N'campaign.laggingCount';"))!);
            Assert.Equal("Values stored in documents", (string?)await ScalarAsync(
                "SELECT Value FROM sys_ecr.UiString WHERE [Key] = N'registries.usageKind.data' AND LanguageCode = N'en';"));
            Assert.True(await RevisionAsync() > before, "Revision не піднявся після видалення ключа.");
        }
        finally
        {
            await ExecuteAsync(
                "DELETE FROM sys_ecr.UiString WHERE [Key] = N'registries.usageKind.data';", string.Empty, expectedRows: null);
        }
    }

    private async Task SeedAsync()
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
        await new SeedRunner(db).RunAsync(CancellationToken.None);
    }

    private Task SetValueAsync(string value)
        => ExecuteAsync(
            "UPDATE sys_ecr.UiString SET Value = @v WHERE [Key] = @k AND LanguageCode = N'en';",
            value);

    private async Task<string> ValueAsync()
        => (string)(await ScalarAsync(
            "SELECT Value FROM sys_ecr.UiString WHERE [Key] = @k AND LanguageCode = N'en';"))!;

    private async Task<int> RevisionAsync()
        => (int)(await ScalarAsync("SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1;"))!;

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("@k", Key);
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string query, string value, int? expectedRows = 1)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("@k", Key);
        command.Parameters.AddWithValue("@v", value);
        var affected = await command.ExecuteNonQueryAsync();
        if (expectedRows is { } expected)
        {
            Assert.Equal(expected, affected);
        }
    }
}
