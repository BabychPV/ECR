// src/Ecr.Application/Errors/SafeLogException.cs
using System.Text.RegularExpressions;

namespace Ecr.Application.Errors;

/// <summary>
/// Виняток драйвера, придатний для серверного журналу: тип, код (SQLSTATE / номер SQL Server) і повідомлення ПІСЛЯ
/// скрабера (P-1126-SRCCRED, R-02, AC-2b).
/// </summary>
/// <remarks>
/// ⛔ Журнал отримує цей запис, а не <see cref="Exception"/>: сирий виняток драйвера несе <c>Errors[]</c>, внутрішні
/// винятки й текст, який драйвер міг скласти з рядка з'єднання. Скрабер прибирає точні значення, відомі в момент
/// з'єднання (пароль, логін, секрет середовища), і будь-яку пару <c>PWD=</c>/<c>Password=</c>/<c>UID=</c>/<c>User ID=</c>.
/// </remarks>
/// <param name="Type">Тип винятку (без простору імен).</param>
/// <param name="Code">SQLSTATE або номер помилки; <c>null</c> — немає.</param>
/// <param name="Message">Повідомлення після скрабера.</param>
public sealed partial record SafeLogException(string Type, string? Code, string Message)
{
    /// <summary>Чим замінюється прибране.</summary>
    public const string Mask = "***";

    /// <summary>Безпечний запис винятку.</summary>
    /// <param name="error">Виняток.</param>
    /// <param name="code">SQLSTATE / номер.</param>
    /// <param name="secrets">Точні значення, які не мають потрапити в журнал (порожні пропускаються).</param>
    public static SafeLogException Of(Exception error, string? code, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new SafeLogException(error.GetType().Name, code, Scrub(error.Message, secrets));
    }

    /// <summary>Прибирає секрети й пари облікових даних із тексту.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="secrets">Точні значення.</param>
    public static string Scrub(string? text, params string?[] secrets)
    {
        var result = text ?? string.Empty;

        // ⛔ L4: і ті форми, у яких секрет стоїть у рядку з'єднання ODBC — у фігурних дужках, `}` подвоєна.
        var forms = (secrets ?? [])
            .Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(s => new[] { "{" + s!.Replace("}", "}}", StringComparison.Ordinal) + "}", s.Replace("}", "}}", StringComparison.Ordinal), s })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length);

        foreach (var secret in forms)
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return CredentialPair().Replace(result, m => m.Groups[1].Value + "=" + Mask);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Type} [{Code}] {Message}";

    [GeneratedRegex(@"(pwd|password|uid|user\s*id)\s*=\s*(\{[^}]*\}|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPair();
}
