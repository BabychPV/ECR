using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Храповик локалізації подробиць відмов: місць, де виняток 4xx несе готове
/// УКРАЇНСЬКЕ речення замість <c>Details["messageKey"]</c>, не стає більше
/// (`Q-341`).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Мови продукту — <c>en</c>/<c>ru</c>/<c>kz</c>; української серед
/// них немає. <c>Title</c> відповіді вже резолвиться каталогом (`D-95`), а
/// <c>Detail</c> поруч — лише тоді, коли виняток несе <c>messageKey</c>
/// (`Q-314`, <c>ExceptionHandlingMiddleware.ResolveGenericMessageAsync</c>).
/// Замір 2026-09-17: 426 кидків із готовим реченням проти 16 із ключем. Один
/// прохід усього цього не закриє, а без сторожа розрив росте сам собою — кожен
/// новий обробник пишеться за зразком сусіднього.
///
/// ⚠ Чому перелік ФАЙЛІВ із числом, а не одне число на репозиторій. Голий
/// лічильник рухає будь-який рефакторинг у будь-якому файлі, тож єдиний спосіб
/// його «полагодити» — збільшити число, і сторож перетворюється на шум. Перелік
/// адресний: червоним стає рівно той файл, який змінили, і повідомлення називає
/// рядок, куди додати ключ. Перевірка йде В ОБИДВА БОКИ (той самий прийом, що
/// `ContractIntegrityTests.ReservedCodes` і `contracts/trace-exempt.md`):
/// число, яке стало МЕНШИМ, теж червоне — інакше перелік тихо розійшовся б із
/// дійсністю і перестав бути заміром.
///
/// ⛔ Чим цей вибір поганий, чесно. Він рахує РЯДКИ КОДУ, а не відмови, які
/// бачить людина: перенесення кидка з файлу у файл дає дві правки переліку, за
/// якими немає жодної зміни поведінки. Він не відрізняє відмову, яку оператор
/// бачить щодня, від тієї, куди не ходить ніхто. І він не вміє сказати, що
/// ключ, який у кидку є, справді заведений у каталозі — лише що ключ названий.
///
/// ✎ <c>NotFoundException</c> ТЕПЕР у переліку типів. Доти його не було з
/// причини, яка зникла: у самого типу не було параметра <c>Details</c>, тож
/// покласти ключ у його кидок не було КУДИ, і рядки переліку про 404 були б
/// вимогами, які неможливо виконати. Параметр додано (`Q-341`,
/// `Ecr.Application/Errors/EcrException.cs`), разом із ним — <c>e.Details</c>
/// в армі 404 у <c>ExceptionHandlingMiddleware.Map</c> (там стояла жорстка
/// <c>null</c>, тобто подробиця відкидалася ще до резолвера).
///
/// ⛔ Через це перелік у <c>contracts/localization-debt.md</c> ВИРІС, і це не
/// регрес: 404 були в коді й до цього, просто сторож на них не дивився. Число
/// стало більшим саме тому, що замір став чеснішим.
/// </remarks>
public sealed partial class MessageKeyRatchetTests
{
    /// <summary>Перелік-замір: файл → скільки кидків без ключа.</summary>
    private const string LedgerFile = "contracts/localization-debt.md";

    /// <summary>
    /// Кидок винятку, який <c>ExceptionHandlingMiddleware.Map</c> віддає
    /// клієнтові як 4xx — тобто такого, чию подробицю ЧИТАЄ людина.
    /// </summary>
    /// <remarks>
    /// ⚠ Решта винятків (<c>InvalidOperationException</c>, <c>ArgumentException</c>
    /// тощо) доїжджає як 500, а для 500 подробиця стала й беззмістовна
    /// НАВМИСНО (`ФВ-6.11`): локалізувати там нічого.
    /// </remarks>
    /// <remarks>
    /// ⚠ Кваліфікатор перед іменем типу НЕОБОВ'ЯЗКОВИЙ
    /// (<c>(?:[A-Za-z_]\w*\s*\.\s*)*</c>). Без цього сторож не бачив понад
    /// тридцяти кидків, написаних як <c>throw new Errors.NotFoundException(…)</c>
    /// чи <c>throw new Application.Errors.BusinessRuleException(…)</c>, — серед
    /// них два з тих, що локалізує `Q-341`. Дірка не в тому, що число було
    /// меншим, а в тому, що НОВИЙ кидок, написаний із кваліфікатором, сторож
    /// пропустив би мовчки.
    /// </remarks>
    [GeneratedRegex(
        @"throw\s+new\s+(?:[A-Za-z_]\w*\s*\.\s*)*"
        + @"(?:BusinessRuleException|AccessDeniedException|ConcurrencyConflictException"
        + @"|DomainException|SourceAuthenticationException|NotFoundException)\s*\(")]
    private static partial Regex ThrowSite();

    /// <summary>Рядок переліку: <c>| `src/…cs` | 7 |</c>.</summary>
    [GeneratedRegex(@"^\|\s*`([^`]+)`\s*\|\s*(\d+)\s*\|", RegexOptions.Multiline)]
    private static partial Regex LedgerRow();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кидків_без_messageKey_не_стає_більше()
    {
        var actual = Unkeyed();
        var ledger = Ledger();

        Assert.NotEmpty(ledger);

        var failures = new List<string>();

        foreach (var (path, lines) in actual.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var allowed = ledger.GetValueOrDefault(path, 0);

            if (lines.Count > allowed)
            {
                // ⛔ Повідомлення називає РЯДКИ, а не лише число: «стало
                // більше» відправляє читача шукати самому те, що сторож уже
                // знає. Показуються рівно зайві кидки — ті, що понад замір.
                var offending = string.Join(
                    ", ",
                    lines.Skip(allowed).Select(l => $"{path}:{l}"));

                failures.Add(
                    $"{path}: кидків 4xx без Details[\"messageKey\"] — {lines.Count}, у {LedgerFile} — {allowed}. "
                    + $"Додай messageKey у {offending} (ключ + сирі підстановки окремими полями, "
                    + $"рядок каталогу — у src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql; "
                    + $"українське речення лишається запасним).");
            }
            else if (lines.Count < allowed)
            {
                failures.Add(
                    $"{path}: кидків без messageKey лишилося {lines.Count}, а {LedgerFile} обіцяє {allowed}. "
                    + $"Зменш число до {lines.Count} — борг звужується лише записом, інакше перелік перестає бути заміром.");
            }
        }

        foreach (var (path, allowed) in ledger.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!actual.ContainsKey(path))
            {
                failures.Add(
                    $"{path}: у {LedgerFile} обіцяно {allowed} кидків без messageKey, а їх немає жодного "
                    + $"(файл локалізовано або зник) — прибери рядок із переліку.");
            }
        }

        // ⛔ `Assert.True` з готовим текстом, а не `Assert.Empty(failures)`:
        // xUnit друкує колекцію ОБРІЗАНОЮ («…»), і саме та частина рядка, де
        // сказано, у який файл і рядок додати ключ, зникає першою. Сторож,
        // чиє повідомлення не дочитати, вимагає йти читати його код.
        Assert.True(
            failures.Count == 0,
            $"Локалізація подробиць відмов ({LedgerFile}):"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Кидки 4xx без <c>messageKey</c>: шлях файлу → номери рядків.
    /// </summary>
    private static Dictionary<string, List<int>> Unkeyed()
    {
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in SourceTree.Production())
        {
            foreach (Match site in ThrowSite().Matches(file.Text))
            {
                // Індекс відкривної дужки — останній символ збігу.
                var arguments = ArgumentList(file.Text, site.Index + site.Length - 1);

                if (arguments.Contains("messageKey", StringComparison.Ordinal))
                {
                    continue;
                }

                var line = file.Text.Take(site.Index).Count(c => c == '\n') + 1;

                if (!found.TryGetValue(file.Path, out var lines))
                {
                    found[file.Path] = lines = [];
                }

                lines.Add(line);
            }
        }

        return found;
    }

    /// <summary>
    /// Текст списку аргументів від відкривної дужки до парної закривної.
    /// </summary>
    /// <remarks>
    /// ⛔ Дужки рахуються ПОЗА рядковими літералами. Наївний підрахунок
    /// спотикається об дужку всередині тексту повідомлення — а саме такі
    /// повідомлення тут і шукаються, тобто помилявся б він рівно на предметі
    /// перевірки. Інтерпольовані вставки пропускаються разом із літералом:
    /// дужки в них збалансовані за побудовою.
    /// </remarks>
    private static string ArgumentList(string text, int openIndex)
    {
        var depth = 0;

        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            {
                i = SkipVerbatimString(text, i + 1);
                continue;
            }

            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[openIndex..(i + 1)];
                }
            }
        }

        // Дужка не закрилася — віддаємо решту файлу. Кидок від цього
        // порахується як «з ключем» тільки якщо ключ там справді є.
        return text[openIndex..];
    }

    /// <summary>Індекс закривної лапки звичайного літерала.</summary>
    private static int SkipString(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Індекс закривної лапки дослівного (<c>@"…"</c>) літерала.</summary>
    private static int SkipVerbatimString(string text, int quote)
    {
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return text.Length - 1;
    }

    /// <summary>Індекс закривної лапки символьного літерала.</summary>
    private static int SkipChar(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '\'')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Перелік-замір із <c>contracts/localization-debt.md</c>.</summary>
    private static Dictionary<string, int> Ledger()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, "contracts", "localization-debt.md"));

        return LedgerRow().Matches(text).ToDictionary(
            m => m.Groups[1].Value,
            m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            StringComparer.Ordinal);
    }
}
