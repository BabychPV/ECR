// src/Ecr.Application/Templates/ColumnDefBatchHandler.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує пачку колонок однієї таблиці версії-чернетки за ОДИН запит і ОДНУ транзакцію (G13, Land).
/// </summary>
/// <remarks>
/// ⛔ Чому не цикл по <see cref="SaveColumnDefHandler"/>: кожен його виклик читає структуру ВСІЄЇ версії
/// (<c>GetWithStructureAsync</c>), пише блок, аудит і комміт, а потім скидає кеш метаданих — тобто вартість
/// колонки росте з розміром шаблону, і 5000 колонок Land давали години (O(n²)). Тут структура читається раз,
/// кеш скидається раз, комміт один.
///
/// ⚠ Перевірки ті самі, що в одиночному <c>PUT</c> (незмінний тип, код видаленої колонки, одиниця, стиль, довідник,
/// класифікація зміни), і вони йдуть ДО першої мутації: пачка або застосовується цілком, або не змінює нічого.
/// Один код — одна колонка в пачці: дві колонки з тим самим кодом дали б неоднозначний результат.
/// </remarks>
public sealed class SaveColumnDefsBatchHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IUnitCatalog units,
    IRegistryStore? registries = null,
    IStyleCatalog? styles = null)
{
    /// <summary>Право на редагування структури версії (<c>02-contracts.md</c> §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Скільки колонок приймає один запит.</summary>
    public const int MaxColumns = 500;

    /// <summary>Створює або змінює колонки таблиці.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належать колонки.</param>
    /// <param name="items">Колонки пачки; порядок відповіді збігається з порядком запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або таблиці немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Пачка завелика чи має повтор коду (<c>ECR-TMPL-0422</c>); будь-яка відмова одиночного запису колонки.
    /// </exception>
    public async Task<IReadOnlyList<ColumnDefDto>> HandleAsync(
        int templateVersionId, int tableDefId, IReadOnlyList<SaveColumnDefItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        if (items.Count > MaxColumns)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Пачка містить {items.Count.ToString(CultureInfo.InvariantCulture)} колонок, найбільше — "
                + $"{MaxColumns.ToString(CultureInfo.InvariantCulture)}. Розбийте її на кілька запитів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.columnBatchTooLarge",
                    ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                    ["max"] = MaxColumns.ToString(CultureInfo.InvariantCulture),
                });
        }

        var duplicate = items
            .GroupBy(i => i.Code, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Код колонки «{duplicate.Key}» повторюється в пачці: один код — одна колонка.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.columnBatchDuplicateCode",
                    ["columnCode"] = duplicate.Key,
                });
        }

        if (items.Count == 0)
        {
            return [];
        }

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();
        version.TouchDraft(userId, clock.UtcNow);

        var table = SaveColumnDefHandler.FindTable(version, tableDefId);
        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // Довідники читаються раз на пачку: у одиночному записі це запит на колонку.
        var versionStyles = styles is not null && items.Any(i => i.Command.StyleId is not null)
            ? await styles.GetAsync(templateVersionId, ct).ConfigureAwait(false)
            : null;
        var checkedRegistries = new HashSet<int>();

        // ── Перевірка всієї пачки ДО першої мутації ──────────────────────────────────────────────
        var plan = new List<PlannedColumn>(items.Count);
        foreach (var item in items)
        {
            var ecrCode = EcrCode.Create(item.Code);
            var command = item.Command;

            var existing = table.Columns.FirstOrDefault(
                c => !c.IsDeleted && string.Equals(c.Code, ecrCode.Value, StringComparison.Ordinal));

            if (existing is null
                && table.Columns.Any(c => c.IsDeleted && string.Equals(c.Code, ecrCode.Value, StringComparison.Ordinal)))
            {
                throw new BusinessRuleException(
                    ErrorCodes.TemplateInvalid,
                    $"Код колонки «{item.Code}» зайнятий видаленою колонкою цієї таблиці. "
                    + "Заведіть колонку з іншим кодом або клонуйте версію.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.columnCodeTakenByDeleted",
                        ["tableDefId"] = tableDefId.ToString(CultureInfo.InvariantCulture),
                        ["columnCode"] = item.Code,
                    });
            }

            if (existing is not null && existing.DataType != command.DataType)
            {
                throw new BusinessRuleException(
                    ErrorCodes.TemplateInvalid,
                    $"Тип колонки «{item.Code}» незмінний після створення "
                    + $"({existing.DataType} → {command.DataType}). Заведіть нову колонку або клонуйте версію.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.columnDataTypeImmutable",
                        ["columnCode"] = item.Code,
                        ["oldDataType"] = existing.DataType.ToString(),
                        ["newDataType"] = command.DataType.ToString(),
                    });
            }

            await SaveColumnDefHandler.RequireKnownUnitAsync(units, command.UnitId, item.Code, ct).ConfigureAwait(false);

            if (versionStyles is not null && command.StyleId is { } styleId && existing?.StyleId != styleId
                && !versionStyles.ContainsKey(styleId))
            {
                throw new BusinessRuleException(
                    ErrorCodes.TemplateInvalid,
                    $"Стиль {styleId} не належить версії {templateVersionId}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.presentationValueInvalid",
                        ["entityType"] = nameof(ColumnDef),
                        ["field"] = nameof(ColumnDef.StyleId),
                    });
            }

            if (command.DataType == CellDataType.Lookup
                && (existing is null || command.LookupRegistryDefId is null
                    || existing.LookupRegistryDefId != command.LookupRegistryDefId)
                && (command.LookupRegistryDefId is not { } registryId || checkedRegistries.Add(registryId)))
            {
                await LookupRegistryGuard
                    .RequireForColumnAsync(
                        registries, access, userId, command.LookupRegistryDefId, existing?.LookupRegistryDefId,
                        item.Code, ct)
                    .ConfigureAwait(false);
            }

            var change = existing is null
                ? classifier.ClassifyAddition(nameof(ColumnDef))
                : classifier.Classify(nameof(ColumnDef), nameof(ColumnDef.IsRequired), hasDocuments);

            SaveTableRelationHandler.RejectBreaking(change, item.Code, hasDocuments, "Зміна");

            plan.Add(new PlannedColumn(
                ecrCode,
                new LocalizedText(new Dictionary<string, string>(command.HeaderL10n, StringComparer.OrdinalIgnoreCase)),
                command,
                existing,
                change,
                existing is null ? null : SaveColumnDefHandler.Describe(existing)));
        }

        // ── Один запис: мутації, один SaveChanges по Id нових, аудит, комміт ────────────────────────
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            var nextOrdinal = table.Columns.Count == 0 ? 0 : table.Columns.Max(c => c.Ordinal) + 1;
            var created = false;

            foreach (var step in plan)
            {
                if (step.Existing is null)
                {
                    var ordinal = step.Command.Ordinal ?? nextOrdinal;
                    nextOrdinal = Math.Max(nextOrdinal, ordinal + 1);

                    var column = new ColumnDef(tableDefId, step.Code, step.Header, ordinal, step.Command.DataType);
                    SaveColumnDefHandler.ApplyOptionalFields(column, step.Command);
                    table.AddColumn(column);
                    step.Column = column;
                    created = true;
                }
                else
                {
                    step.Existing.Rename(step.Header);
                    SaveColumnDefHandler.ApplyOptionalFields(step.Existing, step.Command);

                    if (step.Command.Ordinal is { } ordinal)
                    {
                        step.Existing.Reorder(ordinal);
                    }

                    step.Column = step.Existing;
                }
            }

            // Id нових колонок потрібні аудиту: один SaveChanges на всю пачку, а не на колонку.
            if (created)
            {
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }

            foreach (var step in plan)
            {
                var column = step.Column!;
                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        clock.UtcNow, templateVersionId, nameof(ColumnDef), column.Id,
                        step.Change, step.OldJson is null ? "Create" : "Update",
                        step.OldJson, SaveColumnDefHandler.Describe(column), ChangeReason: null,
                        ChangedByUserId: userId, CorrelationId: null),
                    innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Один скид кешу метаданих на пачку (в одиночному записі — на колонку).
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return [.. plan.Select(step => SaveColumnDefHandler.Map(step.Column!))];
    }

    /// <summary>Перевірений крок пачки: усе, що потрібно застосуванню й аудиту.</summary>
    private sealed class PlannedColumn(
        EcrCode code, LocalizedText header, SaveColumnDefCommand command, ColumnDef? existing,
        ChangeClass change, string? oldJson)
    {
        public EcrCode Code { get; } = code;

        public LocalizedText Header { get; } = header;

        public SaveColumnDefCommand Command { get; } = command;

        public ColumnDef? Existing { get; } = existing;

        public ChangeClass Change { get; } = change;

        public string? OldJson { get; } = oldJson;

        public ColumnDef? Column { get; set; }
    }
}

/// <summary>Колонка пачки: код і налаштування, як у одиночному <c>PUT …/columns/{code}</c>.</summary>
/// <param name="Code">Код колонки.</param>
/// <param name="Command">Налаштування колонки.</param>
public sealed record SaveColumnDefItem(string Code, SaveColumnDefCommand Command);
