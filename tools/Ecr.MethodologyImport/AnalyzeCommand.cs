using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reading;

namespace Ecr.MethodologyImport;

/// <summary>Зв'язує читач → модель → аналіз. Окремо від <c>Program</c>, щоб тести не ходили через консоль.</summary>
public static class AnalyzeCommand
{
    public static (MethodologyModel Model, AnalysisReport Report) Run(
        Stream input, string library = MethodologyAnalyzer.DefaultLibrary, bool normalize = true)
    {
        var builder = new MethodologyModelBuilder();
        var readStats = AfXmlReader.Read(input, builder.Add);
        return Analyze(builder.Build(), readStats, library, normalize);
    }

    /// <summary>
    /// Аналіз готової моделі — спільна частина джерел (AF XML, дзеркала SQL старої системи): нормалізація посилань,
    /// типи формул, блокери. Модель із будь-якого джерела проходить ТУ САМУ перевірку.
    /// </summary>
    public static (MethodologyModel Model, AnalysisReport Report) Analyze(
        MethodologyModel model, AfReadStats readStats, string library = MethodologyAnalyzer.DefaultLibrary, bool normalize = true)
    {
        var applied = (IReadOnlyList<NormalizationApplied>)[];
        if (normalize)
        {
            (model, applied) = ReferenceNormalizer.Normalize(model);
        }

        var inference = FormulaTypeInference.InferDetailed(model, library);
        var analysis = MethodologyAnalyzer.Analyze(model, readStats, library);
        var blockers = inference.Converged
            ? analysis.Blockers
            : [.. analysis.Blockers, $"{FormulaTypeInference.NotConvergedBlocker}: типи формул не збіглись за {FormulaTypeInference.MaxPasses} проходів — результат неповний"];
        var report = analysis with
        {
            Blockers = blockers,
            Normalizations = applied,
            FormulaTypes = ImportDiagnostics.FormulaTypes(model, inference.Shapes),
            Units = ImportDiagnostics.Units(model),
            ColumnNeeds = ImportDiagnostics.ColumnNeeds(model),
        };
        return (model, report);
    }
}
