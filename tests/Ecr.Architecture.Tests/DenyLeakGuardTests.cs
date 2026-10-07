// tests/Ecr.Architecture.Tests/DenyLeakGuardTests.cs
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// T1-01: тексти помилок, деталі відмов, повідомлення валідації й логи не несуть
/// імен чи значень заборонених (<c>Deny</c>) колонок і таблиць.
/// </summary>
/// <remarks>
/// ⛔ Предмет. T1-01 закрив значення джерела в повідомленні Check: текст звірки
/// містить значення ОБОХ сторін, а видимість рахувалась лише за приймачем, тож
/// читач із <c>Deny</c> на колонку-джерело бачив її значення в панелі валідації,
/// на «Подати» і в експорті. Виправлення — <c>ValidationMessage.SourceTableDefId</c>
/// і фільтр <c>HiddenValidationIssues.CanSee</c>/<c>ForViewer</c>. Сторож тримає
/// цей клас дефектів закритим для НОВИХ місць:
/// <list type="number">
/// <item>кожен виклик <c>CheckEvaluator.ToMessages</c> передає таблицю джерела
/// (параметр необов'язковий — пропуск компілюється й мовчки відкриває значення);</item>
/// <item>кожен файл, що віддає <c>ValidationMessage</c> назовні, фільтрує їх за
/// видимістю, або стоїть у <see cref="UnfilteredConsumers"/> з причиною;</item>
/// <item>у файлах Deny-контексту (ті, що питають межі читання чи рішення про
/// заборону, <see cref="DenyMarker"/>) кожен вираз, який називає колонку, таблицю
/// чи несе значення комірки (<see cref="NamesOrValuesIn"/>), у тексті
/// інтерпольованого рядка або в деталях відмови (<c>["key"] = …</c>) — лише за
/// поіменним записом у <see cref="Reviewed"/> з причиною, чому це не витік;</item>
/// <item>логи в цих файлах таких виразів не несуть зовсім (переліку немає).</item>
/// </list>
///
/// ⚠ D-276 — свідомий виняток, і сторож його не чіпає: <c>Deny</c> на даних не діє
/// на Rollup-агрегати й регуляторні зрізи (<c>rpt.v_*</c>, <c>ReportSnapshot</c>).
/// Їхні файли не питають межі читання документа, тож у Deny-контекст не входять;
/// додавати їм фільтр, щоб «задовольнити сторож», — зламати рішення замовника.
///
/// ⚠ Статичний сторож (<see cref="TestCategories.Static"/>): він ловить НОВЕ місце
/// за текстом, а не доводить, що наявні місця не течуть. Поведінку тримають
/// <c>CheckSourceDenyValidationTests</c>, <c>HiddenValidationIssuesTests</c>,
/// <c>SubmitRelationCheckTests</c>. Перелік <see cref="Reviewed"/> — храповик:
/// новий вираз червоний, зниклий — теж (перелік мусить зменшуватись, а не гнити).
///
/// Мутації (прогнано локально, не запушено): (1) прибрати
/// <c>relation.SourceTableDefId</c> у <c>RelationCheckRunner</c> — червоний перший
/// тест; (2) новий файл Application, що проектує <c>ValidationMessage.Message</c>
/// без фільтра, — червоний другий; (3) <c>$"… {columnCodeById[address.ColumnDefId]}"</c> у
/// винятку <c>GetTableSliceHandler</c> — червоний третій; (4)
/// <c>LogWarning("Denied column {Column}", code)</c> там само — червоний четвертий.
/// </remarks>
public sealed partial class DenyLeakGuardTests
{
    /// <summary>
    /// Файли, що споживають <c>ValidationMessage</c> без власного фільтра видимості, — з причиною.
    /// </summary>
    private static readonly Dictionary<string, string> UnfilteredConsumers = new(StringComparer.Ordinal)
    {
        // Визначення й виробники: тут повідомлення народжуються, назовні їх віддають обробники нижче.
        ["src/Ecr.Application/Validation/ValidationMessage.cs"] = "визначення запису",
        ["src/Ecr.Application/Validation/ValidationEngine.cs"] = "виробник: правила таблиці",
        ["src/Ecr.Application/Validation/TableValidation.cs"] = "виробник: структура й обов'язковість",
        ["src/Ecr.Application/Validation/RelationCheckRunner.cs"] = "виробник: зв'язки Check (джерело — правило 1)",
        ["src/Ecr.Application/Calculations/CheckEvaluator.cs"] = "виробник: текст звірки, SourceTableDefId",
        ["src/Ecr.Application/Validation/ValidationMessageTemplates.cs"] =
            "шаблони й Localize: працює над УЖЕ відфільтрованим списком (GetValidationResultHandler маскує до Localize; доказ — T1_01_Check_з_ключем_…)",
        ["src/Ecr.Application/Documents/GetTableStatusHandler.cs"] =
            "лише лічильники Error/Warning по таблицях, видимих за CanReadTable; ні тексту, ні колонки",
        ["src/Ecr.Application/Documents/PatchCellsHandler.Workbook.cs"] =
            "partial PatchCellsHandler: повідомлення йдуть через EnsureValidationPassesAsync (фільтр visible)",
    };

    /// <summary>
    /// Вирази, що називають колонку/таблицю чи несуть значення, у текстах і деталях Deny-контексту — з причиною.
    /// </summary>
    /// <remarks>
    /// Ключ — <c>файл | вид | вираз</c>; значення — скільки разів і чому не витік.
    /// ⚠ Рядки з позначкою «⚠ ВІДОМИЙ ВИТІК» — справжні знахідки сторожа, не виправдання:
    /// перевірка режиму рядків іде ДО рішення про доступ, тож код таблиці під <c>Deny</c>
    /// дістається тому, хто пише в неї за ідентифікатором екземпляра. Виправлення — окремою
    /// задачею (код таблиці → ідентифікатор або перевірка після доступу), тоді рядок прибрати.
    /// </remarks>
    private static readonly Dictionary<string, (int Count, string Why)> Reviewed = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Application/Consistency/GetConsistencyIssuesHandler.cs | деталь severity | severity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)"] =
            (1, "ехо числового параметра запиту (вага 1..3), не колонка і не значення; відмова ДО читання журналу"),
        ["src/Ecr.Adapters.Excel/ExcelExporter.cs | текст | block.SheetName"] =
            (1, "посилання між аркушами книги, куди йдуть лише видимі таблиці (HiddenTableDefIds)"),
        ["src/Ecr.Adapters.Excel/ImportDiffBuilder.cs | текст | table.Code"] =
            (1, "таблиця книги, яку ExcelImporter уже пропустив за CanReadTable"),
        ["src/Ecr.Adapters.Excel/ImportDiffBuilder.cs | деталь tableCode | table.Code"] =
            (1, "те саме, що текст TooManyChanges"),
        ["src/Ecr.Application/Documents/CreateRowHandler.cs | текст | key.Value"] =
            (2, "ключ рядка — ввід самого запиту або щойно згенерований GUID, не дані колонок"),
        ["src/Ecr.Application/Documents/CreateRowHandler.cs | деталь rowKey | key.Value"] =
            (1, "те саме: ключ із запиту"),
        ["src/Ecr.Application/Documents/CreateRowHandler.cs | текст | table.Code"] =
            (2, "⚠ ВІДОМИЙ ВИТІК: «RowMode» іде ДО RequireCreateAllowedAsync; «межа рядків» — після доступу"),
        ["src/Ecr.Application/Documents/CreateRowHandler.cs | деталь tableCode | table.Code"] =
            (2, "⚠ ВІДОМИЙ ВИТІК: те саме, що текст (rowsFromTemplate до доступу, rowLimitReached після)"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | текст | change.Code"] =
            (1, "поле шапки з запиту, якого немає у версії (ехо вводу); шапка — не Column/Table"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | деталь headerFieldCode | change.Code"] =
            (1, "те саме, що текст"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | текст | field.Code"] =
            (2, "поле шапки, яке пише сам автор запиту; шапка — не Column/Table; другий — ECR-HDR-4223 (PS-P1D): те саме поле запиту, відмова після перевірки гранта на проєкт"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | деталь headerFieldCode | field.Code"] =
            (2, "те саме, що текст"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | текст | missingCode"] =
            (1, "D-12: поле шапки, яке пише сам автор запиту (неіснуючий запис довідника); шапка — не Column/Table"),
        ["src/Ecr.Application/Documents/DocumentHeaderHandlers.cs | деталь headerFieldCode | missingCode"] =
            (1, "те саме, що текст"),
        ["src/Ecr.Application/Documents/GetTableSliceHandler.cs | текст | code"] =
            (2, "ключ «рядок:колонка» лише для колонок, що пройшли CanReadColumn"),
        ["src/Ecr.Application/Documents/GetTableSliceHandler.cs | текст | column.Code"] =
            (1, "ключ формату по колонках зрізу, вже відфільтрованих CanReadColumn"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | code"] =
            (1, "колонки з запиту немає у версії (ехо вводу)"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | деталь columnCode | code"] =
            (1, "те саме, що текст"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | columnCode"] =
            (2, "ехо вводу (колонки немає у версії); ECR-CALC-0437 — ValidationMessage, фільтр visible"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | деталь columnCode | columnCode"] =
            (1, "колонки з запиту немає у версії (ехо вводу)"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | applied.MethodologyCode"] =
            (1, "код методології, не колонки; текст — ValidationMessage, фільтр visible"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | codeById[a.ColumnDefId]"] =
            (1, "ключі «рядок:колонка» лише комірок, які батч щойно записав з дозволом"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | cell.ColumnCode"] =
            (1, "колонку назвав сам запит; текст — причина рішення, не значення"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | текст | table.Code"] =
            (2, "⚠ ВІДОМИЙ ВИТІК: EnforceRowCreationRules іде ДО EnsureAccessAsync (fixedRowMode, dynamicRowLimit)"),
        ["src/Ecr.Application/Documents/PatchCellsHandler.cs | деталь tableCode | table.Code"] =
            (2, "⚠ ВІДОМИЙ ВИТІК: те саме, що текст"),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "T1-01")]
    public void Повідомлення_Check_завжди_несуть_таблицю_джерела()
    {
        var calls = new List<string>();
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production())
        {
            var code = Lex(file.Text).Code;

            foreach (Match match in ToMessagesCall().Matches(code))
            {
                var open = match.Index + match.Length - 1;
                var args = TopLevelArguments(code, open);
                var where = $"{file.Path}:{LineOf(code, match.Index)}";
                calls.Add(where);

                if (args.Count < 6 && !args.Exists(a => a.TrimStart().StartsWith("sourceTableDefId:", StringComparison.Ordinal)))
                {
                    offenders.Add($"{where}: аргументів {args.Count}, таблиці джерела немає");
                }
            }
        }

        Assert.NotEmpty(calls);
        Assert.True(
            offenders.Count == 0,
            "CheckEvaluator.ToMessages без таблиці джерела (T1-01): текст звірки містить значення ОБОХ сторін, "
            + "і без SourceTableDefId читач із Deny на джерело бачить його значення:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "T1-01")]
    public void Повідомлення_валідації_назовні_лише_через_фільтр_видимості()
    {
        var consumers = ValidationMessageConsumers();
        var fresh = consumers
            .Where(f => !UnfilteredConsumers.ContainsKey(f.Path) && !VisibilityFilter().IsMatch(f.Code))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();
        var gone = UnfilteredConsumers.Keys
            .Except(consumers.Select(f => f.Path), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            fresh.Count == 0,
            "Файл споживає ValidationMessage без фільтра видимості (T1-01, S6): повідомлення називає колонку "
            + "і несе її значення — пропусти його через HiddenValidationIssues.ForViewer/CanSee "
            + "(або DocumentReadScope.CanReadAt), або впиши файл у UnfilteredConsumers з причиною:"
            + Environment.NewLine + string.Join(Environment.NewLine, fresh));
        Assert.True(
            gone.Count == 0,
            "Файл більше не споживає ValidationMessage — прибери його з UnfilteredConsumers:"
            + Environment.NewLine + string.Join(Environment.NewLine, gone));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "T1-01")]
    public void Тексти_й_деталі_відмов_у_Deny_контексті_не_називають_нових_колонок_і_значень()
    {
        var actual = Inventory();
        var fresh = new List<string>();
        var gone = new List<string>();

        foreach (var (key, places) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var allowed = Reviewed.TryGetValue(key, out var entry) ? entry.Count : 0;
            if (places.Count > allowed)
            {
                fresh.Add($"{key}  ×{places.Count} (у переліку {allowed}): {string.Join(", ", places)}");
            }
        }

        foreach (var (key, entry) in Reviewed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = actual.TryGetValue(key, out var places) ? places.Count : 0;
            if (count < entry.Count)
            {
                gone.Add($"{key}  ×{count} (у переліку {entry.Count})");
            }
        }

        Assert.True(
            fresh.Count == 0,
            "Новий вираз з іменем колонки/таблиці чи значенням комірки в тексті або деталях відмови файлу, "
            + "що працює з Deny (T1-01). Перевір: чи може це побачити читач із забороною на цю колонку/таблицю "
            + "(відмова ДО рішення про доступ, ехо чужого рядка, лог)? Якщо ні — впиши в Reviewed з причиною; "
            + "якщо так — заміни ідентифікатором або перенеси після перевірки доступу:"
            + Environment.NewLine + string.Join(Environment.NewLine, fresh));
        Assert.True(
            gone.Count == 0,
            "Запис Reviewed більше не відповідає коду — зменш лічильник або прибери рядок:"
            + Environment.NewLine + string.Join(Environment.NewLine, gone));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "T1-01")]
    public void Логи_в_Deny_контексті_не_несуть_імен_колонок_і_значень()
    {
        var offenders = new List<string>();

        foreach (var file in DenyContextFiles())
        {
            var lexed = Lex(file.Text);

            foreach (Match match in LogCall().Matches(lexed.Code))
            {
                if (Declaration().IsMatch(lexed.Code[..match.Index]))
                {
                    continue;
                }

                var open = match.Index + match.Length - 1;
                var close = ClosingParen(lexed.Code, open);
                var args = lexed.Code[(open + 1)..close];

                // Шаблон повідомлення — звичайний літерал: його текст не дані; дані — аргументи й дірки $"…".
                var plain = PlainLiteral().Replace(args, "\"\"");
                var holes = lexed.Holes.Where(h => h.Position > open && h.Position < close).Select(h => h.Expression);

                if (NamesOrValuesIn(InterpolatedLiteral().Replace(plain, "\"\"")) || holes.Any(NamesOrValuesIn))
                {
                    offenders.Add($"{file.Path}:{LineOf(lexed.Code, match.Index)}: {match.Groups["name"].Value}(…)");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Лог у файлі Deny-контексту несе ім'я колонки/таблиці чи значення (T1-01): журнал читає не той, "
            + "кому дозволено дані. Логуй ідентифікатори (TableDefId, ColumnDefId) і причину відмови:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Поточний перелік: <c>файл | вид | вираз</c> → місця (рядки).</summary>
    private static Dictionary<string, List<int>> Inventory()
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        void Add(string path, string kind, string expression, int line)
        {
            var key = $"{path} | {kind} | {Normalize(expression)}";
            if (!result.TryGetValue(key, out var lines))
            {
                result[key] = lines = [];
            }

            lines.Add(line);
        }

        foreach (var file in DenyContextFiles())
        {
            var lexed = Lex(file.Text);

            foreach (var hole in lexed.Holes.Where(h => NamesOrValuesIn(h.Expression)))
            {
                Add(file.Path, "текст", hole.Expression, LineOf(lexed.Code, hole.Position));
            }

            foreach (Match match in DetailEntry().Matches(lexed.Code))
            {
                var expression = match.Groups["expr"].Value;
                if (NamesOrValuesIn(PlainLiteral().Replace(expression, "\"\"")))
                {
                    Add(file.Path, $"деталь {match.Groups["key"].Value}", expression, LineOf(lexed.Code, match.Index));
                }
            }
        }

        return result;
    }

    private static IEnumerable<SourceFile> DenyContextFiles()
        => SourceTree.Production().Where(f => DenyMarker().IsMatch(Lex(f.Text).Code));

    private static List<(string Path, string Code)> ValidationMessageConsumers()
        => [.. SourceTree.Production()
            .Where(f => !f.Path.EndsWith("/HiddenValidationIssues.cs", StringComparison.Ordinal))
            .Select(f => (f.Path, Code: Lex(f.Text).Code))
            .Where(f => ValidationMessageType().IsMatch(PlainLiteral().Replace(f.Code, "\"\"")))];

    private static string Normalize(string expression) => Whitespace().Replace(expression.Trim(), " ");

    private static int LineOf(string text, int position)
    {
        var line = 1;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>Аргументи верхнього рівня виклику, що починається з дужки <paramref name="open"/>.</summary>
    private static List<string> TopLevelArguments(string code, int open)
    {
        var close = ClosingParen(code, open);
        var args = new List<string>();
        var depth = 0;
        var start = open + 1;

        for (var i = open + 1; i < close; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(code[start..i]);
                start = i + 1;
            }
        }

        if (code[start..close].Trim().Length > 0)
        {
            args.Add(code[start..close]);
        }

        return args;
    }

    private static int ClosingParen(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Незакрита дужка на позиції {open}.");
    }

    /// <summary>Грубий пропуск літерала в уже очищеному від коментарів коді (лапки з екрануванням).</summary>
    private static int SkipLiteral(string code, int quote)
    {
        var verbatim = quote > 0 && (code[quote - 1] == '@' || (quote > 1 && code[quote - 2] == '@'));
        for (var i = quote + 1; i < code.Length; i++)
        {
            if (!verbatim && code[i] == '\\')
            {
                i++;
                continue;
            }

            if (code[i] == '"')
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return code.Length - 1;
    }

    /// <summary>
    /// Код без коментарів (тієї ж довжини: коментар → пробіли, переноси рядка збережено) і дірки
    /// інтерпольованих рядків верхнього рівня з позиціями.
    /// </summary>
    /// <remarks>
    /// ⚠ Власний лексер, а не <c>SourceTree.CodeLines</c>: той замінює літерали порожніми, а саме
    /// дірки <c>{…}</c> в <c>$"…"</c> — предмет сторожа. Розуміє звичайні, дослівні (<c>@</c>) і сирі
    /// (<c>"""</c>, <c>$$</c>) рядки, символьні літерали й екранування <c>{{</c>.
    /// </remarks>
    private static Lexed Lex(string text)
    {
        var code = new StringBuilder(text.Length);
        var holes = new List<Hole>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    code.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                for (; i < end; i++)
                {
                    code.Append(text[i] == '\n' ? '\n' : ' ');
                }

                continue;
            }

            if (c == '\'')
            {
                var end = i + 1;
                while (end < text.Length && text[end] != '\'' && text[end] != '\n')
                {
                    end += text[end] == '\\' ? 2 : 1;
                }

                end = Math.Min(end + 1, text.Length);
                code.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c is '"' or '$' or '@' && StringStart(text, i) is { } start)
            {
                var end = ReadString(text, start, holes, record: true);
                code.Append(text, i, end - i);
                i = end;
                continue;
            }

            code.Append(c);
            i++;
        }

        return new Lexed(code.ToString(), holes);
    }

    private sealed record StringShape(int Quote, int Dollars, bool Verbatim, int Quotes);

    /// <summary>Чи починається з <paramref name="i"/> рядковий літерал, і якого виду.</summary>
    private static StringShape? StringStart(string text, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '$' or '@'))
        {
            return null;
        }

        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < text.Length && text[j] is '$' or '@')
        {
            if (text[j] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            j++;
        }

        if (j >= text.Length || text[j] != '"')
        {
            return null;
        }

        var quotes = 0;
        while (j + quotes < text.Length && text[j + quotes] == '"')
        {
            quotes++;
        }

        // `""` — порожній звичайний літерал, не сирий.
        return new StringShape(j, dollars, verbatim, quotes >= 3 ? quotes : 1);
    }

    /// <summary>Читає літерал; повертає позицію за ним. Дірки верхнього рівня — у <paramref name="holes"/>.</summary>
    private static int ReadString(string text, StringShape shape, List<Hole> holes, bool record)
    {
        var raw = shape.Quotes >= 3;
        var open = Math.Max(1, shape.Dollars);
        var i = shape.Quote + shape.Quotes;

        while (i < text.Length)
        {
            var c = text[i];

            if (raw)
            {
                if (c == '"' && Run(text, i, '"') >= shape.Quotes)
                {
                    return i + shape.Quotes;
                }
            }
            else if (shape.Verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }
            }
            else
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c is '"' or '\n')
                {
                    return i + 1;
                }
            }

            if (shape.Dollars > 0 && c == '{')
            {
                var run = Run(text, i, '{');
                if (!raw && run >= 2)
                {
                    i += 2;
                    continue;
                }

                if (run >= open)
                {
                    var exprStart = i + run;
                    var exprEnd = HoleEnd(text, exprStart, out var after);
                    if (record)
                    {
                        holes.Add(new Hole(exprStart, text[exprStart..exprEnd]));
                    }

                    i = after;
                    continue;
                }

                i += run;
                continue;
            }

            i++;
        }

        return i;
    }

    /// <summary>Кінець виразу дірки (без формату й вирівнювання) і позиція за її закривною дужкою.</summary>
    private static int HoleEnd(string text, int start, out int after)
    {
        var depth = 0;
        var exprEnd = -1;
        var i = start;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is '"' or '$' or '@' && StringStart(text, i) is { } nested)
            {
                i = ReadString(text, nested, [], record: false);
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    after = i + Run(text, i, '}');
                    return exprEnd < 0 ? i : exprEnd;
                }

                depth--;
            }
            else if (depth == 0 && exprEnd < 0 && (c == ',' || (c == ':' && !(i + 1 < text.Length && text[i + 1] == ':'))))
            {
                exprEnd = i;
            }

            i++;
        }

        after = text.Length;
        return exprEnd < 0 ? text.Length : exprEnd;
    }

    private static int Run(string text, int i, char c)
    {
        var n = 0;
        while (i + n < text.Length && text[i + n] == c)
        {
            n++;
        }

        return n;
    }

    private sealed record Hole(int Position, string Expression);

    private sealed record Lexed(string Code, List<Hole> Holes);

    /// <summary>Що робить файл Deny-контекстом: межі читання документа чи рішення про заборону.</summary>
    [GeneratedRegex(@"\b(DocumentReadScope|CanReadAt|CanReadColumn|CanReadTable|CanReadSheet|CanReadSheetCode|HiddenTableIds|HiddenColumnIds|HiddenTableDefIds|HiddenColumnDefIds|HiddenValidationIssues|EditDenyReason)\b")]
    private static partial Regex DenyMarker();

    /// <summary>
    /// Чи називає вираз колонку, таблицю, аркуш чи несе значення комірки: бодай один ідентифікатор у ньому —
    /// <c>Code</c>/<c>Name</c>/<c>Title</c>/<c>Caption</c>/<c>Label</c>/<c>Text</c>/<c>Value</c> (з префіксом і
    /// суфіксом мапи: <c>columnCodeById[…]</c>, <c>codeById</c>, <c>cell.ColumnCode</c>, <c>ValueNumeric</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ За ідентифікаторами, а не за <c>.Code</c> наприкінці: пошук у мапі кодів
    /// (<c>columnCodeById[id]</c>) — той самий витік, і ранній варіант сторожа його пропускав
    /// (мутація: дірка <c>{columnCodeById[address.ColumnDefId]}</c> лишалась зеленою).
    /// </remarks>
    private static bool NamesOrValuesIn(string expression)
        => Identifier().Matches(expression)
            .Select(m => m.Value)
            .Any(token => !NotData.Contains(token) && DataWord().IsMatch(token));

    /// <summary>Ідентифікатори, що лише схожі на дані: типи, службові члени, коди помилок.</summary>
    private static readonly HashSet<string> NotData = new(StringComparer.Ordinal)
    {
        "ErrorCodes", "StatusCode", "HttpStatusCode", "GetHashCode", "HasValue", "nameof", "ValueTuple",
        "MessageKey", "TryGetValue", "GetValueOrDefault", "CellValueReader",
    };

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(?:\w*[a-z_])?(?:Code|Name|NameL10n|Title|Caption|Label|Text|Value\w*)s?(?:ById|ByCode|ByKey|Of|Map|Lookup)?$|^(?:code|name|title|caption|label|text|value)s?(?:[A-Z]\w*)?$")]
    private static partial Regex DataWord();

    [GeneratedRegex(@"\bCheckEvaluator\s*\.\s*ToMessages\s*\(")]
    private static partial Regex ToMessagesCall();

    [GeneratedRegex(@"\bHiddenValidationIssues\s*\.\s*(ForViewer|CanSee)\b|\.CanReadAt\s*\(")]
    private static partial Regex VisibilityFilter();

    [GeneratedRegex(@"\bValidationMessage\b")]
    private static partial Regex ValidationMessageType();

    /// <summary>Виклик логера: <c>logger.LogWarning(</c>, <c>.Log(</c>, метод <c>[LoggerMessage]</c> <c>LogX(</c>.</summary>
    [GeneratedRegex(@"(?<![\w])(?<!\bMathF?\.)(?<name>Log(?!2\b|10\b)\w*)\s*\(")]
    private static partial Regex LogCall();

    /// <summary>Перед збігом стоїть оголошення методу (<c>void LogX(</c>), а не виклик.</summary>
    [GeneratedRegex(@"\b(void|partial|Task)\s+$")]
    private static partial Regex Declaration();

    /// <summary>Запис деталей відмови в ініціалізаторі словника: <c>["key"] = вираз</c>.</summary>
    [GeneratedRegex(@"\[\s*""(?<key>\w+)""\s*\]\s*=\s*(?<expr>[^,;\r\n}]+)")]
    private static partial Regex DetailEntry();

    /// <summary>Звичайний (не інтерпольований) літерал.</summary>
    [GeneratedRegex(@"(?<![$@\w])""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex PlainLiteral();

    [GeneratedRegex(@"\$@?""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex InterpolatedLiteral();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
