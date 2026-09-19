// src/Ecr.Application/Localization/UiStringAdminHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;

namespace Ecr.Application.Localization;

/// <summary>Покриття перекладу однієї мови.</summary>
/// <param name="LanguageCode">Мова.</param>
/// <param name="Total">Скільки ключів у мові за замовчуванням — стільки й треба перекласти.</param>
/// <param name="Translated">Скільки з них мають власний непорожній переклад.</param>
/// <param name="Missing">Скільки показуються підміною.</param>
public sealed record UiStringCoverageDto(string LanguageCode, int Total, int Translated, int Missing);

/// <summary>Покриття перекладу по мовах продукту.</summary>
/// <param name="Languages">Увімкнені мови в порядку показу.</param>
public sealed record UiStringCoverageResponse(IReadOnlyList<UiStringCoverageDto> Languages);

/// <summary>Адміністративний перелік рядків мови — без fallback.</summary>
/// <param name="LanguageCode">Мова.</param>
/// <param name="Items">Рядки за ключем.</param>
public sealed record UiStringListResponse(string LanguageCode, IReadOnlyList<UiStringRawRow> Items);

/// <summary>
/// Покриття перекладу (<c>BE-13</c>); право <c>System.ManageLocalization</c>.
/// </summary>
/// <remarks>
/// ⚠ Рахується з того самого «сирого» переліку, що й фільтр «лише відсутні»:
/// лічильник і перелік, пораховані двома різними запитами, рано чи пізно
/// розійшлися б на одиницю, і термінолог шукав би рядок, якого немає.
/// </remarks>
public sealed class GetUiStringCoverageHandler(
    IUiStringCatalog catalog, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає покриття по кожній увімкненій мові.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<UiStringCoverageResponse> HandleAsync(CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, SetUiStringHandler.Permission, ct).ConfigureAwait(false);

        var languages = await catalog.ListLanguagesAsync(ct).ConfigureAwait(false);
        var result = new List<UiStringCoverageDto>(languages.Count);

        foreach (var language in languages)
        {
            var rows = await catalog.ListRawAsync(language.Code, ct).ConfigureAwait(false);
            var translated = rows.Count(row => row.Value is not null);

            result.Add(new UiStringCoverageDto(language.Code, rows.Count, translated, rows.Count - translated));
        }

        return new UiStringCoverageResponse(result);
    }
}

/// <summary>
/// Адміністративний перелік рядків мови (<c>BE-13</c>); право
/// <c>System.ManageLocalization</c>.
/// </summary>
public sealed class ListUiStringsHandler(
    IUiStringCatalog catalog, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає рядки мови без підміни відсутнього перекладу.</summary>
    /// <param name="languageCode">Мова.</param>
    /// <param name="missingOnly">Лише рядки без перекладу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<UiStringListResponse> HandleAsync(string languageCode, bool missingOnly, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, SetUiStringHandler.Permission, ct).ConfigureAwait(false);

        var rows = await catalog.ListRawAsync(languageCode, ct).ConfigureAwait(false);

        return new UiStringListResponse(
            languageCode, missingOnly ? [.. rows.Where(row => row.Value is null)] : rows);
    }
}
