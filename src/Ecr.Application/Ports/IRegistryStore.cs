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
}
