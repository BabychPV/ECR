using Ecr.Application.Documents.VersionMigration;

namespace Ecr.Application.Ports;

/// <summary>Що лежить у документах проєкту — вхід планувальника переносу (ФВ-7.5).</summary>
/// <param name="DocumentIds">Документи проєкту.</param>
/// <param name="Cells">Непорожні значення за колонками й описами рядків поточної версії.</param>
/// <param name="HeaderValues">Непорожні значення шапки за полями поточної версії.</param>
/// <param name="LockedSheets">Скільки пар «аркуш × період» подано або затверджено.</param>
public sealed record VersionMigrationScope(
    IReadOnlyList<long> DocumentIds,
    IReadOnlyList<VersionMigrationCellUsage> Cells,
    IReadOnlyDictionary<int, long> HeaderValues,
    int LockedSheets);

/// <summary>Сховище переносу документів проєкту на нову версію шаблону (ФВ-7.5).</summary>
public interface IDocumentVersionMigrationStore
{
    /// <summary>
    /// Блокує рядок проєкту до кінця транзакції й повертає його поточну версію
    /// шаблону; <c>null</c> — проєкту немає.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише всередині транзакції: без неї блок знімається одразу, і два
    /// переноси того самого проєкту розійшлися б з планом, який перевіряли.
    /// </remarks>
    public Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct);

    /// <summary>Рахує, що лежить у документах проєкту.</summary>
    public Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct);

    /// <summary>
    /// Переносить дані всіх документів проєкту за планом і перемикає версію
    /// проєкту. Викликається лише в транзакції, після
    /// <see cref="LockProjectVersionAsync"/>.
    /// </summary>
    public Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct);
}
