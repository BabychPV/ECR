-- NAE:src-guard ── ECR-REG-0422 ── ru/kz: рядок таблиці дозволу посилається на НАЕ лише джерелом (звіт людини про НАЕ, 05.11); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.naeSourceRequired', N'ru', N'Поле «{field}»: узел «{entryCode}» — уровня «{level}», а в строке разрешения можно выбрать только источник выбросов (SRC).'),
    (N'err.ECR-REG-0422.naeSourceRequired', N'kz', N'«{field}» өрісі: «{entryCode}» түйіні «{level}» деңгейінде, ал рұқсат жолында тек шығарынды көзін (SRC) таңдауға болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:src-guard ── кінець секції ──
-- NAE:src-guard ── ECR-REG-0422 ── en: рядок дозволу — лише джерело НАЕ; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.naeSourceRequired', N'en', N'Field «{field}»: node «{entryCode}» is a «{level}» level node; a permit row can only reference an emission source (SRC).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:src-guard ── кінець секції ──
