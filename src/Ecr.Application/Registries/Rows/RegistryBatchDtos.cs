// src/Ecr.Application/Registries/Rows/RegistryBatchDtos.cs
using Ecr.Application.Registries.Rules;

namespace Ecr.Application.Registries.Rows;

/// <summary>Пакет змін рядків довідника (<c>POST /registries/{code}/entries/batch</c>, RT-14).</summary>
/// <param name="Items">Рядки пакета, ≤ <see cref="RegistryBatchHandler.MaxItems"/>.</param>
public sealed record RegistryBatchRequest(IReadOnlyList<RegistryBatchItemDto> Items);

/// <summary>Один рядок пакета.</summary>
/// <param name="ClientRowId">Ідентифікатор рядка на клієнті — ним звіт адресує результат.</param>
/// <param name="Op"><c>upsert</c> або <c>delete</c>.</param>
/// <param name="Id">Наявний запис; <c>null</c> — новий (лише для <c>upsert</c>).</param>
/// <param name="Code">
/// Код нового запису довідника з ручним кодом; для <c>CodeMode = Auto</c> і для наявного запису — порожньо.
/// </param>
/// <param name="BaseVersion">
/// <c>version</c> рядка з <c>GET …/rows</c> (<c>D-166</c>); не збігся — помилка рядка <c>entryChanged</c>.
/// <c>null</c> — без перевірки.
/// </param>
/// <param name="Values">Значення за кодами полів; поле, якого немає, не змінюється.</param>
public sealed record RegistryBatchItemDto(
    string ClientRowId,
    string Op,
    long? Id,
    string? Code,
    string? BaseVersion,
    IReadOnlyDictionary<string, object?>? Values);

/// <summary>Звіт пакета — завжди 200, як імпорт CSV.</summary>
/// <param name="Applied">Чи записано зміни; хоч одна помилка рядка або <c>dryRun</c> — ні.</param>
/// <param name="DryRun">Прогін без запису.</param>
/// <param name="Added">Нових записів.</param>
/// <param name="Updated">Змінених записів.</param>
/// <param name="Deleted">Видалених записів.</param>
/// <param name="Unchanged">Записів без фактичної зміни.</param>
/// <param name="Rows">Результат кожного рядка пакета в його порядку.</param>
public sealed record RegistryBatchResult(
    bool Applied,
    bool DryRun,
    int Added,
    int Updated,
    int Deleted,
    int Unchanged,
    IReadOnlyList<RegistryBatchRowResult> Rows)
{
    /// <summary>
    /// Порушення правил довідника після пакета (RT-17a, §7.1): записані рядки й батьки композиції.
    /// Застосований пакет несе лише <c>Info</c>/<c>Warning</c> (<c>Error</c> — відмова
    /// <c>422 ECR-REG-4221</c>); <c>dryRun</c> — усі рівні, і <c>Error</c> теж: так сітка бачить Σ до
    /// збереження. Порожньо — порушень немає або пакет не дійшов до правил через помилки рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ У <c>dryRun</c> <c>entryId</c> НОВОГО запису вигаданий (транзакцію відкочено), а код — заглушка
    /// авто-коду; правила батька завжди адресують справжній запис.
    /// </remarks>
    public IReadOnlyList<RegistryRuleViolationDto> Rules { get; init; } = [];
}

/// <summary>Результат рядка пакета.</summary>
/// <param name="ClientRowId">Ідентифікатор рядка на клієнті.</param>
/// <param name="Status"><c>added</c>, <c>updated</c>, <c>unchanged</c>, <c>deleted</c> або <c>error</c>.</param>
/// <param name="EntryId">Запис; для нового — лише після запису.</param>
/// <param name="Version">Нова версія рядка після запису — наступний <c>baseVersion</c>; інакше <c>null</c>.</param>
/// <param name="Errors">Помилки рядка; порожньо — рядок пройшов.</param>
public sealed record RegistryBatchRowResult(
    string ClientRowId,
    string Status,
    long? EntryId,
    string? Version,
    IReadOnlyList<RegistryBatchRowError> Errors);

/// <summary>Помилка рядка пакета.</summary>
/// <param name="Field">Поле; <c>null</c> — помилка рядка цілком.</param>
/// <param name="ErrorCode">Код помилки (<c>ECR-…</c>).</param>
/// <param name="MessageKey">Ключ тексту в каталозі.</param>
/// <param name="Params">Параметри тексту.</param>
public sealed record RegistryBatchRowError(
    string? Field,
    string ErrorCode,
    string MessageKey,
    IReadOnlyDictionary<string, string?> Params);
