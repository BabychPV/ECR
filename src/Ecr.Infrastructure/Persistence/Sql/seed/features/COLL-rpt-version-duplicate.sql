-- COLL:rpt-version-duplicate ── ru/kz: повторний номер версії опису звіту (ECR-RPT-4091.version); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-RPT-4091.version', N'ru', N'Версия «{version}» в этом описании отчёта уже есть: номер версии должен быть уникальным.'),
    (N'err.ECR-RPT-4091.version', N'kz', N'«{version}» нұсқасы бұл есеп анықтамасында бұрыннан бар: нұсқа нөмірі бірегей болуы тиіс.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rpt-version-duplicate ── кінець секції ──
