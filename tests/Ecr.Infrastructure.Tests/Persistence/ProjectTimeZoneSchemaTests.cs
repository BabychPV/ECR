// tests/Ecr.Infrastructure.Tests/Persistence/ProjectTimeZoneSchemaTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// `doc.Project.TimeZoneId` на **реальному** SQL Server: IANA обов'язковий,
/// DEFAULT прибраний (`H-13`, директива ПК-1 №06 §3).
/// </summary>
/// <remarks>
/// ⛔ Перевірка саме на базі, а не в домені. Домен — не єдиний, хто пише в цю
/// колонку: сідінг, фікстури і руки DBA пишуть повз нього, а DEFAULT
/// `N'Central Asia Standard Time'` спрацьовував саме на такій вставці — тихо
/// і назавжди. Пояс вічний (`ФВ-1.1a`), тож «тихо і назавжди» тут означає
/// вічну властивість проєкту, обрану схемою 2016 року.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectTimeZoneSchemaTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData("Central Asia Standard Time")]
    [InlineData("West Asia Standard Time")]
    [InlineData("+05:00")]
    [InlineData("UTC+13")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task База_відхиляє_Windows_ідентифікатор_і_зсув(string timeZoneId)
    {
        var projectId = await SeedProjectAsync().ConfigureAwait(true);

        // ⚠ UPDATE, а не INSERT: перевіряється саме обмеження колонки, і воно
        // має діяти на обох шляхах. Проєкт уже існує, тобто його межі вже
        // пораховані — і тим гірше, якби пояс під ними мовчки замінили.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE doc.Project SET TimeZoneId = N'{timeZoneId}' WHERE Id = {projectId}"))
            .ConfigureAwait(true);

        Assert.Contains("CK_Project_TzIana", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task База_відхиляє_порожній_пояс()
    {
        var projectId = await SeedProjectAsync().ConfigureAwait(true);

        // ⛔ Порожній рядок гірший за Windows-ідентифікатор: `PeriodStateJob`
        // мав для нього мовчазний `TimeZoneInfo.Utc`, тобто межі періодів
        // з'їжджали на весь зсув майданчика і жодного винятку не було.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE doc.Project SET TimeZoneId = N'' WHERE Id = {projectId}"))
            .ConfigureAwait(true);

        Assert.Contains("CK_Project_TzIana", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task Вставка_без_поясу_падає_бо_DEFAULT_прибраний()
    {
        var projectId = await SeedProjectAsync().ConfigureAwait(true);

        // ⛔ Головне твердження. Доки стояв `DF_Project_Tz`, ЦЕЙ САМИЙ рядок
        // вставлявся успішно і отримував `N'Central Asia Standard Time'` —
        // Windows-ідентифікатор, +06:00, вибраний за людину схемою.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO doc.Project
                (Code, NameL10n, PeriodStart, PeriodEnd, TemplateVersionId,
                 PeriodKind, PeriodPolicyId, Status)
            SELECT CONCAT(N'TZPROBE_', NEWID()), p.NameL10n, p.PeriodStart, p.PeriodEnd,
                   p.TemplateVersionId, p.PeriodKind, p.PeriodPolicyId, p.Status
            FROM doc.Project p WHERE p.Id = {projectId}
            """)).ConfigureAwait(true);

        // Колонка `NOT NULL` без DEFAULT: SQL Server каже саме про неї.
        Assert.Contains("TimeZoneId", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Asia/Aqtau")]
    [InlineData("Asia/Almaty")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("UTC")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task IANA_база_приймає(string timeZoneId)
    {
        var projectId = await SeedProjectAsync().ConfigureAwait(true);

        // ⚠ Межа правила. Без цієї половини «сітка» могла б відкидати все
        // підряд і виглядати робочою: заборона без дозволеного боку не
        // доводить нічого.
        await ExecuteAsync(
            $"UPDATE doc.Project SET TimeZoneId = N'{timeZoneId}' WHERE Id = {projectId}")
            .ConfigureAwait(true);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Проєкт із повним ланцюгом FK; кожен виклик створює свій.</summary>
    private async Task<int> SeedProjectAsync()
        => (await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 1, rowCount: 1)
            .ConfigureAwait(true)).ProjectId;

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
