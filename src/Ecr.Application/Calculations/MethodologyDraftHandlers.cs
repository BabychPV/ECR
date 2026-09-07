// src/Ecr.Application/Calculations/MethodologyDraftHandlers.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Усі версії методології, **включно з чернетками** (ФВ-9.15).
/// </summary>
/// <remarks>
/// ⛔ Окремий обробник, а не прапорець у <see cref="ListMethodologiesHandler"/>.
/// Той перелік описує, чим РАХУЮТЬ: він бере лише опубліковані версії й
/// обчислює межу вікна дії з початку наступної. Чернетка вікна не має, і
/// підмішати її туди означало б, що версія без дати потрапляє в перелік, за
/// яким вибирають методологію на період.
/// </remarks>
public sealed class ListMethodologyVersionsHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання версій (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає версії методології.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версії в порядку номера.</returns>
    /// <exception cref="NotFoundException">Методології немає.</exception>
    public async Task<IReadOnlyList<MethodologyDraftVersionDto>> HandleAsync(
        int methodologyId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var methodology = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Методології {methodologyId} не існує.");

        var versions = await drafts.GetAllVersionsAsync(methodologyId, ct).ConfigureAwait(false);

        return [.. versions.Select(Map)];
    }

    /// <summary>Складає DTO версії.</summary>
    /// <param name="version">Версія методології.</param>
    /// <returns>Версія для конфігуратора.</returns>
    /// <remarks>
    /// ⚠ <c>IsEditable</c> рахується з <c>Status</c> ТУТ, а не в клієнті.
    /// Клієнт, який виводить це сам, тримає другу копію правила «опублікована
    /// незмінна» (ФВ-13.2) — і саме вона розійдеться з доменом, коли з'явиться
    /// третій стан.
    /// </remarks>
    public static MethodologyDraftVersionDto Map(MethodologyVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new MethodologyDraftVersionDto(
            version.Id,
            version.Version,
            version.Status,
            version.Level,
            version.EffectiveFrom,
            version.NumericMode,
            version.CalendarMode,
            version.TraceLevel,
            version.Status == TemplateVersionStatus.Draft,
            version.CreatedByUserId);
    }
}

/// <summary>
/// Створює версію-чернетку — порожню або як **клон** наявної (ФВ-9.1).
/// </summary>
/// <remarks>
/// ⛔ Це єдиний спосіб змінити опубліковану версію. Вона незмінна за
/// побудовою (ФВ-13.2), бо на її числа вже посилаються подані форми; «зміна» —
/// це нова версія з власним вікном дії, і саме тому клонування є частиною
/// редагування, а не зручністю.
/// </remarks>
public sealed class CreateMethodologyVersionHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⚠ Саме <c>EditFormula</c>, а не <c>Publish</c>. Чернетка нічого не
    /// рахує і нічого не змінює в поданих числах, доки її не опублікують;
    /// вимагати для неї небезпечне право означало б, що методолог не може
    /// почати роботу без старшого методолога.
    /// </remarks>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Створює чернетку.</summary>
    /// <param name="methodologyId">Методологія-контейнер.</param>
    /// <param name="versionNumber">Номер нової версії; унікальний у межах методології.</param>
    /// <param name="copyFromVersionId">
    /// Версія-джерело; <c>null</c> — порожня чернетка.
    /// </param>
    /// <param name="level">Рівень драбини виразності для порожньої чернетки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Створену чернетку.</returns>
    /// <exception cref="NotFoundException">Методології або версії-джерела немає.</exception>
    /// <exception cref="BusinessRuleException">Номер версії вже зайнято.</exception>
    public async Task<MethodologyDraftVersionDto> HandleAsync(
        int methodologyId,
        string versionNumber,
        int? copyFromVersionId,
        CalculationLevel level,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionNumber);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Анонімний запит не створює версій методології.");

        var methodology = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Методології {methodologyId} не існує.");

        var draft = await BuildAsync(methodology, versionNumber, copyFromVersionId, level, userId, ct)
            .ConfigureAwait(false);

        // ⛔ Номер версії перевіряє АГРЕГАТ, а не запит. Два однакові номери
        // роблять посилання в аудиті неоднозначним заднім числом, і побачити
        // це можна лише поруч із сусідніми версіями (`UQ_MethodologyVersion`).
        methodology.AddVersion(draft);

        await drafts.SaveDraftAsync(draft, copyFromVersionId, ct).ConfigureAwait(false);

        return ListMethodologyVersionsHandler.Map(draft);
    }

    /// <summary>Готує чернетку: клон джерела або порожню версію.</summary>
    /// <param name="methodology">Методологія-контейнер.</param>
    /// <param name="versionNumber">Номер нової версії.</param>
    /// <param name="copyFromVersionId">Версія-джерело; <c>null</c> — порожня.</param>
    /// <param name="level">Рівень для порожньої чернетки.</param>
    /// <param name="userId">Автор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Чернетку, ще не додану до агрегату.</returns>
    private async Task<MethodologyVersion> BuildAsync(
        Methodology methodology,
        string versionNumber,
        int? copyFromVersionId,
        CalculationLevel level,
        int userId,
        CancellationToken ct)
    {
        if (copyFromVersionId is not { } sourceId)
        {
            return new MethodologyVersion(methodology.Id, versionNumber, level, userId, clock.UtcNow);
        }

        var source = await drafts.FindVersionAsync(sourceId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {sourceId} не існує.");

        // ⚠ Джерело мусить належати ЦІЙ методології. Інакше клон успадкував би
        // формули чужого модуля, лишившись у переліку версій цього — і
        // розбіжність було б видно вже тільки в числах.
        if (source.MethodologyId != methodology.Id)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Версія {sourceId} належить методології {source.MethodologyId}, "
                + $"а не {methodology.Id}: клонувати її сюди не можна.");
        }

        // ⛔ Рівень береться з ДЖЕРЕЛА, а не з запиту. Клон, який змінив рівень
        // драбини (ФВ-9.2), — це вже інша методологія, а не її нова редакція.
        return source.CloneAsDraft(versionNumber, userId, clock.UtcNow);
    }
}

/// <summary>Формули версії методології (ФВ-9.15).</summary>
public sealed class ListMethodologyFormulasHandler(
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання формул (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає формули версії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Формули в порядку обчислення.</returns>
    public async Task<IReadOnlyList<MethodologyFormulaDto>> HandleAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var formulas = await methodologies
            .GetFormulasAsync(methodologyVersionId, ct)
            .ConfigureAwait(false);

        return [.. formulas.Select(f => new MethodologyFormulaDto(
            f.Id, f.Code, f.Expression, f.ResultType, f.OutputUnitId, f.EvaluationOrder))];
    }
}

/// <summary>
/// Заводить або змінює формулу **чернетки** (ФВ-9.15).
/// </summary>
/// <remarks>
/// ⛔ Обробник не перевіряє стану версії сам. Перевіряє домен —
/// <c>MethodologyVersion.AddFormula</c> і <c>EditFormula</c>, — і це не
/// формальність: другий обробник, який колись з'явиться (константи, правила),
/// не зобов'язаний пам'ятати про правило, а викликати домен зобов'язаний.
///
/// ⚠ Синтаксис виразу тут НЕ перевіряється, і це рішення, а не пропуск.
/// Чернетка — робоче місце: вираз у ній буває недописаний, і відмовляти в
/// збереженні означало б змусити людину тримати недописане в голові.
/// Розбирає й відхиляє його публікація (ФВ-9.14, <c>FORMULA_NOT_FOUND</c> і
/// сусіди), а показує при введенні редактор виразів (ФВ-9.15a).
/// </remarks>
public sealed class SaveMethodologyFormulaHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Записує формулу; створює її, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="expression">Вираз діалекту методологій.</param>
    /// <param name="resultType">Число чи текст.</param>
    /// <param name="outputUnitId">Одиниця результату; <c>null</c> — зняти.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записану формулу.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія опублікована, вираз порожній або одиниця стоїть на тексті.
    /// </exception>
    public async Task<MethodologyFormulaDto> HandleAsync(
        int methodologyVersionId,
        string code,
        string expression,
        FormulaResultType resultType,
        int? outputUnitId,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var formulaCode = EcrCode.Create(code);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var existing = await drafts
            .FindFormulaAsync(methodologyVersionId, formulaCode.Value, ct)
            .ConfigureAwait(false);

        MethodologyFormula formula;

        if (existing is null)
        {
            formula = version.AddFormula(formulaCode, expression, resultType, outputUnitId);
            drafts.Add(formula);
        }
        else
        {
            version.EditFormula(existing, expression, resultType, outputUnitId);
            formula = existing;
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new MethodologyFormulaDto(
            formula.Id,
            formula.Code,
            formula.Expression,
            formula.ResultType,
            formula.OutputUnitId,
            formula.EvaluationOrder);
    }
}

/// <summary>Прибирає формулу з версії-чернетки (ФВ-9.15).</summary>
public sealed class DeleteMethodologyFormulaHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування формул (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Видаляє формулу.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або формули немає.</exception>
    /// <exception cref="BusinessRuleException">Версія опублікована.</exception>
    /// <remarks>
    /// ⚠ Видалення формули з ОПУБЛІКОВАНОЇ версії не буває навіть теоретично:
    /// на її результат посилаються вже пораховані числа, і рядок
    /// <c>calc.CalculationResult</c> без формули неможливо ні пояснити, ні
    /// відтворити (ФВ-9.13).
    /// </remarks>
    public async Task HandleAsync(int methodologyVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var formula = await drafts.FindFormulaAsync(methodologyVersionId, code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Формули «{code}» у версії {methodologyVersionId} немає.");

        version.RemoveFormula(formula);
        drafts.Remove(formula);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
