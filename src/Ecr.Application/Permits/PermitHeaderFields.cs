// src/Ecr.Application/Permits/PermitHeaderFields.cs
using Ecr.Application.Permits.Dto;

namespace Ecr.Application.Permits;

/// <summary>
/// Фіксовані поля шапки дозволу і правила іменування реєстрів типу (Permits, PM-03).
/// </summary>
/// <remarks>
/// ⚠ Поле <c>Location</c> — текст, а не посилання на довідник <c>Locations</c>: такого довідника в сіді немає
/// (розташування в джерелах — рядки EN/RU), а вигаданий код зламав би публікацію типу на чистій базі.
/// </remarks>
public static class PermitHeaderFields
{
    /// <summary>Код поля композиції в таблицях: посилання на дозвіл-батька.</summary>
    public const string PermitFieldCode = "PERMIT";

    /// <summary>Код первинного ключа шапки й таблиць.</summary>
    public const string PrimaryKeyCode = "PK";

    /// <summary>Група прапорців звітних форм.</summary>
    public const string ReportingGroup = "Reporting_forms";

    /// <summary>Усі поля шапки в порядку показу.</summary>
    public static IReadOnlyList<PermitTypeColumnDto> All { get; } =
    [
        Field("Permit_Number", "Permit number", "Номер разрешения", "String", required: true, key: true, 1),
        Field("Location", "Location", "Расположение", "String", required: false, key: false, 2),
        Field("Issue_Date", "Issue date", "Дата выдачи", "Date", required: false, key: false, 3),
        Field("Actual_End_Date", "Actual end date", "Фактическая дата окончания", "Date", required: false, key: false, 4),
        Field("Form_300", "Reported in form 300", "Входит в форму 300", "Bool", false, false, 5, ReportingGroup),
        Field("Form_301", "Reported in form 301", "Входит в форму 301", "Bool", false, false, 6, ReportingGroup),
        Field("Form_302", "Reported in form 302", "Входит в форму 302", "Bool", false, false, 7, ReportingGroup),
        Field("TOx", "Reported in TOx", "Входит в TOx", "Bool", false, false, 8, ReportingGroup),
    ];

    /// <summary>Код реєстру шапки типу.</summary>
    /// <param name="typeCode">Код типу.</param>
    public static string HeaderRegistryCode(string typeCode) => "PRM_" + typeCode;

    /// <summary>Код реєстру таблиці типу.</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="tableCode">Код таблиці.</param>
    public static string TableRegistryCode(string typeCode, string tableCode) => $"PRM_{typeCode}_{tableCode}";

    /// <summary>Код прихованого реєстру списку «лише для типу».</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="columnCode">Код стовпця.</param>
    public static string ListRegistryCode(string typeCode, string tableCode, string columnCode)
        => $"PRM_{typeCode}_{tableCode}_{columnCode}";

    private static PermitTypeColumnDto Field(
        string code, string en, string ru, string dataType, bool required, bool key, int order, string? group = null)
        => new(
            code,
            new Dictionary<string, string> { ["en"] = en, ["ru"] = ru },
            dataType, null, required, key, group, null, null, null, null, null, null, order);
}
