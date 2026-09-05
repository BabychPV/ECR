using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Services;

/// <summary>
/// Будує календар періодів проєкту (ФВ-1.5).
/// </summary>
/// <remarks>
/// Чиста функція, як і <c>EditRules</c>: календар визначає, у які партиції
/// ляжуть дані на роки вперед, і помилка тут виявиться не при написанні коду,
/// а на архівації. Без бази його можна прогнати цілком.
/// </remarks>
public static class PeriodCalendar
{
    /// <summary>Верхня межа порядкового номера періоду (D-108).</summary>
    /// <remarks>
    /// ⚠ Дванадцять — НЕ довільне число. Партиційна функція перелічує межі як
    /// <c>YYYY01…YYYY12</c>, і <c>Sequence = 13</c> мовчки ліг би в грудневу
    /// партицію та поїхав в архів разом із груднем. Тому обмеження стоїть і в
    /// домені, і в базі (<c>CK_Period_Seq</c>) — одного місця замало: дані
    /// потрапляють у таблицю не лише через домен.
    /// </remarks>
    public const byte MaxSequence = 12;

    /// <summary>Скільки періодів дає ця періодичність.</summary>
    /// <param name="kind">Періодичність проєкту.</param>
    /// <param name="customCount">Кількість для <c>Custom</c>.</param>
    /// <exception cref="DomainException">Кількість перевищує <see cref="MaxSequence"/>.</exception>
    public static int CountFor(PeriodKind kind, int customCount = 0)
    {
        var count = kind switch
        {
            PeriodKind.Monthly => 12,
            PeriodKind.Quarterly => 4,
            PeriodKind.Yearly => 1,
            _ => customCount,
        };

        return count is >= 1 and <= MaxSequence
            ? count
            : throw new DomainException(
                "ECR-PRD-4224",
                $"Кількість періодів {count} поза межами 1..{MaxSequence} (D-108).");
    }

    /// <summary>Ключ періоду: <c>Year*100 + Sequence</c> (R-A6).</summary>
    /// <param name="year">Рік звіту.</param>
    /// <param name="sequence">Порядковий номер у році, 1…12.</param>
    /// <exception cref="DomainException">Номер поза 1…12.</exception>
    public static PeriodKey KeyFor(int year, byte sequence)
    {
        if (sequence is < 1 or > MaxSequence)
        {
            throw new DomainException(
                "ECR-PRD-4224",
                $"Порядковий номер періоду {sequence} поза межами 1..{MaxSequence} (D-108).");
        }

        return PeriodKey.Create(year, sequence);
    }

    /// <summary>
    /// Створює періоди, яких ще немає.
    /// </summary>
    /// <param name="project">Проєкт із межами звітного року і періодичністю.</param>
    /// <param name="policy">Політика зсувів.</param>
    /// <param name="siteTimeZone">Пояс майданчика.</param>
    /// <param name="existing">Уже створені періоди проєкту.</param>
    /// <param name="customCount">Кількість періодів для <c>Custom</c>.</param>
    /// <returns>Лише НОВІ періоди — повторний виклик дає порожній список.</returns>
    public static IReadOnlyList<Period> Build(
        Project project,
        PeriodPolicy policy,
        TimeZoneInfo siteTimeZone,
        IReadOnlyCollection<Period> existing,
        int customCount = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(siteTimeZone);
        ArgumentNullException.ThrowIfNull(existing);

        var year = project.PeriodStart.Year;
        var count = CountFor(project.PeriodKind, customCount);
        var known = existing.Select(p => p.PeriodKeyValue).ToHashSet();
        var created = new List<Period>();

        // ⛔ Межі наявних періодів ПЕРЕРАХОВУЮТЬСЯ. До `A7-26` календар лише
        // доповнював набір, хоч власний коментар нижче й обіцяв роботу «після
        // зміни політики»: наявні періоди пропускалися цілком, тому зміна
        // політики або поясу майданчика не діяла на них ніколи.
        //
        // ⚠ Другий бік того самого: період, створений в обхід календаря,
        // лишався з НЕОБЧИСЛЕНИМИ межами (`0001-01-01`), а калькулятор станів
        // читає їх як «усе вже минуло» і оголошує період закритим. Система
        // виглядала налаштованою і не приймала жодного значення.
        //
        // ⚠ Стан періоду від цього не змінюється: він зберігається окремо, а
        // задача станів не переводить закритий період назад (ФВ-1.12).
        foreach (var period in existing)
        {
            period.RecomputeBoundaries(policy, siteTimeZone);
        }

        for (byte sequence = 1; sequence <= count; sequence++)
        {
            var key = KeyFor(year, sequence);

            // Ідемпотентність: повторний виклик не створює дублікатів. Календар
            // будують і при створенні проєкту, і після зміни політики — другий
            // раз він має лише доповнювати.
            if (!known.Add(key.Value))
            {
                continue;
            }

            var (start, end) = Bounds(project, sequence, count);
            var period = new Period(project.Id, key, sequence, start, end);
            period.RecomputeBoundaries(policy, siteTimeZone);
            created.Add(period);
        }

        return created;
    }

    /// <summary>Межі періоду за його номером і кількістю періодів у році.</summary>
    private static (DateOnly Start, DateOnly End) Bounds(Project project, byte sequence, int count)
    {
        var year = project.PeriodStart.Year;

        // ⚠ Місяці рахуються від номера періоду, а не діленням року на рівні
        // частини: квартал — це три КАЛЕНДАРНІ місяці, і 365/4 дало б межі,
        // яких немає в жодному звіті.
        var monthsPerPeriod = 12 / count;
        var firstMonth = ((sequence - 1) * monthsPerPeriod) + 1;
        var start = new DateOnly(year, firstMonth, 1);
        var lastMonth = firstMonth + monthsPerPeriod - 1;
        var end = new DateOnly(year, lastMonth, DateTime.DaysInMonth(year, lastMonth));

        // Проєкт може починатися й закінчуватися всередині року — календар не
        // виходить за його межі.
        return (start < project.PeriodStart ? project.PeriodStart : start,
                end > project.PeriodEnd ? project.PeriodEnd : end);
    }
}
