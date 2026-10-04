// src/Ecr.Application/Integration/RegistrySync/RegistrySyncElementLimits.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Integration.RegistrySync;

/// <summary>
/// Елемент джерела проти ширини колонок <c>dic.RegistryExternalKey</c> — до знімка синку
/// (аудит 2026-10-03, L4-03).
/// </summary>
/// <remarks>
/// ⛔ Ідентифікатор чи шлях, ширший за колонку, доїжджав до <c>SaveChanges</c> і валив увесь прогін.
/// Задовгий ідентифікатор — як відсутній: елемент не потрапляє в знімок, і знімок неповний (зниклих
/// за ним не оголошують). Задовгий шлях — не оновлюється (<c>null</c>: планувальник шлях не
/// змінює). Обидва — рядок відмови в журналі прогону.
/// </remarks>
public static class RegistrySyncElementLimits
{
    /// <summary>Довжина префікса задовгого ідентифікатора в тексті відмови.</summary>
    private const int IdPreviewLength = 64;

    /// <summary>Елемент, придатний для запису, і відмова, якщо щось відкинуто.</summary>
    /// <param name="element">Елемент із переліку джерела.</param>
    /// <returns>
    /// <c>Element = null</c> — ідентифікатор задовгий, елемент не зіставляти; інакше — той самий
    /// елемент або копія без шляху. <c>Rejection</c> — текст відмови або <c>null</c>.
    /// </returns>
    public static (SourceElement? Element, string? Rejection) Fit(SourceElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.ExternalId is { Length: > RegistryExternalKey.MaxExternalIdLength } id)
        {
            return (null, string.Create(
                CultureInfo.InvariantCulture,
                $"element={id[..IdPreviewLength]}…; error=externalIdTooLong; length={id.Length}; max={RegistryExternalKey.MaxExternalIdLength}"));
        }

        if (element.Path is { Length: > RegistryExternalKey.MaxExternalPathLength } path)
        {
            return (element with { Path = null }, string.Create(
                CultureInfo.InvariantCulture,
                $"element={element.ExternalId}; error=externalPathTooLong; length={path.Length}; max={RegistryExternalKey.MaxExternalPathLength}"));
        }

        return (element, null);
    }
}
