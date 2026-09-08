// src/Ecr.Application/Templates/PeriodAccessRuleHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Авторство правил доступу до періоду версії-чернетки через API (<c>ФВ-2.15</c>,
/// W5.4 — другий зріз цього завдання поруч із <c>ValidationRuleHandlers</c>).
/// </summary>
/// <remarks>
/// ⛔ <b>Три дії, а не дві.</b> <c>SheetDef</c> і <c>ValidationRule</c> мають
/// природний код (<c>EcrCode</c>) із унікальним індексом, тому одна дія
/// <c>PUT …/{code}</c> і створює, і змінює (<c>D2-147</c>). У
/// <c>PeriodAccessRuleDef</c> НЕМАЄ поля <c>Code</c> і НЕМАЄ жодного
/// унікального індексу (перевірено в
/// <c>PeriodAccessRuleDefConfiguration</c>: лише <c>CK_PAR_Target</c>,
/// <c>CK_PAR_Range</c>, <c>CK_PAR_Kind</c> — обмеження цілісності, не
/// ідентичності). Створити правило можна лише фабричним методом з
/// обов'язковим для свого виду параметром (<see cref="PeriodAccessRuleDef.AlwaysReadOnly"/>,
/// <see cref="PeriodAccessRuleDef.HeaderRows"/>,
/// <see cref="PeriodAccessRuleDef.EditablePeriodOnly"/>,
/// <see cref="PeriodAccessRuleDef.ForRelativeWindow"/>,
/// <see cref="PeriodAccessRuleDef.ForSourceWindow"/>,
/// <see cref="PeriodAccessRuleDef.ForExpression"/>), і єдина адреса, яку
/// сутність узагалі має, — це <c>Id</c>, який призначає база вже ПІСЛЯ
/// <c>SaveChanges</c>. <c>PUT</c> за адресою, якої до створення не існує, не
/// може одночасно й створювати: тому тут <c>POST</c> заводить нове правило
/// (<see cref="CreatePeriodAccessRuleHandler"/>), а <c>PUT …/{id}</c> лише
/// змінює наявне (<see cref="SavePeriodAccessRuleHandler"/>) — те саме
/// розходіння з чотирма методами W5.0, яке звіт цього зрізу пояснює окремо.
///
/// ⚠ <c>RuleKind</c> і специфічний для нього параметр (<c>FromSequence</c>/
/// <c>ToSequence</c>, <c>RelativeOffset</c>, <c>SourceColumnDefId</c>,
/// <c>ConditionExpr</c>) НЕ редагуються через <c>PUT</c>: вони визначають, ЩО
/// робить правило, а не ЯК воно застосовується. Це той самий поділ, що й у
/// <c>SheetDef</c> — <c>Code</c> незмінний після створення, решта полів
/// редагується вільно. Щоб змінити вид правила чи його параметр — видаліть і
/// заведіть заново; часткова зміна виду без зміни параметра дала б
/// непослідовний стан (наприклад, <c>SourceWindow</c> без
/// <c>SourceColumnDefId</c>), якого не існує в базі (<c>CK_PAR_Kind</c>).
///
/// ⛔ На відміну від <c>ValidationRuleHandlers.cs</c>, тут НЕМАЄ виклику
/// <see cref="IMetadataCache.InvalidateAsync"/>. Причина — не забудькуватість,
/// а перевірений факт: <c>MetadataCache.LoadAsync</c> (`Ecr.Infrastructure`)
/// узагалі не завантажує <c>PeriodAccessRuleDef</c> — ні в кешований знімок,
/// ні деінде. Усі споживачі читають правила НАПРЯМУ з бази через
/// <see cref="ITemplateVersionStore.ListPeriodAccessRulesAsync"/>
/// (<c>AsNoTracking</c>, без кешу) — так само робить і
/// <c>GetAccessMatrixHandler</c>. Інвалідація кешу, якого правила ніколи не
/// торкаються, нічого не змінила б: той самий висновок, з якого
/// <c>SaveTableRelationHandler</c>/<c>DeleteTableRelationHandler</c> теж не
/// викликають <c>InvalidateAsync</c> — <c>TableRelationDef</c> так само
/// повз кеш.
/// </remarks>
public static class PeriodAccessRuleMapper
{
    /// <summary>Складає DTO правила для відповіді.</summary>
    public static PeriodAccessRuleDto Map(PeriodAccessRuleDef rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new PeriodAccessRuleDto(
            rule.Id, rule.TemplateVersionId, rule.RuleKind, rule.SheetDefId, rule.TableDefId,
            rule.RoleId, rule.RowKind, rule.FromSequence, rule.ToSequence, rule.SourceColumnDefId,
            rule.RelativeOffset, rule.ConditionExpr, rule.OnOutOfWindow);
    }

    /// <summary>Стан правила для аудиту.</summary>
    public static string Describe(PeriodAccessRuleDef rule)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            RuleKind = rule.RuleKind.ToString(),
            rule.SheetDefId,
            rule.TableDefId,
            rule.RoleId,
            RowKind = rule.RowKind?.ToString(),
            rule.FromSequence,
            rule.ToSequence,
            rule.SourceColumnDefId,
            rule.RelativeOffset,
            rule.ConditionExpr,
            OnOutOfWindow = rule.OnOutOfWindow.ToString(),
        });

    /// <summary>
    /// Перевіряє <c>CK_PAR_Target</c> на рівні застосунку — до звернення в базу.
    /// </summary>
    /// <exception cref="BusinessRuleException">Ні аркуш, ні таблиця не задані.</exception>
    public static void EnsureHasTarget(int? sheetDefId, int? tableDefId)
    {
        if (sheetDefId is null && tableDefId is null)
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                "Правило доступу до періоду має стосуватися аркуша або таблиці: " +
                "порожні обидва означають правило, що не діє ніде (CK_PAR_Target).");
        }
    }

    /// <summary>Перевіряє, що аркуш і таблиця (якщо задані) належать цій версії.</summary>
    /// <exception cref="BusinessRuleException">Аркуш або таблиця чужі версії.</exception>
    public static void EnsureBelongs(
        Domain.Entities.Configuration.TemplateVersion version, int? sheetDefId, int? tableDefId)
    {
        if (sheetDefId is { } sheet && version.Sheets.All(s => s.Id != sheet))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Аркуша {sheet} у версії {version.Id} немає: зовнішній ключ це прийняв би, " +
                "а версія перестала б бути замкненою одиницею.");
        }

        if (tableDefId is { } table && version.Sheets.SelectMany(s => s.Tables).All(t => t.Id != table))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                $"Таблиці {table} у версії {version.Id} немає: зовнішній ключ це прийняв би, " +
                "а версія перестала б бути замкненою одиницею.");
        }
    }

    /// <summary>Будує правило за фабрикою, обраною <see cref="PeriodAccessRuleKind"/>.</summary>
    /// <exception cref="BusinessRuleException">Обов'язковий для виду параметр не заданий.</exception>
    public static PeriodAccessRuleDef Build(int templateVersionId, CreatePeriodAccessRuleCommand command)
    {
        var rule = command.RuleKind switch
        {
            PeriodAccessRuleKind.AlwaysReadOnly =>
                PeriodAccessRuleDef.AlwaysReadOnly(templateVersionId, command.OnOutOfWindow),

            PeriodAccessRuleKind.HeaderRows =>
                PeriodAccessRuleDef.HeaderRows(templateVersionId, command.OnOutOfWindow),

            PeriodAccessRuleKind.EditablePeriodOnly =>
                PeriodAccessRuleDef.EditablePeriodOnly(
                    templateVersionId, command.OnOutOfWindow, command.FromSequence, command.ToSequence),

            PeriodAccessRuleKind.RelativeWindow =>
                PeriodAccessRuleDef.ForRelativeWindow(
                    templateVersionId, command.RelativeOffset ?? 0, command.OnOutOfWindow),

            PeriodAccessRuleKind.SourceWindow =>
                PeriodAccessRuleDef.ForSourceWindow(
                    templateVersionId,
                    command.SourceColumnDefId ?? throw new BusinessRuleException(
                        ErrorCodes.TemplateInvalid,
                        "Вид SourceWindow потребує SourceColumnDefId: без нього правило " +
                        "виглядає налаштованим і не блокує нічого (CK_PAR_Kind)."),
                    command.OnOutOfWindow),

            PeriodAccessRuleKind.Expression =>
                PeriodAccessRuleDef.ForExpression(
                    templateVersionId, command.ConditionExpr ?? string.Empty, command.OnOutOfWindow),

            _ => throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid, $"Невідомий вид правила: {command.RuleKind}."),
        };

        rule.ForSheet(command.SheetDefId).ForTable(command.TableDefId)
            .ForRole(command.RoleId).ForRows(command.RowKind);

        return rule;
    }
}

/// <summary>Заводить нове правило доступу до періоду (<c>ФВ-2.15</c>).</summary>
public sealed class CreatePeriodAccessRuleHandler(
    ITemplateVersionStore store,
    IRepository<PeriodAccessRuleDef, int> rules,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює правило.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="command">Вид і налаштування правила.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Створене правило разом із призначеним <c>Id</c>.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена, обов'язковий параметр виду не заданий,
    /// або ні аркуш, ні таблиця не вказані.
    /// </exception>
    public async Task<PeriodAccessRuleDto> HandleAsync(
        int templateVersionId, CreatePeriodAccessRuleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ ПЕРШИМ ділом і доменом — так само, як SaveSheetDefHandler.
        version.EnsureStructurallyMutable();

        PeriodAccessRuleMapper.EnsureHasTarget(command.SheetDefId, command.TableDefId);
        PeriodAccessRuleMapper.EnsureBelongs(version, command.SheetDefId, command.TableDefId);

        var rule = PeriodAccessRuleMapper.Build(templateVersionId, command);

        rules.Add(rule);

        // ⛔ Запис ПЕРЕД аудитом — щойно доданій сутності ще бракує Id до
        // SaveChanges (той самий привід, що в SaveSheetDefHandler).
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(PeriodAccessRuleDef), rule.Id,
                classifier.ClassifyAddition(nameof(PeriodAccessRuleDef)), "Create",
                OldJson: null, PeriodAccessRuleMapper.Describe(rule), ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return PeriodAccessRuleMapper.Map(rule);
    }
}

/// <summary>Налаштування нового правила, що приходять із форми.</summary>
/// <param name="RuleKind">Вид правила; визначає обов'язковий параметр нижче.</param>
/// <param name="OnOutOfWindow">Поведінка поза вікном; не <c>Hide</c>.</param>
/// <param name="SheetDefId">Аркуш, якого стосується правило.</param>
/// <param name="TableDefId">Таблиця, якої стосується правило.</param>
/// <param name="RoleId"><c>null</c> — правило діє для всіх ролей.</param>
/// <param name="RowKind"><c>null</c> — на всі види рядків.</param>
/// <param name="FromSequence">Для <c>EditablePeriodOnly</c>: від якого номера періоду.</param>
/// <param name="ToSequence">Для <c>EditablePeriodOnly</c>: до якого номера періоду.</param>
/// <param name="SourceColumnDefId">Для <c>SourceWindow</c>: колонка-джерело вікна; обов'язкова.</param>
/// <param name="RelativeOffset">Для <c>RelativeWindow</c>: зсув ±N періодів; обов'язковий, додатний.</param>
/// <param name="ConditionExpr">Для <c>Expression</c>: булевий вираз; обов'язковий.</param>
public sealed record CreatePeriodAccessRuleCommand(
    PeriodAccessRuleKind RuleKind,
    OutOfWindowBehavior OnOutOfWindow,
    int? SheetDefId,
    int? TableDefId,
    int? RoleId,
    RowKind? RowKind,
    byte? FromSequence,
    byte? ToSequence,
    int? SourceColumnDefId,
    short? RelativeOffset,
    string? ConditionExpr);

/// <summary>Правило доступу до періоду для відповіді API.</summary>
/// <param name="Id">Ідентифікатор правила; адреса <c>PUT</c>/<c>DELETE …/{id}</c>.</param>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="RuleKind">Вид правила.</param>
/// <param name="SheetDefId">Аркуш; <c>null</c> — не обмежено аркушем.</param>
/// <param name="TableDefId">Таблиця; <c>null</c> — не обмежено таблицею.</param>
/// <param name="RoleId"><c>null</c> — для всіх ролей.</param>
/// <param name="RowKind"><c>null</c> — на всі види рядків.</param>
/// <param name="FromSequence">Від якого номера періоду; <c>null</c> — без обмеження.</param>
/// <param name="ToSequence">До якого номера періоду; <c>null</c> — без обмеження.</param>
/// <param name="SourceColumnDefId">Колонка-джерело вікна (<c>SourceWindow</c>).</param>
/// <param name="RelativeOffset">Зсув ±N періодів (<c>RelativeWindow</c>).</param>
/// <param name="ConditionExpr">Умова (<c>Expression</c>).</param>
/// <param name="OnOutOfWindow">Поведінка поза вікном.</param>
public sealed record PeriodAccessRuleDto(
    int Id,
    int TemplateVersionId,
    PeriodAccessRuleKind RuleKind,
    int? SheetDefId,
    int? TableDefId,
    int? RoleId,
    RowKind? RowKind,
    byte? FromSequence,
    byte? ToSequence,
    int? SourceColumnDefId,
    short? RelativeOffset,
    string? ConditionExpr,
    OutOfWindowBehavior OnOutOfWindow);

/// <summary>
/// Змінює наявне правило доступу до періоду: прив'язку до аркуша/таблиці/ролі/
/// виду рядків і поведінку поза вікном (<c>ФВ-2.15</c>, <c>ФВ-2.16</c>).
/// </summary>
/// <remarks>
/// ⚠ <c>RuleKind</c> і специфічний параметр виду тут НЕ приймаються —
/// докладніше в зауваженні класу <see cref="PeriodAccessRuleMapper"/> вище.
/// </remarks>
public sealed class SavePeriodAccessRuleHandler(
    ITemplateVersionStore store,
    IRepository<PeriodAccessRuleDef, int> rules,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Змінює правило.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="ruleId">Правило.</param>
    /// <param name="command">Нові прив'язка й поведінка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Змінене правило.</returns>
    /// <exception cref="NotFoundException">Версії або правила немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена, або ні аркуш, ні таблиця не вказані.
    /// </exception>
    public async Task<PeriodAccessRuleDto> HandleAsync(
        int templateVersionId, int ruleId, UpdatePeriodAccessRuleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var rule = await rules.FindAsync(ruleId, ct).ConfigureAwait(false);

        if (rule is null || rule.TemplateVersionId != templateVersionId)
        {
            // ⚠ Перевірка належності версії — той самий привід, що
            // ListTableCodesAsync у TableRelationHandlers: ідентифікатор
            // приходить із мережі, і без цієї умови PUT однієї версії міг би
            // змінити правило чужої.
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Правила {ruleId} у версії {templateVersionId} немає.");
        }

        PeriodAccessRuleMapper.EnsureHasTarget(command.SheetDefId, command.TableDefId);
        PeriodAccessRuleMapper.EnsureBelongs(version, command.SheetDefId, command.TableDefId);

        var oldJson = PeriodAccessRuleMapper.Describe(rule);

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.Classify(
            nameof(PeriodAccessRuleDef), nameof(PeriodAccessRuleDef.OnOutOfWindow), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), hasDocuments, "Зміна");

        rule.SetOutOfWindowBehavior(command.OnOutOfWindow);
        rule.ForSheet(command.SheetDefId).ForTable(command.TableDefId)
            .ForRole(command.RoleId).ForRows(command.RowKind);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(PeriodAccessRuleDef), rule.Id,
                change, "Update",
                oldJson, PeriodAccessRuleMapper.Describe(rule), ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return PeriodAccessRuleMapper.Map(rule);
    }
}

/// <summary>Прив'язка й поведінка правила, що приходять із форми редагування.</summary>
/// <param name="OnOutOfWindow">Поведінка поза вікном; не <c>Hide</c>.</param>
/// <param name="SheetDefId">Аркуш, якого стосується правило.</param>
/// <param name="TableDefId">Таблиця, якої стосується правило.</param>
/// <param name="RoleId"><c>null</c> — правило діє для всіх ролей.</param>
/// <param name="RowKind"><c>null</c> — на всі види рядків.</param>
public sealed record UpdatePeriodAccessRuleCommand(
    OutOfWindowBehavior OnOutOfWindow,
    int? SheetDefId,
    int? TableDefId,
    int? RoleId,
    RowKind? RowKind);

/// <summary>Прибирає правило доступу до періоду — фізично (<c>ФВ-2.15</c>).</summary>
/// <remarks>
/// ⛔ Фізичне видалення, а не м'яке: на правило доступу ніхто не посилається
/// за ідентифікатором (жодна інша сутність не зберігає
/// <c>PeriodAccessRuleDefId</c>) — той самий висновок, що й для
/// <c>ValidationRule</c>/<c>TableRelationDef</c>.
/// </remarks>
public sealed class DeletePeriodAccessRuleHandler(
    ITemplateVersionStore store,
    IRepository<PeriodAccessRuleDef, int> rules,
    ChangeClassifier classifier,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє правило.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="ruleId">Правило.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або правила немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    /// <exception cref="ConcurrencyConflictException">На версії є документи.</exception>
    public async Task HandleAsync(int templateVersionId, int ruleId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var rule = await rules.FindAsync(ruleId, ct).ConfigureAwait(false);

        if (rule is null || rule.TemplateVersionId != templateVersionId)
        {
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Правила {ruleId} у версії {templateVersionId} немає.");
        }

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(PeriodAccessRuleDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(
            change, ruleId.ToString(System.Globalization.CultureInfo.InvariantCulture), hasDocuments, "Видалення");

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow, templateVersionId, nameof(PeriodAccessRuleDef), rule.Id,
                change, "Delete",
                PeriodAccessRuleMapper.Describe(rule), NewJson: null, ChangeReason: null,
                ChangedByUserId: userId, CorrelationId: null),
            ct).ConfigureAwait(false);

        rules.Remove(rule);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
