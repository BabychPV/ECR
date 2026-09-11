// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuilderTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізів для звітності. У `rpt.*` потрапляють **усі** зрізи,
/// включно з `Draft` — щоб числа можна було перевірити **до** затвердження
/// (D-65). Фільтр для регулятора стоїть у вʼюсі, не в RDL (ФВ-10.11).
/// </summary>
[Collection("SqlServer")]
public sealed class ReportSnapshotBuilderTests(SqlServerFixture sql)
{
    private const int Version = 1;
    private const int Project = 1;
    private const int Period = 202601;

    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.7")]
    [Trait("Requirement", "ФВ-10.10")]
    public void Статус_зрізу_успадковується_від_стану_даних()
    {
        // ⚠ Статус не задається окремо — він ВИВОДИТЬСЯ зі стану аркушів
        // (D-65). Окреме поле стало б другим джерелом істини і рано чи пізно
        // показало б регулятору Approved на чернетці.
        Assert.Equal(SnapshotStatus.Draft, Snapshot(SnapshotStatus.Draft).Status);
        Assert.Equal(SnapshotStatus.Approved, Snapshot(SnapshotStatus.Approved).Status);

        // Перерахунок після зміни стану аркушів змінює і статус зрізу.
        var snapshot = Snapshot(SnapshotStatus.Draft);
        snapshot.RefreshStatus(SnapshotStatus.Approved);
        Assert.Equal(SnapshotStatus.Approved, snapshot.Status);

        // ⛔ У зрізі немає стану Rejected: відхилений аркуш повертається в
        // роботу і зрізу не дає взагалі. Спільний enum із DocumentStatus
        // створив би стан, який неможливо ні побудувати, ні пояснити.
        Assert.Equal(
            [SnapshotStatus.Draft, SnapshotStatus.Approved, SnapshotStatus.Submitted],
            Enum.GetValues<SnapshotStatus>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.2")]
    public void Чернеткові_зрізи_теж_потрапляють_у_rpt()
    {
        var draft = Snapshot(SnapshotStatus.Draft);

        // ⚠ Чернетковий зріз БУДУЄТЬСЯ і зберігається — саме щоб числа можна
        // було перевірити ДО затвердження (D-65). Будувати лише затверджені
        // означало б, що помилку видно вперше вже після погодження.
        draft.Complete(rowCount: 12, contentHash: null, calculationRunId: 77, parametersJson: null);
        draft.MakeCurrent();

        Assert.Equal(SnapshotStatus.Draft, draft.Status);
        Assert.True(draft.IsCurrent);
        Assert.Equal(12, draft.RowCount);
    }

    /// <remarks>
    /// ⛔ Аудит фази 2 (чесність тестів). Стара версія лише шукала текст
    /// `s.Status IN (1, 2)` у файлі скрипта — не виконувала запит, не
    /// заводила жодного `Draft`-зрізу. Доведено мутацією: заміна фільтра на
    /// `1 = 1` (реальний витік чернеток регулятору) лишала літеральний
    /// підрядок усередині коментаря — тест і далі проходив.
    ///
    /// ⚠ Реальна перевірка тому — запит до РОЗГОРНУТОЇ вʼюхи на живому SQL
    /// Server: два зрізи ОДНІЄЇ версії й проєкту, різні періоди (унікальний
    /// індекс `UX_ReportSnapshot_Current` не дозволив би два поточні зрізи в
    /// одному періоді), обидва з реальним рядком — Draft і Approved.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.11")]
    public async Task Вʼюха_для_регулятора_віддає_лише_Approved_і_Submitted()
    {
        const int draftPeriod = 202601;
        const int approvedPeriod = 202602;

        await DeployViewAsync();

        await using var db = CreateContext();

        // ⛔ ReportSnapshot.ProjectId несе реальний зовнішній ключ на
        // doc.Project (FK_Snap_Project). Project.TemplateVersionId теж має
        // реальний FK (`FK_Project_TV` на cfg.TemplateVersion) — раніше тут
        // стояв захардкоджений `templateVersionId: 2` без відповідного рядка
        // в базі, і вставка Project валилася саме на цьому обмеженні
        // (той самий дефект, що й у `ReopenRaceTests.ArrangeAsync`).
        var tag = Guid.NewGuid().ToString("N")[..10];
        var template = new Template(
            EcrCode.Create($"T{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water report template" }),
            createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync(CancellationToken.None);

        var templateVersion = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(templateVersion);
        await db.SaveChangesAsync(CancellationToken.None);

        var project = new Project(
            EcrCode.Create($"P{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water report project" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: templateVersion.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync(CancellationToken.None);

        var def = new ReportDef(
            EcrCode.Create("WaterReport"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(def.Id, "v1", "[]", "{}", Now);
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var draft = new ReportSnapshot(version.Id, project.Id, draftPeriod, SnapshotStatus.Draft, Now, builtByUserId: null);
        draft.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);
        draft.MakeCurrent();

        var approved = new ReportSnapshot(version.Id, project.Id, approvedPeriod, SnapshotStatus.Approved, Now, builtByUserId: null);
        approved.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);
        approved.MakeCurrent();

        db.ReportSnapshots.AddRange(draft, approved);
        await db.SaveChangesAsync(CancellationToken.None);

        var draftRow = new ReportRow(draft.Id, rowNo: 1, columnCode: "A");
        draftRow.SetValue(valueString: null, valueNumeric: 1m, valueDate: null);
        var approvedRow = new ReportRow(approved.Id, rowNo: 1, columnCode: "A");
        approvedRow.SetValue(valueString: null, valueNumeric: 2m, valueDate: null);
        db.ReportRows.AddRange(draftRow, approvedRow);
        await db.SaveChangesAsync(CancellationToken.None);

        var periods = await QueryPeriodsAsync(project.Id);

        // ⛔ Доказ сценарію: період Approved-зрізу є, період Draft-зрізу —
        // немає. Обидва зрізи мають РЕАЛЬНИЙ рядок і РЕАЛЬНИЙ поточний
        // прапорець — розрізняє їх лише статус, той самий, який фільтрує
        // вʼюха.
        Assert.DoesNotContain(draftPeriod, periods);
        Assert.Contains(approvedPeriod, periods);
    }

    /// <summary>
    /// Розгортає `rpt.v_WaterReport_v1` виконанням РЕАЛЬНОГО файлу скрипта —
    /// не власним переказом його вмісту. `SqlServerFixture` цей скрипт не
    /// накочує сама (він не в переліку `RunScriptAsync`), тож без цього
    /// кроку вʼюхи в тестовій базі не існувало б узагалі.
    /// </summary>
    private async Task DeployViewAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", "05-rpt-views.sql");
        var script = await File.ReadAllTextAsync(path).ConfigureAwait(false);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var batch in Ecr.Infrastructure.Persistence.SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Періоди, видимі крізь регуляторну вʼюху для проєкту.</summary>
    private async Task<List<int>> QueryPeriodsAsync(int projectId)
    {
        var result = new List<int>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT PeriodKey FROM rpt.v_WaterReport_v1 WHERE ProjectId = @p";
        command.Parameters.AddWithValue("@p", projectId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void IsCurrent_перемикається_однією_транзакцією()
    {
        var previous = Snapshot(SnapshotStatus.Approved);
        var next = Snapshot(SnapshotStatus.Draft);

        previous.MakeCurrent();
        Assert.True(previous.IsCurrent);

        // ⚠ Обидві половини — зняти зі старого і поставити новому — мусять
        // бути нероздільні. Між ними існує стан із двома поточними зрізами, і
        // регуляторна вʼюха в цю мить повернула б ПОДВОЄНІ рядки: не помилку,
        // а просто вдвічі більше число.
        previous.Supersede();
        next.MakeCurrent();

        Assert.False(previous.IsCurrent);
        Assert.True(next.IsCurrent);

        // Стан «двоє поточних» неможливо зберегти: його тримає фільтрований
        // унікальний індекс UX_ReportSnapshot_Current.
        var configuration = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Configurations",
            "ReportingConfiguration.cs"));

        Assert.Contains("UX_ReportSnapshot_Current", configuration, StringComparison.Ordinal);
        Assert.Contains("HasFilter(\"[IsCurrent] = 1\")", configuration, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.5")]
    [Trait("Requirement", "ФВ-5.14")]
    [Trait("Requirement", "ФВ-9.17")]
    public void Поданий_зріз_не_перебудовується_ніколи()
    {
        var snapshot = Snapshot(SnapshotStatus.Approved);
        snapshot.MarkSubmitted(userId: 9);

        Assert.Equal(SnapshotStatus.Submitted, snapshot.Status);

        // ⛔ Після подання зріз ІММУТАБЕЛЬНИЙ (ФВ-9.17). Потреба показати інші
        // числа закривається НОВИМ зрізом: інакше звіт, роздрукований учора, і
        // той самий звіт сьогодні дали б різні числа без жодного сліду.
        var error = Assert.Throws<DomainException>(
            () => snapshot.RefreshStatus(SnapshotStatus.Draft));

        Assert.Equal("ECR-RPT-0409", error.ErrorCode);
        Assert.Equal(SnapshotStatus.Submitted, snapshot.Status);
    }

    private static ReportSnapshot Snapshot(SnapshotStatus status)
        => new(Version, Project, Period, status, Now, builtByUserId: null);

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
