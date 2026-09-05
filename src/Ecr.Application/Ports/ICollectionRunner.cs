// src/Ecr.Application/Ports/ICollectionRunner.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Виконавець збору із зовнішнього джерела.
/// </summary>
/// <remarks>
/// ⚠ Порт існує з причини компонування, а не з любові до абстракцій:
/// <c>CollectionJob</c> живе в <c>Ecr.Infrastructure</c>, а
/// <c>CollectionRunner</c> — в <c>Ecr.Adapters.PiAf</c>, і це <b>сусідні</b>
/// проєкти (05-skeleton §4). Без порту задача не могла б викликати збирача
/// взагалі — або довелося б з'єднати інфраструктуру з адаптером посиланням,
/// яке архітектурний тест ловить одразу.
/// </remarks>
public interface ICollectionRunner
{
    /// <summary>Збирає дані сутності за діапазон.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="fromUtc">Початок діапазону, включно.</param>
    /// <param name="toUtc">Кінець діапазону, виключно.</param>
    /// <param name="progress">Прогрес для фонової задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Відмова джерела <b>не</b> кидає виняток: діапазон лишається
    /// непокритим і потрапляє в наздоганяння. Виняток тут означає, що збирати
    /// не можна взагалі — зламана конфігурація або змінена одиниця джерела.
    /// </remarks>
    public Task RunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, IJobProgress progress, CancellationToken ct);
}
