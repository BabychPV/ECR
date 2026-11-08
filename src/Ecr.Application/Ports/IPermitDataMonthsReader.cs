// src/Ecr.Application/Ports/IPermitDataMonthsReader.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Rule (h), Contract tab: in which months a document holds data — the months a permit picked in a header field
/// (document level) must cover.
/// </summary>
/// <remarks>
/// <para>
/// A month has data when a non-deleted row of an original (not a sheet copy) table instance of the document has a numeric
/// value there that was ENTERED or IMPORTED (a calculated value never counts): month columns of a «months in columns» table give their month of the
/// instance year; a table without month columns gives every month of the instance period. In a table WITH month columns
/// only the month columns count — its descriptors and opening balance are not monthly data.
/// </para>
/// <para>
/// ⚠ Tables that carry their own permit column are excluded by the caller: their rows are governed by their own permit
/// (rule h2), not by the document's.
/// </para>
/// </remarks>
public interface IPermitDataMonthsReader
{
    /// <summary>Months (first day of each), ascending, in which the document holds data.</summary>
    /// <param name="documentId">Document.</param>
    /// <param name="excludedTableDefIds">Tables not to look at.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="excludedColumnDefIds">
    /// Columns not to look at (the author's hidden columns: a refusal must not reveal data the author cannot read).
    /// </param>
    public Task<IReadOnlyList<DateOnly>> DocumentMonthsWithDataAsync(
        long documentId,
        IReadOnlyCollection<int> excludedTableDefIds,
        CancellationToken ct,
        IReadOnlyCollection<int>? excludedColumnDefIds = null);

    /// <summary>
    /// The months with data (as <see cref="DocumentMonthsWithDataAsync"/>) and, in the same read, the months that hold
    /// ONLY calculated non-zero values (human 08.11, «Попередження»): no entered/imported value of the covered tables in
    /// that month. Calculated-only months are a non-blocking Warning in Issues, never a refusal or a Submit block.
    /// </summary>
    /// <param name="documentId">Document.</param>
    /// <param name="excludedTableDefIds">Tables not to look at.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="excludedColumnDefIds">Columns not to look at.</param>
    public Task<PermitDocumentMonths> DocumentMonthsByKindAsync(
        long documentId,
        IReadOnlyCollection<int> excludedTableDefIds,
        CancellationToken ct,
        IReadOnlyCollection<int>? excludedColumnDefIds = null);
}

/// <summary>Months of a document by kind of data (rule h, human 08.11).</summary>
/// <param name="WithData">Months with entered/imported data — blocking.</param>
/// <param name="CalculatedOnly">Months with only calculated non-zero values — a Warning.</param>
public sealed record PermitDocumentMonths(IReadOnlyList<DateOnly> WithData, IReadOnlyList<DateOnly> CalculatedOnly);
