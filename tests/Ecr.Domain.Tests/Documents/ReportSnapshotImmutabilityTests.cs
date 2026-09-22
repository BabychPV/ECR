// tests/Ecr.Domain.Tests/Documents/ReportSnapshotImmutabilityTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Поданий зріз іммутабельний — і за статусом, і за ВМІСТОМ (ФВ-9.17,
/// <c>ER-C-11</c>, <c>H-23b</c>).
/// </summary>
/// <remarks>
/// ⛔ Іммутабельність трималася лише на статусі: <c>RefreshStatus</c>
/// відмовляв, а <c>Complete</c> — ні. Тобто зріз, за яким звітність уже
/// подано, не міг помінятися на вигляд, зате міг тихо помінятися по суті:
/// кількість рядків і контрольна сума перезаписувалися, і той самий «поданий»
/// зріз віддавав інші числа. Це рівно та розбіжність, заради якої існує
/// <c>ER-C-11</c>: «трохи інші останні знаки» — це інша подана форма.
/// </remarks>
public sealed class ReportSnapshotImmutabilityTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ReportSnapshot Snapshot()
        => new(reportVersionId: 1, projectId: 3, periodKey: 202601, SnapshotStatus.Draft, Now, builtByUserId: null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public void Поданий_зріз_не_перебудовується()
    {
        var snapshot = Snapshot();
        snapshot.Complete(rowCount: 10, contentHash: [1], calculationRunId: 7, parametersJson: null);
        snapshot.MarkSubmitted(userId: 9);

        // ⛔ Регресія: охоронець прибрали — і повторна побудова мовчки
        // перепише числа під тією самою позначкою «подано». Ніщо не впаде;
        // розбіжність побачить регулятор, звіряючи дві роздруківки.
        var error = Assert.Throws<DomainException>(
            () => snapshot.Complete(rowCount: 11, contentHash: [2], calculationRunId: 8, parametersJson: null));

        Assert.Equal(ErrorCodes.ReportImmutable, error.ErrorCode);
        Assert.Equal(10, snapshot.RowCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Requirement", "ФВ-10.2")]
    public void Зріз_народжений_поданим_завершується_рівно_раз()
    {
        // ⚠ Статус успадковується від ДАНИХ (D-65): аркуші періоду вже подані,
        // тож зріз НАРОДЖУЄТЬСЯ `Submitted` — ще не маючи ні рядків, ні суми.
        var snapshot = new ReportSnapshot(
            reportVersionId: 1, projectId: 3, periodKey: 202601,
            SnapshotStatus.Submitted, Now, builtByUserId: null);

        // ⛔ Перше завершення мусить пройти: перебудовувати тут ще нічого.
        // Доки умовою був самий лише статус, воно відмовляло `ECR-RPT-0409` —
        // і зріз за поданий період не будувався ВЗАГАЛІ, тобто рівно тоді,
        // коли він і потрібен регуляторові (`tools/smoke.ps1`, крок 23).
        snapshot.Complete(rowCount: 10, contentHash: [1], calculationRunId: 7, parametersJson: null);

        Assert.Equal(10, snapshot.RowCount);

        // ⛔ А друге — ні: тепер зріз справді побудований, і його вміст
        // лишається тим, який бачив регулятор.
        var error = Assert.Throws<DomainException>(
            () => snapshot.Complete(rowCount: 11, contentHash: [2], calculationRunId: 8, parametersJson: null));

        Assert.Equal(ErrorCodes.ReportImmutable, error.ErrorCode);
        Assert.Equal(10, snapshot.RowCount);
        Assert.Equal(new byte[] { 1 }, snapshot.ContentHash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public void Поданий_зріз_не_міняє_статусу()
    {
        var snapshot = Snapshot();
        snapshot.MarkSubmitted(userId: 9);

        Assert.Throws<DomainException>(() => snapshot.RefreshStatus(SnapshotStatus.Draft));
        Assert.Equal(SnapshotStatus.Submitted, snapshot.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public void Повторне_подання_не_переписує_автора()
    {
        // ⚠ Аркушів у періоді кілька, і подання приходить кількома викликами.
        // Кожен наступний інакше стирав би того, хто справді закрив звітність,
        // останнім, хто натиснув кнопку.
        var snapshot = Snapshot();

        snapshot.MarkSubmitted(userId: 9);
        snapshot.MarkSubmitted(userId: 42);

        Assert.Equal(9, snapshot.BuiltByUserId);
    }
}
