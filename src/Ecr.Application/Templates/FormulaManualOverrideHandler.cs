using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Templates;

/// <summary>
/// ✎ D-461: вмикає/вимикає «Дозволити ручне значення» на формулі шаблону — і в ОПУБЛІКОВАНІЙ версії, без нової
/// версії (налаштування, а не структура; рішення людини 30.10).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Право — <c>Template.Edit</c> (ГЛОБАЛЬНЕ, <c>PermissionScopes.Global</c>): той самий носій, що правка структури й
/// презентаційний патч опублікованої версії. Лічильник у відповіді — лише ДВА числа (документи, комірки) по всій версії,
/// без ідентифікаторів, назв чи проєктів: носій глобального права шаблонів і так бачить усі версії шаблону, а
/// конкретних документів відповідь не розкриває.</item>
/// <item>Увімкнення діє одразу для всіх документів версії (кеш знімка інвалідується). Вимкнення наявних ручних значень
/// НЕ стирає (вони лишаються, перерахунок їх не чіпає, їх можна повернути до формули), лише забороняє нові.</item>
/// <item><c>dryRun</c> — нічого не змінює, лише повертає лічильник: клієнт попереджає ПЕРЕД вимкненням.</item>
/// <item>Зміна — рядок <c>aud.StructureChange</c> (<c>EntityType = FormulaDef</c>, клас <c>Presentation</c>, старе й нове
/// значення JSON-ом) у тій самій транзакції; рядок версії блокується першим (порядок C5, як у презентаційного патча).</item>
/// </list>
/// </remarks>
public sealed class FormulaManualOverrideHandler(
    IFormulaOverrideStore store,
    ITemplateVersionStore versions,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право ендпоінта.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Застосовує (або лише рахує, <paramref name="dryRun"/>) зміну прапорця.</summary>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — у версії немає такої формули.</exception>
    public async Task<FormulaManualOverrideResult> HandleAsync(
        int templateVersionId, int formulaId, bool allow, bool dryRun, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var target = await store.FindFormulaAsync(templateVersionId, formulaId, ct).ConfigureAwait(false)
                     ?? throw NotFound(templateVersionId, formulaId);

        if (dryRun || target.AllowManualOverride == allow)
        {
            var counts = await store.CountAsync(target, ct).ConfigureAwait(false);
            return new FormulaManualOverrideResult(formulaId, target.AllowManualOverride, Changed: false, counts.Documents, counts.Cells);
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ C5: рядок версії ПЕРШИМ (стан тут не перевіряється — налаштування законне й на опублікованій версії).
            await versions.LockVersionForUpdateAsync(templateVersionId, innerCt).ConfigureAwait(false);
            await store.SetAllowManualOverrideAsync(formulaId, allow, innerCt).ConfigureAwait(false);

            // ⛔ Ревізія знімка піднімається, як у презентаційного патча: ключ кешу знімка (і похідних — «позначки є»
            // у перерахунку) несе ревізію, тож УСІ вузли, а не лише цей, бачать новий прапорець.
            await versions.IncrementPresentationRevisionAsync(templateVersionId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, "FormulaDef", formulaId,
                    ChangeClass.Presentation, "Update",
                    OldJson: Json(!allow), NewJson: Json(allow), ChangeReason: null,
                    ChangedByUserId: profile.UserId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Увімкнення/вимкнення має діяти одразу: знімок версії (рішення доступу, зріз, запис) — з кешу.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        var after = await store.CountAsync(target, ct).ConfigureAwait(false);
        return new FormulaManualOverrideResult(formulaId, allow, Changed: true, after.Documents, after.Cells);
    }

    private static string Json(bool allow)
        => allow ? "{\"AllowManualOverride\":true}" : "{\"AllowManualOverride\":false}";

    private static NotFoundException NotFound(int templateVersionId, int formulaId)
        => new(
            "ECR-TMPL-0404",
            $"Формули {formulaId} у версії {templateVersionId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.formulaById",
                ["formulaId"] = formulaId.ToString(CultureInfo.InvariantCulture),
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>✎ D-461: відповідь на зміну «Дозволити ручне значення».</summary>
/// <param name="FormulaId">Формула.</param>
/// <param name="AllowManualOverride">Прапорець ПІСЛЯ виклику.</param>
/// <param name="Changed">Чи прапорець справді змінився (<c>false</c> — dryRun або вже був таким).</param>
/// <param name="OverriddenDocuments">У скількох документах версії зараз є ручні значення замість цієї формули.</param>
/// <param name="OverriddenCells">Скільки комірок усього.</param>
public sealed record FormulaManualOverrideResult(
    int FormulaId, bool AllowManualOverride, bool Changed, int OverriddenDocuments, int OverriddenCells);
