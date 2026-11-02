-- COLL:d230 ── ru/kz відмови схеми зв'язку Rollup/Check (D-230); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.relationSpecInvalid', N'ru', N'Связь «{relationCode}»: {detail}'),
    (N'err.ECR-TMPL-0422.relationSpecInvalid', N'kz', N'«{relationCode}» байланысы: {detail}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d230 ── кінець секції ──

