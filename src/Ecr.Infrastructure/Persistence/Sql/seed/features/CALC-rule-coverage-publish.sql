-- CALC:rule-coverage-publish ── ru/kz ФВ-13.9 (AN-13): попередження публікації методології про рядки без правила ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'publish.warning.ruleCoverageGaps', N'ru', N'Покрытие правил ({from}-{to}): {combinations} комбинаций значений ({rows} строк) не подходят ни под одно правило этой версии и не будут рассчитаны. Проверьте матрицу покрытия версии.'),
    (N'publish.warning.ruleCoverageGaps', N'kz', N'Ережелерді қамту ({from}-{to}): {combinations} мәндер комбинациясы ({rows} жол) осы нұсқаның бірде-бір ережесіне сәйкес келмейді және есептелмейді. Нұсқаның қамту матрицасын тексеріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CALC:rule-coverage-publish ── кінець секції ──

-- CALC:rule-coverage-publish ── en ФВ-13.9 (AN-13): попередження публікації методології про рядки без правила ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'publish.warning.ruleCoverageGaps', N'en', N'Rule coverage ({from}-{to}): {combinations} value combinations ({rows} rows) match no rule of this version and will not be calculated. Check the coverage matrix of the version.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CALC:rule-coverage-publish ── кінець секції ──
