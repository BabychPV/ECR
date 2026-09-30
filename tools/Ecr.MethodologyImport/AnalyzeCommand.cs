using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reading;

namespace Ecr.MethodologyImport;

/// <summary>Зв'язує читач → модель → аналіз. Окремо від <c>Program</c>, щоб тести не ходили через консоль.</summary>
public static class AnalyzeCommand
{
    public static (MethodologyModel Model, AnalysisReport Report) Run(
        Stream input, string library = MethodologyAnalyzer.DefaultLibrary)
    {
        var builder = new MethodologyModelBuilder();
        var readStats = AfXmlReader.Read(input, builder.Add);
        var model = builder.Build();
        return (model, MethodologyAnalyzer.Analyze(model, readStats, library));
    }
}
