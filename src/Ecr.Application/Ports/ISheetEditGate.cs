using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Серіалізація подання аркуша і правок його комірок на ключі
/// «документ × аркуш × період».
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Правка перевіряла стан аркуша (<c>EditRules</c>, <c>DocumentSubmitted</c>)
/// ПОЗА своєю транзакцією запису, а подання не брало жодного блокування до
/// <c>SaveChanges</c>. Під <c>READ_COMMITTED_SNAPSHOT ON</c> правка, що
/// приходила, поки подання ще не зафіксоване, бачила <c>Draft</c> і проходила;
/// зріз подання (<c>calc.SubmissionSnapshot</c>) її не містив, а жива комірка й
/// журнал (<c>aud.CellChange</c>) — містили
/// (<c>docs/build/UX-PASS-2026-09-23.md</c>, <c>SubmitEditRaceTests</c>).
///
/// ⚠ Два режими, а не один: правки між собою НЕ серіалізуються (спільний
/// режим сумісний сам із собою), тож звичайне збереження не стоїть у черзі за
/// сусідом. Чекають лише правка й подання ТОГО САМОГО аркуша за ТОЙ САМИЙ
/// період — одне на одне.
///
/// ⛔ Обидва методи — лише ВСЕРЕДИНІ відкритої транзакції: блокування
/// тримається до її коміту чи відкату, і саме це робить «перевірив стан — записав»
/// атомарним.
/// </remarks>
public interface ISheetEditGate
{
    /// <summary>
    /// Спільне блокування під запис комірок аркуша; повертає стан аркуша,
    /// прочитаний ПІСЛЯ того, як блокування взято.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Стан аркуша; <see cref="DocumentStatus.Draft"/>, якщо рядка стану ще немає.
    /// Подання, яке йшло паралельно, до цього моменту вже зафіксоване або відкочене.
    /// </returns>
    public Task<DocumentStatus> EnterEditAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Виняткове блокування під подання аркуша: чекає, доки зафіксуються правки,
    /// що вже пишуть, і не пускає нових до коміту подання.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task EnterSubmitAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
