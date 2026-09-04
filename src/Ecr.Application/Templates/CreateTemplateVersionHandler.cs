// src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>Створює чернетку версії шаблону — порожню або клоном (ФВ-2.8).</summary>
public sealed partial class CreateTemplateVersionHandler(
    ITemplateVersionStore versions, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(versionNumber);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може створювати версії шаблону.");

        if (!VersionFormat.IsMatch(versionNumber))
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Номер версії «{versionNumber}» не відповідає формату Major.Minor.Patch.Build.");
        }

        var now = clock.UtcNow;

        // ⚠ Клон зберігає Code і RowKey (ФВ-2.8): нові Id, старі ідентичності.
        // Інакше формули клону почали б посилатися в порожнечу — а помітили б
        // це вже на першому перерахунку, коли числа замість значень дали б
        // #REF.
        var versionId = cloneFromVersionId is { } source
            ? await versions.CloneAsync(source, versionNumber, userId, now, ct).ConfigureAwait(false)
            : await versions.CreateDraftAsync(templateId, versionNumber, userId, now, ct)
                .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return versionId;
    }
}
