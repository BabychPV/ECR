// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuilderTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізів для звітності. У `rpt.*` потрапляють **усі** зрізи,
/// включно з `Draft` — щоб числа можна було перевірити **до** затвердження
/// (D-65). Фільтр для регулятора стоїть у вʼюсі, не в RDL (ФВ-10.11).
/// </summary>
public sealed class ReportSnapshotBuilderTests
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

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.11")]
    public void Вʼюха_для_регулятора_віддає_лише_Approved_і_Submitted()
    {
        // Фільтр стоїть у ВʼЮСІ (`05-rpt-views.sql`: `s.Status IN (1, 2)`), а
        // не в RDL: інакше його одного дня забудуть поставити в новому звіті,
        // і регулятор побачить чернетку (ФВ-10.11, D-65).
        var script = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "05-rpt-views.sql"));

        Assert.Contains("s.Status IN (1, 2)", script, StringComparison.Ordinal);
        Assert.Contains("s.IsCurrent = 1", script, StringComparison.Ordinal);

        // Числа фільтра — це саме Approved і Submitted, а не «перші два».
        Assert.Equal(1, (byte)SnapshotStatus.Approved);
        Assert.Equal(2, (byte)SnapshotStatus.Submitted);
        Assert.Equal(0, (byte)SnapshotStatus.Draft);
    }

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
