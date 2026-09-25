// src/Ecr.Application/Documents/GetTableStatusHandler.cs

using System.Text.Json;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Заповненість таблиць документа за період і кількість зауважень до кожної
/// (<c>BE-10</c>) — джерело для «68 of 91 tables filled» і крапки помилки в
/// дереві аркушів.
/// </summary>
/// <remarks>
/// ⛔ <b>Знаменник рахує роботу ЛЮДИНИ, а не розмір шаблону.</b> У
/// <see cref="TableStatusDto.InputCells"/> не входять:
/// <list type="bullet">
/// <item>обчислювані колонки (<c>ColumnDef.IsComputed</c> — <c>Formula</c> і
/// <c>Calculated</c>) та колонки лише для читання
/// (<c>ColumnDef.IsReadOnly</c> — їх заповнює імпорт або збір);</item>
/// <item>таблиці, закриті правилом доступу до періоду — рішення ухвалює та
/// сама чиста функція <see cref="PeriodAccessRules.Evaluate"/>, що й на
/// шляху запису, а не друге визначення того самого правила;</item>
/// <item>рядки, закриті правилом за видом рядка
/// (<c>PeriodAccessRuleKind.HeaderRows</c>) або описом рядка
/// (<c>RowDef.IsReadOnly</c>).</item>
/// </list>
/// Без цього «заповнено 40 %» означало б «60 % — формули»: число, яке
/// виглядає як прогрес, вимірювало б частку формул у шаблоні.
///
/// ⚠ <b>Чому НЕ <c>IAccessDecisionService.CanEditSliceAsync</c></b>, хоч він
/// і відповідає на схоже питання по комірці. Три причини, і кожна сама по
/// собі достатня:
/// <list type="number">
/// <item>він робить кілька запитів НА ЕКЗЕМПЛЯР таблиці — на 91 таблиці це
/// сотні походів у базу при бюджеті 150 мс;</item>
/// <item>він складає в рішення ще й стан робочого процесу: поданий аркуш дав
/// би <c>InputCells = 0</c>, тобто «0 of 0 filled» на повністю заповненому
/// документі;</item>
/// <item>він складає в рішення гранти, і тоді знаменник залежав би від того,
/// ХТО дивиться. «68 з 91» — властивість документа, а не глядача.</item>
/// </list>
///
/// ⛔ <b><c>ErrorCount</c>/<c>WarningCount</c> — <c>null</c>, а не нуль, доки
/// документ не перевіряли</b> (<c>D15-06</c>, <c>A7-28</c>). Джерело —
/// ОСТАННІЙ збережений підсумок (<c>IValidationResultStore.GetLatestAsync</c>),
/// а не повторний прогін: перевірка великого документа коштує три секунди, і
/// робити її на кожне відкриття дерева аркушів означало б платити їх у
/// найгірший момент. Немає підсумку — немає й чисел; нуль під документом,
/// якого ніхто не перевіряв, — це не «зауважень немає», це «ми не знаємо».
/// </remarks>
public sealed class GetTableStatusHandler(
    IRowStore rowStore,
    ITableFillStore fill,
    IMetadataCache metadata,
    IValidationResultStore results,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ Саме <c>Document.View</c>, а не власне право: перелік несе коди
    /// аркушів і те, скільки в документі введено — тобто факти про його зміст.
    /// Хто бачить документ, бачить і його заповненість; хто не бачить —
    /// не бачить нічого (<c>403</c>).
    /// </remarks>
    public const string Permission = "Document.View";

    /// <summary>Віддає заповненість таблиць документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; екземпляри таблиць існують окремо на кожен (R-A6).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<TableStatusDto>> HandleAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        // ⛔ Ті самі ДВІ перевірки, що й у сусіднього `GetDocumentTablesHandler`
        // (`A7-53`, `Q-172`): функціональне право каже «цей користувач узагалі
        // працює з документами», грант — «з ЦИМ».
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var key = PeriodKey.Parse(periodKey);

        // ⚠ Екземпляри НЕ матеріалізуються (на відміну від
        // `GetDocumentTablesHandler.EnsureTableInstancesAsync`): це запит про
        // стан, і створювати щось на шляху читання статусу він не має права.
        // Документ, який за цей період ще не відкривали, чесно віддає
        // порожній перелік.
        var instances = await rowStore
            .GetTableInstancesAsync(documentId, key, ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return [];
        }

        var templateVersionId = instances[0].TemplateVersionId;

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var rules = await fill
            .GetPeriodAccessRulesAsync(templateVersionId, ct)
            .ConfigureAwait(false);

        var live = instances.Select(i => i.TableDefId).ToHashSet();

        // ── Структура: що саме має заповнити людина ──────────────────────
        var shapes = new List<TableShape>(instances.Count);
        var computedColumns = new List<int>();

        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted).OrderBy(s => s.Ordinal))
        {
            foreach (var table in sheet.Tables.Where(t => !t.IsDeleted).OrderBy(t => t.Ordinal))
            {
                if (!live.Contains(table.Id))
                {
                    // Таблиця описана в шаблоні, але екземпляра за цей період
                    // немає: аркуш до документа не входить (ФВ-3.2).
                    continue;
                }

                var columns = table.Columns.Where(c => !c.IsDeleted).ToList();

                computedColumns.AddRange(columns
                    .Where(c => c.IsComputed || c.IsReadOnly)
                    .Select(c => c.Id));

                shapes.Add(new TableShape(
                    table.Id,
                    sheet.Code,
                    InputColumnCount: columns.Count(c => !c.IsComputed && !c.IsReadOnly),
                    TemplateRowCount: table.Rows.Count(r => !r.IsDeleted),
                    IsClosed: Blocks(rules, profile, sheet.Id, table.Id, RowKind.Item, key),
                    ClosedRowDefCount: table.Rows.Count(
                        r => !r.IsDeleted
                             && (r.IsReadOnly
                                 || Blocks(rules, profile, sheet.Id, table.Id, r.RowKind, key)))));
            }
        }

        var counts = await fill
            .GetFillCountsAsync(documentId, key, computedColumns, ct)
            .ConfigureAwait(false);

        var countsByTable = counts.ToDictionary(c => c.TableDefId);

        // ── Зауваження: з ОСТАННЬОГО підсумку, без повторного прогону ────
        var summary = await results
            .GetLatestAsync(documentId, key.Value, ct)
            .ConfigureAwait(false);

        var findings = FindingsByTable(summary?.MessagesJson);

        var result = new List<TableStatusDto>(shapes.Count);

        foreach (var shape in shapes)
        {
            var counted = countsByTable.GetValueOrDefault(shape.TableDefId);

            // ⛔ `R-13`. Рядки, яких у базі ЩЕ НЕМАЄ, теж треба заповнити. Документ,
            // який за цей період ще не відкривали, має екземпляри таблиць, але
            // рядки шаблону матеріалізуються лише при читанні зрізу — і тоді
            // `RowCount = 0` давав `InputCells = 0` на КОЖНІЙ таблиці, а
            // порожній документ показував «Tables filled completely: 92 of 92».
            // Рядки, задані шаблоном, — нижня межа знаменника; динамічні
            // рядки, додані людиною, рахуються понад неї з бази.
            var rowCount = Math.Max(counted?.RowCount ?? 0, shape.TemplateRowCount);

            // ⚠ Опис рядка може існувати в шаблоні й не бути матеріалізованим
            // у цьому екземплярі — тоді віднімати його нема від чого.
            var inputRows = shape.IsClosed
                ? 0
                : Math.Max(0, rowCount - shape.ClosedRowDefCount);

            var inputCells = inputRows * shape.InputColumnCount;

            // ⚠ Стеля. Перевищити знаменник чисельник може рівно в одному
            // випадку: значення ввели ДО того, як колонку зробили
            // обчислюваною або рядок закрили правилом. Показати «5 з 4»
            // означало б винести цю історію на екран замість відповіді на
            // питання «чи все заповнено».
            var filled = shape.IsClosed
                ? 0
                : Math.Min(counted?.FilledCells ?? 0, inputCells);

            var tableFindings = findings?.GetValueOrDefault(shape.TableDefId);

            result.Add(new TableStatusDto(
                shape.TableDefId,
                shape.SheetCode,
                filled,
                inputCells,

                // ⛔ `findings is null` — перевірки не було, і тоді НЕ нуль.
                // `findings` є, а таблиці в ньому немає — перевірка була й
                // порушень у цій таблиці не знайшла, і тоді саме нуль.
                findings is null ? null : tableFindings?.Errors ?? 0,
                findings is null ? null : tableFindings?.Warnings ?? 0));
        }

        return result;
    }

    /// <summary>
    /// Чи закриває правило доступу до періоду запис у таку комірку.
    /// </summary>
    /// <remarks>
    /// ⚠ Факти навмисно БЕЗ <c>SourceValues</c> і <c>ExpressionResults</c>:
    /// <c>SourceWindow</c> залежить від того, на який запис довідника
    /// посилається КОНКРЕТНИЙ рядок, а <c>Expression</c> — від значень цього
    /// рядка. Обидва види без своїх фактів трактуються
    /// <see cref="PeriodAccessRules"/> як «не застосовується», а не як
    /// заборона — тобто знаменник від них не зменшується. Це свідомо: вони
    /// закривають ОКРЕМІ рядки, а не таблицю, і рахувати їх тут означало б
    /// або збрехати в один бік (закрити зайве), або зробити відповідь на
    /// статус дорожчою за саме читання документа. Той самий компроміс уже
    /// ухвалено на шляху прав: <c>AccessDecisionService</c> передає
    /// <c>EmptyExpressions</c>.
    ///
    /// ⚠ <c>CurrentSequence: null</c> з тієї ж причини — і <c>RelativeWindow</c>
    /// без відомого поточного періоду теж «не застосовується» (те саме, що
    /// робить <c>PeriodAccessRules.RelativeWindow</c>).
    /// </remarks>
    private static bool Blocks(
        IReadOnlyList<PeriodAccessRuleDef> rules,
        AccessProfile profile,
        int sheetDefId,
        int tableDefId,
        RowKind rowKind,
        PeriodKey periodKey)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        var facts = new PeriodRuleFacts(
            sheetDefId,
            tableDefId,
            rowKind,
            (byte)periodKey.Sequence,
            periodKey.Year,
            CurrentSequence: null,
            ColumnMonthNumber: null,
            EmptySourceValues,
            EmptyExpressions);

        return PeriodAccessRules.Evaluate(rules, facts, profile.RoleIds).Blocks;
    }

    /// <summary>
    /// Помилки й попередження, розкладені по таблицях; <c>null</c> —
    /// перевірку не запускали.
    /// </summary>
    /// <remarks>
    /// ⚠ Рівень <c>Info</c> не рахується: макет знає дві позначки — помилку й
    /// попередження, і третя дорога сюди не веде.
    /// </remarks>
    private static Dictionary<int, TableFindings>? FindingsByTable(string? messagesJson)
    {
        if (messagesJson is null)
        {
            return null;
        }

        var messages = JsonSerializer.Deserialize<List<ValidationMessage>>(messagesJson) ?? [];

        return messages
            .GroupBy(m => m.TableDefId)
            .ToDictionary(
                g => g.Key,
                g => new TableFindings(
                    g.Count(m => m.Severity == ValidationSeverity.Error),
                    g.Count(m => m.Severity == ValidationSeverity.Warning)));
    }

    private static readonly IReadOnlyDictionary<int, SourceValidity> EmptySourceValues
        = new Dictionary<int, SourceValidity>();

    private static readonly IReadOnlyDictionary<string, bool> EmptyExpressions
        = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Те, що про таблицю знає ШАБЛОН, — без жодного запиту в базу.</summary>
    private sealed record TableShape(
        int TableDefId,
        string SheetCode,
        int InputColumnCount,
        int TemplateRowCount,
        bool IsClosed,
        int ClosedRowDefCount);

    /// <summary>Скільки зауважень кожного рівня припало на таблицю.</summary>
    private sealed record TableFindings(int Errors, int Warnings);
}
