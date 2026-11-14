using Ecr.Application.Ports;
using Microsoft.Extensions.Caching.Distributed;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Готові книги експорту в розподіленому кеші.
/// </summary>
/// <remarks>
/// ⚠ Те саме сховище, що й у diff-ів імпорту, і з тієї самої причини: між
/// побудовою і забиранням минає час, і другий запит цілком може потрапити на
/// інший інстанс застосунку.
///
/// ⚠ Книга на 500×60×12 — це одиниці мегабайтів. Класти її в кеш прийнятно
/// саме тому, що строк життя короткий: година, а не доба.
/// </remarks>
public sealed class ExportStore(IDistributedCache cache) : IExportStore
{
    /// <summary>Префікс ключа — щоб книги не змішалися з рештою кешу.</summary>
    /// <remarks>
    /// ⛔ S6: <c>v2</c> — заголовок запису виріс на власника. Запис старої
    /// форми (без власника) під новим префіксом просто не знаходиться —
    /// «побудуйте експорт заново», а не байти книги, прочитані як власник.
    /// Старі записи живуть годину й зникають самі.
    /// X-1 (аудит 2026-11): <c>v3</c> — у заголовку ще й ім'я файлу (довжина UTF-8 як <c>int</c> + байти); записи
    /// <c>v2</c> так само не знаходяться й зникають самі.
    /// </remarks>
    public const string KeyPrefix = "ecr:export:v3:";

    /// <summary>
    /// Довжина заголовка запису: <c>documentId</c> як <c>long</c> (Q-180),
    /// власник як <c>int</c> (S6) і довжина імені файлу як <c>int</c> (X-1; <c>-1</c> — імені немає) попереду
    /// байтів імені та вмісту книги.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>IDistributedCache</c> зберігає лише опаковані байти — жодної
    /// схеми БД тут немає (кеш на <c>AddDistributedSqlServerCache</c> — це
    /// одна таблиця «ключ → блоб», її форма не міняється), тож `documentId`
    /// і власник пакуються просто ПОПЕРЕДУ вмісту, а не окремими колонками.
    /// </remarks>
    private const int HeaderLength = sizeof(long) + sizeof(int) + sizeof(int);

    /// <inheritdoc />
    public Task SaveAsync(
        string exportId, long documentId, int ownerUserId, byte[] content, TimeSpan lifetime, CancellationToken ct,
        string? fileBaseName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        ArgumentNullException.ThrowIfNull(content);

        var name = fileBaseName is null ? [] : System.Text.Encoding.UTF8.GetBytes(fileBaseName);
        var envelope = new byte[HeaderLength + name.Length + content.Length];
        BitConverter.TryWriteBytes(envelope, documentId);
        BitConverter.TryWriteBytes(envelope.AsSpan(sizeof(long)), ownerUserId);
        BitConverter.TryWriteBytes(envelope.AsSpan(sizeof(long) + sizeof(int)), fileBaseName is null ? -1 : name.Length);
        name.CopyTo(envelope, HeaderLength);
        content.CopyTo(envelope, HeaderLength + name.Length);

        // ⛔ Абсолютний строк, а не ковзний: книга не має «продовжувати життя»
        // від того, що її кілька разів завантажили.
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime };

        return cache.SetAsync(KeyPrefix + exportId, envelope, options, ct);
    }

    /// <inheritdoc />
    public async Task<ExportedBook?> FindAsync(string exportId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);

        var envelope = await cache.GetAsync(KeyPrefix + exportId, ct).ConfigureAwait(false);
        if (envelope is null || envelope.Length < HeaderLength)
        {
            return null;
        }

        var documentId = BitConverter.ToInt64(envelope, 0);
        var ownerUserId = BitConverter.ToInt32(envelope, sizeof(long));
        var nameLength = BitConverter.ToInt32(envelope, sizeof(long) + sizeof(int));
        var nameBytes = Math.Max(nameLength, 0);
        if (envelope.Length < HeaderLength + nameBytes)
        {
            return null;
        }

        var fileBaseName = nameLength < 0 ? null : System.Text.Encoding.UTF8.GetString(envelope, HeaderLength, nameBytes);
        var content = envelope[(HeaderLength + nameBytes)..];

        return new ExportedBook(documentId, ownerUserId, content, fileBaseName);
    }
}
