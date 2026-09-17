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
    /// ⚠ Загального коду «щось не знайдено» в каталозі
    /// (`02-contracts.md` §7) немає **навмисно**: клієнт має розрізняти
    /// «немає шаблону» і «немає рядка», бо це різні дії користувача. Тому
    /// тип, для якого коду немає, кидає не вигаданий код, а
    /// <see cref="InvalidOperationException"/> — це дефект коду, а не стан
    /// даних, і мовчки підставляти чужий код гірше.
    ///
    /// ⚠ Кількість кодів `0404` тут не називається: вона змінюється разом із
    /// каталогом, а число в коментарі — ні. За збігом стежить сторож
    /// <c>ContractIntegrityTests</c>.
    /// </remarks>
    private static readonly Dictionary<Type, NotFoundText> NotFoundTexts = new()
    {
        [typeof(Domain.Entities.Configuration.Template)] =
            new("ECR-TMPL-0404", "err.ECR-TMPL-0404.template", "шаблон", "templateId"),
        [typeof(Domain.Entities.Configuration.TemplateVersion)] =
            new("ECR-TMPL-0404", "err.ECR-TMPL-0404.templateVersion", "версію шаблону", "versionId"),

        // ⚠ Ключ і плейсхолдер тут НЕ нові: `err.ECR-DOC-0404.document` уже
        // заведений і вже вживається — той самий факт («документа немає»)
        // мусить читатися однаково, яким би шляхом код до нього не дійшов.
        [typeof(Domain.Entities.Documents.Document)] =
            new("ECR-DOC-0404", "err.ECR-DOC-0404.document", "документ", "documentId"),
        [typeof(Domain.Entities.Documents.TableRow)] =
            new("ECR-ROW-0404", "err.ECR-ROW-0404.tableRow", "рядок таблиці", "rowId"),
        [typeof(Domain.Entities.Dictionaries.RegistryEntry)] =
            new("ECR-REG-0404", "err.ECR-REG-0404.registryEntry", "запис довідника", "entryId"),
    };

    /// <summary>Як розповісти про відсутню сутність людині.</summary>
    /// <param name="Code">Код каталогу помилок.</param>
    /// <param name="MessageKey">Ключ рядка інтерфейсу для локалізованої подробиці.</param>
    /// <param name="Subject">
    /// Назва сутності у ЗНАХІДНОМУ відмінку для запасного українського
    /// речення («не знайдено <b>версію шаблону</b>»).
    /// </param>
    /// <param name="IdParameter">
    /// Ім'я плейсхолдера ідентифікатора в шаблоні каталогу. ⚠ Не спільне
    /// <c>{id}</c>: для документа ключ уже існує й уже вживає
    /// <c>{documentId}</c>, а підставляти в чужий шаблон інше ім'я означало б
    /// лишити плейсхолдер незаміненим просто в тексті для користувача.
    /// </param>
    /// <remarks>
    /// ⛔ Два нові поля з'явилися через дефект, видимий лише на екрані
    /// користувача. Повідомлення будувалося як
    /// <c>$"{typeof(T).Name} з ідентифікатором {id} не знайдено."</c> — тобто
    /// клієнтові їхало ІМ'Я КЛАСУ .NET: «TemplateVersion з ідентифікатором 5
    /// не знайдено». Для оператора це не назва нічого: у продукті немає
    /// сутності «TemplateVersion», є «версія шаблону». Локалізувати таке
    /// речення було б гірше, ніж лишити: переклад показав би внутрішнє ім'я
    /// типу під виглядом тексту для людини.
    ///
    /// ⚠ Ключ окремий для КОЖНОГО типу, хоч код помилки в двох із них
    /// спільний (<c>ECR-TMPL-0404</c> у шаблона й версії). Один ключ на код
    /// повернув би ту саму ваду з іншого боку: «не знайдено шаблон», коли
    /// насправді немає версії, — і людина шукала б не те.
    ///
    /// ⚠ Запасне українське речення лишається: його бачить журнал сервера, і
    /// воно ж спрацьовує, якщо ключа в каталозі немає
    /// (<c>ResolveGenericMessageAsync</c>).
    /// </remarks>
    private sealed record NotFoundText(
        string Code, string MessageKey, string Subject, string IdParameter);

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
    {
        var found = await FindAsync(id, ct).ConfigureAwait(false);
        if (found is not null)
        {
            return found;
        }

        if (!NotFoundTexts.TryGetValue(typeof(T), out var text))
        {
            throw new InvalidOperationException(
                $"Для {typeof(T).Name} немає коду «не знайдено» в каталозі. "
                + "Додайте код у 02-contracts.md §7 або використайте спеціалізований порт.");
        }

        // ⚠ Ідентифікатор іде в `Details` РЯДКОМ: `ResolveGenericMessageAsync`
        // підставляє в шаблон каталогу лише поля типу `string`, тож `TId`
        // (найчастіше `int`/`long`) мовчки лишився б незаміненим
        // плейсхолдером `{id}` просто в тексті для користувача.
        throw new NotFoundException(
            text.Code,
            $"Не знайдено {text.Subject} з ідентифікатором {id}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = text.MessageKey,
                [text.IdParameter] = id?.ToString() ?? string.Empty,
            });
    }

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
