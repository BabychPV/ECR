// src/Ecr.Application/Documents/PatchCellStylesHandler.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Користувацьке оформлення комірок (<c>PATCH …/tables/{id}/cell-styles</c>).
/// </summary>
/// <remarks>
/// ⚠ КАРКАС контрактного коміту (L3a, 12.10): форма запиту перевіряється
/// по-справжньому (<see cref="PatchCellStylesRequest.EnsureValid"/>), а запис
/// відмовляє чесною <c>422 ECR-CELL-4224</c> з ключем <c>notAvailable</c> —
/// без мовчазного «200, нічого не зроблено». Сховище, права, аудит і запис
/// приходять у L3b (окремий коміт із міграцією <c>doc.CellStyle</c>).
/// </remarks>
public sealed class PatchCellStylesHandler
{
    /// <summary>Застосовує зміни оформлення до комірок екземпляра таблиці.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-CELL-4224</c>.</exception>
    public Task<PatchCellStylesResponse> HandleAsync(
        long documentId, long tableInstanceId, PatchCellStylesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        request.EnsureValid();

        throw new BusinessRuleException(
            ErrorCodes.CellStyleInvalid,
            "Збереження користувацького оформлення комірок ще не підключене до сховища.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-CELL-4224.notAvailable",
            });
    }
}
