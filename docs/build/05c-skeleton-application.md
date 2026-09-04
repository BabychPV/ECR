# 05c — Скелет: `Ecr.Application`

> Частина [`05-skeleton.md`](05-skeleton.md).
>
> `Ecr.Application` містить **use-cases і порти**. Він не знає ані про EF Core,
> ані про ASP.NET, ані про PI AF — це перевіряється архітектурним тестом і є
> межею, яка робить систему тестованою без бази.

---

## 1. Файли, що копіюються з контрактів

| Файл | Джерело |
|---|---|
| `Ports/ICellStore.cs` | [`02-contracts.md#ports`](02-contracts.md#ports) |
| `Ports/IMetadataCache.cs` | те саме |
| `Ports/IFormulaEngine.cs` | те саме |
| `Ports/ICalculationModule.cs` | те саме |
| `Ports/IExternalDataSource.cs` | те саме |
| `Ports/IBackgroundJobScheduler.cs` | те саме |
| `Ports/ISqlCapabilities.cs` | те саме |
| `Ports/IUnitOfWork.cs` | те саме |
| `Security/EditDecision.cs` | [`02-contracts.md#access-contract`](02-contracts.md#access-contract) |
| `Security/AccessProfile.cs` | те саме |
| `Security/IAccessDecisionService.cs` | те саме |
| `Errors/EcrException.cs` | [`02-contracts.md#error-model`](02-contracts.md#error-model) |
| `Documents/Dto/PatchCellsRequest.cs` | [`02-contracts.md#dto`](02-contracts.md#dto) |
| `Documents/Dto/PatchCellsResponse.cs` | те саме |
| `Documents/Dto/CellConflictDto.cs` | те саме |
| `Documents/Dto/TableSliceDto.cs` | те саме |

⛔ **`IExternalDataSink` не створюється взагалі** (`R-A5`): запису в PI AF немає,
а порожній інтерфейс «на майбутнє» — це запрошення його колись реалізувати.

---

## 2. Порти, яких немає в контрактах

### `src/Ecr.Application/Ports/IRepository.cs`
MODULE: application | STAGE: 1
SCOPE: мінімальний доступ до агрегатів.
NOT IN SCOPE: `IQueryable` назовні — інакше деталі EF протікають у use-cases.

```csharp
namespace Ecr.Application.Ports;

/// <summary>
/// Сховище агрегата. Навмисно вузьке: <see cref="IQueryable{T}"/> назовні не
/// віддається, бо тоді деталі провайдера протікають у use-cases і
<c>ToList()</c> без <c>Take()</c> стає питанням дисципліни, а не типу.
/// </summary>
/// <typeparam name="T">Тип агрегата.</typeparam>
/// <typeparam name="TId">Тип ідентифікатора.</typeparam>
public interface IRepository<T, in TId> where T : class
{
    /// <summary>Знаходить за ідентифікатором або повертає <c>null</c>.</summary>
    Task<T?> FindAsync(TId id, CancellationToken ct);

    /// <summary>Знаходить або кидає <see cref="Errors.NotFoundException"/>.</summary>
    Task<T> GetAsync(TId id, CancellationToken ct);

    /// <summary>Додає новий агрегат.</summary>
    void Add(T entity);

    /// <summary>Позначає агрегат видаленим (фізичне видалення — лише де це дозволено).</summary>
    void Remove(T entity);
}
```

---

### `src/Ecr.Application/Ports/IAuditWriter.cs`
MODULE: application | STAGE: 1
CONTRACT: 02a-db-schema.md#aud
SCOPE: запис аудиту **пакетно**.
NOT IN SCOPE: читання аудиту — це окремий запит зі свого хендлера.

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис аудиту. Пакетний **навмисно**: окремий <c>INSERT</c> на кожну комірку
/// не вкладається в бюджет збереження діапазону (300 мс на 100 комірок).
/// </summary>
public interface IAuditWriter
{
    /// <summary>Записує зміни комірок однією операцією, у тій самій транзакції.</summary>
    Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct);

    /// <summary>Записує структурну зміну.</summary>
    Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct);

    /// <summary>Записує подію безпеки.</summary>
    Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct);

    /// <summary>Записує подію публікації з diff <b>результатів</b>, а не коду (ФВ-9.6).</summary>
    Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct);
}

/// <summary>Зміна комірки для аудиту.</summary>
/// <param name="ChangedAt">Момент зміни в UTC — партиційний ключ аудиту.</param>
/// <param name="Address">Адреса комірки.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="RowKey">Ключ рядка — щоб аудит читався без join.</param>
/// <param name="OldValue">Старе значення в текстовому вигляді.</param>
/// <param name="NewValue">Нове значення.</param>
/// <param name="ChangedByUserId">Автор (<b>не SID</b>, R-A2).</param>
/// <param name="Origin">UserEdit | Import | Recalculation | Migration.</param>
/// <param name="IsLateEdit">Зміна в <c>Grace</c> або після <c>Reopen</c> (D-70).</param>
/// <param name="CorrelationId">Наскрізний ідентифікатор запиту.</param>
public sealed record CellChangeRecord(
    DateTime ChangedAt,
    CellAddress Address,
    long DocumentId,
    string RowKey,
    string? OldValue,
    string? NewValue,
    int ChangedByUserId,
    string Origin,
    bool IsLateEdit,
    string? CorrelationId);

/// <summary>Структурна зміна метаданих.</summary>
public sealed record StructureChangeRecord(
    DateTime ChangedAt, int TemplateVersionId, string EntityType, int EntityId,
    Domain.Enums.ChangeClass ChangeClass, string Operation,
    string? OldJson, string? NewJson, string? ChangeReason, int ChangedByUserId, string? CorrelationId);

/// <summary>Подія безпеки.</summary>
public sealed record SecurityEventRecord(
    DateTime ChangedAt, string EventType, int? TargetUserId, int? TargetRoleId,
    string? DetailsJson, int ChangedByUserId, string? CorrelationId);

/// <summary>Публікація версії шаблону або методології.</summary>
public sealed record PublicationEventRecord(
    DateTime ChangedAt, string EntityType, int EntityId,
    string? ResultDiffJson, string ChangeReason, int ChangedByUserId);
```

---

### `src/Ecr.Application/Ports/IExcelExporter.cs` / `IExcelImporter.cs`
MODULE: application | STAGE: 5

```csharp
namespace Ecr.Application.Ports;

using Ecr.Application.Documents.Dto;

/// <summary>Експорт документа у <c>.xlsx</c>.</summary>
public interface IExcelExporter
{
    /// <summary>
    /// Формує книгу. Довга операція — виконується у фоні з прогресом
    /// (бюджет 10 с p95, tz/08 §8.2).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="options">Режим: тільки значення чи з формулами.</param>
    Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct);
}

/// <summary>Налаштування експорту.</summary>
/// <param name="IncludeFormulas">Транслювати наші вирази в Excel-синтаксис (ФВ-4.2).</param>
/// <param name="IncludeStyles">Переносити стилі шаблону.</param>
/// <param name="Language">Мова заголовків.</param>
public sealed record ExcelExportOptions(bool IncludeFormulas, bool IncludeStyles, string Language);

/// <summary>Імпорт із <c>.xlsx</c> — завжди через попередній перегляд diff (ФВ-4.3).</summary>
public interface IExcelImporter
{
    /// <summary>
    /// Розбирає файл і будує diff **без застосування**. Показує, що зміниться,
    /// що конфліктує і що буде відхилено правами або станом періоду.
    /// </summary>
    Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct);

    /// <summary>Застосовує раніше побудований diff після підтвердження користувачем.</summary>
    Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct);
}

/// <summary>Результат попереднього перегляду імпорту.</summary>
/// <param name="PreviewToken">Токен для застосування; діє обмежений час.</param>
/// <param name="Changes">Комірки, які зміняться.</param>
/// <param name="Rejected">Комірки, які буде відхилено, із причиною.</param>
/// <param name="Conflicts">Комірки, змінені іншим користувачем після відкриття.</param>
public sealed record ImportPreview(
    string PreviewToken,
    IReadOnlyList<ImportChange> Changes,
    IReadOnlyList<ImportRejection> Rejected,
    IReadOnlyList<CellConflictDto> Conflicts);

/// <summary>Зміна, яку принесе імпорт.</summary>
public sealed record ImportChange(string RowKey, string ColumnCode, object? OldValue, object? NewValue);

/// <summary>Відхилена комірка з причиною — користувач має бачити, які саме (ФВ-4.4).</summary>
public sealed record ImportRejection(string RowKey, string ColumnCode, string ReasonCode, string Message);
```

---

### `src/Ecr.Application/Ports/IReportSnapshotBuilder.cs`
MODULE: application | STAGE: 5

```csharp
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

/// <summary>
/// Побудова зрізу звітності. <c>rpt.*</c> — **зріз без логіки**: агрегації
/// робить сервіс тут, вʼюха лише проєктує (ФВ-0.3).
/// </summary>
public interface IReportSnapshotBuilder
{
    /// <summary>
    /// Будує зріз. Статус успадковується від даних: <c>Draft</c>, поки аркуші
    /// не затверджені (D-65) — регуляторні вʼюхи такий зріз не віддають.
    /// </summary>
    Task<long> BuildAsync(int reportVersionId, int projectId, PeriodKey? periodKey,
                          string? parametersJson, CancellationToken ct);

    /// <summary>Позначає зріз поданим — після цього він іммутабельний.</summary>
    Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct);

    /// <summary>Перераховує статус зрізу після зміни стану затвердження аркушів.</summary>
    Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct);
}
```

---

### `src/Ecr.Application/Security/IPasswordHasher.cs`
MODULE: application | STAGE: 3

```csharp
namespace Ecr.Application.Security;

/// <summary>Хешування паролів локальних облікових записів.</summary>
public interface IPasswordHasher
{
    /// <summary>Хешує пароль. Результат містить сіль і параметри алгоритму.</summary>
    string Hash(string password);

    /// <summary>Перевіряє пароль. Час виконання не має залежати від правильності.</summary>
    bool Verify(string password, string hash);

    /// <summary>Чи потрібно перехешувати через зміну параметрів алгоритму.</summary>
    bool NeedsRehash(string hash);
}
```

---

### `src/Ecr.Application/Common/ICurrentUser.cs`
MODULE: application | STAGE: 1

```csharp
namespace Ecr.Application.Common;

/// <summary>
/// Поточний користувач запиту. Реалізація в <c>Ecr.Api</c> дістає його з
/// cookie — і саме тому нижче рівня входу **не видно**, як він увійшов
/// (ФВ-6.2).
/// </summary>
public interface ICurrentUser
{
    /// <summary>Ідентифікатор; <c>null</c> для анонімного запиту.</summary>
    int? UserId { get; }

    /// <summary>Ім'я для аудиту і повідомлень.</summary>
    string? UserName { get; }

    /// <summary>Наскрізний ідентифікатор запиту.</summary>
    string CorrelationId { get; }

    /// <summary>Мова інтерфейсу для локалізації повідомлень.</summary>
    string Language { get; }
}
```

---

### `src/Ecr.Application/Common/PagedResult.cs` і `CursorPagination.cs`
MODULE: application | STAGE: 1
CONTRACT: 02-contracts.md#api-conventions

```csharp
namespace Ecr.Application.Common;

/// <summary>
/// Сторінка результатів. Ендпоінтів, що повертають «усе», не існує —
/// перевіряється архітектурним тестом.
/// </summary>
/// <param name="Items">Елементи сторінки.</param>
/// <param name="NextCursor">Курсор наступної сторінки; <c>null</c> — кінець.</param>
/// <param name="TotalCount">Загальна кількість; <c>null</c>, якщо підрахунок дорогий.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalCount);

/// <summary>Параметри курсорної пагінації.</summary>
/// <param name="Limit">Розмір сторінки: типово 50, максимум 500.</param>
/// <param name="Cursor">Курсор попередньої сторінки.</param>
public sealed record CursorRequest(int Limit = 50, string? Cursor = null)
{
    /// <summary>Максимум, більше якого запит відхиляється з <c>400</c>.</summary>
    public const int MaxLimit = 500;

    /// <summary>Перевіряє межі.</summary>
    public bool IsValid => Limit is > 0 and <= MaxLimit;
}
```

---

## 3. Use-cases: метадані

### `src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs`
MODULE: application-templates | STAGE: 1
CONTRACT: 02b-expressions.md#publish-checks
SCOPE: публікація версії з повною валідацією цілісності.
NOT IN SCOPE: сам розбір виразів — це `IFormulaEngine`.

```csharp
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Публікує версію шаблону. Це найважливіша операція конфігуратора: після неї
/// структура заморожена, а всі перевірки, які можна зробити наперед, уже
/// зроблені.
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється з переліком проблем.
/// Часткова публікація неможлива за побудовою.
/// </remarks>
public sealed class PublishTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    IFormulaEngine formulaEngine,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Виконує публікацію.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто публікує.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Валідація не пройдена; у <c>Details</c> — перелік діагностик.
    /// </exception>
    public Task PublishAsync(int templateVersionId, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) завантажити версію з усіма аркушами, таблицями, колонками, рядками, формулами; " +
            "2) виконати ВСІ 12 перевірок із 02b-expressions.md#publish-checks — синтаксис, резолвінг " +
            "посилань, типи, ациклічність графа, розкриття діапазонів у списки RowKey, набір функцій " +
            "діалекту, сигнатури, СУМІСНІСТЬ ОДИНИЦЬ (ECR-TMPL-4223), предикати динамічних діапазонів; " +
            "3) зібрати всі діагностики і, якщо є хоч одна — кинути BusinessRuleException('ECR-TMPL-0422') " +
            "зі списком, НЕ зупиняючись на першій: користувач має побачити всі проблеми одразу; " +
            "4) зберегти FormulaDependency і EvaluationOrder; " +
            "5) version.Publish(userId, clock.UtcNow); " +
            "6) audit.WritePublicationEventAsync; 7) uow.SaveChangesAsync; " +
            "8) metadataCache.InvalidateAsync.");
}
```

---

### `src/Ecr.Application/Templates/CloneTemplateVersionHandler.cs`
MODULE: application-templates | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Створює нову чернетку на основі опублікованої версії. **Єдиний спосіб**
/// внести структурну зміну після публікації (ФВ-7.1).
/// </summary>
public sealed class CloneTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Клонує версію.</summary>
    /// <param name="sourceVersionId">Версія-джерело.</param>
    /// <param name="newVersion">Номер нової версії.</param>
    /// <param name="userId">Автор.</param>
    /// <returns>Ідентифікатор створеної чернетки.</returns>
    public Task<int> CloneAsync(int sourceVersionId, string newVersion, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: глибоко скопіювати аркуші, таблиці, колонки, рядки, стилі, формули, " +
            "правила валідації, зв'язки таблиць і правила періодів; " +
            "ClonedFromVersionId = sourceVersionId; Status = Draft; PresentationRevision = 0; " +
            "нові Id призначає БД. Дані документів НЕ копіюються — вони лишаються на старій версії " +
            "до явної міграції (ФВ-7.7).");
}
```

---

### `src/Ecr.Application/Templates/PatchPresentationHandler.cs`
MODULE: application-templates | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Патчить презентаційний шар **опублікованої** версії «на льоту»: підписи,
/// стилі, <c>Ordinal</c>, формати (ФВ-7.2).
/// </summary>
/// <remarks>
/// Це та операція, заради якої існує <c>PresentationRevision</c>: користувач
/// може виправити підпис колонки без клонування версії і без міграції даних.
/// </remarks>
public sealed class PatchPresentationHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Застосовує презентаційні зміни.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="patchJson">Перелік змін у форматі <c>{entityType, entityId, field, value}</c>.</param>
    /// <param name="userId">Автор.</param>
    /// <returns>Нове значення <c>PresentationRevision</c>.</returns>
    /// <exception cref="Errors.BusinessRuleException">
    /// Серед змін є структурна — <c>ECR-TMPL-0409</c>.
    /// </exception>
    public Task<int> PatchAsync(int templateVersionId, string patchJson, int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) розібрати patchJson; " +
            "2) для КОЖНОЇ зміни викликати classifier.Classify і переконатися, що клас = Presentation; " +
            "   будь-що інше → BusinessRuleException('ECR-TMPL-0409') з переліком порушень; " +
            "3) застосувати зміни; " +
            "4) інкрементувати PresentationRevision ОДНИМ statement з OUTPUT (R-B7) — " +
            "   read-modify-write у застосунку заборонений, бо інстансів ≥2; " +
            "5) version.ApplyPresentationRevision(нове значення); " +
            "6) audit.WriteStructureChangeAsync з ChangeClass.Presentation.");
}
```

---

## 4. Use-cases: документи

### `src/Ecr.Application/Documents/PatchCellsHandler.cs`
MODULE: application-documents | STAGE: 1
CONTRACT: 02-contracts.md#dto
SCOPE: **найгарячіший шлях запису в системі**.
NOT IN SCOPE: перерахунок формул — він ставиться в чергу, а не робиться тут.

```csharp
using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Documents;

/// <summary>
/// Пакетна зміна комірок. Бюджет — **p95 300 мс на 100 комірок**
/// (tz/08 §8.2), тому кожна зайва дія тут коштує дорого.
/// </summary>
/// <remarks>
/// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
/// весь батч. «Перезаписати мовчки» не є опцією — користувач має побачити
/// розбіжність (B04 §2.3).
/// </remarks>
public sealed class PatchCellsHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    Validation.ValidationEngine validation,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Застосовує зміни.</summary>
    /// <exception cref="Errors.ConcurrencyConflictException">
    /// Розбіжність <c>baseVersion</c> — <c>ECR-CELL-0409</c>.
    /// </exception>
    /// <exception cref="Errors.AccessDeniedException">
    /// Хоч одна комірка недоступна — <c>ECR-ACCS-0403</c> із причиною.
    /// </exception>
    /// <exception cref="Errors.BusinessRuleException">
    /// Комірковий <c>Error</c> валідації — <c>ECR-CELL-0422</c>.
    /// </exception>
    public Task<PatchCellsResponse> HandleAsync(PatchCellsRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — порядок критичний для бюджету 300 мс:\n" +
            "1) метадані з IMetadataCache (без звернення до БД);\n" +
            "2) AccessProfile з кешу сесії; access.CanEditSliceAsync ОДНИМ викликом — " +
            "   поштучна перевірка комірок не вкладається в бюджет;\n" +
            "3) кожен рядок із BaseVersion = null трактувати як СТВОРЕННЯ (R-B2): RowKey обов'язковий, " +
            "   дублікат → ECR-ROW-0409;\n" +
            "4) звірити baseVersion решти рядків; будь-яка розбіжність → зібрати ВСІ конфлікти " +
            "   і кинути ConcurrencyConflictException — увесь батч відхиляється;\n" +
            "5) валідація: комірковий Error блокує (ECR-CELL-0422), рівні рядка й вище — ні (R-B3);\n" +
            "6) розкласти зміни на три операції (R-B4): значення → upsert, null → delete, " +
            "   isEmpty → upsert з IsEmpty = 1;\n" +
            "7) ОДНА транзакція: cellStore.ApplyAsync + audit.WriteCellChangesAsync ПАКЕТНО + " +
            "   підняти ModifiedAt зачеплених рядків (інакше RowVersion не зміниться і " +
            "   оптимістичне блокування тихо не працює);\n" +
            "8) поза транзакцією: поставити dirty-set у чергу перерахунку через jobs;\n" +
            "9) повернути нові RowVersion і незаблокувальні повідомлення валідації.");
}
```

---

### `src/Ecr.Application/Documents/GetTableSliceHandler.cs`
MODULE: application-documents | STAGE: 1

```csharp
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Зріз таблиці для grid. **Найважчий регулярний запит системи**: бюджет
/// p95 1.5 с на 500×60, з яких 600 мс — вибірка з SQL (tz/08 §8.2).
/// </summary>
public sealed class GetTableSliceHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access)
{
    /// <summary>Читає зріз.</summary>
    public Task<TableSliceDto> HandleAsync(long documentId, long tableInstanceId,
                                           AccessProfile profile, string language, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) метадані таблиці з кешу; 2) cellStore.ReadSliceAsync — ОДИН запит; " +
            "3) access.CanEditSliceAsync — ОДИН виклик на весь зріз; " +
            "4) спроєктувати в TableSliceDto: порожні комірки НЕ включати (ФВ-3.8), " +
            "клієнт візьме DefaultValue; " +
            "5) CellPermissions — компактна мапа 'rowKey:columnCode' → причина заборони, " +
            "щоб grid одразу знав, що read-only, а що приховати. " +
            "Заборонено: N+1, звернення до БД за метаданими, поштучна перевірка прав.");
}
```

---

### `src/Ecr.Application/Documents/CreateRowHandler.cs`
MODULE: application-documents | STAGE: 1

```csharp
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Додає рядок у динамічну таблицю.</summary>
public sealed class CreateRowHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Створює рядок і повертає його ключ.</summary>
    /// <exception cref="Errors.BusinessRuleException">
    /// Таблиця не дозволяє динамічні рядки, перевищено <c>MaxDynamicRows</c>,
    /// або <c>RowKey</c> уже існує (<c>ECR-ROW-0409</c>).
    /// </exception>
    public Task<RowKey> HandleAsync(long documentId, long tableInstanceId, RowKey? requestedKey,
                                    AccessProfile profile, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити TableDef.AllowsDynamicRows; " +
            "2) перевірити MaxDynamicRows проти поточної кількості; " +
            "3) ключ: requestedKey або RowKey.NewDynamic() — GUID у форматі 'N' (ФВ-2.5); " +
            "4) Id узяти з SEQUENCE doc.TableRowSeq ДО вставки — це дозволяє " +
            "завантажити рядок і комірки одним проходом; " +
            "5) Ordinal = max + 1; 6) перевірити унікальність (PeriodKey, TableInstanceId, RowKey).");
}
```

---

### `src/Ecr.Application/Documents/ValidateDocumentHandler.cs`
MODULE: application-documents | STAGE: 2

```csharp
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Повна валідація документа перед поданням (ФВ-5.1).</summary>
public sealed class ValidateDocumentHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    ValidationEngine engine,
    IUnitOfWork uow)
{
    /// <summary>Виконує валідацію всіх аркушів документа за період.</summary>
    /// <returns>Повідомлення трьох рівнів; наявність <c>Error</c> блокує <c>Submit</c>.</returns>
    public Task<IReadOnlyList<ValidationMessage>> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прогнати правила рівнів Cell/Row/Table/Document по всіх таблицях документа; " +
            "зберегти підсумок у wf.ValidationResult; повернути повний перелік. " +
            "Бюджет — 3 с p95 на весь документ, тому читати дані пакетно, а не по таблиці.");
}
```

---

## 5. Валідація і перерахунок

### `src/Ecr.Application/Validation/ValidationMessage.cs`
MODULE: application-validation | STAGE: 2

```csharp
using Ecr.Domain.Enums;

namespace Ecr.Application.Validation;

/// <summary>Повідомлення валідації.</summary>
/// <param name="Severity">Рівень.</param>
/// <param name="RuleCode">Код правила з <c>cfg.ValidationRule</c>.</param>
/// <param name="Message">Локалізований текст.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="RowKey">Рядок; <c>null</c> — рівень таблиці або документа.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — рівень рядка і вище.</param>
/// <param name="BlocksSave">
/// Чи блокує збереження. <c>true</c> **лише** для коміркового <c>Error</c> (R-B3).
/// </param>
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string RuleCode,
    string Message,
    int TableDefId,
    string? RowKey,
    string? ColumnCode,
    bool BlocksSave);
```

---

### `src/Ecr.Application/Validation/ValidationEngine.cs`
MODULE: application-validation | STAGE: 2

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Validation;

/// <summary>
/// Виконує правила валідації. Рівні розрізняються не за суворістю тексту, а
/// за тим, що вони блокують (R-B3): комірковий <c>Error</c> блокує запис,
/// решта — лише подання.
/// </summary>
public sealed class ValidationEngine(IFormulaEngine formulaEngine)
{
    /// <summary>Перевіряє одну комірку — виконується синхронно на шляху запису.</summary>
    public IReadOnlyList<ValidationMessage> ValidateCell(
        ColumnDef column, Domain.ValueObjects.CellValueData value, IReadOnlyList<ValidationRule> rules)
        => throw new NotImplementedException(
            "TODO: спершу структурна перевірка column.ValidateValue (тип, обов'язковість, довідник, " +
            "одиниця), потім правила Scope = 0. Повертати ВСІ порушення, не перше.");

    /// <summary>Перевіряє рядок, таблицю або документ — не блокує запис.</summary>
    public IReadOnlyList<ValidationMessage> ValidateScope(
        byte scope, IReadOnlyList<ValidationRule> rules, IValidationContext context)
        => throw new NotImplementedException(
            "TODO: обчислити вираз кожного правила через formulaEngine; " +
            "FALSE → повідомлення відповідного рівня. Помилка обчислення виразу — " +
            "це Warning про несправне правило, а не Error даних: інакше зламане правило " +
            "заблокує роботу з коректними даними.");
}

/// <summary>Контекст для правил рівня рядка і вище.</summary>
public interface IValidationContext
{
    /// <summary>Значення комірки поточного рядка.</summary>
    object? GetCell(string columnCode);

    /// <summary>Значення комірки конкретного рядка таблиці.</summary>
    object? GetCell(string rowKey, string columnCode);
}
```

---

### `src/Ecr.Application/Recalculation/DirtySet.cs`
MODULE: application-recalc | STAGE: 2

```csharp
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Набір комірок, які треба перерахувати. Інкрементний перерахунок — не
/// оптимізація, а вимога: перераховувати весь документ на кожну зміну означає
/// вийти з бюджету на порядок.
/// </summary>
public sealed class DirtySet
{
    private readonly HashSet<CellAddress> _cells = [];

    /// <summary>Додає змінену комірку.</summary>
    public void Add(CellAddress address) => _cells.Add(address);

    /// <summary>Комірки, з яких починається розкриття графа.</summary>
    public IReadOnlyCollection<CellAddress> Seeds => _cells;

    /// <summary>Чи є що перераховувати.</summary>
    public bool IsEmpty => _cells.Count == 0;
}
```

---

### `src/Ecr.Application/Recalculation/RecalculationService.cs`
MODULE: application-recalc | STAGE: 2

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Інкрементний перерахунок формул шаблону.
/// </summary>
/// <remarks>
/// Результати **формул шаблону** матеріалізуються в <c>doc.CellValue</c> з
/// <c>IsCalculated = 1</c>. Результати **методологій** сюди не потрапляють —
/// вони живуть у <c>calc.CalculationResult</c> (D-69). Плутати ці два шляхи
/// не можна.
/// </remarks>
public sealed class RecalculationService(
    ICellStore cellStore,
    IMetadataCache metadata,
    IFormulaEngine formulaEngine)
{
    /// <summary>Перераховує залежне піддерево.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="dirty">Змінені комірки.</param>
    public Task RecalculateAsync(long documentId, PeriodKey periodKey, DirtySet dirty, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) за зворотним індексом cfg.FormulaDependency знайти формули, залежні від seeds; " +
            "2) розкрити транзитивно, поки набір не перестане рости; " +
            "3) відсортувати за EvaluationOrder (він уже обчислений при Publish — не сортувати граф тут); " +
            "4) обчислити і записати з IsCalculated = 1 пакетно; " +
            "5) крос-аркушні rollup позначити брудними і перерахувати ВІДКЛАДЕНО (debounce ~300 мс), " +
            "а не синхронно — інакше зміна однієї комірки тягне ланцюг по всьому документу; " +
            "6) формули з IsSnapshot не перераховувати каскадом ніколи.");
}
```

---

## 6. Решта use-cases

Усі наведені **повністю** в §7 нижче. Таблиця лишається покажчиком
«файл → етап → ключова вимога»; спільний зразок той самий: конструктор із
портами, один публічний асинхронний метод, XML-doc українською, тіло —
`NotImplementedException` зі змістовним TODO.

| Файл | STAGE | Ключова вимога, яку описує TODO |
|---|---|---|
| `Templates/CreateTemplateVersionHandler.cs` | 1 | створення чернетки; `Code` валідується `EcrCode` |
| `Templates/DiffTemplateVersionsHandler.cs` | 1 | diff двох версій із класифікацією змін |
| `Templates/GetTemplateStructureHandler.cs` | 1 | структура з кешу, без звернення до БД |
| `Documents/CreateDocumentHandler.cs` | 1 | склад документа за `SheetGroupRule`; `BusinessKey` із колонок `IsBusinessKey` |
| `Periods/BuildPeriodCalendarHandler.cs` | 3 | календар за `PeriodKind`; `PeriodKey = Year*100 + Sequence`; межі в поясі майданчика |
| `Periods/SetCurrentPeriodHandler.cs` | 3 | `Pinned` вимагає причини; **не впливає на доступ** (D-77) |
| `Periods/ReopenPeriodHandler.cs` | 3 | `Closed → Grace`; право `Period.Reopen`; для архівного року — розархівація |
| `Workflow/SubmitSheetHandler.cs` | 3 | гранулярність аркуш × період; блокує наявність `Error`; створює `SubmissionSnapshot` |
| `Workflow/ApproveSheetHandler.cs` | 3 | маршрут погодження; оновлює статус зрізів `rpt.*` |
| `Workflow/ReopenDocumentHandler.cs` | 3 | поданий → `Draft`; неможливо при `Closed` періоді (`ECR-PRD-4223`) |
| `Registries/GetRegistryEntriesHandler.cs` | 4 | резолвінг «станом на дату періоду»; каскад; кеш за `DataRevision` |
| `Registries/UpsertRegistryEntryHandler.cs` | 4 | заборона фізичного видалення при посиланнях (`ECR-REG-0409`); `BumpDataRevision` |
| `Registries/RegistryResolver.cs` | 4 | темпоральний вибір запису; фільтр і каскад |
| `Units/ConvertUnitHandler.cs` | 4 | делегує `UnitConverter`; різні розмірності → `ECR-UOM-0422` |
| `Calculations/RunCalculationHandler.cs` | 4 | топологічний порядок; **бюджет повного року ≤ 10 хв** (ПРД-13) |
| `Calculations/PublishMethodologyHandler.cs` | 4 | diff **результатів** на золотому наборі; чотири очі (`ECR-CALC-0409`) |
| `Calculations/SimulateMethodologyHandler.cs` | 4 | прогін без запису результату (ФВ-13.5) |
| `Localization/GetUiStringsHandler.cs` | 3 | увесь каталог мови + `Revision` як `ETag`; fallback на мову за замовчуванням; ключі `err.<код>` — звідси ж (ФВ-14.9a) |
| `Localization/SetUiStringHandler.cs` | 3 | запис рядка + інкремент `UiStringRevision` **одним statement із `OUTPUT`**, як `PresentationRevision` (R-B7) |
| `Security/StartSimulationHandler.cs` | 3 | причина обов'язкова; `ActorUserId <> SubjectUserId`; запис `aud.SimulationSession` **до** видачі профілю |
| `Security/EndSimulationHandler.cs` | 3 | проставляє `EndedAt`; завершити чужий сеанс не можна |
| `Security/ChangePasswordHandler.cs` | 3 | знімає `MustChangePassword`; оновлює `SecurityStamp` → усі інші сеанси падають |
| `Security/EnsureBootstrapAdminHandler.cs` | 3 | **лише перший старт** (D-115): читає `ECR_Bootstrap__Password`, створює локального адміністратора з `MustChangePassword = 1`. Запис існує → нічого не робить; змінної немає → попередження, не помилка. Пароль не логується (ФВ-6.11) |
| `Security/DisableBootstrapAdminHandler.cs` | 3 | викликається після появи активного доменного адміністратора; `IsActive = 0`, запис **не видаляється** (D-97) |
| `Registries/SetEntryValidityHandler.cs` | 4 | зміна вікна дії → `IOrphanScanner.RescanForEntryAsync` у тій самій транзакції (ФВ-8.13a) |

---

## 7. Решта use-cases — повний текст

> Двадцять п'ять файлів. Усі в просторі імен, що збігається зі шляхом; усі
> залежності — через порти з [`02-contracts.md`](02-contracts.md#ports).

### `src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs`
MODULE: application-templates | STAGE: 1

```csharp
// src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>Створює чернетку версії шаблону — порожню або клоном (ФВ-2.8).</summary>
public sealed class CreateTemplateVersionHandler(
    IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(int templateId, string versionNumber, int? cloneFromVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) versionNumber валідувати як Major.Minor.Patch.Build;\n" +
            "2) коди елементів — через EcrCode (D-89): регекс ^[A-Za-z][A-Za-z0-9_]{0,63}$;\n" +
            "3) при cloneFromVersionId — глибокий клон зі ЗБЕРЕЖЕННЯМ RowKey і Code " +
            "   (ФВ-2.8): нові Id, старі ідентичності. Інакше формули клону " +
            "   почнуть посилатися в порожнечу;\n" +
            "4) Status = Draft; PresentationRevision = 0.");
}
```

### `src/Ecr.Application/Templates/DiffTemplateVersionsHandler.cs`
MODULE: application-templates | STAGE: 1

```csharp
// src/Ecr.Application/Templates/DiffTemplateVersionsHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Templates;

/// <summary>
/// Diff двох версій за **ідентичністю**, не за позицією (ФВ-2.7, АРХ-2).
/// </summary>
/// <remarks>
/// Порівняння за `Ordinal` дало б «змінено все» після будь-якого
/// перевпорядкування — саме та хиба, через яку в чинному рішенні неможливо
/// зрозуміти, що насправді змінилося.
/// </remarks>
public sealed class DiffTemplateVersionsHandler(IMetadataCache metadata)
{
    public Task<TemplateDiffDto> HandleAsync(int fromVersionId, int toVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) зіставляти елементи за Code (колонки, аркуші, таблиці) і RowKey (рядки);\n" +
            "2) класифікувати кожну зміну: Compatible / Migratable / Breaking (ФВ-7.3);\n" +
            "3) окремо позначити презентаційні зміни — вони не потребують нової версії (ФВ-7.2);\n" +
            "4) повернути також ВПЛИВ: скільки документів прив'язано до fromVersionId.");
}
```

### `src/Ecr.Application/Templates/GetTemplateStructureHandler.cs`
MODULE: application-templates | STAGE: 1

```csharp
// src/Ecr.Application/Templates/GetTemplateStructureHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Templates;

/// <summary>
/// Структура опублікованої версії — **з кешу, без звернення до БД**.
/// Ключ `v{id}:r{rev}` (ФВ-2.5) робить інвалідацію непотрібною: інша
/// ревізія — інший ключ.
/// </summary>
public sealed class GetTemplateStructureHandler(IMetadataCache metadata)
{
    public Task<TemplateStructureDto> HandleAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: metadata.GetStructureAsync за ключем v{id}:r{rev}. " +
            "Промах кешу — прогрів із БД одним запитом на всю версію, не по аркушах. " +
            "Звернення до DbContext звідси заборонене архітектурним тестом.");
}
```

### `src/Ecr.Application/Documents/CreateDocumentHandler.cs`
MODULE: application-documents | STAGE: 1

```csharp
// src/Ecr.Application/Documents/CreateDocumentHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Documents;

/// <summary>Створює документ і його склад аркушів (ФВ-3.1, ФВ-3.2).</summary>
/// <remarks>
/// Документ **наскрізний по періодах**: період живе на рядках і комірках, а не
/// тут. Унікальність — `(ProjectId, BusinessKey)`.
/// </remarks>
public sealed class CreateDocumentHandler(
    IMetadataCache metadata, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(int projectId, int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити склад за SheetGroupRule: RequiresAll / RequiresOne / " +
            "   Optional; порушення → ECR-DOC-0422;\n" +
            "2) BusinessKey скласти зі значень колонок IsBusinessKey; дублікат у " +
            "   межах проєкту → ECR-DOC-0409;\n" +
            "3) ⛔ статусу документа НЕ ставити — його не існує (D-93). Робочий стан " +
            "   з'явиться в wf.ApprovalState при першому Submit;\n" +
            "4) TableInstance створювати ліниво, при першому записі в період, " +
            "   а не одразу на всі 12: більшість документів заповнюють не всі.");
}
```

### `src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs`
MODULE: application-periods | STAGE: 3

```csharp
// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Periods;

/// <summary>Будує календар періодів проєкту (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarHandler(IUnitOfWork uow, IClock clock)
{
    public Task<int> HandleAsync(int projectId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) кількість періодів за PeriodKind: Monthly = 12, Quarterly = 4, " +
            "   Yearly = 1, Custom = задано вручну;\n" +
            "2) PeriodKey = Year*100 + Sequence; Sequence ЛИШЕ 1..12 (D-108), " +
            "   інакше ECR-PRD-4224 — верхня межа не довільна, вона збігається " +
            "   з межами партиційної функції;\n" +
            "3) межі рахувати опівночі В ПОЯСІ МАЙДАНЧИКА, потім у UTC (D-68). " +
            "   DateTime.Now заборонений, час лише з IClock;\n" +
            "4) RecomputeBoundaries за PeriodPolicy проєкту;\n" +
            "5) ідемпотентність: повторний виклик не створює дублікатів.");
}
```

### `src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs`
MODULE: application-periods | STAGE: 3

```csharp
// src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Periods;

/// <summary>
/// Поточний період проєкту: `Auto` або `Pinned` (ФВ-1.13, D-77).
/// </summary>
/// <remarks>
/// ⚠ Використовується **лише як значення за замовчуванням** — які період
/// підставити в документ, розклад чи параметр звіту. **На рішення про доступ
/// не впливає ніколи** (ФВ-1.14): інакше `Pinned` став би способом обійти
/// закриття періоду.
/// </remarks>
public sealed class SetCurrentPeriodHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(int projectId, int? pinnedPeriodId, string? reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) pinnedPeriodId = null → режим Auto, веде PeriodStateJob;\n" +
            "2) інакше режим Pinned: reason ОБОВ'ЯЗКОВИЙ, період має належати проєкту;\n" +
            "3) запис у aud.StructureChange: хто, коли, з чого на що, чому;\n" +
            "4) ⛔ жодних перевірок доступу на основі цього значення — ані тут, " +
            "   ані деінде (ФВ-1.14).");
}
```

### `src/Ecr.Application/Periods/ReopenPeriodHandler.cs`
MODULE: application-periods | STAGE: 3

```csharp
// src/Ecr.Application/Periods/ReopenPeriodHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Periods;

/// <summary>
/// Адміністративне відкриття закритого періоду (ФВ-1.10). Право
/// <c>Period.Reopen</c> — небезпечне, у складені ролі не входить (ФВ-6.12).
/// </summary>
public sealed class ReopenPeriodHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(int periodId, string reason, DateTime? until, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) взяти doc.Period з UPDLOCK і перечитати стан У ТРАНЗАКЦІЇ — " +
            "   інакше PeriodStateJob може закрити період посеред операції (ФВ-1.10a);\n" +
            "2) Archived НЕ відкривається: це кінцевий стан (ФВ-1.10);\n" +
            "3) reason обов'язковий; until — необов'язковий, за замовчуванням " +
            "   до кінця доби в поясі майданчика;\n" +
            "4) Closed → Grace, а не → Open: правки після закриття лишаються " +
            "   пізніми і мають позначатися IsLateEdit (D-70);\n" +
            "5) аудит із причиною.");
}
```

### `src/Ecr.Application/Workflow/SubmitSheetHandler.cs`
MODULE: application-workflow | STAGE: 3

```csharp
// src/Ecr.Application/Workflow/SubmitSheetHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>
/// Подання аркуша за період — гранулярність `аркуш × період` (D-38).
/// Створює **іммутабельний зріз** вхідних даних (ФВ-5.7, ФВ-9.4).
/// </summary>
public sealed class SubmitSheetHandler(
    ICellStore cellStore,
    IAccessDecisionService access,
    Validation.ValidationEngine validation,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) доступ рівня Submit на аркуш (ФВ-6.13);\n" +
            "2) повна валідація: БУДЬ-ЯКИЙ Error будь-якого рівня блокує (ФВ-5.19), " +
            "   на відміну від запису, де блокує лише комірковий (D-90);\n" +
            "3) рядки з IsOrphaned блокують подання → ECR-SUB-4221 (ФВ-8.13).\n" +
            "   ⚠ До Етапу 4 прапорець ніхто не ставить — перевірка коректна і " +
            "   завжди пропускає; це не несправність;\n" +
            "4) створити SubmissionSnapshot: зліпок значень + версії шаблону, " +
            "   методологій і реєстрів + контрольна сума;\n" +
            "5) wf.ApprovalState: Draft → Submitted, зафіксувати автора і час;\n" +
            "6) оновити статус зрізів rpt.* (ФВ-10.10).");
}
```

### `src/Ecr.Application/Workflow/ApproveSheetHandler.cs`
MODULE: application-workflow | STAGE: 3

```csharp
// src/Ecr.Application/Workflow/ApproveSheetHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>Затвердження або відхилення аркуша за маршрутом (ФВ-5.13…ФВ-5.17).</summary>
public sealed class ApproveSheetHandler(
    IAccessDecisionService access, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, bool approved, string? reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) доступ рівня Approve; поточний крок маршруту має відповідати " +
            "   ролі користувача (ФВ-5.17);\n" +
            "2) approved = false → Status = Draft, reason ОБОВ'ЯЗКОВИЙ (ФВ-5.15);\n" +
            "3) approved = true → якщо є наступний крок, перейти до нього, інакше " +
            "   Status = Approved;\n" +
            "4) при Approved дані стають дійсними (ФВ-5.14) — оновити статус зрізу rpt.*;\n" +
            "5) ⛔ статус документа не чіпати: його немає (D-93).");
}
```

### `src/Ecr.Application/Workflow/ReopenDocumentHandler.cs`
MODULE: application-workflow | STAGE: 3

```csharp
// src/Ecr.Application/Workflow/ReopenDocumentHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>
/// Повернення поданого аркуша в <c>Draft</c> для правки (ФВ-5.20a, D-67).
/// </summary>
/// <remarks>
/// Повторне подання створює **новий** зріз; старий лишається `Submitted`
/// назавжди і не перераховується ніколи (ФВ-9.17). Саме тому правка поданої
/// форми — окрема дія з причиною, а не просто редагування.
/// </remarks>
public sealed class ReopenDocumentHandler(
    IAccessDecisionService access, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, string reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) право Document.Reopen (небезпечне, ФВ-6.12);\n" +
            "2) взяти doc.Period з UPDLOCK: при Closed → ECR-PRD-4223. " +
            "   Спершу Reopen ПЕРІОДУ, потім аркуша (ФВ-5.20a);\n" +
            "3) reason обов'язковий;\n" +
            "4) ApprovalState.Reopen(...): Submitted/Approved → Draft;\n" +
            "5) ⛔ старий SubmissionSnapshot НЕ чіпати і не позначати недійсним — " +
            "   він доказова база того, що було подано (ФВ-5.7);\n" +
            "6) усі подальші зміни позначати IsLateEdit = 1 (D-70).");
}
```

### `src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs`
MODULE: application-registries | STAGE: 4

```csharp
// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Registries;

/// <summary>
/// Записи довідника **станом на дату періоду**, а не «активні зараз»
/// (ФВ-8.5).
/// </summary>
/// <remarks>
/// Різниця принципова: документ за березень має бачити дозволи, чинні в
/// березні, навіть якщо сьогодні жовтень і половина з них уже недійсна.
/// </remarks>
public sealed class GetRegistryEntriesHandler(IMetadataCache metadata, RegistryResolver resolver)
{
    public Task<IReadOnlyList<RegistryEntryDto>> HandleAsync(
        string registryCode, DateOnly asOf, long? parentEntryId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) резолвити через RegistryResolver: темпоральність + каскад;\n" +
            "2) parentEntryId фільтрує каскадом (ФВ-8.4): WaterBody звужується " +
            "   вибраним Permit;\n" +
            "3) кеш за ключем {registryCode}:{DataRevision}:{asOf}; DataRevision " +
            "   інкрементує UpsertRegistryEntryHandler — та сама схема, що з " +
            "   метаданими (ФВ-2.5);\n" +
            "4) віддавати Id і Display; у комірці зберігається Id (ФВ-8.8).");
}
```

### `src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs`
MODULE: application-registries | STAGE: 4

```csharp
// src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Registries;

/// <summary>Створення і зміна запису довідника (ФВ-8.6, ФВ-8.7).</summary>
public sealed class UpsertRegistryEntryHandler(
    ICellStore cellStore, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(RegistryEntryUpsertDto dto, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) Code валідувати як EcrCode (D-89);\n" +
            "2) значення полів — типізовано за RegistryFieldDef.DataType, не рядком;\n" +
            "3) ⛔ фізичне видалення заборонене, якщо на запис посилаються дані → " +
            "   ECR-REG-0409. Це перевіряє FK у базі (ФВ-8.7), а не задача вночі;\n" +
            "4) BumpDataRevision — інакше кеш віддаватиме старі записи;\n" +
            "5) зміна ВІКНА ДІЇ йде не сюди, а в SetEntryValidityHandler: вона " +
            "   тягне перерахунок IsOrphaned.");
}
```

### `src/Ecr.Application/Registries/RegistryResolver.cs`
MODULE: application-registries | STAGE: 4

```csharp
// src/Ecr.Application/Registries/RegistryResolver.cs
namespace Ecr.Application.Registries;

/// <summary>
/// Темпоральний вибір записів довідника з урахуванням каскаду і фільтра.
/// Виділений окремо, бо потрібен і в UI, і у валідації, і в резолвінгу
/// посилань виразів — три місця з однаковим правилом.
/// </summary>
public sealed class RegistryResolver
{
    /// <summary>Чинні на дату записи з урахуванням батьківського вибору.</summary>
    public IReadOnlyList<long> Resolve(int registryDefId, DateOnly asOf, long? parentEntryId)
        => throw new NotImplementedException(
            "TODO: 1) фільтр IsValidOn(asOf): межі ВКЛЮЧНІ з обох боків;\n" +
            "2) IsActive і не IsDeleted;\n" +
            "3) каскад: якщо задано parentEntryId — лише записи з відповідним " +
            "   зв'язком у RegistryEntryLink;\n" +
            "4) порядок — за Ordinal, потім за Code: стабільний вивід потрібен, " +
            "   щоб список у UI не «стрибав» між запитами.");
}
```

### `src/Ecr.Application/Registries/SetEntryValidityHandler.cs`
MODULE: application-registries | STAGE: 4

```csharp
// src/Ecr.Application/Registries/SetEntryValidityHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Registries;

/// <summary>
/// Зміна вікна чинності запису довідника — і **негайний** перерахунок
/// <c>IsOrphaned</c> на рядках, що на нього посилаються (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// Перерахунок тут, а не вночі, з однієї причини: той, хто звузив вікно, має
/// одразу побачити наслідок. Нічна перевірка лишається — вона ловить те, що
/// змінилося іншими шляхами.
/// </remarks>
public sealed class SetEntryValidityHandler(
    IOrphanScanner scanner, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(long registryEntryId, DateOnly? from, DateOnly? to, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) entry.SetValidity(from, to);\n" +
            "2) У ТІЙ САМІЙ транзакції — scanner.RescanForEntryAsync(registryEntryId);\n" +
            "3) сканер працює В ОБИДВА боки: звуження ставить ознаку, розширення " +
            "   ЗНІМАЄ. Без другого виправлення довідника не розблокує Submit;\n" +
            "4) повернути кількість зачеплених рядків — користувач має бачити " +
            "   масштаб того, що щойно зробив;\n" +
            "5) аудит зміни вікна.");
}
```

### `src/Ecr.Application/Units/ConvertUnitHandler.cs`
MODULE: application-units | STAGE: 4

```csharp
// src/Ecr.Application/Units/ConvertUnitHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Units;

/// <summary>
/// Явна конверсія одиниць. **Неявних конверсій не буває** (ФВ-16.4, D-74):
/// рушій перетворює величину лише за викликом <c>CONVERT</c> у виразі або за
/// правилом мапінгу інтеграції.
/// </summary>
public sealed class ConvertUnitHandler(Ecr.Domain.Services.UnitConverter converter)
{
    public Task<decimal> HandleAsync(decimal value, string fromUnit, string toUnit, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) різні розмірності → ECR-UOM-0422 (ФВ-16.3). НЕ шукати шлях " +
            "   «через базу»: маса в об'єм не переводиться без щільності, а " +
            "   щільність — контекстний коефіцієнт, не конверсія (ФВ-16.5);\n" +
            "2) однакові одиниці → повернути значення без арифметики: множення " +
            "   на 1.0 у decimal дає зайве заокруглення;\n" +
            "3) маршрут from → base → to через FactorToBase;\n" +
            "4) арифметика лише в decimal, ніколи в double (ФВ-9.11).");
}
```

### `src/Ecr.Application/Calculations/RunCalculationHandler.cs`
MODULE: application-calculations | STAGE: 4

```csharp
// src/Ecr.Application/Calculations/RunCalculationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Calculations;

/// <summary>
/// Прогін розрахунку. **Бюджет повного року — 10 хвилин** (ПРД-13, D-63)
/// проти 20 у чинній системі.
/// </summary>
/// <remarks>
/// Це вимога, а не результат заміру: профіль по модулях (`J-1`) показує,
/// звідки взяти різницю. Тому <c>ModulesProfileJson</c> заповнюється завжди,
/// а не лише в діагностичному режимі.
/// </remarks>
public sealed class RunCalculationHandler(
    ICalculationModule module,
    IUnitOfWork uow,
    IBackgroundJobScheduler jobs,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task<long> HandleAsync(int projectId, int? periodKey, int? triggeredByUserId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) ⚠ ЗАКРИТІ ПЕРІОДИ автоматично не перераховувати НІКОЛИ " +
            "   (ФВ-9.7): без окремого погодження → ECR-CALC-4221;\n" +
            "2) ⚠ зріз із IsSubmitted = 1 не перераховується взагалі (ФВ-9.17): " +
            "   потреба змінити подану цифру закривається Reopen, а не перерахунком;\n" +
            "3) версія методології — за датою періоду, не за 'поточною' (ФВ-9.3);\n" +
            "4) порядок формул — топологічний, узятий із публікації, не будувати " +
            "   граф щоразу (ФВ-9.4);\n" +
            "5) NumericMode версії задає МОМЕНТ округлення: Legacy — після кожної " +
            "   операції, Strict — на виході (ФВ-9.16a);\n" +
            "6) паралелізм по незалежних гілках графа; ізоляція від інтерактивного " +
            "   піку обов'язкова — 10 хвилин перерахунку не мають з'їсти p95 операторів;\n" +
            "7) заповнити ModulesProfileJson;\n" +
            "8) перемикання IsCurrent — ОДНА транзакція (ФВ-9.11).");
}
```

### `src/Ecr.Application/Calculations/PublishMethodologyHandler.cs`
MODULE: application-calculations | STAGE: 4

```csharp
// src/Ecr.Application/Calculations/PublishMethodologyHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Calculations;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона змінює числа, які вже подані.
/// </summary>
/// <remarks>
/// Тому тут diff **результатів** на золотому наборі, а не diff коду (ФВ-9.6):
/// змінений рядок формули нічого не каже, а змінена на 4 % емісія — каже все.
/// </remarks>
public sealed class PublishMethodologyHandler(
    ICalculationModule module,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task HandleAsync(int methodologyVersionId, string changeReason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) чотири очі: публікує НЕ автор останньої правки → ECR-CALC-0409 (D-40);\n" +
            "2) зелений тест обов'язковий → інакше ECR-CALC-0422 (ФВ-9.12);\n" +
            "3) перевірка одиниць: несумісні величини без CONVERT → ECR-TMPL-4223 (ФВ-16.6);\n" +
            "4) перетин і покриття правил прив'язки (ФВ-13.9);\n" +
            "5) топологічний порядок формул → SetEvaluationOrder; цикл → відмова;\n" +
            "6) DIFF РЕЗУЛЬТАТІВ на золотому наборі; NumericMode і CalendarMode " +
            "   ОБОВ'ЯЗКОВО в diff (ФВ-7.8) — їх зміна тихо змінює всі числа;\n" +
            "7) changeReason обов'язковий (ФВ-14.7);\n" +
            "8) ⛔ закриті періоди не перераховувати автоматично навіть після " +
            "   публікації (ФВ-9.7).");
}
```

### `src/Ecr.Application/Calculations/SimulateMethodologyHandler.cs`
MODULE: application-calculations | STAGE: 4

```csharp
// src/Ecr.Application/Calculations/SimulateMethodologyHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>
/// Прогін методології **без запису результату** (ФВ-13.5): подивитися, що
/// вийде, до публікації.
/// </summary>
public sealed class SimulateMethodologyHandler(ICalculationModule module)
{
    public Task<SimulationResultDto> HandleAsync(int methodologyVersionId, int periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) прогін у пам'яті; ⛔ жодного запису в calc.CalculationResult " +
            "   і жодного CalculationRun — інакше симуляція засмітить історію " +
            "   прогонів, за якою відновлюють числа;\n" +
            "2) трейс завжди Full незалежно від TraceLevel версії: сенс симуляції " +
            "   саме в тому, щоб побачити кроки;\n" +
            "3) повернути і результат, і різницю з поточною опублікованою версією.");
}
```

### `src/Ecr.Application/Localization/GetUiStringsHandler.cs`
MODULE: application-localization | STAGE: 3

```csharp
// src/Ecr.Application/Localization/GetUiStringsHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Localization;

/// <summary>
/// Каталог рядків інтерфейсу за мовою і областю (ФВ-14.9, D-95, D-114).
/// </summary>
/// <remarks>
/// Область `Public` віддається **анонімно** — сторінка входу потребує підписів
/// кнопок раніше, ніж хтось автентифікований. `Private` — після входу, бо
/// назви адміністративних областей не мають бути видимі невідомому
/// відвідувачу (ФВ-14.2).
/// </remarks>
public sealed class GetUiStringsHandler(IUiStringCatalog catalog)
{
    public Task<UiStringCatalog> HandleAsync(string languageCode, bool publicOnly, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) кеш за ключем {lang}:{scope}:{revision} (ФВ-14.9c) — та сама " +
            "   схема, що з метаданими: нова версія дає новий ключ, тому " +
            "   інвалідація не потрібна і два інстанси не розійдуться;\n" +
            "2) fallback: немає ключа в мові → мова за замовчуванням → САМ КЛЮЧ. " +
            "   Порожнечу не повертати ніколи: одна забута локалізація не має " +
            "   ламати екран;\n" +
            "3) ключі err.<код> — звідси ж, окремого сховища немає (ФВ-14.9a);\n" +
            "4) віддавати Revision як ETag; збіг з If-None-Match → 304.");
}
```

### `src/Ecr.Application/Localization/SetUiStringHandler.cs`
MODULE: application-localization | STAGE: 3

```csharp
// src/Ecr.Application/Localization/SetUiStringHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Localization;

/// <summary>Зміна рядка каталогу; право <c>System.ManageLocalization</c>.</summary>
public sealed class SetUiStringHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(string key, string languageCode, string value, byte scope, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) upsert рядка;\n" +
            "2) інкремент UiStringRevision ОДНИМ statement із OUTPUT — той самий " +
            "   прийом, що з PresentationRevision (R-B7): два адміністратори, що " +
            "   правлять переклад одночасно, не мають отримати однакову версію;\n" +
            "3) зміна scope з Private на Public — рішення про видимість, тому " +
            "   в аудит із автором;\n" +
            "4) повернути нову Revision.");
}
```

### `src/Ecr.Application/Security/StartSimulationHandler.cs`
MODULE: application-security | STAGE: 3

```csharp
// src/Ecr.Application/Security/StartSimulationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Починає сеанс «очима користувача» (ФВ-6.16a, D-96). Право
/// <c>Security.Simulate</c>.
/// </summary>
/// <remarks>
/// ⚠ Запис у <c>aud.SimulationSession</c> робиться **до** видачі профілю.
/// Інакше збій між видачею і записом лишив би сеанс перегляду чужих даних без
/// сліду — а слід тут і є суттю вимоги.
/// </remarks>
public sealed class StartSimulationHandler(
    ISimulationService simulation, ICurrentUser currentUser, IClock clock)
{
    public Task<long> HandleAsync(int subjectUserId, string reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) reason обов'язковий; симуляція себе → ECR-SIM-0422;\n" +
            "2) simulation.StartAsync — запис сеансу ПЕРШИМ, профіль потім;\n" +
            "3) профіль будувати ЗАНОВО і ⛔ НЕ класти в кеш (ФВ-6.16a п. 4): " +
            "   під ключем суб'єкта він дістався б справжньому користувачеві " +
            "   з прапорцем IsSimulation;\n" +
            "4) у профілі виставити IsSimulation, SimulatedForUserId, " +
            "   SimulationActorUserId — автором дій лишається той, хто симулює;\n" +
            "5) клієнт зобов'язаний показувати банер увесь сеанс.");
}
```

### `src/Ecr.Application/Security/EndSimulationHandler.cs`
MODULE: application-security | STAGE: 3

```csharp
// src/Ecr.Application/Security/EndSimulationHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>Завершує сеанс симуляції.</summary>
public sealed class EndSimulationHandler(
    ISimulationService simulation, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long sessionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) завершити можна ЛИШЕ власний сеанс — інакше один " +
            "   адміністратор обриває чужий і псує його аудит;\n" +
            "2) проставити EndedAt; запис не видаляти (D-25);\n" +
            "3) повернути клієнта до власного профілю — тобто скинути кеш сесії, " +
            "   бо профіль симуляції там і не лежав.");
}
```

### `src/Ecr.Application/Security/ChangePasswordHandler.cs`
MODULE: application-security | STAGE: 3

```csharp
// src/Ecr.Application/Security/ChangePasswordHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Зміна власного пароля. Знімає <c>MustChangePassword</c> і **обов'язково**
/// крутить <c>SecurityStamp</c> (ФВ-6.7).
/// </summary>
public sealed class ChangePasswordHandler(
    IPasswordHasher hasher, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(string currentPassword, string newPassword, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) лише локальні облікові записи: доменний пароль не наш;\n" +
            "2) перевірити поточний пароль — навіть при MustChangePassword;\n" +
            "3) новий пароль за PasswordPolicy (довжина, блокування — ФВ-6.4a);\n" +
            "4) user.SetPassword: знімає прапорець і крутить SecurityStamp, тому " +
            "   всі інші сесії стають недійсними негайно;\n" +
            "5) ⛔ ні поточний, ні новий пароль не логувати і не класти в " +
            "   повідомлення помилки (ФВ-6.11) — це перевіряється тестом;\n" +
            "6) в аудит — факт зміни без значень.");
}
```

### `src/Ecr.Application/Security/EnsureBootstrapAdminHandler.cs`
MODULE: application-security | STAGE: 3

```csharp
// src/Ecr.Application/Security/EnsureBootstrapAdminHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Створює локального адміністратора при першому старті (ФВ-6.18, D-115).
/// Пароль — зі змінної середовища <c>ECR_Bootstrap__Password</c>.
/// </summary>
/// <remarks>
/// Без цього запису систему неможливо налаштувати до під'єднання домену. Але
/// він же є найпривабливішою мішенню, тому живе за трьома обмеженнями: один на
/// систему, обов'язкова зміна пароля при першому вході, автоматичне вимкнення
/// після появи доменного адміністратора.
/// </remarks>
public sealed class EnsureBootstrapAdminHandler(
    IPasswordHasher hasher, IUnitOfWork uow, IClock clock)
{
    public Task HandleAsync(string? bootstrapPassword, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) якщо bootstrap-запис уже існує — не робити НІЧОГО і " +
            "   змінну ігнорувати: повторне створення скинуло б пароль " +
            "   працюючої системи;\n" +
            "2) якщо запису немає і змінної немає — ПОПЕРЕДЖЕННЯ, не помилка. " +
            "   Це нормальний стан контуру, де вхід переведено на домен (D-115);\n" +
            "3) створити User(Provider = Local, IsBootstrapAdmin = true, " +
            "   MustChangePassword = true) з роллю адміністратора;\n" +
            "4) ⛔ пароль не логувати, не повертати, не класти в жодне " +
            "   повідомлення (ФВ-6.11);\n" +
            "5) унікальність гарантує частковий індекс UX_User_Bootstrap — " +
            "   гонка двох інстансів при старті дасть помилку вставки, а не " +
            "   другий запис.");
}
```

### `src/Ecr.Application/Security/DisableBootstrapAdminHandler.cs`
MODULE: application-security | STAGE: 3

```csharp
// src/Ecr.Application/Security/DisableBootstrapAdminHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Вимикає bootstrap-адміністратора, щойно з'явився активний доменний
/// (ФВ-6.18, D-97). **Не видаляє** — запис потрібен в аудиті.
/// </summary>
public sealed class DisableBootstrapAdminHandler(
    IUnitOfWork uow, IAuditWriter audit, IClock clock)
{
    public Task<bool> HandleAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) умова — є щонайменше один АКТИВНИЙ доменний користувач із " +
            "   правом Security.ManageUsers. Не просто «є доменний користувач»: " +
            "   інакше перший же рядовий співробітник вимкне адміністратора;\n" +
            "2) user.DisableAsBootstrap(): IsActive = false, новий SecurityStamp;\n" +
            "3) IsBootstrapAdmin лишити — за ним видно, звідки взявся перший " +
            "   адміністратор системи;\n" +
            "4) в аудит; повернути, чи справді вимкнули;\n" +
            "5) викликати після кожного призначення ролі, а не за розкладом: " +
            "   вікно між появою доменного адміністратора і вимкненням має бути " +
            "   якомога коротшим.");
}
```
