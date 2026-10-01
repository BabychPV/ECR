// src/Ecr.Api/Security/CspReportParser.cs

using System.Text.Json;

namespace Ecr.Api.Security;

/// <summary>Одне порушення CSP, зведене до безпечного для журналу вигляду.</summary>
/// <param name="BlockedUri">Що заблоковано: адреса БЕЗ query/fragment або службове слово (<c>inline</c>, <c>eval</c>).</param>
/// <param name="ViolatedDirective">Порушена директива, обрізана.</param>
/// <param name="Directive">Ім'я директиви для тегу метрики: із закритого переліку або <c>other</c>.</param>
/// <param name="DocumentUri">Сторінка, на якій сталося, БЕЗ query/fragment.</param>
/// <param name="SourceFile">Файл-джерело порушення БЕЗ query/fragment; порожньо, якщо не названо.</param>
/// <param name="LineNumber">Рядок у файлі; <c>null</c>, якщо не названо.</param>
public sealed record CspViolation(
    string BlockedUri,
    string ViolatedDirective,
    string Directive,
    string DocumentUri,
    string SourceFile,
    int? LineNumber);

/// <summary>
/// Розбір тіла звіту CSP: застарілий <c>report-uri</c> і Reporting API.
/// </summary>
/// <remarks>
/// ⛔ Тіло приходить від АНОНІМНОГО джерела, і кожне поле — недовірене. Тому:
/// <list type="bullet">
/// <item><b>query і fragment відрізаються</b> в усіх адресах — там бувають
/// ідентифікатори документів, а іноді й токени; у журнал їх нести немає
/// причини (вимога S14). Разом із ними йде <c>userinfo</c>.</item>
/// <item><b>довжина обрізається</b> (<see cref="MaxFieldLength"/>) — журнал не
/// має рости від чужого тіла.</item>
/// <item><b>керівні символи замінюються</b> — розщеплення рядка журналу.</item>
/// <item><b>тег метрики — із закритого переліку</b> директив: довільний рядок
/// у тезі — це необмежена кількість часових рядів, якою нападник
/// роздуває пам'ять експортера.</item>
/// </list>
///
/// ⚠ Дві форми тіла. Застаріла (<c>Content-Type: application/csp-report</c>):
/// <c>{"csp-report":{"blocked-uri":…,"violated-directive":…}}</c>, ключі через
/// дефіс. Reporting API (<c>application/reports+json</c>, директива
/// <c>report-to</c>): масив <c>[{"type":"csp-violation","body":{"blockedURL":…}}]</c>,
/// ключі в camelCase. Firefox шле лише першу, Chrome — другу, якщо є
/// <c>report-to</c>, тож приймач знає обидві.
/// </remarks>
public static class CspReportParser
{
    /// <summary>Скільки символів лишається від будь-якого текстового поля.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>Скільки звітів обробляється з одного масиву Reporting API.</summary>
    private const int MaxReportsPerRequest = 20;

    /// <summary>Назви директив CSP — єдині значення тегу <c>directive</c>.</summary>
    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "default-src", "script-src", "script-src-elem", "script-src-attr",
        "style-src", "style-src-elem", "style-src-attr",
        "img-src", "font-src", "connect-src", "media-src", "object-src",
        "frame-src", "child-src", "worker-src", "manifest-src", "prefetch-src",
        "frame-ancestors", "base-uri", "form-action", "navigate-to",
        "require-trusted-types-for", "trusted-types",
    };

    /// <summary>Тег метрики для всього, чого немає в переліку.</summary>
    public const string OtherDirective = "other";

    /// <summary>Розбирає тіло звіту.</summary>
    /// <param name="body">Сирі байти тіла.</param>
    /// <param name="violations">Порушення; порожньо, якщо тіло — не звіт про порушення CSP.</param>
    /// <returns><c>false</c> — тіло не JSON або не має жодної відомої форми звіту (400).</returns>
    public static bool TryParse(ReadOnlyMemory<byte> body, out IReadOnlyList<CspViolation> violations)
    {
        violations = [];

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var result = new List<CspViolation>();

            switch (root.ValueKind)
            {
                case JsonValueKind.Object when root.TryGetProperty("csp-report", out var legacy):
                    if (legacy.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    result.Add(Read(legacy, Legacy));
                    break;

                case JsonValueKind.Object when IsReportingItem(root):
                    result.AddRange(ReadReportingItem(root));
                    break;

                case JsonValueKind.Array:
                    foreach (var item in root.EnumerateArray().Take(MaxReportsPerRequest))
                    {
                        if (item.ValueKind == JsonValueKind.Object && IsReportingItem(item))
                        {
                            result.AddRange(ReadReportingItem(item));
                        }
                    }

                    break;

                default:
                    return false;
            }

            violations = result;
            return true;
        }
    }

    /// <summary>Ім'я директиви для метрики: перше слово, мала літера, лише з переліку.</summary>
    /// <param name="directive">Значення <c>violated-directive</c> чи <c>effectiveDirective</c>.</param>
    public static string DirectiveName(string? directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return OtherDirective;
        }

        var name = directive.AsSpan().Trim();
        var space = name.IndexOfAny(' ', '\t');
        var first = (space < 0 ? name : name[..space]).ToString().ToLowerInvariant();

        return KnownDirectives.Contains(first) ? first : OtherDirective;
    }

    /// <summary>
    /// Адреса без query, fragment і userinfo; службові слова й схеми без хоста —
    /// без змін чи згорнуті до схеми.
    /// </summary>
    /// <param name="value">Адреса із звіту.</param>
    public static string StripUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();

        // `data:image/png;base64,…` — сам вміст не потрібен і може бути великим.
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "data";
        }

        if (text.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return "blob";
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            text = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }
        else
        {
            var cut = text.IndexOfAny(['?', '#']);
            if (cut >= 0)
            {
                text = text[..cut];
            }
        }

        return Clean(text);
    }

    private static readonly FieldNames Legacy = new(
        "blocked-uri", "violated-directive", "effective-directive", "document-uri", "source-file", "line-number");

    private static readonly FieldNames Reporting = new(
        "blockedURL", "violatedDirective", "effectiveDirective", "documentURL", "sourceFile", "lineNumber");

    private static bool IsReportingItem(JsonElement item)
        => item.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && item.TryGetProperty("body", out var body)
           && body.ValueKind == JsonValueKind.Object;

    private static IEnumerable<CspViolation> ReadReportingItem(JsonElement item)
    {
        // Інші типи Reporting API (deprecation, intervention, …) — не наш предмет: не помилка, але й не рахуємо.
        if (!string.Equals(item.GetProperty("type").GetString(), "csp-violation", StringComparison.Ordinal))
        {
            yield break;
        }

        yield return Read(item.GetProperty("body"), Reporting);
    }

    private static CspViolation Read(JsonElement body, FieldNames names)
    {
        var violated = Text(body, names.Violated);
        var effective = Text(body, names.Effective);

        // ⚠ Тег — за `effective` (точніша: `script-src-elem`, а не `script-src`),
        // а коли її немає — за `violated`.
        var directive = DirectiveName(string.IsNullOrEmpty(effective) ? violated : effective);

        return new CspViolation(
            StripUrl(Text(body, names.Blocked)),
            Clean(string.IsNullOrEmpty(violated) ? effective : violated),
            directive,
            StripUrl(Text(body, names.Document)),
            StripUrl(Text(body, names.Source)),
            Line(body, names.Line));
    }

    private static string Text(JsonElement body, string name)
        => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? Line(JsonElement body, string name)
        => body.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var line)
           && line >= 0
            ? line
            : null;

    /// <summary>Керівні символи → <c>?</c>, довжина ≤ <see cref="MaxFieldLength"/>.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var span = text.Length > MaxFieldLength ? text.AsSpan(0, MaxFieldLength) : text.AsSpan();

        return string.Create(span.Length, span.ToString(), static (buffer, source) =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private sealed record FieldNames(
        string Blocked, string Violated, string Effective, string Document, string Source, string Line);
}
