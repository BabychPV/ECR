using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecr.TestKit;

/// <summary>
/// Лічильник звернень до БД за категоріями запиту.
/// </summary>
/// <remarks>
/// Інструмент рядка <c>MS-01</c> директиви №14 (частина 3, §3.8). На нього
/// спираються храповики <c>WR-04</c> («<c>PATCH</c> 100 комірок у теплому
/// стані ≤ 10 звернень») і <c>RD-03</c> («<c>GET</c> зрізу ≤ 8»). Храповик —
/// це число, яке не має права рости; отже лічильник мусить бути таким, щоб
/// його не можна було обдурити випадково.
///
/// ⛔ ГОЛОВНЕ ОБМЕЖЕННЯ, і воно не косметичне.
/// <see cref="DbCommandInterceptor"/> бачить лише команди, які випустив
/// <b>EF Core</b>. Найгарячіший шлях запису — <c>NormalizedCellStore</c> —
/// команди EF не випускає взагалі: він бере з'єднання
/// (<c>db.Database.GetDbConnection()</c>, <c>NormalizedCellStore.cs:237</c>) і
/// створює <c>connection.CreateCommand()</c> напряму (<c>:331</c>, <c>:423</c>,
/// <c>:462</c>, <c>:560</c>). Тобто <c>ClaimRowsAsync</c>, <c>DELETE</c>,
/// <c>MERGE doc.CellValue</c> і <c>TouchRowsAsync</c> для цього класу
/// НЕВИДИМІ — а це рівно ті звернення, заради яких храповик і ставиться.
///
/// ⛔ Наслідок: храповик, побудований на самому лише
/// <see cref="DbCommandCounter"/>, був би хибнозеленим — він показував би
/// «звернень мало» саме тому, що не бачить головних. Для повного числа
/// використовуйте <see cref="SqlClientCommandCounter"/>, який ловить усі
/// команди <c>Microsoft.Data.SqlClient</c> у процесі — і EF-ові, і сирі.
/// Цей клас лишається для випадків, коли цікавий саме EF-шлях (наприклад,
/// <c>N+1</c> у зрізі), і тоді його вужчий обсяг — перевага, а не вада.
///
/// ⚠ Обидва лічильники ділять одну <see cref="CommandTally"/> й одну
/// таксономію категорій, тож їхні числа зіставні між собою.
///
/// ⚠ Не вішайте обидва одночасно на один процес: EF-ові команди потраплять у
/// підрахунок двічі. Про це попереджає <see cref="CommandTally.Sources"/>.
/// </remarks>
/// <example>
/// <code>
/// var counter = new DbCommandCounter();
/// await using var db = new EcrDbContext(new DbContextOptionsBuilder&lt;EcrDbContext&gt;()
///     .UseSqlServer(connectionString)
///     .AddInterceptors(counter)
///     .Options);
///
/// counter.Tally.Reset();            // теплий стан: скидаємо розігрів
/// await handler.HandleAsync(...);
/// var seen = counter.Tally.Snapshot();
/// Assert.True(seen.Total &lt;= 10, seen.Format());
/// </code>
/// </example>
public sealed class DbCommandCounter : DbCommandInterceptor
{
    /// <summary>Підрахунок; можна віддати спільним для кількох контекстів.</summary>
    public CommandTally Tally { get; }

    /// <summary>Новий лічильник із власним підрахунком.</summary>
    public DbCommandCounter()
        : this(new CommandTally())
    {
    }

    /// <summary>Лічильник, який пише у спільний підрахунок.</summary>
    /// <param name="tally">Куди складати. Не <c>null</c>.</param>
    public DbCommandCounter(CommandTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        Tally = tally;
        Tally.Declare("ef-interceptor");
    }

    /// <inheritdoc/>
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Чи була транзакція відкрита в мить виконання.
    /// </summary>
    /// <remarks>
    /// ⚠ Питають <c>command.Transaction</c>, а не
    /// <c>eventData.Context.Database.CurrentTransaction</c>. Друге відповідає
    /// на «чи тримає транзакцію КОНТЕКСТ», а не «чи пішла ЦЯ команда всередині
    /// транзакції»: команда на іншому з'єднанні (інший <c>DbContext</c>,
    /// <c>IDistributedCache</c> на SQL) при відкритій транзакції контексту
    /// зарахувалася б як транзакційна, хоча жодного стосунку до неї не має.
    /// </remarks>
    private void Record(DbCommand command, CommandEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(eventData);

        Tally.Add(command.CommandText, command.Transaction is not null, command.Parameters.Count);
    }
}

/// <summary>Підрахунок команд за категоріями — спільне ядро обох лічильників.</summary>
/// <remarks>
/// ⚠ Потокобезпечний навмисно: під навантаженням команди йдуть із десятків
/// потоків одночасно, і лічильник, який це ламає, робить замір недійсним
/// мовчки — не падінням, а заниженим числом.
/// </remarks>
public sealed class CommandTally
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sources = new(StringComparer.Ordinal);
    private int _unparsed;

    /// <summary>Хто саме підключений до цього підрахунку.</summary>
    /// <remarks>
    /// Двоє одночасно — це подвійний облік EF-команд, і
    /// <see cref="CommandTallySnapshot.Format"/> про це кричить.
    /// </remarks>
    public IReadOnlyCollection<string> Sources => _sources.Keys.ToArray();

    /// <summary>Оголошує джерело команд.</summary>
    /// <param name="source">Ім'я джерела.</param>
    public void Declare(string source) => _sources.TryAdd(source, 0);

    /// <summary>Зараховує одну команду.</summary>
    /// <param name="commandText">Текст запиту.</param>
    /// <param name="inTransaction">Чи була команда всередині транзакції.</param>
    /// <param name="parameterCount">Скільки параметрів несла команда.</param>
    public void Add(string? commandText, bool inTransaction, int parameterCount)
    {
        var category = CommandCategory.Of(commandText);
        if (category == CommandCategory.Unparsed)
        {
            Interlocked.Increment(ref _unparsed);
        }

        var bucket = _buckets.GetOrAdd(category, _ => new Bucket());
        Interlocked.Increment(ref bucket.Count);
        if (inTransaction)
        {
            Interlocked.Increment(ref bucket.InTransaction);
        }

        Interlocked.Add(ref bucket.Parameters, parameterCount);

        // ⚠ Розмір батчу (кількість параметрів) — не декорація. Саме він
        // відрізняє «один план на будь-який батч» (WR-02, TVP) від «план на
        // кожен розмір чанка» (WR-01): якщо різних значень багато, планів у
        // кеші теж багато.
        InterlockedMin(ref bucket.MinParameters, parameterCount);
        InterlockedMax(ref bucket.MaxParameters, parameterCount);
    }

    /// <summary>Скидає підрахунок — так відділяється розігрів від заміру.</summary>
    public void Reset()
    {
        _buckets.Clear();
        Interlocked.Exchange(ref _unparsed, 0);
    }

    /// <summary>Знімок на цю мить.</summary>
    /// <returns>Незмінний знімок, придатний до друку й порівняння.</returns>
    public CommandTallySnapshot Snapshot()
    {
        var rows = _buckets
            .Select(pair => new CategoryCount(
                pair.Key,
                Volatile.Read(ref pair.Value.Count),
                Volatile.Read(ref pair.Value.InTransaction),
                Volatile.Read(ref pair.Value.MinParameters) == int.MaxValue
                    ? 0
                    : Volatile.Read(ref pair.Value.MinParameters),
                Volatile.Read(ref pair.Value.MaxParameters) == int.MinValue
                    ? 0
                    : Volatile.Read(ref pair.Value.MaxParameters)))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ToArray();

        return new CommandTallySnapshot(
            rows,
            Volatile.Read(ref _unparsed),
            [.. _sources.Keys.OrderBy(s => s, StringComparer.Ordinal)]);
    }

    private static void InterlockedMin(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (seen <= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (seen >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private sealed class Bucket
    {
        public int Count;
        public int InTransaction;
        public long Parameters;
        public int MinParameters = int.MaxValue;
        public int MaxParameters = int.MinValue;
    }
}

/// <summary>Скільки команд однієї категорії і скільки з них у транзакції.</summary>
/// <param name="Category">Категорія — <c>«ДІЄСЛОВО схема.Таблиця»</c>.</param>
/// <param name="Count">Скільки команд.</param>
/// <param name="InTransaction">Скільки з них усередині транзакції.</param>
/// <param name="MinParameters">Найменший розмір батчу (параметрів у команді).</param>
/// <param name="MaxParameters">Найбільший розмір батчу.</param>
public sealed record CategoryCount(
    string Category, int Count, int InTransaction, int MinParameters, int MaxParameters);

/// <summary>Незмінний знімок підрахунку.</summary>
/// <param name="Categories">Категорії, від найчастішої.</param>
/// <param name="Unparsed">Скільки команд не вдалося класифікувати.</param>
/// <param name="Sources">Підключені джерела команд.</param>
public sealed record CommandTallySnapshot(
    IReadOnlyList<CategoryCount> Categories,
    int Unparsed,
    IReadOnlyList<string> Sources)
{
    /// <summary>Усього звернень до БД.</summary>
    public int Total => Categories.Sum(c => c.Count);

    /// <summary>Скільки з них усередині транзакції.</summary>
    public int InTransaction => Categories.Sum(c => c.InTransaction);

    /// <summary>Скільки команд однієї категорії.</summary>
    /// <param name="category">Категорія, наприклад <c>"MERGE doc.CellValue"</c>.</param>
    /// <returns>Кількість; <c>0</c>, якщо такої не було.</returns>
    public int this[string category]
        => Categories.FirstOrDefault(c => string.Equals(c.Category, category, StringComparison.Ordinal))?.Count ?? 0;

    /// <summary>
    /// Готовий текст для повідомлення тесту, що впав.
    /// </summary>
    /// <returns>Таблиця категорій із застереженнями.</returns>
    /// <remarks>
    /// ⛔ Застереження друкуються ПЕРШИМИ і завжди. Храповик, який показав
    /// «5 звернень» тому, що лічильник не бачив сирих команд
    /// <c>NormalizedCellStore</c>, гірший за відсутній храповик: він дає
    /// підставу вважати роботу зробленою.
    /// </remarks>
    public string Format()
    {
        var text = new StringBuilder();

        if (Sources.Contains("ef-interceptor") && !Sources.Contains("sqlclient-diagnostics"))
        {
            text.AppendLine(
                "⚠ Джерело лише EF: сирі команди NormalizedCellStore (MERGE/ClaimRows/Touch) "
                + "У ЦЕ ЧИСЛО НЕ ВХОДЯТЬ. Для повного — SqlClientCommandCounter.");
        }

        if (Sources.Count > 1)
        {
            text.AppendLine(
                "⚠ Підключено кілька джерел одночасно — EF-команди зараховані двічі: "
                + string.Join(", ", Sources));
        }

        if (Unparsed > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"⚠ Не класифіковано команд: {Unparsed}.");
        }

        text.AppendLine(CultureInfo.InvariantCulture,
            $"Звернень усього: {Total}, із них у транзакції: {InTransaction}.");

        foreach (var row in Categories)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {row.Category,-40} {row.Count,5}  у транзакції {row.InTransaction,5}  параметрів {row.MinParameters}…{row.MaxParameters}");
        }

        return text.ToString();
    }
}

/// <summary>
/// Категорія команди: дієслово плюс основна таблиця.
/// </summary>
/// <remarks>
/// ⚠ Розбір навмисно грубий і без regex по всьому тексту: він мусить бути
/// дешевим (виконується на КОЖНУ команду під навантаженням) і передбачуваним.
/// Що не розпізнано — чесно йде в <see cref="Unparsed"/>, а не тихо
/// приписується сусідній категорії.
///
/// ⛔ Дужки EF (<c>[doc].[CellValue]</c>) і сирий SQL сховища
/// (<c>doc.CellValue</c>) мусять дати ОДНУ категорію — інакше та сама таблиця
/// рахувалася б двома рядками, і храповик на ній не тримався б.
/// </remarks>
public static class CommandCategory
{
    /// <summary>Категорія для команди, яку не вдалося розібрати.</summary>
    public const string Unparsed = "?? нерозпізнано";

    private static readonly string[] Verbs =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE"];

    /// <summary>Категорія тексту запиту.</summary>
    /// <param name="commandText">Текст команди.</param>
    /// <returns><c>«ДІЄСЛОВО схема.Таблиця»</c> або <see cref="Unparsed"/>.</returns>
    public static string Of(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return Unparsed;
        }

        var tokens = Tokenize(commandText);
        var verbAt = FindVerb(tokens);
        if (verbAt < 0)
        {
            return Unparsed;
        }

        var verb = tokens[verbAt].ToUpperInvariant();
        if (string.Equals(verb, "EXECUTE", StringComparison.Ordinal))
        {
            verb = "EXEC";
        }

        var target = FindTarget(tokens, verbAt, verb);

        return target is null ? verb : string.Concat(verb, " ", target);
    }

    /// <summary>
    /// Ділить текст на токени, викидаючи коментарі й рядкові літерали.
    /// </summary>
    /// <param name="text">Текст команди.</param>
    /// <returns>Токени.</returns>
    /// <remarks>
    /// ⚠ Рядкові літерали викидаються, бо всередині них трапляються ті самі
    /// слова: <c>'SELECT'</c> у seed-даних або <c>N'doc.CellValue'</c> у
    /// повідомленні помилки зробили б категорію брехливою.
    /// </remarks>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>(32);
        var token = new StringBuilder(32);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                Flush(tokens, token);
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush(tokens, token);
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(text.Length, i + 2);
                continue;
            }

            if (c == '\'')
            {
                Flush(tokens, token);
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    i++;
                }

                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ',' || c == ';' || c == '(' || c == ')' || c == '=')
            {
                Flush(tokens, token);
                i++;
                continue;
            }

            token.Append(c);
            i++;
        }

        Flush(tokens, token);
        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder token)
    {
        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
            token.Clear();
        }
    }

    /// <summary>
    /// Перше дієслово, що починає справжній оператор.
    /// </summary>
    /// <param name="tokens">Токени команди.</param>
    /// <returns>Індекс дієслова або <c>-1</c>.</returns>
    /// <remarks>
    /// ⛔ Пропуск преамбули обов'язковий. EF ставить перед батчем
    /// <c>SET IMPLICIT_TRANSACTIONS OFF; SET NOCOUNT ON;</c>, а сховище —
    /// <c>DECLARE @claimed TABLE(...)</c>. Розбір «перше слово тексту» дав би
    /// категорії <c>SET</c> і <c>DECLARE</c> на всі гарячі команди — тобто
    /// рівно на ті, заради яких лічильник існує.
    /// </remarks>
    private static int FindVerb(List<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            foreach (var verb in Verbs)
            {
                if (string.Equals(tokens[i], verb, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Основна таблиця оператора.</summary>
    /// <param name="tokens">Токени команди.</param>
    /// <param name="verbAt">Індекс дієслова.</param>
    /// <param name="verb">Дієслово у верхньому регістрі.</param>
    /// <returns>Ім'я вигляду <c>схема.Таблиця</c> або <c>null</c>.</returns>
    private static string? FindTarget(List<string> tokens, int verbAt, string verb)
    {
        var anchors = verb switch
        {
            "SELECT" or "DELETE" => new[] { "FROM" },
            "INSERT" => ["INTO"],
            "UPDATE" or "MERGE" => [],
            "EXEC" => [],
            _ => [],
        };

        if (anchors.Length == 0)
        {
            // UPDATE/MERGE/EXEC несуть ціль одразу після дієслова; у MERGE
            // між ними може стояти необов'язкове INTO.
            var at = verbAt + 1;
            if (at < tokens.Count && string.Equals(tokens[at], "INTO", StringComparison.OrdinalIgnoreCase))
            {
                at++;
            }

            return at < tokens.Count ? Normalize(tokens[at]) : null;
        }

        for (var i = verbAt + 1; i < tokens.Count; i++)
        {
            foreach (var anchor in anchors)
            {
                if (string.Equals(tokens[i], anchor, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < tokens.Count)
                {
                    return Normalize(tokens[i + 1]);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Зводить <c>[doc].[CellValue]</c> і <c>doc.CellValue</c> до одного імені.
    /// </summary>
    /// <param name="raw">Токен імені.</param>
    /// <returns>Нормалізоване ім'я або <c>null</c>, якщо це не ім'я таблиці.</returns>
    private static string? Normalize(string raw)
    {
        var name = raw.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Trim();

        // Похідна таблиця, змінна-таблиця або TVP: власного імені немає.
        if (name.Length == 0 || name.StartsWith('@') || name.StartsWith('#'))
        {
            return name.Length == 0 ? null : name;
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2
            ? string.Concat(parts[^2], ".", parts[^1])
            : parts.Length == 1 ? parts[0] : null;
    }
}
