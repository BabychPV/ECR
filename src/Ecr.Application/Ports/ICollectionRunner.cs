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
    /// <returns>
    /// Що прогін насправді змінив: наздоганяння читає прогалини й ДО
    /// <paramref name="fromUtc"/>, і задача мусить знати, за який діапазон
    /// переносити зібране в комірки (аудит I1-01).
    /// </returns>
    public Task<CollectionRunSummary> RunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, IJobProgress progress, CancellationToken ct);
}

/// <summary>Підсумок прогону збору.</summary>
/// <param name="ReadFromUtc">
/// Від якого моменту в сирому шарі могли змінитися дані: запитаний початок або,
/// якщо наздоганяння записало давніші точки, мітка найранішої з них.
/// </param>
/// <param name="PointsWritten">Скільки точок записано.</param>
/// <param name="Degraded">
/// Прогін закрито <c>Degraded</c>: джерело відмовило чи не вклалося в час, частину діапазону не прочитано (піде
/// в наздоганяння). Точки, що встигли записатися, - записані; але це НЕ успішний прогін розкладу (I1-06).
/// </param>
/// <remarks>
/// ⛔ Аудит I1-01: без цього матеріалізація ставилася лише на запитане вікно, і
/// точки, які наздоганяння дописало за минулий місяць, у його комірки не
/// потрапляли ніколи — звіт ішов із заниженим числом.
/// </remarks>
public sealed record CollectionRunSummary(DateTime ReadFromUtc, int PointsWritten, bool Degraded = false);
