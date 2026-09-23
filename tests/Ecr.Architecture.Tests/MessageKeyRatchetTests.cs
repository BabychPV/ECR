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
///
/// ✎ Місце — це СТВОРЕННЯ винятку, а не рядок із <c>throw</c> (`Q-341`,
/// розширення 2026-09-23). Доти сито бачило лише <c>throw new T(</c>, і
/// відмова, зібрана у фабриці (<c>throw InvalidCredentials()</c>,
/// <c>throw Mismatch(…)</c>, <c>return new T(…)</c> із <c>TryMap…</c>),
/// проходила повз нього мовчки. Саме так найчастіша інтерактивна відмова
/// продукту — не той тип у комірці — пережила перший зріз українською. Тепер
/// рахується кожне з двох:
/// <list type="bullet">
/// <item><c>new [Кваліфікатор.]T(</c> — будь-де, хоч із <c>throw</c>, хоч із
/// <c>return</c>, <c>=&gt;</c>, гілки <c>switch</c> чи тернарного;</item>
/// <item>цільово-типізоване <c>new(</c> у позиції результату (після
/// <c>=&gt;</c>, <c>return</c>, <c>?</c>, <c>:</c>, <c>??</c>) у тілі члена,
/// ОГОЛОШЕНИЙ тип повернення якого — один із цих T. Без цього пункту сторож
/// бачив би <c>new BusinessRuleException(</c>, але не
/// <c>BusinessRuleException Mismatch(…) =&gt; new(…)</c> — рівно ту форму,
/// якою пишуть фабрики.</item>
/// </list>
/// Чому не хибнить на внутрішніх винятках: типи — лише ті шість, чию подробицю
/// middleware віддає людині (усе інше — 500 або взагалі не виняток), тести й
/// міграції поза <see cref="SourceTree.Production"/>, коментарі замасковано.
/// Двох форм сторож свідомо НЕ вважає боргом: переобгортку
/// (<c>new T(error.ErrorCode, error.Message, details)</c> — текст написано й
/// пораховано там, де виняток створено вперше) і кидок, чий словник подробиць
/// отримав <c>["messageKey"]</c> рядком вище в тій самій області видимості.
/// </remarks>
public sealed partial class MessageKeyRatchetTests
{
    /// <summary>Перелік-замір: файл → скільки відмов без ключа.</summary>
    private const string LedgerFile = "contracts/localization-debt.md";

    /// <summary>
    /// Типи винятків, які <c>ExceptionHandlingMiddleware.Map</c> віддає
    /// клієнтові як 4xx — тобто такі, чию подробицю ЧИТАЄ людина.
    /// </summary>
    /// <remarks>
    /// ⚠ Решта винятків (<c>InvalidOperationException</c>, <c>ArgumentException</c>
    /// тощо) доїжджає як 500, а для 500 подробиця стала й беззмістовна
    /// НАВМИСНО (`ФВ-6.11`): локалізувати там нічого.
    /// </remarks>
    private const string DebtTypes =
        "(?:BusinessRuleException|AccessDeniedException|ConcurrencyConflictException"
        + "|DomainException|SourceAuthenticationException|NotFoundException)";

    /// <summary>Явне створення: <c>new [Кваліфікатор.]T(</c>.</summary>
    /// <remarks>
    /// ⚠ Кваліфікатор перед іменем типу НЕОБОВ'ЯЗКОВИЙ
    /// (<c>(?:[A-Za-z_]\w*\s*\.\s*)*</c>). Без цього сторож не бачив понад
    /// тридцяти кидків, написаних як <c>throw new Errors.NotFoundException(…)</c>
    /// чи <c>throw new Application.Errors.BusinessRuleException(…)</c>.
    /// ⛔ <c>throw</c> перед <c>new</c> НЕ вимагається — див. remarks класу.
    /// </remarks>
    [GeneratedRegex(@"(?<![\w.])new\s+(?:[A-Za-z_]\w*\s*\.\s*)*" + DebtTypes + @"\s*\(")]
    private static partial Regex ExplicitSite();

    /// <summary>
    /// Оголошення члена, що ПОВЕРТАЄ один із типів боргу:
    /// <c>BusinessRuleException Invalid(</c>, <c>Errors.NotFoundException NotFound(</c>.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])(?:[A-Za-z_]\w*\s*\.\s*)*" + DebtTypes + @"\??\s+[A-Za-z_]\w*\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex FactoryDeclaration();

    /// <summary>Цільово-типізоване <c>new(</c> у позиції результату.</summary>
    [GeneratedRegex(@"(?:=>|\breturn\b|\?|:)\s*(new\s*\()")]
    private static partial Regex TargetTypedResult();

    /// <summary>
    /// Переобгортка наявного винятку: повідомлення — ЦІЛИЙ аргумент
    /// <c>error.Message</c>, тобто текст написано й пораховано деінде.
    /// </summary>
    /// <remarks>
    /// ⛔ Лише цілий аргумент верхнього рівня. Речення
    /// <c>$"… не з'єднується: {ex.Message}"</c> — АВТОРСЬКЕ українське речення з
    /// вкладеним текстом драйвера, тобто саме борг; перша версія цього сита
    /// приймала його за переобгортку і мовчки «звужувала» перелік.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z_]\w*\.Message$")]
    private static partial Regex ForwardedMessage();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifier();

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
                // знає. Показуються рівно зайві місця — ті, що понад замір.
                var offending = string.Join(
                    ", ",
                    lines.Skip(allowed).Select(l => $"{path}:{l}"));

                failures.Add(
                    $"{path}: відмов 4xx без Details[\"messageKey\"] — {lines.Count}, у {LedgerFile} — {allowed}. "
                    + $"Додай messageKey у {offending} (ключ + сирі підстановки окремими полями, "
                    + $"рядок каталогу — у src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql; "
                    + $"українське речення лишається запасним). Рахується СТВОРЕННЯ винятку — "
                    + $"і у фабриці (`=> new(…)`, `return new T(…)`), не лише `throw new`.");
            }
            else if (lines.Count < allowed)
            {
                failures.Add(
                    $"{path}: відмов без messageKey лишилося {lines.Count}, а {LedgerFile} обіцяє {allowed}. "
                    + $"Зменш число до {lines.Count} — борг звужується лише записом, інакше перелік перестає бути заміром.");
            }
        }

        foreach (var (path, allowed) in ledger.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!actual.ContainsKey(path))
            {
                failures.Add(
                    $"{path}: у {LedgerFile} обіцяно {allowed} відмов без messageKey, а їх немає жодної "
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
    /// Сито бачить обидві форми фабрики — і чесно відрізняє ключ від його
    /// відсутності.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього тесту розширення сита тримається лише на числі в переліку:
    /// регулярка, яка тихо перестала б бачити <c>=&gt; new(</c>, дала б
    /// червоне «стало менше» у кількох файлах — і найпростіший «ремонт»
    /// зменшив би борг на папері.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сито_бачить_відмову_зібрану_фабрикою()
    {
        const string sample = """
            // throw new BusinessRuleException("коментар — не місце");
            public sealed class Sample
            {
                private static AccessDeniedException Bare() => new("ECR-X", "Речення.");

                private static BusinessRuleException Keyed()
                    => new("ECR-X", "Речення.", new Dictionary<string, object?> { ["messageKey"] = "err.x" });

                private static NotFoundException Block(int id)
                {
                    var list = new List<int>();
                    return new(ErrorCodes.X, $"Рядка {id} немає.");
                }

                private static EcrException? Wrap(EcrException error, Dictionary<string, object?> details)
                    => error switch
                    {
                        NotFoundException => new NotFoundException(error.ErrorCode, error.Message, details),
                        _ => null,
                    };

                private static BusinessRuleException ViaLocal(string message)
                {
                    var details = new Dictionary<string, object?>();
                    details["messageKey"] = "err.y";
                    return new BusinessRuleException("ECR-X", message, details);
                }

                private static object Explicit() => new Errors.ConcurrencyConflictException("ECR-X", "Речення.");

                private static BusinessRuleException ViaInitializer()
                {
                    var details = new Dictionary<string, object?> { ["messageKey"] = "err.z" };
                    return new("ECR-X", "Речення.", details);
                }

                private static BusinessRuleException SiblingKeyIsNotMine(bool a)
                {
                    var keyed = new Dictionary<string, object?> { ["messageKey"] = "err.w" };
                    var bare = new Dictionary<string, object?> { ["code"] = "c" };
                    return a ? new("ECR-X", "Речення.", keyed) : new("ECR-X", "Речення.", bare);
                }
            }
            """;

        var sites = Sites(sample).ToList();
        var code = MaskComments(sample);
        var unkeyed = sites.Where(s => !Keyed(code, s)).Select(s => Line(sample, s.Index)).ToList();

        // Без ключа: Bare (рядок 4), Block (12), Explicit (29) і друга гілка
        // SiblingKeyIsNotMine (41) — ключ сусіднього словника не її. З ключем:
        // Keyed, ViaLocal, ViaInitializer, перша гілка SiblingKeyIsNotMine.
        // Wrap — переобгортка, коментар — не код.
        Assert.Equal(8, sites.Count);
        Assert.Equal([4, 12, 29, 41], unkeyed);
    }

    /// <summary>
    /// Кожен <c>{плейсхолдер}</c> шаблону з сіду названий полем у
    /// <c>Details</c> того кидка, що несе цей <c>messageKey</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Резолвер мовчить: підстановки немає — <c>{periodKey}</c> так і їде
    /// користувачеві фігурними дужками. Сторож ловить розбіжність ІМЕН (одруківка,
    /// перейменований плейсхолдер). ТИП значення (резолвер бере лише
    /// <c>string</c>) із тексту джерела не встановити — його стережуть прогони
    /// справжніх кидків крізь конвеєр (<c>MainPathLocalizedErrorTests</c>).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_плейсхолдер_шаблону_має_підстановку_в_кидку()
    {
        var seed = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var templates = SeedTemplate().Matches(seed).ToDictionary(
            m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        var failures = new List<string>();
        var checkedSites = 0;

        foreach (var file in SourceTree.Production())
        {
            // ⚠ Цей сторож лишається на СТАРОМУ ситі — лише `throw new T(`.
            // Розширене сито (фабрики, `return new T(`) одразу знаходить
            // розбіжність: `UnitOfWork.TryMapDuplicateKey` віддає
            // `err.ECR-REG-0409.entryCodeTaken`, чий шаблон чекає `{id}`, а поля
            // `["id"]` немає. Це виправлення самої відмови — окрема робота
            // (`Q-341`); тут воно не ховається, а назване, і розширення цього
            // сторожа йде разом із ним.
            foreach (var site in Sites(file.Text).Where(s => Thrown(file.Text, s.Index)))
            {
                var key = InlineKey().Match(site.Arguments);

                if (!key.Success || !templates.TryGetValue(key.Groups[1].Value, out var template))
                {
                    continue;
                }

                checkedSites++;

                failures.AddRange(
                    from Match placeholder in Placeholder().Matches(template)
                    let name = placeholder.Groups[1].Value
                    where !site.Arguments.Contains($"[\"{name}\"]", StringComparison.Ordinal)
                    select $"{file.Path}:{Line(file.Text, site.Index)}: шаблон {key.Groups[1].Value} чекає {{{name}}}, "
                           + $"а поля [\"{name}\"] у Details кидка немає — користувач побачить фігурні дужки.");
            }
        }

        // ⛔ Регулярка, що перестала збігатися, дала б нуль перевірок і ЗЕЛЕНЕ.
        Assert.NotEqual(0, checkedSites);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Рядок сіду <c>(N'err.…', N'en', N'шаблон', 0|1)</c>.</summary>
    [GeneratedRegex(@"\(\s*N'(err\.[^']+)'\s*,\s*N'en'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedTemplate();

    /// <summary>Ключ, названий літералом прямо в <c>Details</c> кидка.</summary>
    [GeneratedRegex(@"\[""messageKey""\]\s*=\s*""(err\.[^""]+)""")]
    private static partial Regex InlineKey();

    [GeneratedRegex(@"\{([A-Za-z_]\w*)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Відмови 4xx без <c>messageKey</c>: шлях файлу → номери рядків.
    /// </summary>
    private static Dictionary<string, List<int>> Unkeyed()
    {
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in SourceTree.Production())
        {
            var code = MaskComments(file.Text);
            var lines = Sites(file.Text)
                .Where(s => !Keyed(code, s))
                .Select(s => Line(file.Text, s.Index))
                .ToList();

            if (lines.Count > 0)
            {
                found[file.Path] = lines;
            }
        }

        return found;
    }

    /// <summary>
    /// Усі місця створення винятку боргу у тексті файлу, за зростанням індексу,
    /// без переобгорток.
    /// </summary>
    private static List<Site> Sites(string text)
    {
        var code = MaskComments(text);
        var sites = new SortedDictionary<int, Site>();

        foreach (Match m in ExplicitSite().Matches(code))
        {
            Add(m.Index, m.Index + m.Length - 1);
        }

        foreach (Match declaration in FactoryDeclaration().Matches(code))
        {
            var (start, end) = Body(code, declaration.Index + declaration.Length - 1);

            foreach (Match m in TargetTypedResult().Matches(code[start..end]))
            {
                var group = m.Groups[1];
                Add(start + group.Index, start + group.Index + group.Length - 1);
            }
        }

        return [.. sites.Values];

        void Add(int index, int openParen)
        {
            var arguments = Balanced(code, openParen);

            if (!TopLevelArguments(arguments).Any(a => ForwardedMessage().IsMatch(a)))
            {
                sites.TryAdd(index, new Site(index, arguments));
            }
        }
    }

    /// <summary>
    /// Чи несе місце ключ: названий у самих аргументах або доданий у
    /// переданий словник раніше в тій самій області видимості.
    /// </summary>
    /// <remarks>
    /// ⚠ Друга гілка не вгадує: вона шукає буквально <c>ім'я["messageKey"]</c>
    /// між найближчим присвоєнням цього імені й самим місцем. Словник, який
    /// приходить параметром і отримує ключ у виклику, сюди не потрапляє —
    /// такий кидок рахується боргом, і це вада в бік суворості, не поблажливості.
    /// </remarks>
    private static bool Keyed(string text, Site site)
    {
        if (site.Arguments.Contains("messageKey", StringComparison.Ordinal))
        {
            return true;
        }

        var before = text[..site.Index];

        foreach (var name in Identifier().Matches(site.Arguments).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            var assigned = Regex.Match(
                before, $@"\b{Regex.Escape(name)}\s*=(?![=>])", RegexOptions.RightToLeft);

            if (!assigned.Success)
            {
                continue;
            }

            // Ключ у самому присвоєнні (`var details = new Dictionary { ["messageKey"] = … };`)
            // або доданий згодом (`details["messageKey"] = …`, `details.Add("messageKey", …)`).
            var initializer = before[assigned.Index..StatementEnd(before, assigned.Index + assigned.Length)];
            var afterwards = before[assigned.Index..];

            if (initializer.Contains("\"messageKey\"", StringComparison.Ordinal)
                || afterwards.Contains($"{name}[\"messageKey\"]", StringComparison.Ordinal)
                || afterwards.Contains($"{name}.Add(\"messageKey\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Індекс <c>;</c>, що закриває вираз від <paramref name="start"/>, на нульовій глибині дужок.</summary>
    private static int StatementEnd(string code, int start)
    {
        var depth = 0;
        for (var j = start; j < code.Length; j++)
        {
            var c = code[j];
            if (c == '"' || c == '\'')
            {
                j = SkipLiteral(code, j);
            }
            else if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                return j;
            }
        }

        return code.Length;
    }

    /// <summary>
    /// Межі тіла члена, чий список параметрів відкривається на
    /// <paramref name="openParen"/>: <c>=&gt; …;</c> або <c>{ … }</c>.
    /// </summary>
    private static (int Start, int End) Body(string code, int openParen)
    {
        var i = openParen + Balanced(code, openParen).Length;

        // Обмеження `where T : …` і пробіли між параметрами й тілом.
        while (i < code.Length && code[i] != '{' && code[i] != ';' && code[i] != ')' && code[i] != ','
               && !(code[i] == '=' && i + 1 < code.Length && code[i + 1] == '>'))
        {
            i++;
        }

        if (i >= code.Length || code[i] is ';' or ')' or ',')
        {
            return (i, i); // абстрактний член, делегат, виклик — тіла немає
        }

        if (code[i] == '{')
        {
            return (i, i + Balanced(code, i).Length);
        }

        // Тіло-вираз: до `;` на нульовій глибині дужок.
        return (i, StatementEnd(code, i));
    }

    /// <summary>
    /// Текст від відкривної дужки (<c>(</c> чи <c>{</c>) до парної закривної.
    /// </summary>
    /// <remarks>
    /// ⛔ Дужки рахуються ПОЗА рядковими літералами. Наївний підрахунок
    /// спотикається об дужку всередині тексту повідомлення — а саме такі
    /// повідомлення тут і шукаються, тобто помилявся б він рівно на предметі
    /// перевірки.
    /// </remarks>
    private static string Balanced(string text, int openIndex)
    {
        var open = text[openIndex];
        var close = open == '{' ? '}' : ')';
        var depth = 0;

        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(text, i);
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return text[openIndex..(i + 1)];
            }
        }

        // Дужка не закрилася — віддаємо решту файлу. Місце від цього
        // порахується як «з ключем» тільки якщо ключ там справді є.
        return text[openIndex..];
    }

    /// <summary>
    /// Текст із коментарями, заміненими пробілами (довжина й переноси рядків
    /// зберігаються, тож індекси збігаються з вихідним текстом).
    /// </summary>
    /// <remarks>
    /// ⛔ Без маски сито рахувало б коментар, який ПОЯСНЮЄ, чому
    /// <c>new BusinessRuleException(</c> тут такий, — а таких пояснень у
    /// цьому коді повно.
    /// </remarks>
    private static string MaskComments(string text)
    {
        var chars = text.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(text, i);
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                for (; i < chars.Length && chars[i] != '\n'; i++)
                {
                    chars[i] = ' ';
                }
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                for (; i < end; i++)
                {
                    if (chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }
                }

                i--;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Індекс останнього символу літерала, що починається лапкою на
    /// <paramref name="quote"/>: символьного, звичайного, дослівного
    /// (<c>@"…"</c>), сирого (<c>"""…"""</c>) та інтерпольованого (дірки
    /// <c>{…}</c> із вкладеними лапками).
    /// </summary>
    private static int SkipLiteral(string text, int quote)
    {
        if (text[quote] == '\'')
        {
            for (var i = quote + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '\'' || text[i] == '\n')
                {
                    return i;
                }
            }

            return text.Length - 1;
        }

        var p = quote - 1;
        var verbatim = false;
        var interpolated = false;
        while (p >= 0 && (text[p] == '@' || text[p] == '$'))
        {
            verbatim |= text[p] == '@';
            interpolated |= text[p] == '$';
            p--;
        }

        var run = 0;
        while (quote + run < text.Length && text[quote + run] == '"')
        {
            run++;
        }

        if (run >= 3)
        {
            // Сирий літерал: до першого такого самого ряду лапок.
            var fence = new string('"', run);
            var end = text.IndexOf(fence, quote + run, StringComparison.Ordinal);
            return end < 0 ? text.Length - 1 : end + run - 1;
        }

        for (var i = quote + 1; i < text.Length; i++)
        {
            var c = text[i];

            if (!verbatim && c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i;
            }
            else if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                // Дірка інтерполяції: до парної `}`, вкладені літерали — рекурсивно.
                var depth = 1;
                for (i++; i < text.Length && depth > 0; i++)
                {
                    if (text[i] == '"' || text[i] == '\'')
                    {
                        i = SkipLiteral(text, i);
                    }
                    else if (text[i] == '{')
                    {
                        depth++;
                    }
                    else if (text[i] == '}')
                    {
                        depth--;
                    }
                }

                i--;
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// Аргументи верхнього рівня зі списку <c>(…)</c>, обрізані від пробілів.
    /// </summary>
    private static List<string> TopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 1;

        for (var i = 1; i < arguments.Length; i++)
        {
            var c = arguments[i];

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(arguments, i);
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}' || (c == ')' && depth > 0))
            {
                depth--;
            }
            else if ((c == ',' && depth == 0) || c == ')')
            {
                result.Add(arguments[start..i].Trim());
                start = i + 1;
            }
        }

        return result;
    }

    /// <summary>Чи стоїть перед місцем саме <c>throw</c> (лише пробіли між).</summary>
    private static bool Thrown(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i >= 4 && string.CompareOrdinal(text, i - 4, "throw", 0, 5) == 0
               && (i < 5 || !(char.IsLetterOrDigit(text[i - 5]) || text[i - 5] == '_'));
    }

    private static int Line(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
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

    /// <summary>Місце створення винятку: індекс і текст списку аргументів.</summary>
    private readonly record struct Site(int Index, string Arguments);
}
