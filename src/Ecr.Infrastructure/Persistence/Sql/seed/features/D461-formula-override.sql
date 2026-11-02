-- D461:formula-override ── ru/kz: право, позначки журналу й відмова «ручне значення замість формули» (D-461); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Document.OverrideFormula', N'ru', N'Заменять результат формулы ручным значением'),
    (N'permission.Document.OverrideFormula', N'kz', N'Формула нәтижесін қолмен енгізілген мәнмен ауыстыру'),
    (N'audit.originLabel.FormulaOverride', N'ru', N'Ручное значение вместо формулы'),
    (N'audit.originLabel.FormulaOverride', N'kz', N'Формуланың орнына қолмен енгізілген мән'),
    (N'audit.originLabel.FormulaRestored', N'ru', N'Формула восстановлена'),
    (N'audit.originLabel.FormulaRestored', N'kz', N'Формула қалпына келтірілді'),
    (N'err.ECR-TMPL-0404.formulaById', N'ru', N'В версии шаблона {versionId} нет формулы {formulaId}.'),
    (N'err.ECR-TMPL-0404.formulaById', N'kz', N'{versionId} үлгі нұсқасында {formulaId} формуласы жоқ.'),
    (N'err.ECR-CELL-0422.overrideReasonRequired', N'ru', N'Для ручного значения вместо формулы нужна причина длиной до {max} символов (столбец «{columnCode}», строка {rowKey}).'),
    (N'err.ECR-CELL-0422.overrideReasonRequired', N'kz', N'Формуланың орнына қолмен енгізілген мәнге {max} таңбаға дейінгі себеп қажет («{columnCode}» бағаны, {rowKey} жолы).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D461:formula-override ── кінець секції ──
-- D461:formula-override ── en: право, позначки журналу й відмова «ручне значення замість формули» (D-461); ru/kz — порцією D461:formula-override нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Document.OverrideFormula', N'en', N'Replace a formula result with a manual value', 1),
    (N'audit.originLabel.FormulaOverride', N'en', N'Manual value instead of the formula', 1),
    (N'audit.originLabel.FormulaRestored', N'en', N'Formula restored', 1),
    (N'err.ECR-TMPL-0404.formulaById', N'en', N'There is no formula {formulaId} in template version {versionId}.', 1),
    (N'err.ECR-CELL-0422.overrideReasonRequired', N'en', N'A manual value instead of the formula needs a reason of up to {max} characters (column "{columnCode}", row {rowKey}).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D461:formula-override ── кінець секції ──
