// src/Ecr.Application/Ports/IStyleCatalog.cs

using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Стилі версії шаблону (<c>cfg.StyleDef</c>) за їхніми ідентифікаторами.
/// </summary>
/// <remarks>
/// ⚠ Порт з'явився тому, що <see cref="Ecr.Domain.Entities.Configuration.TemplateVersionSnapshot"/>
/// стилів <b>не несе</b>: у ньому є <c>ColumnDef.StyleId</c> і
/// <c>TableDef.HeaderStyleId</c>, але не самі описи. Без цього порту
/// <c>ExcelExportOptions.IncludeStyles</c> був би прапорцем, який нічого не
/// вмикає, — а користувач бачив би книгу без форматування і вважав це збоєм
/// експорту, а не відсутністю реалізації.
/// <para>
/// Знімком цілком, а не по одному: стилів у версії десятки, колонок —
/// тисячі, і запит на стиль кожної колонки перетворив би експорт на тисячі
/// звернень.
/// </para>
/// </remarks>
public interface IStyleCatalog
{
    /// <summary>Стилі версії: <c>StyleDef.Id</c> → опис.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyDictionary<int, StyleDef>> GetAsync(int templateVersionId, CancellationToken ct);
}
