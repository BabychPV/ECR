-- COLL:an107-import-busy ── ru/kz: AN-107 / P1-04 = S1-02: межа одночасних переглядів імпорту книги вичерпана; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.importBusy', N'ru', N'Сервер занят чтением других импортируемых книг. Повторите попытку через минуту.'),
    (N'err.ECR-REQ-0429.importBusy', N'kz', N'Сервер басқа импортталатын кітаптарды оқумен бос емес. Бір минуттан кейін қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an107-import-busy ── кінець секції ──

-- COLL:an107-import-busy ── AN-107 / P1-04 = S1-02: перегляд імпорту книги — межа одночасних розборів вичерпана (ImportPreviewRateLimitPolicy); ru/kz — порцією COLL:an107-import-busy нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.importBusy', N'en', N'The server is busy reading other imported workbooks. Try again in a minute.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an107-import-busy ── кінець секції ──
