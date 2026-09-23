namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Типізоване значення поля шапки документа. Рівно одне з полів <c>Value*</c>
/// заповнене, або жодне — тоді <c>IsEmpty = true</c> (явна порожнеча, той
/// самий контракт, що <see cref="CellValueData"/>, R-B4).
/// </summary>
public sealed record DocumentHeaderValueData
{
    public string? ValueString { get; init; }
    public decimal? ValueNumeric { get; init; }
    public DateTime? ValueDate { get; init; }
    public bool? ValueBool { get; init; }
    public long? ValueRegistryEntryId { get; init; }
    public int? ValueUnitId { get; init; }
    public bool IsEmpty { get; init; }

    public static DocumentHeaderValueData Empty { get; } = new() { IsEmpty = true };

    public bool IsWellFormed()
    {
        var filled = 0;
        if (ValueString is not null)
        {
            filled++;
        }
        if (ValueNumeric is not null)
        {
            filled++;
        }
        if (ValueDate is not null)
        {
            filled++;
        }
        if (ValueBool is not null)
        {
            filled++;
        }
        if (ValueRegistryEntryId is not null)
        {
            filled++;
        }
        if (ValueUnitId is not null)
        {
            filled++;
        }
        return IsEmpty ? filled == 0 : filled == 1;
    }
}
