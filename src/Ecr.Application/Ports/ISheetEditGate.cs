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
///
/// ⚠ Блокування не взято вчасно (тайм-аут або жертва дедлоку) — обидва методи
/// кидають <c>ConcurrencyConflictException</c> з кодом <c>ECR-DOC-4091</c>
/// («аркуш зайнятий, повторіть за мить»), а не збій сервера.
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

    /// <summary>
    /// Блокування СТРУКТУРИ документа (L6-02): спільне — під будь-який запис у
    /// документ (комірки, рядки, подання, імпорт, шапка), виняткове — під перенос
    /// документа на іншу версію шаблону. Повертає <c>Project.TemplateVersionId</c>
    /// документа, прочитаний ПІСЛЯ того, як блокування взято; <c>null</c> — документа
    /// немає.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="exclusive"><c>true</c> — перенос версії; <c>false</c> — запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Навіщо. Перенос (<c>MigrateDocumentVersionHandler</c>) блокував лише рядок
    /// <c>doc.Project</c>, якого писарі не беруть: правка, що комітилася між
    /// плануванням переносу і його <c>DELETE</c>, губилась, а правка, що будувала
    /// контекст ДО переносу, писала під старим <c>ColumnDefId</c>.
    ///
    /// ⚠ Порядок блокувань ЗАВЖДИ: <c>doc-structure</c> → <c>doc-header</c> →
    /// <c>sheet-edit</c>, і структура — ПЕРШОЮ дією транзакції. Перенос бере
    /// виняткові на всі документи проєкту за зростанням <c>DocumentId</c>.
    ///
    /// ⚠ Писар порівнює повернуту версію з тією, за якою будував запит
    /// (<see cref="Documents.DocumentStructure.EnsureUnchanged"/>): розбіжність —
    /// <c>409 ECR-DOC-4091 structureChanged</c>, а не запис під чужою структурою.
    /// </remarks>
    public Task<int?> EnterStructureAsync(long documentId, bool exclusive, CancellationToken ct);

    /// <summary>
    /// Блокування ШАПКИ документа (L6-06): виняткове — правка шапки, спільне —
    /// подання аркуша (воно валідує шапку й кладе її у зріз).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="exclusive"><c>true</c> — правка шапки; <c>false</c> — подання.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Окремий ключ, а не <c>doc-structure</c> винятково: інакше кожна правка
    /// шапки чекала б на всі збереження комірок документа, а вони — на неї.
    /// Береться ПІСЛЯ структури й ДО блокування аркуша.
    /// </remarks>
    public Task EnterHeaderAsync(long documentId, bool exclusive, CancellationToken ct);
}
