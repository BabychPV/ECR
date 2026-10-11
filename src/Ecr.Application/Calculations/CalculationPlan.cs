// src/Ecr.Application/Calculations/CalculationPlan.cs
using System.Globalization;
using System.Text.Json;

namespace Ecr.Application.Calculations;

/// <summary>
/// Розклад прогону: методології, розбиті на **пакети** залежностей.
/// </summary>
/// <remarks>
/// ⚠ Пакет — це не «група для зручності», а те, що робить бюджет досяжним.
/// Повний річний перерахунок має вкластися в 10 хвилин проти 20 у чинній
/// системі (ПРД-13, D-63), тобто «не гірше» тут не працює. Методології одного
/// пакета незалежні між собою за побудовою, отже виконуються паралельно;
/// послідовний прогін у бюджет не вкладається.
/// <para>
/// Клас **чистий**: жодного сховища, жодного часу. Розклад мусить бути
/// однаковим у прогоні, у симуляції і в тесті — інакше «те саме» обчислення
/// давало б різний порядок і різні числа на межах округлення.
/// </para>
/// </remarks>
public static class CalculationPlan
{
    /// <summary>Розбиває методології на пакети.</summary>
    /// <param name="nodes">Методології з переліком тих, від яких вони залежать.</param>
    /// <returns>Пакети в порядку виконання.</returns>
    /// <exception cref="Errors.BusinessRuleException">
    /// Цикл у залежностях — <c>ECR-TMPL-4221</c>.
    /// </exception>
    public static IReadOnlyList<CalculationBatch> Build(IReadOnlyList<CalculationNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        // ⛔ P2-1: одна версія методології, прив'язана до кількох колонок/таблиць документа,
        // приходить кількома вузлами з тим самим ключем — `ToDictionary` падав («same key already
        // added»), задача робила 4 спроби, а результатів не було. Вузли однієї версії зливаються
        // (залежності — об'єднанням): версія в розкладі одна, а всі її прив'язки виконує викликач.
        var pending = nodes
            .GroupBy(n => n.MethodologyVersionId)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<int>(g
                    .SelectMany(n => n.DependsOn)
                    .Where(d => nodes.Any(x => x.MethodologyVersionId == d))));

        var batches = new List<CalculationBatch>();
        var done = new HashSet<int>();

        while (pending.Count > 0)
        {
            // Готові — ті, чиї залежності вже пораховані. Усі вони незалежні
            // між собою, і саме тому їх можна рахувати одночасно.
            var ready = pending
                .Where(p => p.Value.All(done.Contains))
                .Select(p => p.Key)
                .OrderBy(id => id)
                .ToList();

            if (ready.Count == 0)
            {
                // ⛔ Цикл — відмова, а не «порахуємо як вийде». Порядок, узятий
                // навмання, дав би числа, які змінюються між прогонами.
                var cycle = pending.Keys.Order().ToList();
                throw new Errors.BusinessRuleException(
                    "ECR-TMPL-4221",
                    "Методології утворюють цикл залежностей: "
                    + string.Join(", ", cycle),
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-4221.methodologyCycle",
                        ["cycleLength"] = cycle.Count.ToString(CultureInfo.InvariantCulture),
                        ["methodologyVersionIds"] = cycle,
                    });
            }

            batches.Add(new CalculationBatch(batches.Count, ready));

            foreach (var id in ready)
            {
                done.Add(id);
                pending.Remove(id);
            }
        }

        return batches;
    }
}

/// <summary>Методологія в розкладі прогону.</summary>
/// <param name="MethodologyVersionId">Версія, яка рахує.</param>
/// <param name="DependsOn">Версії, результати яких їй потрібні.</param>
public sealed record CalculationNode(int MethodologyVersionId, IReadOnlyList<int> DependsOn);

/// <summary>
/// Пакет розкладу: методології, що виконуються одночасно.
/// </summary>
/// <remarks>
/// ⚠ Названо «пакетом», а не «рівнем», навмисно: <c>CalculationLevel</c> у
/// цій системі вже означає **рівень драбини виразності** (конфігурація,
/// скрипт, модуль — ФВ-13.5). Два різні поняття під одним іменем читалися б
/// як одне, і питання «якого рівня ця методологія» мало б дві відповіді.
/// </remarks>
/// <param name="Ordinal">Номер пакета; нульовий не має залежностей.</param>
/// <param name="MethodologyVersionIds">Версії цього пакета.</param>
public sealed record CalculationBatch(int Ordinal, IReadOnlyList<int> MethodologyVersionIds);

/// <summary>
/// Профіль прогону по модулях (<c>calc.CalculationRun.ModulesProfileJson</c>).
/// </summary>
/// <remarks>
/// ⚠ Заповнюється **завжди**, а не лише в діагностичному режимі. Бюджет 10
/// хвилин — вимога, а не результат заміру, і без профілю невідомо, звідки
/// брати різницю з двадцятьма (питання J-1). Очікується, що топ-5 модулів
/// дають ~80 % часу; оптимізувати треба саме їх, а не те, що здається
/// повільним.
/// </remarks>
public sealed class ModuleProfile
{
    private readonly Dictionary<string, ModuleStat> _stats = new(StringComparer.Ordinal);
    private readonly List<UnmatchedRow> _unmatched = [];
    private readonly List<RejectedRow> _rejected = [];

    /// <summary>Записує виконання одного модуля.</summary>
    /// <param name="moduleCode">Код модуля.</param>
    /// <param name="elapsed">Витрачений час.</param>
    /// <param name="rows">Скільки рядків оброблено.</param>
    public void Record(string moduleCode, TimeSpan elapsed, int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleCode);

        var stat = _stats.TryGetValue(moduleCode, out var existing)
            ? existing
            : new ModuleStat(moduleCode, TimeSpan.Zero, 0, 0);

        _stats[moduleCode] = stat with
        {
            Elapsed = stat.Elapsed + elapsed,
            Rows = stat.Rows + rows,
            Calls = stat.Calls + 1,
        };
    }

    /// <summary>Домішує статистику іншого профілю (напр. іншого документа) у цей.</summary>
    /// <remarks>
    /// ⚠ Не через <see cref="Record"/> у циклі: той рахує кожен виклик як
    /// один (<c>Calls + 1</c>), тож так узятий чужий <c>Calls</c> загубився б,
    /// якщо модуль у джерелі викликався більш ніж раз. Тут складаються
    /// сирі лічильники обох профілів (Q-151/Q-162: перерахунок кількох
    /// документів в одному прогоні зводить їхні профілі в один).
    /// </remarks>
    public void Merge(ModuleProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);

        _unmatched.AddRange(other._unmatched);
        _rejected.AddRange(other._rejected);

        foreach (var stat in other._stats.Values)
        {
            var existing = _stats.TryGetValue(stat.Code, out var found)
                ? found
                : new ModuleStat(stat.Code, TimeSpan.Zero, 0, 0);

            _stats[stat.Code] = existing with
            {
                Elapsed = existing.Elapsed + stat.Elapsed,
                Rows = existing.Rows + stat.Rows,
                Calls = existing.Calls + stat.Calls,
            };
        }
    }

    /// <summary>Статистика по модулях, від найповільнішого.</summary>
    public IReadOnlyList<ModuleStat> Stats
        => _stats.Values.OrderByDescending(s => s.Elapsed).ToList();

    /// <summary>Чи є хоч один запис.</summary>
    public bool IsEmpty => _stats.Count == 0;

    /// <summary>Рядки, яким не підійшло жодне правило жодної прив'язки таблиці (L-4).</summary>
    public IReadOnlyList<UnmatchedRow> UnmatchedRows => _unmatched;

    /// <summary>Записує рядки без правила.</summary>
    /// <param name="rows">Рядки без правила.</param>
    public void RecordUnmatched(IEnumerable<UnmatchedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _unmatched.AddRange(rows);
    }

    /// <summary>Рядки, яким правило категорії версії не дало ключа категорії (L2-6).</summary>
    public IReadOnlyList<RejectedRow> RejectedRows => _rejected;

    /// <summary>Записує рядки, відхилені правилом категорії.</summary>
    /// <param name="rows">Відхилені рядки.</param>
    public void RecordRejected(IEnumerable<RejectedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _rejected.AddRange(rows);
    }

    /// <summary>Скільки ключів рядків без правила зберігається на документ у профілі (D14).</summary>
    public const int UnmatchedKeysKept = 10;

    /// <summary>Профіль як JSON для <c>ModulesProfileJson</c>.</summary>
    /// <remarks>
    /// Порядок від найповільнішого навмисно: файл читає людина, і перший рядок
    /// має відповідати на питання «куди пішов час».
    /// <para>
    /// ✎ D14: корінь — об'єкт <c>{"modules":[…],"unmatched":[…]}</c>, а не масив модулів, як було.
    /// <c>unmatched</c> — по одному запису на документ, у якого є рядки без правила: кількість і до
    /// <see cref="UnmatchedKeysKept"/> ключів; значень комірок немає навмисно. Ключа немає, коли таких рядків
    /// немає. Старі прогони в базі лишаються масивом — <see cref="ReadUnmatched"/> читає обидві форми.
    /// </para>
    /// </remarks>
    public string ToJson()
    {
        var modules = Stats.Select(s => new
        {
            module = s.Code,
            ms = s.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            rows = s.Rows,
            calls = s.Calls,
        }).ToList();

        var unmatched = _unmatched
            .GroupBy(r => r.DocumentId)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                documentId = g.Key,
                count = g.Count(),
                rows = g.OrderBy(r => r.TableInstanceId).ThenBy(r => r.RowNumber)
                    .Take(UnmatchedKeysKept)
                    .Select(r => new { tableInstanceId = r.TableInstanceId, rowNumber = r.RowNumber, rowKey = r.RowKey })
                    .ToList(),
            })
            .ToList();

        return unmatched.Count == 0
            ? JsonSerializer.Serialize(new { modules })
            : JsonSerializer.Serialize(new { modules, unmatched });
    }

    /// <summary>Читає з <c>ModulesProfileJson</c> рядки без правила ОДНОГО документа (D14).</summary>
    /// <param name="profileJson">Збережений профіль; <c>null</c>, масив старого формату й некоректний JSON дають <c>null</c>.</param>
    /// <param name="documentId">Документ.</param>
    /// <returns>Кількість і до <see cref="UnmatchedKeysKept"/> ключів; <c>null</c> — рядків без правила немає.</returns>
    public static UnmatchedSummary? ReadUnmatched(string? profileJson, long documentId)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("unmatched", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("documentId", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || id.GetInt64() != documentId
                    || !item.TryGetProperty("count", out var count)
                    || count.GetInt32() <= 0)
                {
                    continue;
                }

                var rows = new List<UnmatchedRow>();
                if (item.TryGetProperty("rows", out var rowList) && rowList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rowList.EnumerateArray())
                    {
                        rows.Add(new UnmatchedRow(
                            row.GetProperty("tableInstanceId").GetInt64(),
                            row.GetProperty("rowNumber").GetInt32(),
                            row.GetProperty("rowKey").GetString() ?? string.Empty,
                            documentId));
                    }
                }

                return new UnmatchedSummary(count.GetInt32(), rows);
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                       or FormatException)
        {
            // Профіль — діагностика: зіпсований запис не повинен ламати читання документа.
            return null;
        }
    }
}

/// <summary>Рядки документа без правила в профілі прогону (D14).</summary>
/// <param name="Count">Скільки рядків документа не закрило жодне правило.</param>
/// <param name="Rows">До <see cref="ModuleProfile.UnmatchedKeysKept"/> рядків за зростанням таблиці й номера.</param>
public sealed record UnmatchedSummary(int Count, IReadOnlyList<UnmatchedRow> Rows);

/// <summary>Час і обсяг одного модуля в прогоні.</summary>
/// <param name="Code">Код модуля.</param>
/// <param name="Elapsed">Сумарний час.</param>
/// <param name="Rows">Оброблено рядків.</param>
/// <param name="Calls">Скільки разів викликано.</param>
public sealed record ModuleStat(string Code, TimeSpan Elapsed, int Rows, int Calls);

/// <summary>Рядок таблиці, якому не підійшло жодне правило (діагностика «No matching rule»).</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowNumber">Номер рядка, з 1, у порядку створення (за <c>TableRow.Id</c>).</param>
/// <param name="RowKey">Публічний ключ рядка.</param>
/// <param name="DocumentId">Документ прогону (D14): прогін проєкту зводить профілі багатьох документів в один.</param>
public sealed record UnmatchedRow(long TableInstanceId, int RowNumber, string RowKey, long DocumentId = 0);

/// <summary>
/// Рядок, відхилений правилом категорії (L2-6): ключ категорії не отримано (порожній, помилка обчислення,
/// не текст). Рядок не рахується; решта рядків прив'язки рахується як звичайно.
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowNumber">Номер рядка, з 1, у порядку створення (за <c>TableRow.Id</c>).</param>
/// <param name="RowKey">Публічний ключ рядка. Значень комірок і назв колонок тут немає навмисно.</param>
public sealed record RejectedRow(long TableInstanceId, int RowNumber, string RowKey);
