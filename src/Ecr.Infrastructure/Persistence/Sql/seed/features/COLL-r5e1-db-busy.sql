-- COLL:r5e1-db-busy ── ru/kz: R5-E1 / E1-04: тимчасовий збій БД — 503 ECR-SYS-0503; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SYS-0503.databaseBusy', N'ru', N'База данных временно занята или недоступна, поэтому запрос не выполнен. Обновите данные и повторите через несколько секунд; если ошибка повторяется, обратитесь к администратору.'),
    (N'err.ECR-SYS-0503.databaseBusy', N'kz', N'Дерекқор уақытша бос емес немесе қолжетімсіз, сондықтан сұрау орындалмады. Деректерді жаңартып, бірнеше секундтан кейін қайталаңыз; қате қайталанса, әкімшіге хабарласыңыз.'),
    (N'err.ECR-SYS-0503.databaseBusy.title', N'ru', N'База данных временно недоступна'),
    (N'err.ECR-SYS-0503.databaseBusy.title', N'kz', N'Дерекқор уақытша қолжетімсіз')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r5e1-db-busy ── кінець секції ──

-- COLL:r5e1-db-busy ── R5-E1 / E1-04: тимчасовий збій БД (deadlock 1205 після повторів, тайм-аут -2, обрив з'єднання) — 503 ECR-SYS-0503 з Retry-After; ru/kz — порцією COLL:r5e1-db-busy нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SYS-0503.databaseBusy', N'en', N'The database is temporarily busy or unreachable, so the request was not completed. Reload the data and try again in a few seconds; if it keeps happening, contact your administrator.', 1),
    (N'err.ECR-SYS-0503.databaseBusy.title', N'en', N'Database temporarily unavailable', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r5e1-db-busy ── кінець секції ──
