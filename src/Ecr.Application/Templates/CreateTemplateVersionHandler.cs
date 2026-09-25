// src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>Створює чернетку версії шаблону — порожню або клоном (ФВ-2.8).</summary>
public sealed partial class CreateTemplateVersionHandler(
    ITemplateVersionStore versions,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    Security.IAccessDecisionService access)
{
    /// <summary>Право, без якого версію не створити.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>
    /// Формат номера версії: <c>Major.Minor.Patch.Build</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Номер входить у назву версії, за якою її шукають у журналах, порівнюють
    /// у diff і згадують у листуванні. Вільний рядок перетворив би цей пошук на
    /// археологію: «1.2», «v1.2», «1.2 final» і «1.2(2)» — це чотири різні
    /// версії, які люди вважають однією.
    /// </remarks>
    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex VersionFormat { get; }

    /// <summary>Створює версію.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="versionNumber">Номер нової версії.</param>
    /// <param name="cloneFromVersionId">Версія-джерело; <c>null</c> — порожня.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної чернетки.</returns>
    public async Task<int> HandleAsync(
        int templateId, string versionNumber, int? cloneFromVersionId, CancellationToken ct)
    {
        // ⛔ B-04 (UX-прохід, четвертий раунд): порядок — особа → ПРАВО →
        // валідація. Тут стояв `ArgumentException.ThrowIfNullOrWhiteSpace`
        // ПЕРШИМ рядком: порожній номер давав `500` (конвеєр не знає
        // `ArgumentException`), і давав його будь-кому — ще до перевірки права.
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може створювати версії шаблону.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        // ⛔ Q-238: право перевірялося лише коментарем (`Permission` — константа,
        // яку ніхто не читав). `CloneTemplateVersionHandler` (той самий
        // ендпоінт-сім'я, клонування замість порожньої версії) має цю перевірку
        // (`A7-53`); порожня версія — ні. Той самий прийом.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        RequireVersionNumber(versionNumber);

        var now = clock.UtcNow;

        // ⚠ Клон зберігає Code і RowKey (ФВ-2.8): нові Id, старі ідентичності.
        // Інакше формули клону почали б посилатися в порожнечу — а помітили б
        // це вже на першому перерахунку, коли числа замість значень дали б
        // #REF.
        int versionId;
        if (cloneFromVersionId is { } source)
        {
            await RequireSourceOfTemplateAsync(templateId, source, ct).ConfigureAwait(false);
            versionId = await versions.CloneAsync(source, versionNumber, userId, now, ct).ConfigureAwait(false);
        }
        else
        {
            versionId = await versions.CreateDraftAsync(templateId, versionNumber, userId, now, ct)
                .ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return versionId;
    }

    /// <summary>
    /// Номер версії непорожній і має форму <c>Major.Minor.Patch.Build</c>;
    /// інакше <c>422 ECR-TMPL-0422</c> з ключем (B-04).
    /// </summary>
    /// <param name="versionNumber">Номер із запиту.</param>
    /// <exception cref="BusinessRuleException">Номер порожній або не за форматом.</exception>
    /// <remarks>
    /// ⚠ Спільне для створення й клонування (<see cref="CloneTemplateVersionHandler"/>):
    /// другий шлях приймав будь-який рядок, тож «1.2 final» відхилявся лише
    /// одним із двох входів у ту саму таблицю.
    /// </remarks>
    public static void RequireVersionNumber(string? versionNumber)
    {
        if (string.IsNullOrWhiteSpace(versionNumber))
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                "Номер версії обов'язковий: Major.Minor.Patch.Build.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0422.versionNumberRequired" });
        }

        if (!VersionFormat.IsMatch(versionNumber))
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Номер версії «{versionNumber}» не відповідає формату Major.Minor.Patch.Build.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.versionNumberFormat",
                    ["version"] = versionNumber.Length > 32 ? versionNumber[..32] : versionNumber,
                });
        }
    }

    /// <summary>
    /// Версія-джерело клону належить шаблону з маршруту (X-30).
    /// </summary>
    /// <remarks>
    /// ⛔ Клон пишеться в шаблон ДЖЕРЕЛА (<c>TemplateVersionCloner</c> бере
    /// <c>TemplateId</c> з нього), а не в <paramref name="templateId"/> з адреси:
    /// <c>POST /templates/5/versions {cloneFromVersionId: версія шаблону 7}</c>
    /// відповідав <c>201</c> і мовчки заводив нову версію ШАБЛОНУ 7. Переносити
    /// структуру між шаблонами клон не вміє і не мусить — тож відмова, а не
    /// «клон у правильний шаблон».
    /// </remarks>
    private async Task RequireSourceOfTemplateAsync(int templateId, int sourceVersionId, CancellationToken ct)
    {
        var owner = await versions.FindTemplateOfVersionAsync(sourceVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Версії шаблону {sourceVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = sourceVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        if (owner.Id != templateId)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Версія {sourceVersionId} належить іншому шаблону: клонувати можна лише в межах шаблону {templateId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.cloneSourceOtherTemplate",
                    ["sourceVersionId"] = sourceVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["templateId"] = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }
}
