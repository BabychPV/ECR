// tests/Ecr.Infrastructure.Tests/Persistence/B151MigrationSqlTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B151, разове виправлення даних у міграції (умови координатора 17.11): прапорець «один документ на проєкт» —
/// лише шаблонам названих кодів і лише без дублікатів; шаблон із дублікатами пропускається з PRINT, міграція не
/// падає; повторний прогін нічого не змінює. Тест — на власних кодах (міграція кличе HSE400/HSE401).
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати умову дублікатів (<c>AND t.Code NOT IN (SELECT Code FROM @dup)</c>) — шаблон із дублікатами
/// отримує 1, червоний; прибрати фільтр коду — сторонній шаблон отримує 1, червоний; прибрати PRINT — червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class B151MigrationSqlTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public async Task Прапорець_лише_названим_кодам_без_дублікатів_дублікати_пропущено_з_PRINT_повтор_без_змін()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var clean = await builder.BuildAsync(periodKey: 209901);
        var duplicated = await builder.BuildAsync(periodKey: 209901);
        var other = await builder.BuildAsync(periodKey: 209901);
        var n = Random.Shared.Next(100_000, 999_999);
        var (cleanCode, dupCode) = ($"TB151A{n}", $"TB151B{n}");

        await using (var db = builder.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.Template SET Code = {cleanCode} WHERE Id = {clean.TemplateId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.Template SET Code = {dupCode} WHERE Id = {duplicated.TemplateId}");
            db.Documents.Add(new Document(duplicated.ProjectId, $"B151DUP{n}", 1, new DateTime(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync();
        }

        var script = B151TemplateSingleDocumentPerProject.FlagTemplatesSql(cleanCode, dupCode);

        var first = await RunAsync(script);
        Assert.True(await FlagAsync(clean.TemplateId));
        Assert.False(await FlagAsync(duplicated.TemplateId));
        Assert.False(await FlagAsync(other.TemplateId));
        Assert.Contains(first, m => m.Contains(dupCode, StringComparison.Ordinal) && m.Contains("B151", StringComparison.Ordinal));
        Assert.DoesNotContain(first, m => m.Contains(cleanCode, StringComparison.Ordinal));

        // Повтор: нічого не змінюється; пропущений знову пропущений.
        var second = await RunAsync(script);
        Assert.True(await FlagAsync(clean.TemplateId));
        Assert.False(await FlagAsync(duplicated.TemplateId));
        Assert.Contains(second, m => m.Contains(dupCode, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B151")]
    public void Код_із_лапкою_не_потрапляє_в_SQL()
        => Assert.Throws<ArgumentException>(() => B151TemplateSingleDocumentPerProject.FlagTemplatesSql("HSE'400"));

    private async Task<List<string>> RunAsync(string script)
    {
        var messages = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
        return messages;
    }

    private async Task<bool> FlagAsync(int templateId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Templates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.SingleDocumentPerProject).SingleAsync();
    }
}
