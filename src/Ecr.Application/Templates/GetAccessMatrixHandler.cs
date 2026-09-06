using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Templates;

/// <summary>
/// Попередній перегляд матриці доступу <c>період × аркуш</c> (<c>ФВ-2.18</c>).
/// </summary>
/// <remarks>
/// ⛔ Матриця будується ТИМ САМИМ обчислювачем, що й доступ у документі
/// (<see cref="PeriodAccessRules"/>). Друга реалізація «для попереднього
/// перегляду» показувала б не те, що система робить насправді, — а сенс
/// перегляду рівно в тому, щоб побачити помилку в правилах ДО публікації.
///
/// ⚠ Два види правил залежать від ДАНИХ документа, яких у шаблоні ще
/// немає: <c>SourceWindow</c> (запис довідника, обраний у рядку) і
/// <c>Expression</c> (умова над значеннями рядка). Матриця це
/// <b>оголошує</b> прапорцем <see cref="AccessMatrixSheetDto.DependsOnData"/>,
/// а не мовчки показує «доступно». Мовчання тут було б гіршим за
/// відсутність перегляду: конфігуратор побачив би зелену клітинку там, де
/// в реальному документі буде замок.
/// </remarks>
public sealed class GetAccessMatrixHandler(
    IMetadataCache metadata,
    ITemplateVersionStore versions,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Скільки періодів показувати.</summary>
    /// <remarks>
    /// Дванадцять — максимум для будь-якої гранулярності: <c>Sequence</c>
    /// лежить у <c>1…12</c> (<c>D-108</c>), а квартальний проєкт просто
    /// використає перші чотири. Гранулярність — властивість проєкту, не
    /// шаблону, тому обрати її тут нема з чого.
    /// </remarks>
    private const int PeriodCount = 12;

    /// <summary>Будує матрицю.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<AccessMatrixDto> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var rules = await versions
            .ListPeriodAccessRulesAsync(templateVersionId, ct)
            .ConfigureAwait(false);

        var sheets = new List<AccessMatrixSheetDto>(snapshot.Sheets.Count);

        foreach (var sheet in snapshot.Sheets.OrderBy(s => s.Ordinal))
        {
            var tableIds = sheet.Tables.Select(t => t.Id).ToList();

            // Аркуш без таблиць усе одно оцінюється: правило рівня аркуша
            // стосується його цілком, і показати такий аркуш порожнім рядком
            // означало б сховати саме те правило, заради якого відкрили перегляд.
            if (tableIds.Count == 0)
            {
                tableIds.Add(0);
            }

            var dependsOnData = rules.Any(r =>
                Applies(r, sheet.Id, tableIds)
                && r.RuleKind is PeriodAccessRuleKind.SourceWindow or PeriodAccessRuleKind.Expression);

            var cells = new List<AccessMatrixCellDto>(PeriodCount);

            for (byte sequence = 1; sequence <= PeriodCount; sequence++)
            {
                cells.Add(Cell(rules, sheet.Id, tableIds, sequence));
            }

            sheets.Add(new AccessMatrixSheetDto(
                sheet.Id, sheet.Code, sheet.NameL10n, dependsOnData, cells));
        }

        return new AccessMatrixDto(
            snapshot.TemplateVersionId, snapshot.PresentationRevision, PeriodCount, sheets);
    }

    /// <summary>Стан однієї клітинки матриці.</summary>
    /// <remarks>
    /// ⚠ Аркуш оцінюється ПО ТАБЛИЦЯХ, а не цілком. Правило, обмежене однією
    /// таблицею з п'яти, робить аркуш доступним частково — і саме цей стан
    /// найчастіше й буває помилкою конфігурації. Показати його як
    /// «заблоковано» або як «доступно» означало б втратити те, заради чого
    /// перегляд існує.
    /// </remarks>
    private static AccessMatrixCellDto Cell(
        IReadOnlyList<PeriodAccessRuleDef> rules, int sheetDefId, List<int> tableIds, byte sequence)
    {
        var blocked = 0;
        EditDenyReason reason = EditDenyReason.None;
        string? detail = null;

        foreach (var tableDefId in tableIds)
        {
            var facts = new PeriodRuleFacts(
                sheetDefId,
                tableDefId,
                RowKind.Item,
                sequence,

                // Рік потрібен лише `SourceWindow`, який тут не обчислюється:
                // у шаблоні немає ні документа, ні обраного запису довідника.
                PeriodYear: 0,

                // Поточний період — властивість ПРОЄКТУ, і в шаблоні його
                // немає. `RelativeWindow` тому не застосовується, і матриця
                // каже про це прапорцем, а не мовчазним «доступно».
                CurrentSequence: null,
                ColumnMonthNumber: null,
                SourceValues: EmptyWindows,
                ExpressionResults: EmptyExpressions);

            // Ролі порожні: матриця показує правила, що діють на ВСІХ.
            // Правило, обмежене роллю, у попередньому перегляді не блокує —
            // інакше конфігуратор бачив би замок, якого більшість не побачить.
            var outcome = PeriodAccessRules.Evaluate(rules, facts, EmptyRoles);

            if (outcome.Blocks)
            {
                blocked++;
                reason = outcome.Reason;
                detail ??= outcome.Detail;
            }
        }

        var state = blocked == 0
            ? AccessMatrixState.Editable
            : blocked == tableIds.Count
                ? AccessMatrixState.Blocked
                : AccessMatrixState.Partial;

        return new AccessMatrixCellDto(sequence, state, reason, detail);
    }

    /// <summary>Чи стосується правило цього аркуша або його таблиць.</summary>
    private static bool Applies(PeriodAccessRuleDef rule, int sheetDefId, List<int> tableIds)
        => (rule.SheetDefId is not { } sheet || sheet == sheetDefId)
        && (rule.TableDefId is not { } table || tableIds.Contains(table));

    private static readonly IReadOnlyDictionary<int, SourceValidity> EmptyWindows
        = new Dictionary<int, SourceValidity>();

    private static readonly IReadOnlyDictionary<string, bool> EmptyExpressions
        = new Dictionary<string, bool>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<int> EmptyRoles = new HashSet<int>();
}

/// <summary>Стан клітинки матриці доступу.</summary>
public enum AccessMatrixState : byte
{
    /// <summary>Усі таблиці аркуша доступні на введення.</summary>
    Editable = 0,

    /// <summary>Частина таблиць заблокована правилом.</summary>
    Partial = 1,

    /// <summary>Заблоковані всі таблиці аркуша.</summary>
    Blocked = 2,
}

/// <summary>Матриця доступу <c>період × аркуш</c>.</summary>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PresentationRevision">Ревізія; частина ключа кешу структури.</param>
/// <param name="PeriodCount">Скільки періодів у матриці.</param>
/// <param name="Sheets">Аркуші в порядку шаблону.</param>
public sealed record AccessMatrixDto(
    int TemplateVersionId,
    int PresentationRevision,
    int PeriodCount,
    IReadOnlyList<AccessMatrixSheetDto> Sheets);

/// <summary>Рядок матриці — один аркуш.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="Code">Код аркуша.</param>
/// <param name="NameL10n">Назва аркуша.</param>
/// <param name="DependsOnData">
/// Аркуш має правило, яке залежить від даних документа
/// (<c>SourceWindow</c> або <c>Expression</c>): показана картина неповна.
/// </param>
/// <param name="Cells">Клітинки за порядковими номерами періодів.</param>
public sealed record AccessMatrixSheetDto(
    int SheetDefId,
    string Code,
    Domain.ValueObjects.LocalizedText NameL10n,
    bool DependsOnData,
    IReadOnlyList<AccessMatrixCellDto> Cells);

/// <summary>Клітинка матриці.</summary>
/// <param name="PeriodSequence">Порядковий номер періоду (<c>1…12</c>).</param>
/// <param name="State">Доступно, частково, заблоковано.</param>
/// <param name="Reason">Причина блокування; <c>None</c> — доступно.</param>
/// <param name="Detail">Пояснення для підказки.</param>
public sealed record AccessMatrixCellDto(
    byte PeriodSequence,
    AccessMatrixState State,
    EditDenyReason Reason,
    string? Detail);
