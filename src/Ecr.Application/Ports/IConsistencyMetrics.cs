// src/Ecr.Application/Ports/IConsistencyMetrics.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Видимість знахідок <c>ConsistencyCheckJob</c> у метриках (директива №11,
/// T10 #41).
/// </summary>
/// <remarks>
/// ⛔ Порт, а не прямий виклик <c>Ecr.Api.Observability.EcrMetrics</c>: та
/// живе в <c>Ecr.Api</c>, а <c>ConsistencyCheckJob</c> — в
/// <c>Ecr.Infrastructure</c>, яка на <c>Ecr.Api</c> НЕ посилається (шар нижче
/// не бачить шару вище). Без цього порту метрика й далі мала б нуль
/// викликів — не тому, що її забули викликати, а тому, що викликати її
/// звідти нізвідки не можна було без порушення напрямку залежностей.
/// </remarks>
public interface IConsistencyMetrics
{
    /// <summary>Фіксує знахідки одного різновиду одного проходу перевірки.</summary>
    /// <param name="count">Скільки знайдено цього різновиду.</param>
    /// <param name="kind"><c>RuleCode</c> знахідки (наприклад, <c>ORPHANED_CELL</c>).</param>
    public void RecordIssues(int count, string kind);
}
