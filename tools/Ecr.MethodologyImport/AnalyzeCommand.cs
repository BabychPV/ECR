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
        var model = builder.Build();
        var applied = (IReadOnlyList<NormalizationApplied>)[];
        if (normalize)
        {
            (model, applied) = ReferenceNormalizer.Normalize(model);
        }

        var shapes = FormulaTypeInference.Infer(model, library);
        var report = MethodologyAnalyzer.Analyze(model, readStats, library) with
        {
            Normalizations = applied,
            FormulaTypes = ImportDiagnostics.FormulaTypes(model, shapes),
            Units = ImportDiagnostics.Units(model),
            ColumnNeeds = ImportDiagnostics.ColumnNeeds(model),
        };
        return (model, report);
    }
}
