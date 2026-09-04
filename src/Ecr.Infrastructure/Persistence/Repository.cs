using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IRepository{T, TId}"/> поверх <see cref="EcrDbContext"/>.
/// </summary>
/// <remarks>
/// Тонкий шар навмисно: він не ховає EF, а лише дає застосунку доступ до
/// сутностей без посилання на EF (`05-skeleton.md` §4). Усе складніше за
/// «знайти за ключем» пишеться спеціалізованим портом — як <c>IRowStore</c> чи
/// <c>ICellStore</c>: узагальнений репозиторій із запитами перетворюється на
/// другу, гіршу копію LINQ.
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1: порт `IRepository` там
/// оголошений, реалізація — ні (`Q-050`).
/// </remarks>
public sealed class Repository<T, TId>(EcrDbContext db) : IRepository<T, TId>
    where T : class
{
    /// <summary>
    /// Код помилки «не знайдено» за типом сутності.
    /// </summary>
    /// <remarks>
    /// ⚠ Каталог кодів фіксований (`02-contracts.md` §7) і містить рівно
    /// чотири коди `0404`. Загального коду «щось не знайдено» в ньому немає
    /// **навмисно**: клієнт має розрізняти «немає шаблону» і «немає рядка»,
    /// бо це різні дії користувача. Тому тип, для якого коду немає, кидає не
    /// вигаданий код, а <see cref="InvalidOperationException"/> — це дефект
    /// коду, а не стан даних, і мовчки підставляти чужий код гірше.
    /// </remarks>
    private static readonly Dictionary<Type, string> NotFoundCodes = new()
    {
        [typeof(Domain.Entities.Configuration.Template)] = "ECR-TMPL-0404",
        [typeof(Domain.Entities.Configuration.TemplateVersion)] = "ECR-TMPL-0404",
        [typeof(Domain.Entities.Documents.Document)] = "ECR-DOC-0404",
        [typeof(Domain.Entities.Documents.TableRow)] = "ECR-ROW-0404",
        [typeof(Domain.Entities.Dictionaries.RegistryEntry)] = "ECR-REG-0404",
    };

    /// <inheritdoc />
    public async Task<T?> FindAsync(TId id, CancellationToken ct)
        => await db.Set<T>().FindAsync([id], ct).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Кидає <see cref="NotFoundException"/>, а не повертає <c>null</c>: у
    /// use-case «взяти шаблон, якого немає» — це 404, і перетворювати його на
    /// <c>NullReferenceException</c> десятьма рядками нижче немає сенсу.
    /// </remarks>
    public async Task<T> GetAsync(TId id, CancellationToken ct)
        => await FindAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               NotFoundCodes.TryGetValue(typeof(T), out var code)
                   ? code
                   : throw new InvalidOperationException(
                       $"Для {typeof(T).Name} немає коду «не знайдено» в каталозі. " +
                       "Додайте код у 02-contracts.md §7 або використайте спеціалізований порт."),
               $"{typeof(T).Name} з ідентифікатором {id} не знайдено.");

    /// <inheritdoc />
    public void Add(T entity) => db.Set<T>().Add(entity);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Фізичне видалення. У системі діє soft delete (ФВ-7.6), тому цей метод
    /// застосовний лише до того, на що ніхто не посилається; для решти
    /// сутність має власний <c>SoftDelete</c>.
    /// </remarks>
    public void Remove(T entity) => db.Set<T>().Remove(entity);
}
