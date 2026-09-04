// src/Ecr.Application/Calculations/CalculationPlan.cs
using System.Globalization;
using System.Text.Json;

namespace Ecr.Application.Calculations;

/// <summary>
/// Розклад прогону: методології, розбиті на **рівні** залежностей.
/// </summary>
/// <remarks>
/// ⚠ Рівень — це не «група для зручності», а те, що робить бюджет досяжним.
/// Повний річний перерахунок має вкластися в 10 хвилин проти 20 у чинній
/// системі (ПРД-13, D-63), тобто «не гірше» тут не працює. Методології одного
/// рівня незалежні між собою за побудовою, отже виконуються паралельно;
/// послідовний прогін у бюджет не вкладається.
/// <para>
/// Клас **чистий**: жодного сховища, жодного часу. Розклад мусить бути
/// однаковим у прогоні, у симуляції і в тесті — інакше «те саме» обчислення
/// давало б різний порядок і різні числа на межах округлення.
/// </para>
/// </remarks>
public static class CalculationPlan
{
    /// <summary>Розбиває методології на рівні.</summary>
    /// <param name="nodes">Методології з переліком тих, від яких вони залежать.</param>
    /// <returns>Рівні в порядку виконання.</returns>
    /// <exception cref="Errors.BusinessRuleException">
    /// Цикл у залежностях — <c>ECR-TMPL-4221</c>.
    /// </exception>
    public static IReadOnlyList<CalculationLevel> Build(IReadOnlyList<CalculationNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var pending = nodes.ToDictionary(
            n => n.MethodologyVersionId,
            n => new HashSet<int>(n.DependsOn.Where(d => nodes.Any(x => x.MethodologyVersionId == d))));

        var levels = new List<CalculationLevel>();
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
                throw new Errors.BusinessRuleException(
                    "ECR-TMPL-4221",
                    "Методології утворюють цикл залежностей: "
                    + string.Join(", ", pending.Keys.Order()));
            }

            levels.Add(new CalculationLevel(levels.Count, ready));

            foreach (var id in ready)
            {
                done.Add(id);
                pending.Remove(id);
            }
        }

        return levels;
    }
}

/// <summary>Методологія в розкладі прогону.</summary>
/// <param name="MethodologyVersionId">Версія, яка рахує.</param>
/// <param name="DependsOn">Версії, результати яких їй потрібні.</param>
public sealed record CalculationNode(int MethodologyVersionId, IReadOnlyList<int> DependsOn);

/// <summary>Рівень розкладу: методології, що виконуються одночасно.</summary>
/// <param name="Ordinal">Номер рівня; нульовий не має залежностей.</param>
/// <param name="MethodologyVersionIds">Версії цього рівня.</param>
public sealed record CalculationLevel(int Ordinal, IReadOnlyList<int> MethodologyVersionIds);

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

    /// <summary>Статистика по модулях, від найповільнішого.</summary>
    public IReadOnlyList<ModuleStat> Stats
        => _stats.Values.OrderByDescending(s => s.Elapsed).ToList();

    /// <summary>Чи є хоч один запис.</summary>
    public bool IsEmpty => _stats.Count == 0;

    /// <summary>Профіль як JSON для <c>ModulesProfileJson</c>.</summary>
    /// <remarks>
    /// Порядок від найповільнішого навмисно: файл читає людина, і перший рядок
    /// має відповідати на питання «куди пішов час».
    /// </remarks>
    public string ToJson()
        => JsonSerializer.Serialize(Stats.Select(s => new
        {
            module = s.Code,
            ms = s.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            rows = s.Rows,
            calls = s.Calls,
        }));
}

/// <summary>Час і обсяг одного модуля в прогоні.</summary>
/// <param name="Code">Код модуля.</param>
/// <param name="Elapsed">Сумарний час.</param>
/// <param name="Rows">Оброблено рядків.</param>
/// <param name="Calls">Скільки разів викликано.</param>
public sealed record ModuleStat(string Code, TimeSpan Elapsed, int Rows, int Calls);
