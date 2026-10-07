using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;

namespace Ecr.Application.Templates;

/// <summary>
/// Замінює набір правил умовного форматування версії-чернетки (ФВ-2.6/2.7).
/// PUT-семантика: клієнтський редактор тримає весь список, тож одна операція
/// покриває створення, зміну, перевпорядкування й видалення. Порядок
/// правил у колонці — порядок у запиті; перше спрацьоване правило виграє.
///
/// ⛔ Конкурентність (lost update): заміна цілком не лишає від чужої правки
/// нічого, тож клієнт шле <c>If-Match</c> з версією набору
/// (<see cref="ConditionalFormatsVersion"/>, <c>ETag</c> відповіді <c>GET</c>).
/// Звірка — ПІСЛЯ <c>UPDLOCK</c> на рядку версії (<see cref="DraftVersionLock"/>),
/// у тій самій транзакції, що й запис: дві одночасні заміни з однієї версії —
/// одна <c>200</c>, друга <c>409 ECR-TMPL-0409</c> (<c>condFormatChanged</c>,
/// актуальна версія в <c>details.version</c>). Без заголовка —
/// <c>422 ECR-REQ-0422</c> (<c>condFormatIfMatch</c>), як для грантів ролі.
/// </summary>
public sealed class SaveConditionalFormatsHandler(
    IConditionalFormatStore rules,
    ITemplateVersionStore store,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на редагування структури версії — те саме, що на стиль і колонку.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Стеля набору на версію: захист від випадкового чи зловмисного роздування.</summary>
    public const int MaxRules = 500;

    /// <summary>Записує набір правил.</summary>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-TMPL-0409</c> — версія заморожена; <c>ECR-CFG-0422</c> — правило невалідне.
    /// </exception>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="requested">Повний набір правил.</param>
    /// <param name="ifMatch">
    /// Значення <c>If-Match</c> — версія набору, з якої почалася правка (<c>ETag</c>
    /// відповіді <c>GET</c>). Обов'язкове.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Немає <c>If-Match</c> — <c>ECR-REQ-0422</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// Набір змінили після читання — <c>ECR-TMPL-0409</c>, актуальна версія у <c>details.version</c>.
    /// </exception>
    public async Task<IReadOnlyList<ConditionalFormatRuleDto>> HandleAsync(
        int templateVersionId, IReadOnlyList<ConditionalFormatRuleDto> requested, string? ifMatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var expected = Integration.ListCollectionSchedulesHandler.NormalizeETag(ifMatch)
                       ?? throw new BusinessRuleException(
                           ErrorCodes.RequestInvalid,
                           "Заміна правил умовного форматування має нести заголовок If-Match з ETag прочитаного набору.",
                           new Dictionary<string, object?>
                           {
                               ["messageKey"] = "err.ECR-REQ-0422.condFormatIfMatch",
                           });

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();
        version.TouchDraft(userId, clock.UtcNow);

        if (requested.Count > MaxRules)
        {
            throw new DomainException(
                "ECR-CFG-0422",
                $"Занадто багато правил умовного форматування: {requested.Count} > {MaxRules}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CFG-0422.condFormatLimit",
                    ["max"] = MaxRules,
                });
        }

        var all = version.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns).ToList();
        var columns = all.Where(c => !c.IsDeleted).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

        // L9-22: правила колонки, яку видалили (м'яко) після їх створення, лишаються в
        // наборі, і клієнт шле їх назад як є. 422 тут блокував би збереження ВСЬОГО
        // набору версії без виходу з UI — тож «сироти» видаленої колонки мовчки
        // відкидаються (заміна набору прибирає їх і з бази). Код, якого у версії не
        // було ніколи, — і далі 422.
        var deleted = all.Where(c => c.IsDeleted && !columns.Contains(c.Code))
            .Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var built = new List<ConditionalFormatRule>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var r = requested[i];
            if (r.ColumnCode is not null && deleted.Contains(r.ColumnCode))
            {
                continue;
            }

            // ⚠ {index} в усіх ключах condFormat* — номер правила серед правил ТІЄЇ Ж
            // колонки (як Ordinal і як у ConditionalFormatRule.Validate), а не позиція
            // в запиті: інакше «правило 3» з одного ключа й «правило 1» з другого
            // вказували б на різні речі (суха прогонка Н-Е2, 01.10).
            var code = r.ColumnCode ?? string.Empty;
            var ordinal = ordinals.GetValueOrDefault(code) + 1;
            ordinals[code] = ordinal;

            if (!columns.Contains(code))
            {
                throw new DomainException(
                    "ECR-CFG-0422",
                    $"Правило {ordinal} колонки {r.ColumnCode}: колонки у версії немає.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-CFG-0422.condFormatColumn",
                        ["index"] = ordinal,
                        ["columnCode"] = r.ColumnCode,
                    });
            }
            built.Add(new ConditionalFormatRule(
                templateVersionId, r.ColumnCode!, ordinal, r.Operator, Blank(r.Value), Blank(r.ValueTo),
                Blank(r.BackgroundHex), Blank(r.ForegroundHex), r.IsBold));
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            // ⚠ Читання ПІСЛЯ блокування: друга одночасна заміна чекала на
            // UPDLOCK і тепер бачить уже зафіксований набір першої.
            var actual = ConditionalFormatsVersion.Of(
                (await rules.GetAsync(templateVersionId, innerCt).ConfigureAwait(false))
                .Select(ConditionalFormatMapper.Map));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConcurrencyConflictException(
                    ErrorCodes.TemplateFrozen,
                    $"Правила умовного форматування версії {templateVersionId} змінили після того, як їх прочитали.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0409.condFormatChanged",
                        ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["version"] = actual,
                    });
            }

            await rules.ReplaceAsync(templateVersionId, built, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return [.. built.Select(ConditionalFormatMapper.Map)];
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Правила умовного форматування версії — для редактора й (далі) для сітки/експорту.</summary>
public sealed class GetConditionalFormatsHandler(
    IConditionalFormatStore rules,
    ITemplateVersionStore versions,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право читання структури — те саме, що на стилі.</summary>
    public const string Permission = "Template.View";

    /// <summary>Усі правила версії.</summary>
    /// <exception cref="NotFoundException"><c>ECR-TMPL-0404</c> — версії немає.</exception>
    public async Task<IReadOnlyList<ConditionalFormatRuleDto>> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var all = await rules.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        if (all.Count == 0
            && await versions.FindTemplateOfVersionAsync(templateVersionId, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                Domain.Errors.ErrorCodes.TemplateNotFound,
                $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return [.. all.Select(ConditionalFormatMapper.Map)];
    }
}

/// <summary>Версія набору правил умовного форматування — для <c>ETag</c> / <c>If-Match</c>.</summary>
/// <remarks>
/// ⚠ Хеш НАБОРУ, а не лічильник: токена конкурентності в
/// <c>cfg.ConditionalFormatRule</c> немає, а міграція поза цією задачею (токен
/// міграцій черговий) — той самий прийом, що <c>ResourceGrantsVersion</c>.
/// Порядок усередині колонки — пріоритет правил, тож він ВХОДИТЬ у хеш; порядок
/// між колонками — ні (<c>GET</c> сортує за колонкою, <c>PUT</c> повертає в
/// порядку запиту, і це той самий набір).
/// </remarks>
public static class ConditionalFormatsVersion
{
    /// <summary>Хеш набору (hex, 32 символи).</summary>
    /// <param name="rules">Правила версії.</param>
    public static string Of(IEnumerable<ConditionalFormatRuleDto> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var text = new System.Text.StringBuilder();

        // ⚠ `OrderBy` стабільний: усередині колонки лишається порядок застосування.
        foreach (var rule in rules.OrderBy(r => r.ColumnCode, StringComparer.Ordinal))
        {
            text.Append(rule.ColumnCode).Append('\u001f')
                .Append(rule.Operator).Append('\u001f')
                .Append(rule.Value).Append('\u001f')
                .Append(rule.ValueTo).Append('\u001f')
                .Append(rule.BackgroundHex).Append('\u001f')
                .Append(rule.ForegroundHex).Append('\u001f')
                .Append(rule.IsBold ? '1' : '0')
                .Append('\u001e');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

internal static class ConditionalFormatMapper
{
    internal static ConditionalFormatRuleDto Map(ConditionalFormatRule r)
        => new(r.ColumnCode, r.Operator, r.Value, r.ValueTo, r.BackgroundHex, r.ForegroundHex, r.IsBold);
}

/// <summary>Правило умовного форматування (дзеркало клієнтського <c>ConditionalRule</c>).</summary>
/// <param name="ColumnCode">Код колонки версії.</param>
/// <param name="Operator">gt, ge, lt, le, eq, ne, between, empty, notEmpty.</param>
/// <param name="Value">Операнд (число); не потрібен для empty/notEmpty.</param>
/// <param name="ValueTo">Верхня межа — лише для between.</param>
/// <param name="BackgroundHex"><c>#rrggbb</c> або <c>null</c> — колір теми.</param>
/// <param name="ForegroundHex"><c>#rrggbb</c> або <c>null</c>.</param>
/// <param name="IsBold">Жирний.</param>
public sealed record ConditionalFormatRuleDto(
    string ColumnCode,
    string Operator,
    string? Value,
    string? ValueTo,
    string? BackgroundHex,
    string? ForegroundHex,
    bool IsBold);