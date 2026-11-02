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
