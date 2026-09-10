// src/Ecr.Application/Ports/IRegistryStore.cs

using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до довідників: визначення, записи, зв'язки і — окремо — перевірка
/// посилань на запис.
/// </summary>
/// <remarks>
/// ⚠ Новий порт, як <c>IDocumentStore</c> на Етапі 1 (`D1-05`). Наявними його
/// виразити не можна: <c>IMetadataCache</c> віддає знімок ВЕРСІЇ ШАБЛОНУ, а
/// довідник версією шаблону не обмежений і живе за власною ревізією даних.
/// <para>
/// <see cref="CountReferencesAsync"/> винесений окремо тому, що це єдине
/// місце, де застосунок питає базу «чи можна видаляти» (ФВ-8.6). Сам запрет
/// тримає зовнішній ключ <c>doc.CellValue.ValueRegistryEntryId</c> (ФВ-8.7);
/// цей метод потрібен, щоб віддати <c>ECR-REG-0409</c> замість помилки
/// провайдера.
/// </para>
/// </remarks>
public interface IRegistryStore
{
    /// <summary>Визначення довідника за кодом; <c>null</c> — немає.</summary>
    public Task<RegistryDef?> FindDefinitionAsync(string code, CancellationToken ct);

    /// <summary>Визначення довідника за ідентифікатором; <c>null</c> — немає.</summary>
    public Task<RegistryDef?> FindDefinitionByIdAsync(int registryDefId, CancellationToken ct);

    /// <summary>Усі визначення довідників — це метадані, їх десятки.</summary>
    public Task<IReadOnlyList<RegistryDef>> ListDefinitionsAsync(CancellationToken ct);

    /// <summary>
    /// Ставить нове визначення довідника в чергу на вставку; ідентифікатор
    /// з'являється після збереження.
    /// </summary>
    /// <param name="definition">Довідник-контейнер — без жодного поля.</param>
    public void AddDefinition(RegistryDef definition);

    /// <summary>
    /// Записи довідника **без темпорального фільтра**.
    /// </summary>
    /// <remarks>
    /// Фільтрує <c>RegistryResolver</c>, а не сховище: те саме правило потрібне
    /// в UI, у валідації та в резолвінгу посилань виразів, і три різні
    /// реалізації «чинності» розійшлися б на межах вікна.
    /// </remarks>
    public Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(int registryDefId, CancellationToken ct);

    /// <summary>Запис за ідентифікатором; <c>null</c> — немає.</summary>
    public Task<RegistryEntry?> FindEntryAsync(long registryEntryId, CancellationToken ct);

    /// <summary>Запис за кодом у межах довідника; <c>null</c> — немає.</summary>
    public Task<RegistryEntry?> FindEntryByCodeAsync(int registryDefId, string code, CancellationToken ct);

    /// <summary>
    /// Зв'язки, у яких <b>праворуч</b> стоять записи цього довідника: саме
    /// вони звужують список каскадом (ФВ-8.4).
    /// </summary>
    public Task<IReadOnlyList<RegistryEntryLink>> ListInboundLinksAsync(
        int registryDefId, CancellationToken ct);

    /// <summary>
    /// Скільки комірок посилається на запис. Нуль — видаляти можна.
    /// </summary>
    public Task<int> CountReferencesAsync(long registryEntryId, CancellationToken ct);

    /// <summary>
    /// Чи є хоч один період у стані <c>Open</c> або <c>Grace</c>.
    /// </summary>
    /// <remarks>
    /// Потрібно для ФВ-8.9: перемикати master довідника у відкритому періоді
    /// заборонено. Питання ставиться глобально, а не по проєкту, бо довідник
    /// один на всі проєкти — перемикання у «закритому» проєкті змінило б набір
    /// записів у сусідньому, відкритому.
    /// </remarks>
    public Task<bool> HasOpenPeriodAsync(CancellationToken ct);

    /// <summary>Значення полів запису.</summary>
    public Task<IReadOnlyList<RegistryValue>> ListValuesAsync(long registryEntryId, CancellationToken ct);

    /// <summary>Додає запис; ідентифікатор з'являється після збереження.</summary>
    public void Add(RegistryEntry entry);

    /// <summary>Додає значення поля запису.</summary>
    public void AddValue(RegistryValue value);

    /// <summary>
    /// Правила довідника — усі, включно з вимкненими (<c>ФВ-8.12</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Вимкнені теж: конструктор має показувати правило, яке колись діяло,
    /// інакше пояснити стан наявних записів нічим. Фільтрує споживач.
    /// </remarks>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryRuleDef>> ListRulesAsync(int registryDefId, CancellationToken ct);

    /// <summary>Додає правило; ідентифікатор з'являється після збереження.</summary>
    /// <param name="rule">Правило.</param>
    public void AddRule(RegistryRuleDef rule);

    /// <summary>
    /// Мапінг зовнішніх полів на поля цього довідника (<c>ФВ-8.11</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Читається, а не редагується. Мапінг заводять на екрані джерела, де
    /// поруч є перелік тегів; у конструкторі довідника він потрібен, щоб
    /// відповісти на питання «звідки береться це поле» — без нього поле
    /// <c>IsExternallyManaged</c> виглядає як звичайне, а правити його марно.
    /// </remarks>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryFieldMapping>> ListFieldMappingsAsync(
        int registryDefId, CancellationToken ct);

    /// <summary>
    /// Види зв'язків M:N, які насправді трапляються в записах цього довідника.
    /// </summary>
    /// <remarks>
    /// ⛔ Питається БАЗА, а не опис: таблиці <c>cfg.RegistryRelationDef</c> у
    /// схемі немає (`02a-db-schema.md` §5, <c>Q-027</c>), і вид зв'язку живе
    /// рядком у <c>dic.RegistryEntryLink.LinkKind</c>. Тому «зв'язки» в
    /// конструкторі — це те, що є в даних, а не те, що хтось оголосив.
    ///
    /// ⚠ Повертаються згорнуті пари «вид → кількість», а не самі зв'язки:
    /// їх у <c>Permit</c> десятки тисяч, і тягти їх заради переліку видів
    /// означало б вивантажити половину довідника на кожне відкриття екрана.
    /// </remarks>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryLinkKindStat>> ListLinkKindsAsync(
        int registryDefId, CancellationToken ct);
}

/// <summary>Вид зв'язку M:N і скільки таких зв'язків у довіднику.</summary>
/// <param name="LinkKind">Вид відношення: <c>permit-water-body</c>, <c>permit-pollutant</c>.</param>
/// <param name="Count">Скільки зв'язків цього виду.</param>
public sealed record RegistryLinkKindStat(string LinkKind, int Count);

/// <summary>
/// Одне правило мапінгу, що наповнює поле довідника із зовнішнього джерела.
/// </summary>
/// <param name="FieldMapId">Запис <c>ext.EntityFieldMap</c>.</param>
/// <param name="RegistryFieldDefId">Поле довідника, куди лягає значення.</param>
/// <param name="SourceCode">Код сутності джерела (<c>ext.SourceEntity.Code</c>).</param>
/// <param name="SourceField">Поле або тег у джерелі.</param>
/// <param name="TransformCode">Згортання точок періоду; <c>null</c> — не згортається.</param>
/// <param name="SourceUnitCode">Одиниця джерела; <c>null</c> — безрозмірне.</param>
/// <param name="TargetUnitCode">Одиниця поля; <c>null</c> — безрозмірне.</param>
/// <param name="IsActive">Чи діє мапінг.</param>
public sealed record RegistryFieldMapping(
    int FieldMapId,
    int RegistryFieldDefId,
    string SourceCode,
    string SourceField,
    string? TransformCode,
    string? SourceUnitCode,
    string? TargetUnitCode,
    bool IsActive);
