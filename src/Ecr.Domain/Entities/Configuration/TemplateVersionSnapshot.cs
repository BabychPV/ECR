namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Незмінний знімок структури версії для кешу метаданих. Будується один раз
/// і живе під ключем <c>v{id}:r{rev}</c> — інвалідація не потрібна (D-16).
/// </summary>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару.</param>
/// <param name="Sheets">Аркуші з таблицями, колонками і рядками.</param>
/// <param name="ColumnsById">Плоский індекс колонок для швидкого доступу.</param>
/// <param name="RowsByKey">Індекс рядків: <c>(TableDefId, RowKey)</c> → <see cref="RowDef"/>.</param>
public sealed record TemplateVersionSnapshot(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDef> Sheets,
    IReadOnlyDictionary<int, ColumnDef> ColumnsById,
    IReadOnlyDictionary<(int TableDefId, string RowKey), RowDef> RowsByKey)
{
    /// <summary>Ключ кешу.</summary>
    public string CacheKey => $"v{TemplateVersionId}:r{PresentationRevision}";
}
