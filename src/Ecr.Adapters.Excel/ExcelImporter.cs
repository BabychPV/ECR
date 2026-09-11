using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Імпорт із <c>.xlsx</c> — **завжди** через попередній перегляд diff (ФВ-4.3).
/// </summary>
/// <remarks>
/// Імпорт без перегляду — це спосіб непомітно перезаписати чужу роботу.
/// Тому застосування розділене на два кроки, і між ними користувач бачить,
/// що саме зміниться, що конфліктує і що буде відхилено.
/// </remarks>
public sealed class ExcelImporter(
    IMetadataCache metadata,
    IRegistryStore registries,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IImportPreviewStore previews,
    PatchCellsHandler patch,
    ImportDiffBuilder diffBuilder,
    ICellStore cellStore,
    IRowStore rowStore) : IExcelImporter
{
    /// <summary>Порожній зріз — таблиця без жодного рядка чи непорожньої комірки.</summary>
    private static readonly IReadOnlyDictionary<string, long> EmptyRowIds =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyVersions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<CellRecord> EmptySlice = [];

    /// <summary>
    /// Скільки живе побудований diff.
    /// </summary>
    /// <remarks>
    /// ⚠ Пів години — це час на перегляд, а не на робочий день. Diff — знімок
    /// чужих даних на момент побудови; застосований назавтра, він перезаписав
    /// би все, що зробили після нього, і виглядало б це як «імпорт зіпсував
    /// документ».
    /// </remarks>
    public static TimeSpan PreviewLifetime => TimeSpan.FromMinutes(30);

    /// <summary>Налаштування збереження diff; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        using var workbook = Open(file);
        var map = ReadMap(workbook);

        // ⛔ Книга з чужого документа не імпортується в цей. Однакова
        // структура шаблону робить таку книгу правдоподібною до останньої
        // комірки — і саме тому перевірка тут, а не в очах користувача.
        if (map.DocumentId != documentId)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"Книгу вивантажено з документа {map.DocumentId}, а імпорт іде в {documentId}.",
                new Dictionary<string, object?>
                {
                    ["expectedDocumentId"] = documentId,
                    ["actualDocumentId"] = map.DocumentId,
                });
        }

        var period = new PeriodKey(map.PeriodKey);

        // ⛔ Q-234 (аудит фази 3, Excel-обмін). ВЕРСІЯ ШАБЛОНУ РЕЗОЛВИТЬСЯ З
        // БД за documentId/period, а НЕ береться з `map.TemplateVersionId`.
        // Той самий принцип, що й у `IRowStore.ResolveTableInstanceAsync`
        // («клієнт не має диктувати, за якою версією тлумачити дані») —
        // книга .xlsx тут рівно такий самий «клієнт», як і тіло HTTP-запиту:
        // до `A7-29`/цього фікса код довіряв полю, записаному в файл на
        // момент ЕКСПОРТУ, і republish шаблону між експортом і імпортом
        // (звичайна подія за рік звітності) робив ВЕСЬ diff порівнянням проти
        // структури, якої вже нема — з мовчазним ризиком не лише хибного
        // типу комірки (`Read` нижче бере тип з цього знімка), а й того, що
        // `ApplyAsync` впаде на `PatchCellsHandler` (він завжди резолвить
        // ПОТОЧНУ версію) лише на ПІВ книги, лишивши решту незастосованою без
        // жодного натяку в перегляді.
        //
        // ⚠ Той самий виклик заразом дає список `TableInstanceId`, які СПРАВДІ
        // належать цьому документу за цей період — блок книги з чужим
        // (чи вигаданим) `TableInstanceId` інакше пішов би прямо в пакетні
        // читання рядків/комірок нижче без жодної перевірки належності.
        var instances = await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false);

        if (instances.Count == 0)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документа {documentId} за період {map.PeriodKey} не існує або він порожній.");
        }

        var validInstances = instances.ToDictionary(i => i.TableInstanceId, i => i.TableDefId);
        var snapshot = await metadata.GetAsync(instances[0].TemplateVersionId, ct).ConfigureAwait(false);
        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може імпортувати дані.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var lookups = await LookupsAsync(snapshot, ct).ConfigureAwait(false);

        var diffs = new List<TableDiff>(map.Tables.Count);
        var changes = new List<ImportChange>();
        var rejected = new List<ImportRejection>();

        // ⛔ Q-168 (аудит фази 2, продуктивність). Таблиці з файлу, яких немає
        // в чинній версії шаблону, відхиляються ТУТ, ДО пакетного читання —
        // інакше довелося б або читати рядки/комірки для екземпляра, який
        // діагностика все одно відкине, або гілкувати пакетний запит під
        // список, що звужується в циклі.
        var validBlocks = new List<(ExcelTableBlock Block, TableDef Table)>(map.Tables.Count);

        foreach (var block in map.Tables)
        {
            // ⛔ Q-234: екземпляр з файлу має належати ЦЬОМУ документу за
            // ЦЕЙ період, і його `TableDefId` — збігатися з тим, що зараз
            // справді стоїть у БД за цим `TableInstanceId`. Без цієї
            // перевірки книга з чужого документа (той самий шаблон, інший
            // проєкт) могла б підмінити `TableInstanceId` і змусити код нижче
            // прочитати рядки й комірки ЧУЖОГО екземпляра ще ДО будь-якого
            // рішення про доступ.
            if (!validInstances.TryGetValue(block.TableInstanceId, out var actualTableDefId)
                || actualTableDefId != block.TableDefId)
            {
                rejected.Add(new ImportRejection(
                    "—", block.TableCode, "ECR-IMP-0422",
                    "Екземпляра таблиці з файлу немає в цьому документі за цей період: "
                    + "структуру, ймовірно, змінено після експорту."));

                continue;
            }

            if (!tables.TryGetValue(block.TableDefId, out var table))
            {
                rejected.Add(new ImportRejection(
                    "—", block.TableCode, "ECR-IMP-0422",
                    "Таблиці з файлу немає в чинній версії шаблону."));

                continue;
            }

            validBlocks.Add((block, table));
        }

        var tableInstanceIds = validBlocks.ConvertAll(v => v.Block.TableInstanceId);

        // ⛔ Три пакетні запити на ВСЮ книгу замість трьох на КОЖНУ таблицю
        // (~90 у типовому шаблоні): GetRowIdsBatchAsync і GetRowVersionsBatchAsync
        // поруч, ReadSlicesAsync — той самий прийом, що вже закрив Q-165 для
        // ValidateDocumentHandler.
        var rowIdsBatch = await rowStore.GetRowIdsBatchAsync(tableInstanceIds, period, ct).ConfigureAwait(false);
        var versionsBatch = await rowStore.GetRowVersionsBatchAsync(tableInstanceIds, period, ct).ConfigureAwait(false);
        var slicesBatch = await cellStore.ReadSlicesAsync(tableInstanceIds, ct).ConfigureAwait(false);

        foreach (var (block, table) in validBlocks)
        {
            var worksheet = workbook.Worksheet(block.SheetName);

            // ⚠ Рішення про доступ — ПАКЕТНО на зріз. Поштучна перевірка
            // тисяч комірок імпорту не вкладається в жоден бюджет і саме тому
            // спокушає її пропустити.
            var decisions = await access
                .CanEditSliceAsync(profile, block.TableInstanceId, ct)
                .ConfigureAwait(false);

            var rowIds = rowIdsBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyDictionary<string, long>)EmptyRowIds);
            var versions = versionsBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyDictionary<string, string>)EmptyVersions);
            var current = slicesBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyList<CellRecord>)EmptySlice);

            var diff = diffBuilder.Build(
                worksheet, block, map.PeriodKey, table, decisions, lookups, rowIds, versions, current);

            diffs.Add(diff);
            changes.AddRange(diff.Changes);
            rejected.AddRange(diff.Rejected);
        }

        var token = Guid.NewGuid().ToString("N");

        await previews
            .SaveAsync(
                token,
                JsonSerializer.Serialize(new ImportPlan(documentId, map.PeriodKey, diffs), Options),
                PreviewLifetime,
                ct)
            .ConfigureAwait(false);

        // Конфліктів на етапі перегляду ще немає: вони з'являються, якщо між
        // переглядом і застосуванням хтось правив ті самі рядки. Показувати їх
        // наперед означало б вигадати їх.
        return new ImportPreview(token, changes, rejected, []);
    }

    /// <inheritdoc />
    public async Task<int> CountPendingChangesAsync(string previewToken, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(previewToken, documentId: null, ct).ConfigureAwait(false);

        return plan.Tables.Sum(t => t.Changes.Count);
    }

    /// <inheritdoc />
    public async Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(previewToken, documentId, ct).ConfigureAwait(false);

        var applied = 0;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var validation = new List<ValidationMessageDto>();

        foreach (var diff in plan.Tables.Where(t => t.Changes.Count > 0))
        {
            // ⚠ Версії рядків беруться з ПЕРЕГЛЯДУ і передаються як
            // baseVersion. Саме це змушує звичайний шлях запису відхилити
            // весь батч, якщо між переглядом і застосуванням хтось правив ті
            // самі рядки (ECR-CELL-0409) — тобто конфлікт ловить одна
            // перевірка, а не дві, які вміють розійтися.
            var rows = diff.Changes
                .GroupBy(c => c.RowKey, StringComparer.Ordinal)
                .Select(g => new PatchRow(
                    g.Key,
                    diff.RowVersions.GetValueOrDefault(g.Key),
                    g.Select(c => new PatchCell(c.ColumnCode, c.NewValue)).ToList()))
                .ToList();

            // ⚠ Той самий шлях, що й batch-PATCH, із Origin = "Import".
            // Окремий шлях запису означав би, що аудит, валідація і
            // перерахунок для імпорту працюють інакше — і розбіжність
            // виявиться на числах, а не на коді.
            var response = await patch
                .HandleAsync(
                    new PatchCellsRequest(diff.TableInstanceId, diff.PeriodKey, "Import", rows), ct)
                .ConfigureAwait(false);

            applied += response.AppliedCells;
            validation.AddRange(response.Validation);

            foreach (var (key, version) in response.RowVersions)
            {
                versions[key] = version;
            }
        }

        // Прибирається ЛИШЕ після успіху: якщо застосування впало на конфлікті,
        // користувач має змогу подивитися перегляд ще раз, а не будувати його
        // наново з файлу, якого може вже не бути під рукою.
        await previews.RemoveAsync(previewToken, ct).ConfigureAwait(false);

        return new PatchCellsResponse(applied, versions, validation);
    }

    /// <summary>
    /// Читає й розбирає раніше збережений <see cref="ImportPlan"/>.
    /// </summary>
    /// <param name="previewToken">Токен перегляду.</param>
    /// <param name="documentId">
    /// Документ застосування; <c>null</c> — виклик лише РАХУЄ зміни
    /// (<see cref="CountPendingChangesAsync"/>) і документ ще невідомий обробнику.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Спільна для <see cref="CountPendingChangesAsync"/> і
    /// <see cref="ApplyAsync"/> (директива №11, T10 #45): порогове рішення
    /// «синхронно чи в чергу» рахує зміни ТИМ САМИМ читанням, яким їх потім
    /// застосовують, — другий незалежний розбір <c>previewToken</c> міг би
    /// одного дня порахувати інакше, ніж застосує.
    /// </remarks>
    private async Task<ImportPlan> LoadPlanAsync(string previewToken, long? documentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewToken);

        var stored = await previews.FindAsync(previewToken, ct).ConfigureAwait(false)
                     ?? throw new BusinessRuleException(
                         "ECR-IMP-0422",
                         "Перегляд імпорту не знайдено або його строк вийшов: побудуйте його заново.");

        var plan = JsonSerializer.Deserialize<ImportPlan>(stored, Options)
                   ?? throw new BusinessRuleException(
                       "ECR-IMP-0422", "Збережений перегляд імпорту не читається.");

        if (documentId is not null && plan.DocumentId != documentId)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"Перегляд належить документу {plan.DocumentId}, а застосування йде в {documentId}.");
        }

        return plan;
    }

    /// <summary>Відкриває книгу або каже, що це не книга.</summary>
    private static XLWorkbook Open(Stream file)
    {
        try
        {
            return new XLWorkbook(file);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422", "Файл не читається як книга .xlsx.");
        }
    }

    /// <summary>Карта книги з прихованого аркуша.</summary>
    /// <remarks>
    /// ⛔ Книга без карти не імпортується. Зіставляти аркуші й колонки за
    /// позиціями означало б, що вставлена користувачем колонка зсуває всі
    /// дані на одну вправо — і diff покаже це як зміну кожного значення,
    /// цілком правдоподібну на вигляд.
    /// </remarks>
    private static ExcelWorkbookMap ReadMap(XLWorkbook workbook)
    {
        if (!workbook.Worksheets.TryGetWorksheet(ExcelWorkbookMap.SheetName, out var sheet))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "У книзі немає службового аркуша з картою: імпортувати можна лише файл, "
                + "вивантажений цією системою.");
        }

        // ⚠ Карта складається з усіх непорожніх комірок стовпця A: експортер
        // ріже її на шматки по 30 000 символів, бо в одну комірку більше за
        // 32 767 Excel не приймає (`A7-29`). Книги, збережені до цього,
        // читаються тим самим кодом: у них шматок рівно один.
        var json = string.Concat(
            sheet.Column(1)
                .CellsUsed()
                .OrderBy(c => c.Address.RowNumber)
                .Select(c => c.GetString()));

        return (string.IsNullOrWhiteSpace(json)
                   ? null
                   : JsonSerializer.Deserialize<ExcelWorkbookMap>(json, Options))
               ?? throw new BusinessRuleException(
                   "ECR-IMP-0422", "Карта книги порожня або пошкоджена.");
    }

    /// <summary>Коди записів довідників: <c>RegistryDefId</c> → код → <c>Id</c>.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>>> LookupsAsync(
        TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var registryIds = snapshot.ColumnsById.Values
            .Where(c => !c.IsDeleted && c.LookupRegistryDefId is not null)
            .Select(c => c.LookupRegistryDefId!.Value)
            .Distinct()
            .ToList();

        var result = new Dictionary<int, IReadOnlyDictionary<string, long>>();

        foreach (var registryId in registryIds)
        {
            var entries = await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false);

            result[registryId] = entries
                .GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }
}

/// <summary>Збережений між переглядом і застосуванням план імпорту.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Tables">Diff-и таблиць.</param>
public sealed record ImportPlan(long DocumentId, int PeriodKey, IReadOnlyList<TableDiff> Tables);
