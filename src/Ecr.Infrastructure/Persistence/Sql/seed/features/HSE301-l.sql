-- HSE301:L ── резолвінг бібліотечних формул (!Code в імпортовану методологію):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- публікація називає посилання, що не веде нікуди (ФВ-9.14, FORMULA_NOT_FOUND).
    (N'publish.problem.formulaNotFound', N'en', N'Formula {formula}: reference !{name} at position {position} does not match a formula of this version or of any imported methodology, so it would always give #REF.', 1),
    -- Формула бібліотеки рахується в контексті рядка викликача: цикл імпортів, інші режими
    -- і формула речовини бібліотеки з Row-формули — відмови публікації. Ключ A3
    -- importedFormulaNotEvaluated прибрано (секція «Прибрані ключі»).
    (N'publish.problem.importCycle', N'en', N'Imported methodologies form a cycle: {chain}. A methodology cannot use its own formulas through its imports; the calculation would give #CYCLE.', 1),
    (N'publish.problem.importModeMismatch', N'en', N'Imported methodology {library} calculates in {libraryNumeric}/{libraryCalendar} mode, but this version uses {numeric}/{calendar}: its formulas would give different numbers here than in the library itself.', 1),
    (N'publish.problem.rowScopeReferencesLibrarySubstance', N'en', N'Formula {formula} is calculated once per row, but !{name} of imported methodology {library} has a value only for a substance. Make {formula} a per-substance formula or remove the reference.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:L ── кінець секції
