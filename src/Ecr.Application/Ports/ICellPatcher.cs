// src/Ecr.Application/Ports/ICellPatcher.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис комірок від імені інтеграції (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт існує, щоб інтеграція писала ТИМ САМИМ шляхом, що й людина, а не
/// власним. Директива забороняє другий шлях запису прямо, і причина названа:
/// саме другий шлях дав `A7-27` — там мапа колонок будувалася інакше, ніж на
/// основному, і адресація розійшлася.
///
/// ⚠ Порт у прикладному шарі, а реалізація поверх <c>PatchCellsHandler</c>:
/// задача живе в інфраструктурі й не має посилатися на обробники напряму.
/// </remarks>
public interface ICellPatcher
{
    /// <summary>Записує згорнуті значення збору в комірки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="cells">Значення: рядок, колонка, число.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки записано і які комірки лишено за людиною.</returns>
    /// <remarks>
    /// ⛔ Комірку з правкою людини реалізація НЕ перезаписує (`D-118`): людина
    /// виправила навмисно, і інтеграція не має права це стерти. Але й мовчати
    /// не можна — такі комірки повертаються переліком, і викликач кладе їх у
    /// журнал покриття.
    /// </remarks>
    public Task<IntegrationWriteResult> ApplyIntegrationAsync(
        long documentId,
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationCellValue> cells,
        CancellationToken ct);
}

/// <summary>Одне згорнуте значення для запису.</summary>
/// <param name="RowKey">Рядок-адресат із мапінгу.</param>
/// <param name="ColumnDefId">Колонка-адресат.</param>
/// <param name="Value">Згорнуте значення.</param>
public sealed record IntegrationCellValue(string RowKey, int ColumnDefId, decimal Value);

/// <summary>Наслідок запису від інтеграції.</summary>
/// <param name="Applied">Скільки комірок записано.</param>
/// <param name="KeptManual">Комірки, лишені за людиною: <c>rowKey:columnCode</c>.</param>
public sealed record IntegrationWriteResult(int Applied, IReadOnlyList<string> KeptManual);

/// <summary>
/// Журнал покриття збору (<c>itg.CollectionCoverage</c>).
/// </summary>
/// <remarks>
/// ⚠ **Ознака здоров'я — саме журнал, а не тиша.** Прогалина в покритті
/// означає, що даних за проміжок немає, — і це видно, лише якщо покриття
/// записується (ІНТ-3.3).
/// </remarks>
public interface ICoverageJournal
{
    /// <summary>Записує подію покриття.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="status">Статус: <c>SkippedPeriodClosed</c>, <c>ConflictKeptManual</c>.</param>
    /// <param name="details">Пояснення для людини; без стеків (ФВ-6.11).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RecordAsync(
        int sourceEntityId, PeriodKey periodKey, string status, string details, CancellationToken ct);

    /// <summary>Записує кілька подій покриття ОДНИМ <c>SaveChangesAsync</c>.</summary>
    /// <remarks>
    /// ⛔ Q-170 (аудит фази 2, продуктивність). Виклик <see cref="RecordAsync"/>
    /// у циклі на кожен конфлікт «залишено за людиною» коштує окремого
    /// <c>SaveChangesAsync</c> на кожен запис; тут — один похід на весь набір.
    /// </remarks>
    public Task RecordManyAsync(IReadOnlyList<CoverageEvent> events, CancellationToken ct);
}

/// <summary>Одна подія покриття для пакетного запису.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Status">Статус: <c>SkippedPeriodClosed</c>, <c>ConflictKeptManual</c>.</param>
/// <param name="Details">Пояснення для людини; без стеків (ФВ-6.11).</param>
public sealed record CoverageEvent(int SourceEntityId, PeriodKey PeriodKey, string Status, string Details);
