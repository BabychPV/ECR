using Ecr.Application.Ports;

namespace Ecr.Application.Documents;

/// <summary>RPT-STABLE B2: версія шаблону документа за ПЕРІОД (шкала версій проєкту) — один вхід для обробників.</summary>
public static class DocumentPeriodVersion
{
    /// <summary>
    /// Версія за період; <c>0</c> від сховища (обгортка/двійник у тестах без реалізації періодного методу) — чинна версія
    /// документа, як до B2.
    /// </summary>
    /// <param name="documents">Сховище документів.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int> GetAsync(IDocumentStore documents, long documentId, int periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var version = await documents.GetTemplateVersionIdForPeriodAsync(documentId, periodKey, ct).ConfigureAwait(false);
        return version != 0
            ? version
            : await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
    }
}
