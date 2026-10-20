// src/Ecr.Application/Permits/Bindings/SavePermitBindingHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Записує прив'язку таблиці аркуша версії-чернетки до таблиці типу дозволу (Permits, PM-09; <c>PUT</c> за кодом).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Правиться лише чернетка</b>: прив'язка вирішує, звідки в таблиці беруться числа, тож зміна її в
/// опублікованій версії тихо змінила б уже подані форми. Заборону тримає домен
/// (<see cref="TemplateVersion.EnsureStructurallyMutable"/>), а не форма. Той самий порядок, що в
/// <c>SaveTableRelationHandler</c>: перевірки → транзакція (блок рядка версії, запис, аудит, один коміт) →
/// інвалідація кешу метаданих.
/// </para>
/// <para>
/// ⚠ Колонки замінюються цілком: старі видаляються окремим збереженням, нові додаються після нього (ключ колонки -
/// <c>(прив'язка, поле дозволу)</c>, і видалення з вставкою того самого ключа в одному збереженні EF не бере).
/// Усе - у тій самій транзакції. Таблицю аркуша, тип і таблицю типу наявної прив'язки змінити не можна.
/// </para>
/// <para>
/// ⚠ Конкуренція (аудит B16): <c>PUT</c> і <c>DELETE</c> НЕ приймають <c>RowVersion</c> - передумови «стан, який я
/// бачив» немає. Чернетку шаблону серіалізує блок рядка версії (<see cref="DraftVersionLock"/>), а в її межах діє
/// <b>last-write-wins</b>: остання вдала правка замінює прив'язку цілком, навіть якщо редактор читав застарілий токен.
/// Це свідомо: прив'язку правлять рідко, в одній чернетці, а токен у відповіді (<c>rowVersion</c>) - довідковий. Тест
/// <c>PermitBindingSaveTests.Остання_вдала_правка_виграє…</c> фіксує це як контракт. Оптимістична конкуренція
/// (поле <c>rowVersion</c> у запиті + <c>409</c>) - окрема зміна контракту, за потреби.
/// </para>
/// </remarks>
public sealed class SavePermitBindingHandler(
    IRepository<TemplateVersion, int> versions,
    IPermitBindingStore bindings,
    IPermitTypeStore permits,
    IRegistryStore registries,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування прив'язок (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює прив'язку.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код прив'язки (адреса; унікальний у системі).</param>
    /// <param name="request">Налаштування прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія заморожена, прив'язка не складається (0422) або дублює наявну (4101).</exception>
    public async Task<PermitBindingDto> HandleAsync(
        int templateVersionId, string code, SavePermitBindingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.VersionNotFound(templateVersionId);

        // ⛔ ПЕРШИМ ділом і доменом: усе нижче має сенс лише там, де правка взагалі дозволена.
        version.EnsureStructurallyMutable();
        version.TouchDraft(userId, clock.UtcNow);

        var input = ParseInput(request);
        var bindingCode = EcrCode.Create(code);

        PermitSheetBinding saved = null!;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ C5: блок рядка версії першим, «ще чернетка» - під ним.
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            // ⛔ PM-09b2: УСЕ, що перевіряє «цілі» прив'язки (таблиця жива, режим рядків, стеля, дублікат), читається ПІД
            // блоком версії. Правка таблиці й аркуша (SaveTableDef/DeleteTableDef/DeleteSheetDef/DeleteColumnDef) бере
            // той самий блок і перевіряє прив'язки під ним, тож пара «збереження прив'язки» і «зміна цілі» серіалізується:
            // виграє одна, друга бачить результат переможця. Раніше читання йшло до блоку, і між ним та комітом ціль
            // могли змінити (прив'язка лишалась би на видаленій чи зафіксованій таблиці).
            var type = await permits.FindByCodeAsync(request.PermitTypeCode, innerCt).ConfigureAwait(false)
                ?? throw PermitBindingErrors.TypeUnknown(request.PermitTypeCode);
            var table = (await permits.ListTablesAsync(type.Id, innerCt).ConfigureAwait(false))
                .FirstOrDefault(t => string.Equals(t.Code, request.PermitTableCode, StringComparison.Ordinal))
                ?? throw PermitBindingErrors.PermitTableUnknown(type.Code, request.PermitTableCode);

            var context = await BuildContextAsync(templateVersionId, input, type, table, innerCt).ConfigureAwait(false);
            var validation = PermitBindingValidator.Validate(input, context);
            if (!validation.IsValid)
            {
                throw PermitBindingErrors.Invalid(validation.Errors);
            }

            var existing = await bindings.FindForUpdateAsync(templateVersionId, bindingCode.Value, innerCt).ConfigureAwait(false);
            await EnsureNoDuplicateAsync(templateVersionId, bindingCode.Value, input.SheetTableDefId, existing, innerCt).ConfigureAwait(false);

            if (existing is not null
                && (existing.SheetTableDefId != input.SheetTableDefId
                    || existing.PermitTypeDefId != type.Id
                    || existing.PermitTypeTableId != table.Id))
            {
                throw PermitBindingErrors.IdentityChanged(bindingCode.Value);
            }

            IReadOnlyList<PermitSheetBindingColumn> oldColumns = existing is null
                ? []
                : await bindings.ListColumnsForUpdateAsync(existing.Id, innerCt).ConfigureAwait(false);
            var oldJson = existing is null ? null : Describe(existing, oldColumns);

            var hasDocuments = await store.HasDocumentsAsync(templateVersionId, innerCt).ConfigureAwait(false);

            // ⚠ Класифікація йде і для створення: клас пишеться в аудит структурних змін, і «додано» без класу виглядало б
            // у журналі так само, як зміна, яку ніхто не класифікував.
            var change = existing is null
                ? classifier.ClassifyAddition(nameof(PermitSheetBinding))
                : classifier.Classify(nameof(PermitSheetBinding), nameof(PermitSheetBinding.Mode), hasDocuments);

            saved = existing!;

            if (existing is null)
            {
                saved = new PermitSheetBinding(
                    bindingCode, templateVersionId, input.SheetTableDefId, type.Id, table.Id,
                    input.Mode, input.SelectionRule, input.HeaderFieldDefId);
                saved.SetActive(request.IsActive);
                AddColumns(saved, input);
                bindings.Add(saved);

                // Запис ПЕРЕД аудитом: аудит несе EntityId, а в щойно доданої сутності його ще немає.
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                bindings.RemoveColumns(oldColumns);
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

                existing.Reconfigure(input.Mode, input.SelectionRule, input.HeaderFieldDefId);
                existing.SetActive(request.IsActive);
                AddColumns(existing, input);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(PermitSheetBinding), saved.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(saved, saved.Columns), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        var view = await bindings.GetAsync(templateVersionId, bindingCode.Value, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(bindingCode.Value, templateVersionId);
        return PermitBindingMapper.Map(view);
    }

    private static PermitBindingInput ParseInput(SavePermitBindingRequest request)
    {
        var mode = ParseEnum<PermitBindingMode>(request.Mode, "mode");
        var rule = ParseEnum<PermitSelectionRule>(request.SelectionRule, "selectionRule");
        var columns = (request.Columns ?? [])
            .Select(c => new BindingColumnInput(
                ParseEnum<PermitColumnRole>(c.Role, "columns.role"), c.PermitFieldCode, c.SheetColumnDefId))
            .ToList();

        return new PermitBindingInput(request.SheetTableDefId, mode, rule, request.HeaderFieldDefId, columns);
    }

    /// <summary>Рядок контракту → перелічення: точний збіг імені, без чисел.</summary>
    private static T ParseEnum<T>(string? value, string field)
        where T : struct, Enum
    {
        if (value is null || int.TryParse(value, out _) || !Enum.TryParse<T>(value, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw PermitBindingErrors.EnumUnknown(field, value);
        }

        return parsed;
    }

    private async Task<PermitBindingContext> BuildContextAsync(
        int templateVersionId, PermitBindingInput input, PermitTypeDef type, PermitTypeTable table, CancellationToken ct)
    {
        var structure = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        var sheetTable = structure.Sheets
            .Where(s => !s.IsDeleted)
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => !t.IsDeleted && t.Id == input.SheetTableDefId);

        var sheet = sheetTable is null
            ? null
            : new SheetTableInfo(
                sheetTable.Id, sheetTable.Code, sheetTable.RowMode, sheetTable.MaxDynamicRows,
                [.. sheetTable.Columns.Where(c => !c.IsDeleted).Select(c => new SheetColumnInfo(
                    c.Id, c.Code, c.DataType, c.UnitId, c.Precision, c.Scale, c.IsReadOnly, c.LookupRegistryDefId))]);

        var headers = structure.HeaderFields
            .Where(h => !h.IsDeleted)
            .Select(h => new HeaderFieldInfo(h.Id, h.Code, h.DataType, h.LookupRegistryDefId))
            .ToList();

        var definition = await registries.FindDefinitionByIdAsync(table.RegistryDefId, ct).ConfigureAwait(false);
        var metas = (await permits.ListFieldMetasAsync(type.Id, ct).ConfigureAwait(false))
            .ToDictionary(m => m.RegistryFieldDefId);

        var fields = new List<PermitFieldInfo>();
        int? rowCount = null;
        if (definition is not null)
        {
            foreach (var field in definition.Fields.Where(f => f.RelationKind != RegistryRelationKind.Composition))
            {
                metas.TryGetValue(field.Id, out var meta);
                fields.Add(new PermitFieldInfo(
                    field.Code, field.DataType, field.UnitId, meta?.Precision, meta?.Scale, field.RefRegistryDefId));
            }

            if (input.Mode == PermitBindingMode.Rows
                && definition.Fields.FirstOrDefault(f => f.RelationKind == RegistryRelationKind.Composition) is { } composition)
            {
                rowCount = await bindings.MaxPermitRowsAsync(table.RegistryDefId, composition.Id, ct).ConfigureAwait(false);
            }
        }

        return new PermitBindingContext(sheet, headers, type.Status == PermitTypeStatus.Published, fields, rowCount);
    }

    private async Task EnsureNoDuplicateAsync(
        int templateVersionId, string code, int sheetTableDefId, PermitSheetBinding? existing, CancellationToken ct)
    {
        var owner = await bindings.FindCodeBySheetTableAsync(templateVersionId, sheetTableDefId, ct).ConfigureAwait(false);
        if (owner is not null && (existing is null || !string.Equals(owner, existing.Code, StringComparison.Ordinal)))
        {
            throw PermitBindingErrors.SheetTaken(sheetTableDefId, owner);
        }

        // ⚠ Код унікальний по ВСІЙ системі (UQ_PermitSheetBinding_Code): однаковий код в іншій версії теж зайнятий.
        if (existing is null && await bindings.CodeExistsAsync(code, ct).ConfigureAwait(false))
        {
            throw PermitBindingErrors.CodeTaken(code);
        }
    }

    private static void AddColumns(PermitSheetBinding binding, PermitBindingInput input)
    {
        var ordinal = 1;
        foreach (var column in input.Columns)
        {
            binding.AddColumn(new PermitSheetBindingColumn(
                column.Role, EcrCode.Create(column.PermitFieldCode), column.SheetColumnDefId, ordinal++));
        }
    }

    /// <summary>Стан прив'язки для аудиту.</summary>
    internal static string Describe(PermitSheetBinding binding, IEnumerable<PermitSheetBindingColumn> columns)
        => JsonSerializer.Serialize(new
        {
            binding.Code,
            binding.SheetTableDefId,
            binding.PermitTypeDefId,
            binding.PermitTypeTableId,
            Mode = binding.Mode.ToString(),
            SelectionRule = binding.SelectionRule.ToString(),
            binding.HeaderFieldDefId,
            binding.IsActive,
            Columns = columns.OrderBy(c => c.Ordinal).Select(c => new
            {
                Role = c.Role.ToString(),
                c.PermitFieldCode,
                c.SheetColumnDefId,
                c.Ordinal,
            }),
        });
}
