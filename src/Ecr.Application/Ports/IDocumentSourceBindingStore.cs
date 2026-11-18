// src/Ecr.Application/Ports/IDocumentSourceBindingStore.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>B80 (N1): зв'язки «документ-джерело → довідник → споживачі» (<c>cfg.DocumentSourceBinding</c>).</summary>
public interface IDocumentSourceBindingStore
{
    /// <summary>Усі зв'язки з кодами шаблону-джерела, довідника і шаблонів-споживачів.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<DocumentSourceBindingView>> ListAsync(CancellationToken ct);

    /// <summary>Зв'язка для зміни, під блоком оновлення рядка (у поточній транзакції); <c>null</c> — немає.</summary>
    /// <param name="id">Зв'язка.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentSourceBinding?> LockAsync(int id, CancellationToken ct);

    /// <summary>
    /// Документ-джерело, який завантажувач обрав би за рік <paramref name="year"/> (найбільший id серед документів
    /// із таблицями зв'язки за цей рік, без проєкту золотих тестів); <c>null</c> — документа немає.
    /// </summary>
    /// <param name="bindingId">Зв'язка.</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SourceDocumentFacts?> ChosenSourceDocumentAsync(int bindingId, int year, CancellationToken ct);

    /// <summary>Роки, за які в зв'язки є документ-джерело (без проєкту золотих тестів), за зростанням.</summary>
    /// <param name="bindingId">Зв'язка.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<int>> SourceYearsAsync(int bindingId, CancellationToken ct);

    /// <summary>Зберігає зміни відстежуваної зв'язки.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task SaveAsync(CancellationToken ct);
}

/// <summary>Зв'язка для читання.</summary>
/// <param name="Id">Зв'язка.</param>
/// <param name="Code">Код.</param>
/// <param name="SourceTemplateCode">Код шаблону-джерела.</param>
/// <param name="RegistryCode">Код довідника.</param>
/// <param name="SourceState">Стан-ворота.</param>
/// <param name="Policy">Політика згоди.</param>
/// <param name="ReadFromDocument">Біт «читати з документа».</param>
/// <param name="RecalculateDependents">Біт «ставити перерахунок залежних».</param>
/// <param name="ConsumerTemplateCodes">Коди шаблонів-споживачів.</param>
/// <param name="UpdatedAt">Остання зміна стану-воріт, UTC.</param>
/// <param name="UpdatedByUserId">Хто змінив.</param>
public sealed record DocumentSourceBindingView(
    int Id,
    string Code,
    string SourceTemplateCode,
    string RegistryCode,
    DocumentSourceState SourceState,
    DocumentSourcePolicy Policy,
    bool ReadFromDocument,
    bool RecalculateDependents,
    IReadOnlyList<string> ConsumerTemplateCodes,
    DateTime? UpdatedAt,
    int? UpdatedByUserId);

/// <summary>Факти обраного документа-джерела для перевірки перемикання.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKind">Періодичність його проєкту.</param>
/// <param name="WorkflowMode">Режим погодження його версії шаблону.</param>
public sealed record SourceDocumentFacts(long DocumentId, PeriodKind PeriodKind, WorkflowMode WorkflowMode);

/// <summary>
/// B80: перекриття біта зв'язки «читати з документа» ключем конфігурації (ключ, заданий явно, діє один випуск; не
/// заданий — <c>null</c>).
/// </summary>
/// <remarks>
/// ✎ B179 L2 (P-0118-34): ключа <c>Calculations:Hse400RecalculateLandDependents</c> більше немає — він постачався
/// <c>false</c> і мовчки перекривав біт <c>RecalculateDependents</c> (корінь дефекту live). Перерахунок залежних вимикає
/// лише біт зв'язки.
/// </remarks>
public interface IDocumentSourceFlagOverrides
{
    /// <summary>Явне значення <c>Calculations:Hse400CompositionFromDocument</c>; <c>null</c> — не задано.</summary>
    public bool? ReadFromDocument { get; }
}
