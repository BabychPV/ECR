using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.History;

/// <summary>
/// F13 p223 (1): a permit-history write (add / undo / split) changes what PERMIT-SPLIT and PERMIT-VALIDITY report, but
/// Issues show the STORED last check (<see cref="IValidationResultStore"/>) — after «Undo last» they still listed the
/// undone change's PERMIT-SPLIT until the next Validate. The write re-runs the stored check of the document's periods of
/// that year that HAVE one (a period never validated stays unvalidated: nothing is invented).
/// </summary>
internal static class PermitHistoryRevalidation
{
    /// <summary>Re-runs the stored last check of every period of <paramref name="year"/> that has one.</summary>
    /// <param name="validate"><c>null</c> (handler built directly in tests) — nothing is re-run.</param>
    /// <param name="results"><c>null</c> — nothing is re-run.</param>
    /// <param name="documentId">Document.</param>
    /// <param name="year">Year of the change (period keys <c>year·100 + 1…12</c>).</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task RunAsync(
        ValidateDocumentHandler? validate, IValidationResultStore? results, long documentId, int year, CancellationToken ct)
    {
        if (validate is null || results is null)
        {
            return;
        }

        for (var sequence = 1; sequence <= 12; sequence++)
        {
            var periodKey = (year * 100) + sequence;
            if (await results.GetLatestAsync(documentId, periodKey, ct).ConfigureAwait(false) is not null)
            {
                await validate.HandleAsync(documentId, new PeriodKey(periodKey), ct).ConfigureAwait(false);
            }
        }
    }
}
