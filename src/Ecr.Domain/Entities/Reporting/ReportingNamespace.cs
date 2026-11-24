namespace Ecr.Domain.Entities.Reporting;

/// <summary>
/// Простір імен колишніх сутностей звітів (<c>rpt.ReportDef</c>/<c>ReportVersion</c>/<c>ReportSnapshot</c>/<c>ReportRow</c>).
/// </summary>
/// <remarks>
/// ✎ P-1124-3 (D-NEW, людина 24.11): конструктор звітів і зрізи прибрано разом із таблицями. Сутностей тут більше немає,
/// але ~130 згенерованих файлів <c>Migrations/*.Designer.cs</c> мають <c>using Ecr.Domain.Entities.Reporting;</c> — без
/// жодного типу в цьому просторі імен вони не компілюються (CS0246), а історичні міграції не переписуємо. Тому лишається цей
/// порожній маркер; нових типів сюди не додавати.
/// </remarks>
public static class ReportingNamespace;
