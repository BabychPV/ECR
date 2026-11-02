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
-- REG:an29 ── аудит 2026-10-03 (L4-03 = L5-10): текстове значення поля довідника довше за колонку (`RegistryValue.MaxStringLength`); ru/kz — порцією REG:an29 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.valueTooLong', N'en', N'The value is longer than {max} characters ({length}).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG:an29 ── кінець секції ──
