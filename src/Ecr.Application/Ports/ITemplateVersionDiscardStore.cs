// src/Ecr.Application/Ports/ITemplateVersionDiscardStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Відкидання (видалення) версії-чернетки шаблону разом з її структурою.
/// </summary>
/// <remarks>
/// Окремий порт за зразком <see cref="IMethodologyVersionDeletionStore"/>. Усі три методи
/// викликаються в ОДНІЙ транзакції: версія читається під блокуванням, тож паралельна
/// публікація чи клон не проскочать між перевіркою й видаленням.
/// </remarks>
public interface ITemplateVersionDiscardStore
{
    /// <summary>Версія під <c>UPDLOCK, HOLDLOCK</c> (без структури).</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c>, якщо версії немає.</returns>
    public Task<TemplateVersion?> LockAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Що поза конфігурацією версії на неї посилається (проєкти, документи, мапінги, клони…).
    /// </summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Вид використання → кількість; порожньо — версію можна відкинути.</returns>
    public Task<IReadOnlyDictionary<string, int>> FindUsagesAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Видаляє версію і всю її структуру (<c>cfg.*</c>, ресурсні гранти).</summary>
    /// <param name="templateVersionId">Версія, яку дозволив домен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Таблиця → скільки рядків видалено.</returns>
    public Task<IReadOnlyDictionary<string, int>> DeleteAsync(int templateVersionId, CancellationToken ct);
}
