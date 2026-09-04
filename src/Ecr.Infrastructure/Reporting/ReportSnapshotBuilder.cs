using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Будує незмінні зрізи <c>rpt.*</c> для регламентної звітності.
/// </summary>
/// <remarks>
/// Межа з SSRS проходить саме тут: звіти лишаються в SSRS (<c>D-52</c>), а ми
/// віддаємо стабільний контракт даних. <c>rpt.*</c> — зріз **без логіки**:
/// агрегації робить цей сервіс, вʼюха лише проєктує (ФВ-0.3).
/// </remarks>
public sealed class ReportSnapshotBuilder(EcrDbContext db) : IReportSnapshotBuilder
{
    /// <inheritdoc />
    public Task<long> BuildAsync(int reportVersionId, int projectId, PeriodKey? periodKey,
                                 string? parametersJson, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) прочитати дані одним набором запитів, не в циклі по рядках;\n" +
            "2) агрегувати ТУТ — вʼюха rpt.v_* нічого не рахує (ФВ-0.3);\n" +
            "3) записати рядки зрізу пакетно (SqlBulkCopy);\n" +
            "4) Status успадкувати від даних: поки аркуші не затверджені — Draft (D-65), " +
            "   і регуляторні вʼюхи такий зріз не віддають;\n" +
            "5) зафіксувати версії шаблону і методологій, з яких побудовано, — без цього " +
            "   неможливо сказати, на чому стоїть число;\n" +
            "6) periodKey = null означає зріз за весь рік проєкту, а не «за поточний період».");

    /// <inheritdoc />
    public Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: після подання зріз ІММУТАБЕЛЬНИЙ. Повторна побудова створює НОВИЙ зріз, " +
            "а не переписує цей: інакше звіт, роздрукований учора, і той самий звіт сьогодні " +
            "дали б різні числа без жодного сліду.");

    /// <inheritdoc />
    public Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перерахувати статус за поточним станом wf.ApprovalState зачеплених аркушів. " +
            "⚠ Поданий зріз статусу не міняє — він уже іммутабельний.");
}
