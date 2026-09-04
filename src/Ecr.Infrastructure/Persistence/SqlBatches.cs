using System.Text.RegularExpressions;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Ділить SQL-скрипт на батчі за роздільником <c>GO</c>.
/// </summary>
/// <remarks>
/// <c>GO</c> — команда <c>sqlcmd</c>, а не оператор T-SQL: сервер її не
/// розуміє і на ній падає. Тому будь-який скрипт із <c>Persistence/Sql</c>,
/// який виконується <b>з коду</b>, а не через <c>sqlcmd</c>, треба різати
/// самому.
///
/// Хелпер спільний для <see cref="SeedRunner"/> і тестової фікстури: дві копії
/// цього регулярного виразу рано чи пізно розійшлися б, і розбіжність вилізла
/// б у вигляді «у тесті працює, у застосунку ні».
/// </remarks>
public static partial class SqlBatches
{
    /// <summary>Розбиває скрипт на непорожні батчі.</summary>
    public static IReadOnlyList<string> Split(string script)
        => [.. GoSeparator().Split(script ?? string.Empty)
              .Select(batch => batch.Trim())
              .Where(batch => batch.Length > 0)];

    // [^\S\n] — пробільний символ, окрім переводу рядка, тобто зокрема \r.
    // Без нього на файлі з CRLF (а git у Windows їх саме такими й видає)
    // регулярка не спрацьовує: `$` у Multiline стоїть перед \n, і \r лишається
    // непоглинутим. Помилка вилазить аж на сервері — «Incorrect syntax near 'GO'».
    [GeneratedRegex(@"^[^\S\n]*GO[^\S\n]*(?:--[^\n]*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
