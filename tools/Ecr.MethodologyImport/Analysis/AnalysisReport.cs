namespace Ecr.MethodologyImport.Analysis;

public enum ReferenceKind
{
    /// <summary><c>!Name</c> — посилання на формулу (у своїй методології або в бібліотеці Common).</summary>
    Formula,

    /// <summary><c>CST.Name</c> — посилання на константу.</summary>
    Constant,
}

/// <summary>Нерезолвне посилання: де (формула-джерело) і що (токен після Trim).</summary>
public sealed record UnresolvedReference(
    ReferenceKind Kind,
    string Methodology,
    string MethodologyVersion,
    string Formula,
    string FormulaVersion,
    string Token,
    string RawToken,
    string Hint,
    string Path);

/// <summary>Цикл посилань між формулами: перелік ключів формул (відсортований).</summary>
public sealed record ReferenceCycle(IReadOnlyList<string> Formulas);

public sealed record TokenCount(string Token, int Count);

public sealed record VersionSummary(
    string Version,
    int Formulas,
    int Constants,
    int FormulaRefs,
    int ConstantRefs,
    int Unresolved);

public sealed record MethodologySummary(string Name, IReadOnlyList<VersionSummary> Versions);

public sealed record ReferenceTotals(
    int Total,
    int Resolved,
    int Unresolved,
    int CrossMethodology);

public sealed record ReaderSummary(
    long Elements,
    long Attributes,
    long ElementsWithoutName,
    long DuplicateAttributes,
    IReadOnlyDictionary<string, long> Unrecognized,
    long FormulaElements,
    long ConstantElements,
    long OtherElementsWithAttributes,
    long MethodologyFromPath,
    long ResolvedFromConfigString,
    long UnresolvedConfigStrings);

/// <summary>
/// Звіт сухого прогону. Без часу виконання й шляхів середовища: той самий вхід дає той самий
/// JSON байт у байт (сортування — ординальне). Час і розмір вхідного файла друкує лише текстовий звіт.
/// </summary>
public sealed record AnalysisReport(
    int Methodologies,
    int MethodologyVersions,
    int Formulas,
    int Constants,
    int DistinctConstantNames,
    ReferenceTotals FormulaReferences,
    ReferenceTotals ConstantReferences,
    int ParameterArguments,
    int OtherArguments,
    long TrimmedFields,
    int TrimmedArgumentTokens,
    int DuplicateFormulaKeys,
    IReadOnlyList<MethodologySummary> ByMethodology,
    IReadOnlyList<UnresolvedReference> Unresolved,
    IReadOnlyList<TokenCount> UnresolvedTokens,
    int UndeclaredInTextTotal,
    IReadOnlyList<TokenCount> UndeclaredInText,
    IReadOnlyList<ReferenceCycle> Cycles,
    ReaderSummary Reader,
    IReadOnlyList<string> Blockers)
{
    public bool HasBlockers => Blockers.Count > 0;
}
