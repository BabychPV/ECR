namespace Ecr.Api.Observability;

/// <summary>
/// Зіставлення операцій із таблиці бюджету (<c>tz/08</c> §8.2) з метриками.
/// </summary>
/// <remarks>
/// ⚠ Правило контракту (`02-contracts.md` §11): якщо операція є в таблиці
/// бюджету — вона **зобов'язана** мати метрику. Інакше твердження
/// «вкладаємося» нічим не перевірити: без вимірювання «швидко» означає
/// «здається швидко тому, хто це писав».
/// <para>
/// ⛔ Таблиця оголошена ЯВНО, а не виводиться з маршрутів. Операція бюджету —
/// це сценарій («перерахунок піддерева після зміни комірки»), а не ендпоінт:
/// один сценарій може складатися з двох викликів, а один ендпоінт — обслуговувати
/// два сценарії. Автоматичне зіставлення тут дало б відповідь, у яку не можна
/// вірити.
/// </para>
/// </remarks>
public static class BudgetMetrics
{
    /// <summary>Операція бюджету → метрика, що її вимірює.</summary>
    public static IReadOnlyDictionary<string, string> ByOperation { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Читання зрізу"] = EcrMetrics.CellsRead,
            ["batch-PATCH"] = EcrMetrics.CellsWrite,
            ["Перерахунок піддерева"] = EcrMetrics.FormulaEvaluate,

            // Валідація і публікація — довгі синхронні операції; обидві
            // вимірюються тією самою гістограмою, що й фонові задачі, з
            // тегом операції. Окремі метрики на кожну дали б вісім рядків
            // там, де питання одне: «скільки це триває».
            ["Валідація документа"] = EcrMetrics.JobDuration,
            ["Публікація версії шаблону"] = EcrMetrics.JobDuration,
            ["Експорт документа"] = EcrMetrics.JobDuration,
            ["Відкриття адміністративного переліку"] = EcrMetrics.CellsRead,

            // §8.3 — фонові.
            ["Повний річний перерахунок"] = EcrMetrics.CalcFullYear,
            ["Повний перерахунок документа"] = EcrMetrics.JobDuration,
            ["Крос-аркушний rollup"] = EcrMetrics.FormulaEvaluate,
            ["Збір із PI AF"] = EcrMetrics.JobDuration,
            ["Архівація року"] = EcrMetrics.JobDuration,
        };
}
