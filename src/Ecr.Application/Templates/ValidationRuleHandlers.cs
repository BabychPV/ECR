// src/Ecr.Application/Templates/ValidationRuleHandlers.cs
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
/// Записує правило валідації таблиці версії-чернетки: продовження зрізу
/// авторства структури шаблону через API на <c>ValidationRule</c> (W5.4,
/// за зразком <c>SaveSheetDefHandler</c> — <c>ФВ-2.1</c>..<c>ФВ-2.5</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу <c>ValidationRule</c> створював лише <c>Ecr.DataGen</c> і
/// тести напряму через конструктор: авторства правил валідації через API не
/// існувало взагалі.
///
/// ⚠ <c>PUT</c> за кодом, вкладеним у <b>таблицю</b> (<c>tableDefId</c>), а не
/// в аркуш чи версію: сама сутність адресується таблицею
/// (<c>cfg.ValidationRule.TableDefId</c>, унікальний індекс
/// <c>UQ_ValidationRule</c> на <c>(TableDefId, Code)</c>), і код правила
/// унікальний лише в межах ОДНІЄЇ таблиці — той самий код в іншій таблиці
/// версії позначає інше правило. Адреса відтворює це один в один
/// (<c>D2-147</c>).
///
/// ⛔ Читає й пише через <see cref="ITemplateVersionStore.GetWithStructureAsync"/>
/// — <c>TableDef.ValidationRules</c> уже включений у цей граф (на відміну від
/// <c>PeriodAccessRuleDef</c>, який до нього не належить, W5.4 §2). Тому тут,
/// на відміну від <c>PeriodAccessRuleHandlers.cs</c>, потрібен той самий
/// виклик <see cref="IMetadataCache.InvalidateAsync"/>, що й у
/// <c>SaveSheetDefHandler</c>: правила валідації читає кешований знімок
/// (<c>ValidateDocumentHandler</c>), і без інвалідації прогрітий кеш
/// продовжував би застосовувати старий склад правил.
/// </remarks>
public sealed class SaveValidationRuleHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює правило валідації.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить правило.</param>
    /// <param name="code">Код правила; унікальний у межах таблиці.</param>
    /// <param name="command">Налаштування правила.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережене правило.</returns>
    /// <exception cref="NotFoundException">Версії або таблиці немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>).
    /// </exception>
    public async Task<ValidationRuleDto> HandleAsync(
        int templateVersionId, int tableDefId, string code, SaveValidationRuleCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ ПЕРШИМ ділом і доменом — так само, як SaveSheetDefHandler.
        version.EnsureStructurallyMutable();

        var table = version.Sheets
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => t.Id == tableDefId)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Таблиці {tableDefId} у версії {templateVersionId} немає.");

        var ecrCode = EcrCode.Create(code);
        var message = new LocalizedText(new Dictionary<string, string>(command.MessageL10n, StringComparer.OrdinalIgnoreCase));

        var existing = table.ValidationRules.FirstOrDefault(
            r => string.Equals(r.Code, ecrCode.Value, StringComparison.Ordinal));

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж — та сама причина, що в
        // SaveSheetDefHandler: клас пишеться в аудит незалежно від того, чи
        // міг він щось зламати.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(ValidationRule))
            : classifier.Classify(nameof(ValidationRule), nameof(ValidationRule.Expression), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        // ⛔ Q-244: «запис → аудит → SaveChanges» — одним замиканням
        // `IUnitOfWork.ExecuteInTransactionAsync`, коміт рівно один,
        // наприкінці (той самий клас дефекту, що Q-243).
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (existing is null)
            {
                existing = new ValidationRule(
                    tableDefId, ecrCode, command.Severity, command.Scope, command.Expression, message,
                    command.ColumnDefId);
                existing.Update(
                    command.Severity, command.Scope, command.Expression, message, command.ColumnDefId,
                    command.IsActive);

                table.AddValidationRule(existing);

                // ⛔ Запис ПЕРЕД аудитом, і лише для створення — щойно доданій
                // сутності ще бракує Id до SaveChanges (той самий привід, що в
                // SaveSheetDefHandler).
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                existing.Update(
                    command.Severity, command.Scope, command.Expression, message, command.ColumnDefId,
                    command.IsActive);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(ValidationRule), existing.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(existing), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Без цього виклику GET-и, що читають правила валідації з
        // кешованого знімка (ValidateDocumentHandler), лишалися б зі старим
        // складом правил до публікації — той самий клас дефекту, що в
        // SaveSheetDefHandler.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ `existing` завжди присвоєно всередині щойно завершеного замикання
        // — той самий довід, що в `SaveColumnDefHandler`.
        return Map(existing!);
    }

    /// <summary>Складає DTO правила для відповіді.</summary>
    internal static ValidationRuleDto Map(ValidationRule rule)
        => new(
            rule.Id, rule.TableDefId, rule.Code, rule.Severity, rule.Scope, rule.ColumnDefId,
            rule.Expression, rule.MessageL10n, rule.IsActive);

    /// <summary>Стан правила для аудиту.</summary>
    internal static string Describe(ValidationRule rule)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            rule.Code,
            Severity = rule.Severity.ToString(),
            rule.Scope,
            rule.ColumnDefId,
            rule.Expression,
            MessageL10n = rule.MessageL10n.Values,
            rule.IsActive,
        });
}

/// <summary>Налаштування правила валідації, що приходять із форми.</summary>
/// <param name="Severity">Рівень: <c>Info</c>, <c>Warning</c>, <c>Error</c>.</param>
/// <param name="Scope">0 Cell, 1 Row, 2 Table, 3 Document.</param>
/// <param name="Expression">Предикат нашою мовою.</param>
/// <param name="MessageL10n">Текст порушення мовами каталогу.</param>
/// <param name="ColumnDefId">Колонка, до якої прив'язане правило; <c>null</c> — до всіх колонок таблиці.</param>
/// <param name="IsActive">Чи діє правило.</param>
public sealed record SaveValidationRuleCommand(
    ValidationSeverity Severity,
    byte Scope,
    string Expression,
    IReadOnlyDictionary<string, string> MessageL10n,
    int? ColumnDefId,
    bool IsActive);

/// <summary>Правило валідації для відповіді API.</summary>
/// <param name="Id">Ідентифікатор правила.</param>
/// <param name="TableDefId">Таблиця, якій належить правило.</param>
/// <param name="Code">Код правила; він же в повідомленні порушення.</param>
/// <param name="Severity">Рівень.</param>
/// <param name="Scope">0 Cell, 1 Row, 2 Table, 3 Document.</param>
/// <param name="ColumnDefId">Колонка; <c>null</c> — до всіх колонок таблиці.</param>
/// <param name="Expression">Предикат нашою мовою.</param>
/// <param name="MessageL10n">Текст порушення мовами каталогу.</param>
/// <param name="IsActive">Чи діє правило.</param>
public sealed record ValidationRuleDto(
    int Id,
    int TableDefId,
    string Code,
    ValidationSeverity Severity,
    byte Scope,
    int? ColumnDefId,
    string Expression,
    LocalizedText MessageL10n,
    bool IsActive);

/// <summary>
/// Прибирає правило валідації з таблиці чернетки — фізично, а не м'яко.
/// </summary>
/// <remarks>
/// ⛔ На відміну від <c>SheetDef.SoftDelete</c>: на правило валідації ніхто не
/// посилається за ідентифікатором (жодна інша сутність не зберігає
/// <c>ValidationRuleId</c>), тому м'яке видалення тут не рятує жодного
/// посилання, а лише накопичує неактивні рядки. Той самий висновок, що й для
/// <c>TableRelationDef.Remove</c> — фізичне видалення застосовне саме там, де
/// на сутність не посилаються (`Repository.Remove`, `05-skeleton.md` §4).
/// </remarks>
public sealed class DeleteValidationRuleHandler(
    ITemplateVersionStore store,
    IRepository<ValidationRule, int> validationRules,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє правило валідації.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="tableDefId">Таблиця, якій належить правило.</param>
    /// <param name="code">Код правила.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, таблиці або правила немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(int templateVersionId, int tableDefId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var table = version.Sheets
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => t.Id == tableDefId)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Таблиці {tableDefId} у версії {templateVersionId} немає.");

        var rule = table.ValidationRules.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Правила «{code}» у таблиці {tableDefId} немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(ValidationRule), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        // ⛔ Q-244: аудит і видалення/`SaveChanges` тепер одна транзакція.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(ValidationRule), rule.Id,
                    change, "Delete",
                    SaveValidationRuleHandler.Describe(rule), NewJson: null, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            // ⛔ Видаляється через IRepository<ValidationRule,int>, а НЕ через
            // TableDef: у сутності немає (і не заводимо заради самого лише
            // видалення) методу «прибрати з батьківської навігації» — TableDef
            // поза SCOPE цього зрізу (W5.4). EF позначає рядок на видалення за
            // самим трекованим екземпляром `rule`, незалежно від того, чи
            // прибрали його зі списку `TableDef.ValidationRules` — той самий
            // прийом, що й `DeleteTableRelationHandler.relations.Remove(...)`.
            validationRules.Remove(rule);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
