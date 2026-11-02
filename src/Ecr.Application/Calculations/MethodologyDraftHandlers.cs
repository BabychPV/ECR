// src/Ecr.Application/Calculations/MethodologyDraftHandlers.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
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
                "ECR-CALC-0404",
                $"Методології {methodologyId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.methodology",
                    ["methodologyId"] = methodologyId.ToString(CultureInfo.InvariantCulture),
                });

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

    /// <summary>Межа колонки <c>Version</c> у <c>MethodologyVersionConfiguration</c>.</summary>
    public const int MaxVersionLength = 20;

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
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ Номер приходить із тіла запиту: порожній давав `ArgumentException`, задовгий —
        // обрізання рядка в `calc.MethodologyVersion.Version`, і обидва ставали 500.
        if (string.IsNullOrWhiteSpace(versionNumber) || versionNumber.Length > MaxVersionLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.MethodologyInvalid,
                $"Номер версії методології — від 1 до {MaxVersionLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.versionNumber",
                    ["maxLength"] = MaxVersionLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не створює версій методології.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var methodology = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404",
                $"Методології {methodologyId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.methodology",
                    ["methodologyId"] = methodologyId.ToString(CultureInfo.InvariantCulture),
                });

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
                "ECR-CALC-0404",
                $"Версії методології {sourceId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.version",
                    ["methodologyVersionId"] = sourceId.ToString(CultureInfo.InvariantCulture),
                });

        // ⚠ Джерело мусить належати ЦІЙ методології. Інакше клон успадкував би
        // формули чужого модуля, лишившись у переліку версій цього — і
        // розбіжність було б видно вже тільки в числах.
        if (source.MethodologyId != methodology.Id)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Версія {sourceId} належить методології {source.MethodologyId}, "
                + $"а не {methodology.Id}: клонувати її сюди не можна.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.versionWrongMethodology",
                    ["versionId"] = sourceId.ToString(CultureInfo.InvariantCulture),
                    ["sourceMethodologyId"] = source.MethodologyId.ToString(CultureInfo.InvariantCulture),
                    ["targetMethodologyId"] = methodology.Id.ToString(CultureInfo.InvariantCulture),
                });
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

        return [.. formulas.Select(MethodologyFormulaDto.Of)];
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
    IUnitCatalog units,
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
    /// <param name="argumentsCsv">
    /// ⚠ ЗАСТАРІЛЕ поле (похідні аргументи, 2026-11-02; приймається один реліз). Список більше не вводиться:
    /// з нього беруться ЛИШЕ маркери <c>Name?</c> і переносяться у вираз (<c>@Name?</c>) — так скрипти
    /// <c>tools/land</c> (етап <c>OptionalArguments</c>) і старий клієнт працюють без змін. Решта ігнорується;
    /// <c>null</c> — нічого не переносити.
    /// </param>
    /// <param name="scope">
    /// Область (L2-1): <c>Row</c> - раз на рядок (її бачить правило категорії), <c>Substance</c> - на кожну
    /// речовину; <c>null</c> - не змінювати (нова формула - <c>Substance</c>).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записану формулу.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія опублікована, вираз порожній або одиниця стоїть на тексті.
    /// </exception>
    /// <remarks>
    /// ✎ 2026-11-02 (похідні аргументи): <c>ArgumentsCsv</c> більше НЕ вводиться — він ПОХІДНИЙ від виразу
    /// (<see cref="Ecr.Expressions.Binding.ArgumentDeclarationChecker.DeriveCsv"/>) і перераховується на кожному
    /// записі. Необов'язковість — маркер у самому виразі (<c>@Land_Measure_RunningHours?</c>). Колонка лишається,
    /// бо рантайм (<c>OptionalArguments</c>) і публікація читають маркери звідти — їхня семантика (об'єднання по
    /// версії) не змінюється. Звірка «токени == список» (<c>ECR-CALC-0432</c>) тепер завжди істинна і
    /// лишається твердженням публікації, а не перевіркою вводу.
    /// </remarks>
    public async Task<MethodologyFormulaDto> HandleAsync(
        int methodologyVersionId,
        string code,
        string expression,
        FormulaResultType resultType,
        int? outputUnitId,
        string? argumentsCsv,
        MethodologyFormulaScope? scope,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var formulaCode = EcrCode.Create(code);

        // L2-1: значення поза переліком (JSON-число, якого немає) - 422 з ключем каталогу, а не 500 з домену.
        if (scope is { } requested && !Enum.IsDefined(requested))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Область формули «{formulaCode.Value}» невідома: {(byte)requested}. Допустимі: Substance, Row.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.formulaScopeInvalid",
                    ["code"] = formulaCode.Value,
                    ["scope"] = ((byte)requested).ToString(CultureInfo.InvariantCulture),
                });
        }

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404",
                $"Версії методології {methodologyVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.version",
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                });

        // ⛔ B-01: неіснуюча одиниця — 422 з ключем, а не 500 на `FK_MF_Unit`.
        await MethodologyUnitChecks.RequireKnownAsync(units, outputUnitId, formulaCode.Value, ct)
            .ConfigureAwait(false);

        var existing = await drafts
            .FindFormulaAsync(methodologyVersionId, formulaCode.Value, ct)
            .ConfigureAwait(false);

        // ⚠ Застаріле поле: лише його маркери `Name?` переходять у вираз (один реліз сумісності).
        var text = LegacyMarkers(expression, argumentsCsv);

        MethodologyFormula formula;

        if (existing is null)
        {
            formula = version.AddFormula(formulaCode, text, resultType, outputUnitId);
            drafts.Add(formula);
        }
        else
        {
            version.EditFormula(existing, text, resultType, outputUnitId);
            formula = existing;
        }

        // ⚠ Список аргументів — ПОХІДНИЙ від записаного виразу, ставиться після нього й поза `EditFormula`:
        // інваріантів домену він не має, а рантайм читає з нього маркери `?`.
        formula.SetArguments(Ecr.Expressions.Binding.ArgumentDeclarationChecker.DeriveCsv(formula.Expression));

        // L2-1: `null` - «не змінювати» (нова формула лишається Substance, наявна зберігає область).
        if (scope is { } chosen)
        {
            formula.SetScope(chosen);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyFormulaDto.Of(formula);
    }

    /// <summary>Переносить маркери <c>Name?</c> застарілого списку у вираз; <c>null</c>/порожній — вираз як є.</summary>
    /// <param name="expression">Вираз із запиту.</param>
    /// <param name="legacyArgumentsCsv">Застаріле поле <c>argumentsCsv</c>.</param>
    /// <returns>Вираз із маркерами.</returns>
    internal static string LegacyMarkers(string expression, string? legacyArgumentsCsv)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return expression;
        }

        var optional = Ecr.Expressions.Binding.ArgumentDeclarationChecker.OptionalNamesFromCsv(legacyArgumentsCsv);
        return Ecr.Expressions.Binding.ArgumentDeclarationChecker.ApplyOptionalMarkers(expression, optional);
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
                "ECR-CALC-0404",
                $"Версії методології {methodologyVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.version",
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                });

        var formula = await drafts.FindFormulaAsync(methodologyVersionId, code, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404",
                $"Формули «{code}» у версії {methodologyVersionId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.formula",
                    ["formulaCode"] = code,
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                });

        version.RemoveFormula(formula);
        drafts.Remove(formula);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
