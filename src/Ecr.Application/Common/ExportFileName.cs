using System.Globalization;
using System.Text;

namespace Ecr.Application.Common;

/// <summary>Людські й безпечні для Windows/macOS/Linux імена файлів експорту (аудит 2026-11, X-1).</summary>
/// <remarks>
/// ⚠ Кирилиця й казахські літери лишаються як є: повне ім'я йде в `filename*=UTF-8''…` (RFC 5987), а в `filename` —
/// ASCII-запасне (<see cref="AsciiFallback"/>). Не використовувати тут
/// `System.Net.Http.Headers.ContentDispositionHeaderValue.FileName` — для не-ASCII він дає RFC 2047 (`=?utf-8?B?…?=`).
/// </remarks>
public static class ExportFileName
{
    /// <summary>Найбільша довжина основи імені (без розширення), символів.</summary>
    /// <remarks>Excel не відкриває файл із повним шляхом понад 218 символів; 100 лишає запас на теку.</remarks>
    public const int MaxBaseLength = 100;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Безпечна основа імені; порожнє після очищення — <paramref name="fallback"/>.</summary>
    /// <param name="raw">Сире ім'я.</param>
    /// <param name="fallback">Що взяти, якщо від імені нічого не лишилось.</param>
    /// <returns>Очищена й обрізана основа імені.</returns>
    public static string Sanitize(string? raw, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var text = Clean(raw);
        return Truncate(text.Length == 0 ? Clean(fallback) : text, MaxBaseLength);
    }

    /// <summary>Період людською формою (рішення PO 11.11): <c>2026-11</c>, <c>Q2 2026</c>, <c>2026</c>, інакше <c>2026-P05</c>.</summary>
    /// <param name="periodKey"><c>Рік*100 + Номер</c>.</param>
    /// <param name="periodKind"><c>PeriodKind.ToString()</c> проєкту: Monthly / Quarterly / Yearly / Custom; <c>null</c> — невідомо.</param>
    /// <returns>Період у формі для імені файлу.</returns>
    public static string Period(int periodKey, string? periodKind)
    {
        var year = periodKey / 100;
        var sequence = periodKey % 100;
        return periodKind switch
        {
            "Monthly" => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{sequence:D2}"),
            "Quarterly" => string.Create(CultureInfo.InvariantCulture, $"Q{sequence} {year:D4}"),
            "Yearly" => year.ToString("D4", CultureInfo.InvariantCulture),
            _ => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-P{sequence:D2}"),
        };
    }

    /// <summary>
    /// Основа імені експорту: «{назва} ({код}) {період}{суфікс}»; без назви (або назва = код) — «{код} {період}{суфікс}»;
    /// без коду — «{назва} {період}{суфікс}»; без обох — <paramref name="fallback"/>. ОДНА функція для документа (X-1) і
    /// зрізу звіту (X-8), рішення PO 11.11.
    /// </summary>
    /// <param name="code">Код: File Number документа або код звіту.</param>
    /// <param name="name">Назва мовою замовника.</param>
    /// <param name="periodKey">Період; <c>null</c> — без періоду.</param>
    /// <param name="periodKind">Періодичність проєкту (див. <see cref="Period"/>).</param>
    /// <param name="suffix">Хвіст після періоду (наприклад, номер зрізу).</param>
    /// <param name="fallback">Основа, коли немає ні коду, ні назви.</param>
    /// <returns>Основа імені файлу без розширення.</returns>
    /// <remarks>Ріжеться назва, а не код, період чи суфікс: саме вони розрізняють файли.</remarks>
    public static string ForExport(
        string? code, string? name, int? periodKey, string? periodKind, string? suffix = null, string fallback = "document")
    {
        var tail = (periodKey is { } key ? " " + Period(key, periodKind) : string.Empty) + (suffix ?? string.Empty);
        var cleanCode = Clean(code);
        var cleanName = Clean(name);

        string head;
        if (cleanName.Length == 0 || string.Equals(cleanName, cleanCode, StringComparison.OrdinalIgnoreCase))
        {
            head = cleanCode.Length == 0 ? fallback : cleanCode;
        }
        else if (cleanCode.Length == 0)
        {
            head = cleanName;
        }
        else
        {
            var room = MaxBaseLength - tail.Length - cleanCode.Length - 3; // " (" + ")"
            head = room >= 10 ? $"{Truncate(cleanName, room)} ({cleanCode})" : cleanCode;
        }

        return Truncate(head, MaxBaseLength - tail.Length) + tail;
    }

    /// <summary>X-1: «{назва документа} ({File Number}) {період}».</summary>
    /// <param name="businessKey">File Number документа.</param>
    /// <param name="documentName">Назва документа мовою замовника.</param>
    /// <param name="periodKey">Період експорту.</param>
    /// <param name="periodKind">Періодичність проєкту.</param>
    /// <returns>Основа імені файлу без розширення.</returns>
    public static string ForDocument(string? businessKey, string? documentName, int periodKey, string? periodKind)
        => ForExport(businessKey, documentName, periodKey, periodKind);

    /// <summary>X-8: «{назва звіту} ({код звіту}) {період} #{id}»; id — бо зрізів одного звіту й періоду буває кілька.</summary>
    /// <param name="reportCode">Код звіту.</param>
    /// <param name="reportName">Назва звіту мовою замовника.</param>
    /// <param name="periodKey">Період зрізу; <c>null</c> — річний зріз без періоду.</param>
    /// <param name="periodKind">Періодичність проєкту.</param>
    /// <param name="snapshotId">Ідентифікатор зрізу.</param>
    /// <returns>Основа імені файлу без розширення.</returns>
    public static string ForSnapshot(string? reportCode, string? reportName, int? periodKey, string? periodKind, long snapshotId)
        => ForExport(
            reportCode, reportName, periodKey, periodKind,
            suffix: string.Create(CultureInfo.InvariantCulture, $" #{snapshotId}"), fallback: "snapshot");

    /// <summary>
    /// ASCII-запасне ім'я для параметра <c>filename</c> (клієнти без RFC 5987): лишаються друковані ASCII-символи; якщо
    /// від назви нічого не лишилось — дужки навколо коду знімаються («Звіт (LAND-0007) 2026-11» → «LAND-0007 2026-11»);
    /// менше трьох літер/цифр — <paramref name="asciiDefault"/>.
    /// </summary>
    /// <param name="baseName">Основа імені (може містити не-ASCII).</param>
    /// <param name="asciiDefault">Що взяти, коли ASCII-залишок замалий.</param>
    /// <returns>Основа імені з самих друкованих ASCII-символів.</returns>
    public static string AsciiFallback(string baseName, string asciiDefault)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(asciiDefault);

        var builder = new StringBuilder(baseName.Length);
        foreach (var symbol in baseName)
        {
            if (symbol is >= ' ' and <= '~')
            {
                builder.Append(symbol);
            }
        }

        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (text.StartsWith('(') && text.IndexOf(')', StringComparison.Ordinal) is var close and > 0)
        {
            text = (text[1..close] + text[(close + 1)..]).Trim();
        }

        return text.Count(char.IsLetterOrDigit) >= 3 ? text : asciiDefault;
    }

    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var symbol in raw)
        {
            builder.Append(symbol switch
            {
                '"' => '\'',
                '/' or '\\' or '|' => '-',
                ':' or '*' or '?' or '<' or '>' => '_',
                _ when char.IsControl(symbol) => ' ',
                _ => symbol,
            });
        }

        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var trimmed = collapsed.Trim('.', ' ');
        return trimmed.Length > 0 && Reserved.Contains(trimmed.Split('.')[0]) ? "_" + trimmed : trimmed;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // ⚠ За текстовими елементами: різ посеред сурогатної пари дав би зіпсований UTF-8 у заголовку.
        var info = new StringInfo(text);
        var builder = new StringBuilder(max);
        for (var i = 0; i < info.LengthInTextElements; i++)
        {
            var element = info.SubstringByTextElements(i, 1);
            if (builder.Length + element.Length > max)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString().TrimEnd('.', ' ');
    }
}
