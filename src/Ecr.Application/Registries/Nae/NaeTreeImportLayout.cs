// src/Ecr.Application/Registries/Nae/NaeTreeImportLayout.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>Fixed layout of the NAE tree Excel template (one row = one path Region → Area → Site → Equipment → NAE).</summary>
public static class NaeTreeImportLayout
{
    /// <summary>Content type of the template.</summary>
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>File name of the template.</summary>
    public const string FileName = "nae-tree-template.xlsx";

    /// <summary>Sheet name of the template.</summary>
    public const string SheetName = "NAE";

    /// <summary>Column: region (required on every row).</summary>
    public const string Region = "Region";

    /// <summary>Column: area (optional; wins over <see cref="OnOffshore"/>).</summary>
    public const string Area = "Area";

    /// <summary>Column: site (required when the row has equipment or an NAE code).</summary>
    public const string Site = "Site";

    /// <summary>Column: equipment group (optional).</summary>
    public const string Equipment = "Equipment";

    /// <summary>Column: NAE code (optional; a row without it creates only the group nodes).</summary>
    public const string NaeCode = "NAE code";

    /// <summary>Column: English name of the deepest node of the row.</summary>
    public const string NameEn = "Name_En";

    /// <summary>Column: Russian name of the deepest node of the row.</summary>
    public const string NameRu = "Name_Ru";

    /// <summary>Column: Kazakh name of the deepest node of the row.</summary>
    public const string NameKz = "Name_Kz";

    /// <summary>Column: onshore / offshore — the area when <see cref="Area"/> is empty; both filled and different is an error.</summary>
    public const string OnOffshore = "On_Offshore";

    /// <summary>Template columns in order.</summary>
    public static readonly IReadOnlyList<string> Columns =
        [Region, Area, Site, Equipment, NaeCode, NameEn, NameRu, NameKz, OnOffshore];
}

/// <summary>A workbook read for the NAE tree import: the first visible sheet as text cells.</summary>
/// <param name="FileSha256">Hash of the uploaded file.</param>
/// <param name="SheetName">Sheet that was read.</param>
/// <param name="Rows">Rows from row 1, each a list of trimmed cell texts (<c>null</c> — empty), at most the used width.</param>
public sealed record NaeTreeSheet(string FileSha256, string SheetName, IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>Excel side of the NAE tree import (implemented by the Excel adapter, behind the xlsx safety gate).</summary>
public interface INaeTreeWorkbook
{
    /// <summary>Reads the first visible sheet; an unsafe or unreadable package is <c>422</c>.</summary>
    public Task<NaeTreeSheet> ReadAsync(Stream file, CancellationToken ct);

    /// <summary>Builds the empty template (header row of <see cref="NaeTreeImportLayout.Columns"/>).</summary>
    public byte[] BuildTemplate();
}
