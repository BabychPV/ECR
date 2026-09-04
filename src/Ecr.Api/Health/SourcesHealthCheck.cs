using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан зовнішніх джерел: коли востаннє був успішний збір і чи немає прогалин
/// у покритті.
/// </summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1, але секції з вмістом у
/// `05h` немає (Q-015).
///
/// Ознака здоров'я інтеграції — <b>журнал покриття</b>, а не тиша (ІНТ-3.3):
/// система, яка «нічого не повідомляє», і система, яка «нічого не зібрала»,
/// ззовні однакові.
/// </remarks>
public sealed class SourcesHealthCheck : IHealthCheck
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Залежність від <c>ICollectionStore</c> прибрана з конструктора
    /// свідомо: реалізації порту немає до Етапу 5, а health-перевірка, яку
    /// неможливо створити, валить увесь <c>/health/ready</c> винятком
    /// контейнера. Перевірка, яка не працює, гірша за її відсутність
    /// (`Q-051`). Разом із портом сюди повернеться і залежність.
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => Task.FromResult(HealthCheckResult.Degraded(
            "Збір із зовнішніх джерел з'явиться на Етапі 5.",
            data: new Dictionary<string, object>(StringComparer.Ordinal) { ["stage"] = 5 }));
}
