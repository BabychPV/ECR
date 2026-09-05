using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Архівація року.
/// </summary>
/// <remarks>
/// Головне правило процедури: **дані з джерела не видаляються, поки контрольні
/// суми не збіглися**. Тест на обрив посередині перевіряє саме це — і саме він
/// відрізняє відновлювану операцію від такої, що при збої лишає систему в
/// напівстані.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveJobTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Успішна_архівація_переносить_усі_рядки_і_звільняє_партиції()
    {
        var doc = await DocumentAsync(202601);
        await CellAsync(doc, 12500m);

        var before = await CountAsync("doc.CellValue", doc.PeriodKey.Value);
        Assert.True(before > 0);

        await ArchiveAsync(doc.ProjectId, 202601, 202601);

        // Усі рядки в архіві…
        Assert.Equal(before, await CountAsync("arc.CellValue", doc.PeriodKey.Value));

        // …і партиція джерела звільнена. TRUNCATE … WITH (PARTITIONS) звільняє
        // майже миттєво; DELETE на 108 млн рядків роздув би журнал транзакцій.
        Assert.Equal(0, await CountAsync("doc.CellValue", doc.PeriodKey.Value));

        var run = await LastRunAsync(doc.ProjectId);
        Assert.Equal("Completed", run.Status);
        Assert.Equal(202601, run.LastDone);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розбіжність_контрольних_сум_зупиняє_процес()
    {
        var doc = await DocumentAsync(202602);
        await CellAsync(doc, 100m);

        // Підкидаємо в архів зайвий рядок того самого періоду: суми не зійдуться.
        await ExecuteAsync($"""
            INSERT INTO arc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[2]},
                    {doc.TableDefId}, 999, 0, 0)
            """);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => ArchiveAsync(doc.ProjectId, 202602, 202602));

        // ⛔ Процес ЗУПИНЯЄТЬСЯ. Продовжити «бо майже збіглося» означало б
        // видалити джерело під архів, у якому чогось бракує.
        Assert.Contains("контрольних сум", error.Message, StringComparison.Ordinal);

        var run = await LastRunAsync(doc.ProjectId);
        Assert.Equal("Failed", run.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task При_розбіжності_дані_джерела_лишаються_на_місці()
    {
        var doc = await DocumentAsync(202603);
        await CellAsync(doc, 100m);

        var before = await CountAsync("doc.CellValue", doc.PeriodKey.Value);

        await ExecuteAsync($"""
            INSERT INTO arc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[2]},
                    {doc.TableDefId}, 999, 0, 0)
            """);

        await Assert.ThrowsAsync<SqlException>(
            () => ArchiveAsync(doc.ProjectId, 202603, 202603));

        // ⛔ ГОЛОВНЕ ПРАВИЛО процедури: джерело не видаляється, поки суми не
        // збіглися. Саме воно відрізняє відновлювану операцію від такої, що
        // при збої лишає систему без даних.
        Assert.Equal(before, await CountAsync("doc.CellValue", doc.PeriodKey.Value));

        // І знахідка потрапила в журнал: мовчазний провал архівації —
        // найдорожчий із можливих.
        Assert.True(await IssueExistsAsync("ARCHIVE_CHECKSUM"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Обрив_посередині_дозволяє_продовжити_з_наступної_партиції()
    {
        var first = await DocumentAsync(202604);
        await CellAsync(first, 10m);

        await ArchiveAsync(first.ProjectId, 202604, 202604);

        var run = await LastRunAsync(first.ProjectId);

        // ⚠ LastDonePeriodKey — не діагностика, а те, що робить архівацію
        // ВІДНОВЛЮВАНОЮ (АРХ-3a, D-24). Повторний запуск продовжує з
        // наступної партиції, а не починає спочатку: рік — це десятки
        // мільйонів рядків, і другий прохід не вкладеться у вікно.
        Assert.Equal(202604, run.LastDone);

        // Процедура читає саме це поле при відновленні.
        var script = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "03-archive-proc.sql"));

        Assert.Contains("MAX(LastDonePeriodKey) + 1", script, StringComparison.Ordinal);
        Assert.Contains("Status = N'Failed'", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Під_час_архівації_читання_бере_джерело_за_станом_а_не_за_датою()
    {
        var doc = await DocumentAsync(202605);
        await CellAsync(doc, 55m);

        await using var db = sql.CreateContext();
        var reader = new Ecr.Infrastructure.Persistence.ArchiveAwareCellReader(db);

        // До архівації — гаряча схема.
        Assert.False(await reader.IsArchivedAsync(doc.ProjectId, doc.PeriodKey, default)
            );

        await ArchiveAsync(doc.ProjectId, 202605, 202605);

        // ⛔ Після — архів, і вирішує це СТАН прогону, а не вік даних.
        // «Рік старший за два» — здогадка: проєкт може бути закритий і не
        // заархівований, або заархівований достроково.
        await using var after = sql.CreateContext();
        var afterReader = new Ecr.Infrastructure.Persistence.ArchiveAwareCellReader(after);

        Assert.True(await afterReader.IsArchivedAsync(doc.ProjectId, doc.PeriodKey, default)
            );
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розархівація_повертає_рядки_без_втрат()
    {
        var doc = await DocumentAsync(202606);
        await CellAsync(doc, 77.5m);

        var before = await CountAsync("doc.CellValue", doc.PeriodKey.Value);

        await ArchiveAsync(doc.ProjectId, 202606, 202606);
        Assert.Equal(0, await CountAsync("doc.CellValue", doc.PeriodKey.Value));

        await ExecuteAsync(
            $"EXEC arc.usp_RestoreYear @ProjectId = {doc.ProjectId}, "
            + $"@FromPeriodKey = 202606, @ToPeriodKey = 202606");

        // Рівно стільки ж рядків, скільки було: розархівація, що втратила
        // рядок, гірша за архівацію, що його не перенесла — там оригінал на
        // місці, тут його вже немає.
        Assert.Equal(before, await CountAsync("doc.CellValue", doc.PeriodKey.Value));

        // І значення те саме, до останнього знака.
        Assert.Equal(
            77.5m,
            await ScalarAsync<decimal>(
                $"SELECT TOP 1 ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value}")
                );
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_архівного_року_прозоре_для_викликача()
    {
        var doc = await DocumentAsync(202607);
        await CellAsync(doc, 42.25m);

        await using (var hot = sql.CreateContext())
        {
            var reader = new Ecr.Infrastructure.Persistence.ArchiveAwareCellReader(hot);
            var cells = await reader
                .ReadAsync(doc.ProjectId, doc.TableInstanceId, doc.PeriodKey, default)
                ;

            Assert.Equal(42.25m, Assert.Single(cells).Value.ValueNumeric);
        }

        await ArchiveAsync(doc.ProjectId, 202607, 202607);

        await using var archived = sql.CreateContext();
        var afterReader = new Ecr.Infrastructure.Persistence.ArchiveAwareCellReader(archived);

        var fromArchive = await afterReader
            .ReadAsync(doc.ProjectId, doc.TableInstanceId, doc.PeriodKey, default)
            ;

        // ⚠ ТОЙ САМИЙ виклик, той самий тип результату, те саме число. Викликач
        // не знає, де лежать дані, і не повинен знати: інакше кожне місце
        // читання рано чи пізно забуде про архів і покаже порожній звіт
        // замість торішнього.
        Assert.Equal(42.25m, Assert.Single(fromArchive).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Діапазон_партицій_для_квартального_проєкту_охоплює_чотири_а_не_дванадцять()
    {
        // ⛔ Діапазон виводиться з PeriodKind, а не жорстко YYYY01…YYYY12.
        // `PeriodKey = Year*100 + Sequence` (R-A6), і Sequence — порядковий
        // номер періоду в році: у квартальному проєкті їх чотири.
        Assert.Equal((202601, 202612), ArchiveRange.ForYear(2026, PeriodKind.Monthly));
        Assert.Equal((202601, 202604), ArchiveRange.ForYear(2026, PeriodKind.Quarterly));
        Assert.Equal((202601, 202601), ArchiveRange.ForYear(2026, PeriodKind.Yearly));

        // ⚠ Custom не має відомого наперед числа періодів (R-B5) — і мовчазна
        // дванадцятка тут була б здогадкою про чужі дані: `TRUNCATE` по
        // восьми неіснуючих партиціях зачепив би те, чого архівувати не
        // просили.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArchiveRange.ForYear(2026, PeriodKind.Custom));
    }

    /// <summary>Ланцюг «шаблон → документ → рядки» для періоду.</summary>
    private async Task<TestDocument> DocumentAsync(int periodKey)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: periodKey, ct: CancellationToken.None)
            .ConfigureAwait(false);

        // Архівувати можна лише ЗАКРИТИЙ проєкт, і лише коли сплив річний
        // грейс: архівація відкритого року забрала б дані з-під рук того, хто
        // їх зараз заповнює.
        await ExecuteAsync(
            $"UPDATE doc.Project SET Status = 3, ClosedAt = '2020-01-01' WHERE Id = {doc.ProjectId}")
            .ConfigureAwait(false);

        return doc;
    }

    private Task CellAsync(TestDocument doc, decimal value)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue
                (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]},
                    {doc.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, 0, 0)
            """);

    private Task ArchiveAsync(int projectId, int from, int to)
        => ExecuteAsync(
            $"EXEC arc.usp_ArchiveYear @ProjectId = {projectId}, "
            + $"@FromPeriodKey = {from}, @ToPeriodKey = {to}");

    private Task<int> CountAsync(string table, int periodKey)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE PeriodKey = {periodKey}");

    private async Task<bool> IssueExistsAsync(string ruleCode)
        => await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.ConsistencyIssue WHERE RuleCode = N'{ruleCode}'")
            .ConfigureAwait(false) > 0;

    private async Task<(string Status, int? LastDone)> LastRunAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP 1 Status, LastDonePeriodKey FROM itg.ArchiveRun "
            + $"WHERE ProjectId = {projectId} ORDER BY StartedAt DESC, Id DESC";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Корінь репозиторію.</summary>
    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
