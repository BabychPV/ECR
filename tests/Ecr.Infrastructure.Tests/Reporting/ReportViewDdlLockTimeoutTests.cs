// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewDdlLockTimeoutTests.cs
using System.Diagnostics;
using System.Globalization;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// PSS-10 (аудит 20.11): перегенерація вʼюх <c>rpt.*</c> не стає в чергу за довгим читачем із запитом Sch-M без межі —
/// інакше за нею стоять УСІ нові читачі вʼюхи. DDL чекає <c>@DdlLockTimeoutMs</c>, повторює, відступає з 50424, вʼюха
/// лишається старою (набір вʼюх шаблону — узгодженим), свіжий читач не чекає довше межі, застосунок відкладає задачу.
/// </summary>
/// <remarks>
/// ⚠ Три сесії: «писач» тримає X на <c>doc.CellValue</c>; «довгий читач» читає вʼюху під <c>REPEATABLE READ</c> (у базі RCSI
/// звичайний читач не чекав би) і стоїть на <c>LCK_M_S</c>, тримаючи Sch-S на вʼюсі; генератор просить Sch-M. Щоб DDL взагалі
/// був потрібен, текст вʼюхи наперед переписано без позначки генератора (процедура перезаписує «ручну» вʼюху).
/// Мутації: (1) прибрати <c>SET LOCK_TIMEOUT</c> з <c>@ddlWrap</c> — генератор висить до таймауту команди (тест падає по
/// часу), свіжий читач чекає разом із ним; (2) прибрати <c>CATCH</c> 1222 у <c>usp_GenerateStableViews</c> — 1222 замість
/// 50424; (3) у <c>ReportViewGenerator</c> не перетворювати 50424 на відкладення — <c>SqlException</c> замість
/// <see cref="JobDeferredException"/>.
/// </remarks>
[Collection("SqlServer")]
[Trait("Audit", "PSS-10")]
public sealed class ReportViewDdlLockTimeoutTests(SqlServerFixture sql)
{
    private const string TemplateMarker = "-- Згенеровано rpt.usp_GenerateTemplateViews";
    private const string StableMarker = "-- Згенеровано rpt.usp_GenerateStableViews";

    /// <summary>База, на якій працюють помічники: спільна база колекції або власна (тест «усі шаблони»).</summary>
    private SqlServerFixture target = sql;

    /// <summary>
    /// ✎ рев'ю M3: спільний бюджет очікування виклику (<c>@DdlTotalBudgetMs</c>) обриває повтори — 50424 приходить
    /// вчасно, а не -2 від стелі команди клієнта. Без бюджету 10 спроб x (1 с очікування + 1 с паузи) ≈ 19 с.
    /// Мутація: прибрати <c>OR @ddlSpent &gt;= @ddlBudget</c> — генератор чекає ~19 с, тест червоний за часом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Бюджет_очікування_виклику_обриває_повтори_і_дає_50424_вчасно()
    {
        var (doc, view, _) = await PublishedAsync();
        await RewriteWithoutMarkerAsync(view, TemplateMarker);

        await using (var hold = await HoldReaderAsync(doc, view))
        {
            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = {doc.TemplateVersionId}, @DdlLockTimeoutMs = 1000, @DdlMaxAttempts = 10, @DdlTotalBudgetMs = 2500;")));
            watch.Stop();

            Assert.Equal(ReportViewGenerator.BlockedError, error.Number);
            Assert.Contains("call budget 2500 ms", error.Message, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"генератор чекав {watch.Elapsed}");
        }

        Assert.DoesNotContain(TemplateMarker, await DefinitionAsync(view), StringComparison.Ordinal);
    }

    /// <summary>Бюджет генератора — половина стелі команди (без стелі — від типових 30 с SqlClient).</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(60, 30_000)]
    [InlineData(300, 150_000)]
    [InlineData(null, 15_000)]
    [InlineData(0, 15_000)]
    [InlineData(1, 1_000)]
    public void Генератор_дає_процедурі_половину_стелі_команди_на_очікування_DDL(int? timeoutSeconds, int expected)
        => Assert.Equal(expected, ReportViewGenerator.DdlBudgetFor(timeoutSeconds));

    /// <summary>
    /// ✎ рев'ю L7: прогін «усі шаблони» (старт застосунку) — процедура стабільних вʼюх НЕ кидає, а повертає рядок
    /// <c>Blocked</c>; генератор перетворює його на збій картки (ключ прогону по всіх) і відкладення задачі, а повтор
    /// без читача знімає збій. Власна база: прогін «усі шаблони» в спільній базі зачепив би чужі шаблони (50422/50409
    /// сусідніх тестів). Мутація: прибрати гілку <c>blocked is not null</c> у <c>ReportViewGenerator.RunAsync</c> —
    /// генерація «успішна», тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Усі_шаблони_рядок_Blocked_дає_збій_картки_і_відкладення_повтор_знімає()
    {
        var own = SqlServerFixture.WithOwnDatabase("_rvall");
        await own.InitializeAsync();
        try
        {
            target = own;
            var (doc, _, _) = await PublishedAsync();
            await ExecuteAsync(string.Create(
                CultureInfo.InvariantCulture, $"EXEC rpt.usp_GenerateStableViews @TemplateVersionId = {doc.TemplateVersionId};"));
            var stable = await StableViewAsync(doc);
            await RewriteWithoutMarkerAsync(stable, StableMarker);

            var status = new ReportViewStatus();
            await using (var hold = await HoldReaderAsync(doc, stable))
            {
                await using var db = target.CreateContext();
                db.Database.SetCommandTimeout(120);
                var deferred = await Assert.ThrowsAsync<JobDeferredException>(
                    () => new ReportViewGenerator(db, status).GenerateAllAsync(onlyIfChanged: false, CancellationToken.None));
                Assert.Equal(ReportViewGenerator.BlockedRetryDelay, deferred.Delay);
            }

            var failure = Assert.Single(status.Snapshot(), f => f.TemplateVersionId is null && f.StableTemplateId is null);
            Assert.Equal(ReportViewGenerator.BlockedError, failure.Code);
            Assert.DoesNotContain(StableMarker, await DefinitionAsync(stable), StringComparison.Ordinal);

            await using (var db = target.CreateContext())
            {
                db.Database.SetCommandTimeout(120);
                await new ReportViewGenerator(db, status).GenerateAllAsync(onlyIfChanged: false, CancellationToken.None);
            }

            Assert.Contains(StableMarker, await DefinitionAsync(stable), StringComparison.Ordinal);
            Assert.Empty(status.Snapshot());
        }
        finally
        {
            await own.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вʼюха_версії_відступає_перед_довгим_читачем_свіжий_читач_не_чекає_повтор_проходить()
    {
        var (doc, view, _) = await PublishedAsync();
        await RewriteWithoutMarkerAsync(view, TemplateMarker);

        await using (var hold = await HoldReaderAsync(doc, view))
        {
            await using var generator = new SqlConnection(target.ConnectionString);
            await generator.OpenAsync();
            var spid = await SpidAsync(generator);
            await using var command = generator.CreateCommand();
            command.CommandTimeout = 60;
            command.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = {doc.TemplateVersionId}, @DdlLockTimeoutMs = 1500, @DdlMaxAttempts = 2;");
            var watch = Stopwatch.StartNew();
            var run = command.ExecuteNonQueryAsync();

            // Генератор у черзі на Sch-M — свіжий читач (звичайний, RCSI) проходить не довше межі очікування DDL.
            await WaitForWaitTypeAsync(spid, "LCK_M_SCH_M", TimeSpan.FromSeconds(20));
            var fresh = Stopwatch.StartNew();
            await ScalarAsync($"SELECT COUNT(*) FROM rpt.{Q(view)};");
            fresh.Stop();
            Assert.True(fresh.Elapsed < TimeSpan.FromSeconds(5), $"свіжий читач чекав {fresh.Elapsed}");

            var error = await Assert.ThrowsAsync<SqlException>(() => run);
            watch.Stop();
            Assert.Equal(ReportViewGenerator.BlockedError, error.Number);
            Assert.Contains(view, error.Message, StringComparison.Ordinal);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"генератор чекав {watch.Elapsed}");
        }

        // Вʼюху не перезаписано (лишився «ручний» текст); читач пішов — повтор проходить і повертає позначку.
        Assert.DoesNotContain(TemplateMarker, await DefinitionAsync(view), StringComparison.Ordinal);
        await ExecuteAsync(string.Create(
            CultureInfo.InvariantCulture, $"EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = {doc.TemplateVersionId};"));
        Assert.Contains(TemplateMarker, await DefinitionAsync(view), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стабільні_вʼюхи_шаблону_відступають_цілим_набором_а_генератор_відкладає_задачу()
    {
        var (doc, _, templateId) = await PublishedAsync();
        await ExecuteAsync(string.Create(
            CultureInfo.InvariantCulture, $"EXEC rpt.usp_GenerateStableViews @TemplateVersionId = {doc.TemplateVersionId};"));
        var stable = await StableViewAsync(doc);
        await RewriteWithoutMarkerAsync(stable, StableMarker);

        var status = new ReportViewStatus();
        await using (var hold = await HoldReaderAsync(doc, stable))
        {
            await using var db = target.CreateContext();
            var watch = Stopwatch.StartNew();
            var deferred = await Assert.ThrowsAsync<JobDeferredException>(
                () => new ReportViewGenerator(db, status).GenerateStableViewsAsync(templateId, doc.TemplateVersionId, CancellationToken.None));
            watch.Stop();

            Assert.Equal(ReportViewGenerator.BlockedRetryDelay, deferred.Delay);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"генератор чекав {watch.Elapsed}");
        }

        var failure = Assert.Single(status.Snapshot(), f => f.StableTemplateId == templateId);
        Assert.Equal(ReportViewGenerator.BlockedError, failure.Code);

        // Відкат усієї транзакції шаблону: вʼюха лишилась «ручною»; повтор без читача — успіх, збій знято.
        Assert.DoesNotContain(StableMarker, await DefinitionAsync(stable), StringComparison.Ordinal);
        await using (var db = target.CreateContext())
        {
            await new ReportViewGenerator(db, status).GenerateStableViewsAsync(templateId, doc.TemplateVersionId, CancellationToken.None);
        }

        Assert.Contains(StableMarker, await DefinitionAsync(stable), StringComparison.Ordinal);
        Assert.DoesNotContain(status.Snapshot(), f => f.StableTemplateId == templateId);
    }

    /// <summary>Опублікований документ з однією коміркою; вʼюха версії згенерована.</summary>
    private async Task<(TestDocument Doc, string View, int TemplateId)> PublishedAsync()
    {
        var doc = await new TestDocumentBuilder(target.ConnectionString).BuildAsync(columnCount: 2, rowCount: 1);
        await using var db = target.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        version.Publish(publishedByUserId: 1, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 1.5);
            EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = {doc.TemplateVersionId};
            """));

        var view = await ScalarTextAsync(string.Create(CultureInfo.InvariantCulture, $"""
            SELECT TOP (1) o.name FROM sys.views AS o
            WHERE SCHEMA_NAME(o.schema_id) = N'rpt' AND OBJECT_DEFINITION(o.object_id) LIKE N'%ti.TableDefId = {doc.TableDefId};%'
              AND OBJECT_DEFINITION(o.object_id) LIKE N'%{TemplateMarker}%'
            """));
        return (doc, view, version.TemplateId);
    }

    private async Task<string> StableViewAsync(TestDocument doc)
        => await ScalarTextAsync(string.Create(CultureInfo.InvariantCulture, $"""
            SELECT TOP (1) o.name FROM sys.views AS o
            WHERE SCHEMA_NAME(o.schema_id) = N'rpt' AND OBJECT_DEFINITION(o.object_id) LIKE N'%ti.TableDefId IN ({doc.TableDefId})%'
              AND OBJECT_DEFINITION(o.object_id) LIKE N'%{StableMarker}%'
            """));

    /// <summary>Той самий текст, але без позначки генератора — процедура вважатиме вʼюху «ручною» й перезапише.</summary>
    private Task RewriteWithoutMarkerAsync(string view, string marker)
        => ExecuteAsync($"""
            DECLARE @d nvarchar(max) = REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'rpt.{Q(view)}')), N'{marker}', N'-- змінено руками');
            -- Збережений текст може починатися з «CREATE VIEW» — переписуємо як CREATE OR ALTER.
            SET @d = STUFF(@d, 1, CHARINDEX(N'VIEW', @d) + 3, N'CREATE OR ALTER VIEW');
            EXEC sys.sp_executesql @d;
            """);

    /// <summary>Писач тримає X на <c>doc.CellValue</c>; довгий читач стоїть на ньому, тримаючи Sch-S на вʼюсі.</summary>
    private async Task<HeldReader> HoldReaderAsync(TestDocument doc, string view)
    {
        var writer = new SqlConnection(target.ConnectionString);
        await writer.OpenAsync();
        var tx = writer.BeginTransaction();
        await using (var update = writer.CreateCommand())
        {
            update.Transaction = tx;
            // X на всю таблицю до кінця транзакції: читач із блокуваннями чекає незалежно від плану вʼюхи.
            update.CommandText = "SELECT COUNT_BIG(*) FROM doc.CellValue WITH (TABLOCKX, HOLDLOCK);";
            await update.ExecuteNonQueryAsync();
        }

        var reader = new SqlConnection(target.ConnectionString);
        await reader.OpenAsync();
        var readerSpid = await SpidAsync(reader);
        var select = reader.CreateCommand();
        select.CommandTimeout = 120;
        // REPEATABLE READ: у базі RCSI звичайний читач не чекав би на X, а цей — чекає, тримаючи Sch-S на вʼюсі.
        select.CommandText = $"SET TRANSACTION ISOLATION LEVEL REPEATABLE READ; SELECT COUNT(*) FROM rpt.{Q(view)};";
        var pending = select.ExecuteScalarAsync();
        await WaitForWaitTypeAsync(readerSpid, "LCK_M_", TimeSpan.FromSeconds(20));

        return new HeldReader(writer, tx, reader, select, pending);
    }

    private async Task WaitForWaitTypeAsync(int spid, string waitType, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            var current = await ScalarTextOrNullAsync(string.Create(
                CultureInfo.InvariantCulture, $"SELECT wait_type FROM sys.dm_exec_requests WHERE session_id = {spid}"));
            if (current is not null && current.StartsWith(waitType, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"сесія {spid} не стала на {waitType} за {limit}");
    }

    private async Task<string> DefinitionAsync(string view)
        => await ScalarTextAsync($"SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.{Q(view)}'))");

    private static string Q(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task<int> SpidAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS int);";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = text;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<string> ScalarTextAsync(string text)
        => await ScalarTextOrNullAsync(text) ?? throw new InvalidOperationException("порожньо: " + text);

    private async Task<string?> ScalarTextOrNullAsync(string text)
    {
        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed class HeldReader(
        SqlConnection writer, SqlTransaction tx, SqlConnection reader, SqlCommand select, Task<object?> pending) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            await writer.DisposeAsync();
            await pending;
            await select.DisposeAsync();
            await reader.DisposeAsync();
        }
    }
}
