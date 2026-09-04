using ClosedXML.Excel;
using Ecr.Application.Ports;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Експорт документа в <c>.xlsx</c> (ClosedXML, MIT).
/// </summary>
/// <remarks>
/// Бюджет — 10 с p95, тому операція фонова, з прогресом (tz/08 §8.2).
/// ⛔ EPPlus 5+ заборонений ліцензійно (noncommercial), тому альтернативи тут
/// немає і шукати її не треба.
/// </remarks>
public sealed class ExcelExporter(
    ICellStore cellStore,
    IMetadataCache metadata,
    StyleMapper styleMapper,
    FormulaTranslator formulaTranslator) : IExcelExporter
{
    /// <inheritdoc />
    public Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) метадані з кешу; дані — пакетно по таблицях, не по комірках;\n" +
            "2) на кожен SheetDef — аркуш книги; порядок за Ordinal;\n" +
            "3) стилі через styleMapper (шрифт, заливка, межі, формат, об'єднання, " +
            "   заморожені області);\n" +
            "4) якщо options.IncludeFormulas — записувати формулу через formulaTranslator, " +
            "   інакше лише значення;\n" +
            "5) порожні комірки не писати; IsEmpty → порожня комірка без формату помилки;\n" +
            "6) обчислені комірки позначати стилем «тільки читання», щоб при зворотному " +
            "   імпорті було видно, що їх правити не можна;\n" +
            "7) віддавати Stream, не байти: документ на 500×60×12 у пам'яті — це десятки МБ.");
}
