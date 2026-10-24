// src/Ecr.Application/Ports/IOutboxFlushJob.cs
namespace Ecr.Application.Ports;

/// <summary>
/// ✎ D-451: маркер разової задачі «відправити зараз» адресні листи аркуша з черги <c>itg.NotificationOutbox</c>
/// (<c>OutboxFlushJob</c>, <c>Ecr.Infrastructure</c>).
/// </summary>
/// <remarks>
/// ⛔ Без неї лист «Аркуш повернуто» чекав щогодинного <c>NotificationJob</c> — до години, а це пізно для листа,
/// після якого людина має виправляти аркуш (рішення координатора 24.10). Ставиться ЗЛИТТЯМ на одну ціль
/// (<see cref="OutboxFlushTarget.SheetMail"/>, <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>):
/// сплеск подань дає одну задачу, що чекає, а не задачу на кожне подання.
/// <para>
/// ⚠ Задача шле ЛИШЕ листи аркуша (коди <c>sheet.submitted</c>/<c>sheet.returned</c>), а не всю чергу: повний
/// прогін витрачав би спроби чужих подій при недоступній пошті (J1-01, той самий принцип, що негайний алерт
/// <c>CollectionJob</c>). Повторювального тригера немає — лише разова постановка.
/// </para>
/// </remarks>
public interface IOutboxFlushJob : IBackgroundJob;

/// <summary>Ціль злиття задач <see cref="IOutboxFlushJob"/>.</summary>
public static class OutboxFlushTarget
{
    /// <summary>Одна ціль на всі листи аркуша: усі постановки — та сама робота.</summary>
    public const string SheetMail = "outbox-sheet-mail";
}
