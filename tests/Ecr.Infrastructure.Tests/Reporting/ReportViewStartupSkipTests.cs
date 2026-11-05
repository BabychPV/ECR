// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStartupSkipTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// AN-155d: старт застосунку не перегенеровує вʼюхи <c>rpt.v_*</c>, якщо входи (шаблони, колонки, прив'язки, прапор, текст
/// процедур) не змінилися з останньої УСПІШНОЇ повної генерації (<c>@OnlyIfChanged = 1</c>); зміна входу або зникла вʼюха —
/// повна генерація. Власна база: відбиток глобальний для бази, а спільна база колекції містить навмисно «зіткнені» шаблони
/// (повна генерація там завжди кидає 50409 і відбитка не пише).
/// </summary>
/// <remarks>
/// Мутації (лише локально): (1) у <c>05-rpt-views.sql</c> зробити умову пропуску хибною (<c>AND 1 = 0</c>) — перший тест червоний;
/// (2) прибрати з <c>rpt.fn_ViewsFingerprint</c> блок <c>;co=</c> (колонки) — тест зміни колонки червоний (пропуск замість
/// перегенерації, вʼюха зі старою назвою колонки).
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class ReportViewStartupSkipTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_viewskip");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Повторний_старт_без_змін_пропускає_генерацію_і_не_чіпає_вʼюху()
    {
        var doc = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await PublishAsync(doc.TemplateVersionId);

        // Перший старт: відбитка ще немає — повна генерація, відбиток пишеться.
        var first = await RunAsync(onlyIfChanged: true);
        Assert.DoesNotContain("Skipped", first.Template);
        Assert.Contains("Created", first.Template);
        Assert.DoesNotContain("Skipped", first.Stable);

        var viewName = await ViewNameAsync(doc);
        var modified = await ModifyDateAsync(viewName);

        // Другий старт: нічого не змінилося — пропуск обох процедур, вʼюха не переписана (modify_date той самий).
        await Task.Delay(50);
        var second = await RunAsync(onlyIfChanged: true);
        Assert.Equal(["Skipped"], second.Template);
        Assert.Equal(["Skipped"], second.Stable);
        Assert.Equal(modified, await ModifyDateAsync(viewName));

        // Без прапора (публікація, DBA) — завжди повна генерація.
        var forced = await RunAsync(onlyIfChanged: false);
        Assert.DoesNotContain("Skipped", forced.Template);
        Assert.Contains("Unchanged", forced.Template);
    }

    /// <summary>
    /// Знахідка Аудиту-2 (живий стенд, <c>Collection:MonthColumnRouting = true</c>): відбиток збігався, а стабільні вʼюхи
    /// перегенеровувались на кожному старті. Той самий сценарій з <c>@MonthRouting = 1</c> на місячній таблиці.
    /// </summary>
    [Fact]
    public async Task Повторний_старт_з_маршрутизацією_місяців_теж_пропускає_генерацію()
    {
        var doc = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await PublishAsync(doc.TemplateVersionId);

        var first = await RunAsync(onlyIfChanged: true, monthRouting: true);
        Assert.DoesNotContain("Skipped", first.Stable);

        var second = await RunAsync(onlyIfChanged: true, monthRouting: true);
        Assert.Equal(["Skipped"], second.Template);
        Assert.Equal(["Skipped"], second.Stable);

        // Зміна прапора - інший відбиток: повна генерація, а не пропуск.
        var flipped = await RunAsync(onlyIfChanged: true, monthRouting: false);
        Assert.DoesNotContain("Skipped", flipped.Stable);
    }

    /// <summary>
    /// Шлях старту через <see cref="ReportViewGenerator"/> (знахідка Аудиту-2: на живому стенді стабільні вʼюхи
    /// перегенеровувались на кожному старті - @OnlyIfChanged додавався в параметри команди, але не в її текст).
    /// Мутація: прибрати <c>, @OnlyIfChanged</c> з тексту виклику <c>usp_GenerateStableViews</c> - червоний.
    /// </summary>
    [Fact]
    public async Task Генератор_на_другому_старті_пропускає_й_стабільні_вʼюхи()
    {
        var doc = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await PublishAsync(doc.TemplateVersionId);

        var log = new OutcomeLogger();
        await using (var db = _sql.CreateContext())
        {
            await new ReportViewGenerator(db, logger: log).GenerateAsync(null, onlyIfChanged: true, CancellationToken.None);
        }

        await using (var db = _sql.CreateContext())
        {
            await new ReportViewGenerator(db, logger: log).GenerateAsync(null, onlyIfChanged: true, CancellationToken.None);
        }

        Assert.Equal(2, log.Outcomes.Count);
        Assert.Contains("перегенеровано", log.Outcomes[0], StringComparison.Ordinal);
        Assert.Contains("пропущено", log.Outcomes[1], StringComparison.Ordinal);
    }

    /// <summary>Записує лише рядки «вʼюхи rpt.v_* на старті — …» генератора.</summary>
    private sealed class OutcomeLogger : ILogger<ReportViewGenerator>
    {
        public List<string> Outcomes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.Contains("на старті", StringComparison.Ordinal))
            {
                Outcomes.Add(text);
            }
        }
    }

    [Fact]
    public async Task Зміна_колонки_шаблону_перегенеровує_вʼюху_і_вона_її_відображає()
    {
        var doc = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await PublishAsync(doc.TemplateVersionId);
        await RunAsync(onlyIfChanged: true);
        Assert.Equal(["Skipped"], (await RunAsync(onlyIfChanged: true)).Template);

        var viewName = await ViewNameAsync(doc);
        // Код/тип колонки опублікованої версії заморожені тригером; «м'яке» видалення колонки — ні (змінює форму вʼюхи).
        string removed;
        await using (var db = _sql.CreateContext())
        {
            removed = await db.ColumnDefs.Where(c => c.Id == doc.ColumnDefIds[0]).Select(c => c.Code).SingleAsync();
        }

        Assert.Contains(removed, await ColumnsAsync(viewName));
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET IsDeleted = 1 WHERE Id = {doc.ColumnDefIds[0]}");

        var after = await RunAsync(onlyIfChanged: true);

        Assert.DoesNotContain("Skipped", after.Template);
        Assert.Contains("Altered", after.Template);
        Assert.DoesNotContain(removed, await ColumnsAsync(viewName));
        Assert.Equal(["Skipped"], (await RunAsync(onlyIfChanged: true)).Template);
    }

    [Fact]
    public async Task Нова_опублікована_версія_і_зникла_вʼюха_перегенеровуються()
    {
        var doc = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await PublishAsync(doc.TemplateVersionId);
        await RunAsync(onlyIfChanged: true);
        Assert.Equal(["Skipped"], (await RunAsync(onlyIfChanged: true)).Template);

        // Нова опублікована версія (інший шаблон) — входи змінилися.
        var other = await new TestDocumentBuilder(_sql.ConnectionString).BuildAsync(columnCount: 1, rowCount: 1);
        await PublishAsync(other.TemplateVersionId);
        var afterPublish = await RunAsync(onlyIfChanged: true);
        Assert.DoesNotContain("Skipped", afterPublish.Template);
        Assert.Equal(["Skipped"], (await RunAsync(onlyIfChanged: true)).Template);

        // Відбиток збігається, але вʼюху хтось скинув — пропуск небезпечний, генеруємо.
        var viewName = await ViewNameAsync(doc);
        await ExecuteAsync($"DROP VIEW rpt.[{viewName}]");
        var afterDrop = await RunAsync(onlyIfChanged: true);
        Assert.DoesNotContain("Skipped", afterDrop.Template);
        Assert.Contains("Created", afterDrop.Template);
        Assert.NotEmpty(await ColumnsAsync(viewName));
    }

    private async Task PublishAsync(int templateVersionId)
    {
        await using var db = _sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == templateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();
    }

    private async Task<string> ViewNameAsync(TestDocument doc)
    {
        await using var db = _sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        return System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");
    }

    /// <summary>Повна генерація (<c>@TemplateVersionId = NULL</c>) обох процедур; повертає дії кожної.</summary>
    private async Task<(List<string> Template, List<string> Stable)> RunAsync(bool onlyIfChanged, bool monthRouting = false)
    {
        var template = await ActionsAsync(
            "EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = NULL, @MonthRouting = @r, @OnlyIfChanged = @o", 2, onlyIfChanged, monthRouting);
        var stable = await ActionsAsync(
            "EXEC rpt.usp_GenerateStableViews @TemplateVersionId = NULL, @MonthRouting = @r, @OnlyIfChanged = @o", 1, onlyIfChanged, monthRouting);
        return (template, stable);
    }

    private async Task<List<string>> ActionsAsync(string text, int actionColumn, bool onlyIfChanged, bool monthRouting)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = text;
        command.Parameters.AddWithValue("@o", onlyIfChanged);
        command.Parameters.AddWithValue("@r", monthRouting);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(actionColumn));
        }

        return result;
    }

    private async Task<DateTime> ModifyDateAsync(string view)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V')";
        command.Parameters.AddWithValue("@v", view);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> ColumnsAsync(string view)
    {
        var result = new List<string>();
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V')";
        command.Parameters.AddWithValue("@v", view);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }
}
