-- REG:an29 ── ru/kz задовгого текстового значення поля довідника (L4-03 = L5-10); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.valueTooLong', N'ru', N'Значение длиннее {max} символов ({length}).'),
    (N'err.ECR-REG-0422.valueTooLong', N'kz', N'Мән {max} таңбадан ұзын ({length}).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG:an29 ── кінець секції ──
