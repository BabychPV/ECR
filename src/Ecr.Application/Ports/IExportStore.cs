// src/Ecr.Application/Ports/IExportStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Готові книги <c>.xlsx</c>, побудовані фоновою задачею.
/// </summary>
/// <remarks>
/// ⚠ Порт існує, бо експорт **розділений у часі**: задача будує книгу, а
/// забирає її інший запит — можливо, на іншому інстансі застосунку. Тримати
/// файл у пам'яті задачі означало б, що він зникає разом із нею; писати у
/// тимчасову теку інстансу — що його не бачить той, хто прийшов забирати.
/// <para>
/// ⛔ Строк життя обмежений навмисно. Книга — знімок даних на момент побудови;
/// віддана через добу, вона вже не відповідає документу, а виглядає як
/// свіжий експорт.
/// </para>
/// </remarks>
public interface IExportStore
{
    /// <summary>Кладе готову книгу.</summary>
    /// <param name="exportId">Ключ, названий у завданні на експорт.</param>
    /// <param name="content">Вміст книги.</param>
    /// <param name="lifetime">Скільки живе.</param>
    /// <param name="ct">Скасування.</param>
    public Task SaveAsync(string exportId, byte[] content, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Читає книгу; <c>null</c> — її немає або строк вийшов.</summary>
    /// <param name="exportId">Ключ експорту.</param>
    /// <param name="ct">Скасування.</param>
    public Task<byte[]?> FindAsync(string exportId, CancellationToken ct);
}
