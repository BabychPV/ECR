using System.IO.Compression;
using Ecr.Application.Errors;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Запобіжник від zip-bomb для книг <c>.xlsx</c>, що приходять ВІД
/// КОРИСТУВАЧА (`S10`): до ClosedXML книга проходить огляд zip-каталогу —
/// лише заголовки записів, без розпакування.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти межею була лише стеля тіла запиту (~30 МБ СТИСНУТОГО), а deflate
/// дає до ~1000:1: 30 МБ файлу — це десятки гігабайтів XML, які ClosedXML
/// чесно розбирає. Вимір у <c>XlsxZipBombTests</c>: аркуш на 48 МБ пробілів
/// (стиснуто — ~50 КБ) коштував розбору 289 МБ виділень, тобто ~6 байтів
/// пам'яті на байт розпакованого XML. Перевірки карти й документа йдуть уже
/// ПІСЛЯ розбору — вони від цього не захищають.
/// </para>
/// <para>
/// ⚠ Огляд довіряє розмірам із центрального каталогу zip — і це безпечно не
/// через довіру до файлу: <see cref="ZipArchiveEntry.Open"/> у .NET обрізає
/// розпакування рівно на оголошеному <see cref="ZipArchiveEntry.Length"/>
/// (той самий <see cref="ZipArchive"/> стоїть під <c>System.IO.Packaging</c>,
/// яким читає ClosedXML). Запис, що бреше про розмір, дає обірваний XML і
/// відмову розбору, а не більше даних.
/// </para>
/// <para>
/// ⚠ Межі — константи, а не конфігурація: це стеля безпеки, а не параметр
/// експлуатації, і її тихе «підкручування» в appsettings повертало б
/// вразливість без жодного сліду в коді.
/// </para>
/// </remarks>
public static class XlsxSafetyGate
{
    /// <summary>
    /// Стеля стиснутого файлу — рівно стандартна межа тіла запиту Kestrel
    /// (<c>30 000 000</c>), яку ендпоінт імпорту оголошує явно.
    /// </summary>
    /// <remarks>
    /// Діє лише тоді, коли потік не підтримує позиціювання і його доводиться
    /// буферизувати в пам'ять: без стелі цей буфер і став би вразливістю.
    /// </remarks>
    public const long MaxPackageBytes = 30_000_000;

    /// <summary>
    /// Сумарний розпакований розмір усіх записів — 256 МіБ.
    /// </summary>
    /// <remarks>
    /// Від найбільшої реальної книги: шаблон на 91 таблицю в обсязі директиви
    /// (91 × 500 × 60 = 2.73 млн комірок, фейковий світ
    /// `ExcelExporterPerformanceTests`, заповнено КОЖЕН рядок) після експорту
    /// системою — 20 записів, 113 МіБ розпакованого XML, 8.6 МБ файлу (замір
    /// 2026-09-29; заповнений кожен десятий рядок — 69 МіБ). Межа дає запас
    /// ~2.3× на довші рядки й пересохранення в Excel. Вище за неї — книга,
    /// розбір якої коштував би понад півтора гігабайта пам'яті (~6 байтів на
    /// байт XML), тобто вже не імпорт, а відмова в обслуговуванні.
    /// </remarks>
    public const long MaxUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>Скільки записів може мати пакет — 1 000.</summary>
    /// <remarks>
    /// Книга системи — ~10 службових частин і 1–3 частини на аркуш (XML
    /// аркуша, зв'язки, коментарі): навіть 91 таблиця на 91 окремому аркуші —
    /// ~300 записів. Межа втричі вища і відсікає пакет із десятків тисяч
    /// дрібних записів, де кожен окремо «чесний», а разом — обхід каталогу й
    /// тисячі потоків розбору.
    /// </remarks>
    public const int MaxEntries = 1_000;

    /// <summary>Найбільший чесний коефіцієнт стиснення запису — 100:1.</summary>
    /// <remarks>
    /// XML аркуша з однотипними комірками стискається сильно, але вимір на
    /// книзі директиви дав найбільше 13.3:1 (аркуш на 12 МіБ; `sharedStrings`
    /// — 9.1:1), тобто межа — з запасом ~7.5×; класична бомба — ~1000:1
    /// (межа deflate — ~1032:1). Перевіряється лише для записів, більших за
    /// <see cref="RatioCheckFloorBytes"/>: крихітна частина з повторів
    /// (порожні стилі, <c>sharedStrings</c> на три рядки) законно стискається
    /// будь-як і пам'яті не коштує.
    /// </remarks>
    public const int MaxCompressionRatio = 100;

    /// <summary>
    /// Від якого розпакованого розміру запису перевіряється коефіцієнт — 1 МіБ.
    /// </summary>
    public const long RatioCheckFloorBytes = 1024 * 1024;

    /// <summary>Межі за замовчуванням — ті, що діють у продуктиві.</summary>
    public static XlsxLimits DefaultLimits { get; } = new(
        MaxUncompressedBytes, MaxEntries, MaxCompressionRatio, RatioCheckFloorBytes);

    /// <summary>
    /// Оглядає zip-каталог без розпакування. Позиція потоку повертається туди,
    /// де була.
    /// </summary>
    /// <param name="file">Потік із підтримкою позиціювання.</param>
    /// <param name="limits">Межі; <c>null</c> — <see cref="DefaultLimits"/>.</param>
    /// <returns>Вердикт; <see cref="XlsxVerdict.Safe"/> — книгу можна розбирати.</returns>
    public static XlsxVerdict Inspect(Stream file, XlsxLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanSeek)
        {
            throw new ArgumentException("Огляд потребує потоку з позиціюванням.", nameof(file));
        }

        limits ??= DefaultLimits;
        var start = file.Position;

        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);

            // ⚠ `Entries` читає ЛИШЕ центральний каталог: жоден запис тут не
            // розпаковується.
            var entries = zip.Entries;
            if (entries.Count > limits.MaxEntries)
            {
                return XlsxVerdict.TooManyEntries;
            }

            long total = 0;
            foreach (var entry in entries)
            {
                if (entry.Length < 0 || entry.CompressedLength < 0)
                {
                    return XlsxVerdict.Corrupt;
                }

                // Сума не переповнюється: кожен доданок уже не більший за межу.
                if (entry.Length > limits.MaxUncompressedBytes
                    || (total += entry.Length) > limits.MaxUncompressedBytes)
                {
                    return XlsxVerdict.TooLarge;
                }

                if (entry.Length > limits.RatioCheckFloorBytes
                    && entry.Length > Math.Max(entry.CompressedLength, 1) * limits.MaxCompressionRatio)
                {
                    return XlsxVerdict.SuspiciousCompression;
                }
            }

            return XlsxVerdict.Safe;
        }
        catch (InvalidDataException)
        {
            // Немає центрального каталогу, зіпсований заголовок, не zip узагалі.
            return XlsxVerdict.Corrupt;
        }
        finally
        {
            file.Position = start;
        }
    }

    /// <summary>
    /// Той самий огляд із відмовою: непридатний пакет — <c>ECR-IMP-0422</c>
    /// з ключем <c>notAWorkbook</c>, тією самою відмовою, що й на файл, який
    /// ClosedXML не розібрав.
    /// </summary>
    /// <param name="file">Потік із підтримкою позиціювання.</param>
    /// <exception cref="BusinessRuleException">Пакет не пройшов огляд.</exception>
    /// <remarks>
    /// ⚠ Окремого коду й ключа немає навмисно: для користувача це той самий
    /// випадок «файл не читається як книга», а розрізнення «бомба чи просто
    /// зіпсований zip» потрібне журналу, не людині, — воно в тексті винятку.
    /// </remarks>
    public static void EnsureSafe(Stream file)
    {
        var verdict = Inspect(file);
        if (verdict == XlsxVerdict.Safe)
        {
            return;
        }

        throw Rejection($"огляд пакета — {verdict}");
    }

    /// <summary>Відмова «файл не читається як книга» з причиною для журналу.</summary>
    /// <param name="reason">Причина — у тексті винятку, не в деталях відповіді.</param>
    /// <returns>Виняток для <c>throw</c>.</returns>
    public static BusinessRuleException Rejection(string reason)
        => new(
            "ECR-IMP-0422",
            $"Файл не читається як книга .xlsx: {reason}.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.notAWorkbook" });

    /// <summary>
    /// Потік із позиціюванням: сам <paramref name="file"/>, якщо він уміє, або
    /// буфер у пам'яті не більший за <see cref="MaxPackageBytes"/>.
    /// </summary>
    /// <param name="file">Вхідний потік.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Потік для огляду й розбору; <c>null</c> — файл більший за стелю.</returns>
    public static async Task<Stream?> SeekableAsync(Stream file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.CanSeek)
        {
            return file;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await file.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPackageBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}

/// <summary>Межі огляду пакета <c>.xlsx</c>.</summary>
/// <param name="MaxUncompressedBytes">Сумарний розпакований розмір.</param>
/// <param name="MaxEntries">Кількість записів.</param>
/// <param name="MaxCompressionRatio">Коефіцієнт стиснення запису.</param>
/// <param name="RatioCheckFloorBytes">Від якого розміру запису перевіряти коефіцієнт.</param>
public sealed record XlsxLimits(
    long MaxUncompressedBytes, int MaxEntries, int MaxCompressionRatio, long RatioCheckFloorBytes);

/// <summary>Вердикт огляду пакета <c>.xlsx</c>.</summary>
public enum XlsxVerdict
{
    /// <summary>Можна розбирати.</summary>
    Safe,

    /// <summary>Не zip або зіпсований каталог.</summary>
    Corrupt,

    /// <summary>Записів більше за межу.</summary>
    TooManyEntries,

    /// <summary>Розпакований розмір більший за межу.</summary>
    TooLarge,

    /// <summary>Коефіцієнт стиснення запису вищий за чесний.</summary>
    SuspiciousCompression,
}
