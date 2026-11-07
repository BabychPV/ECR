-- PERIOD:clarity ── ru/kz: зрозумілі періоди (квіз людини 07.11): рік переліку документів ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.documentYearOutOfRange', N'ru', N'Год должен быть в диапазоне {min}–{max}.'),
    (N'err.ECR-REQ-0422.documentYearOutOfRange', N'kz', N'Жыл {min}–{max} аралығында болуы тиіс.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERIOD:clarity ── кінець секції ──
-- PERIOD:clarity ── зрозумілі періоди: рік переліку документів (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.documentYearOutOfRange', N'en', N'The year must be between {min} and {max}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERIOD:clarity ── кінець секції ──
