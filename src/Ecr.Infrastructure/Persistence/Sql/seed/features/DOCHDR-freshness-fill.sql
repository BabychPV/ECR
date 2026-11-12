-- DOCHDR:freshness-fill ── ru/kz 12.11 (живий прохід W15r, U3): що означає «повністю заповнено» ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.tablesFilledHint', N'ru', N'Таблица считается полностью заполненной, только когда заполнены все ячейки, которые вы можете вводить, включая необязательные. В таблицах с данными введено хотя бы одно значение.'),
    (N'document.tablesFilledHint', N'kz', N'Кесте сіз енгізе алатын барлық ұяшықтар, оның ішінде міндетті емес ұяшықтар толтырылғанда ғана толық толтырылған болып саналады. Деректері бар кестелерде кемінде бір мән енгізілген.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCHDR:freshness-fill ── кінець секції ──
-- DOCHDR:freshness-fill ── en 12.11 (U3); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.tablesFilledHint', N'en', N'A table counts as fully filled only when every cell you can enter in it has a value, optional cells included. Tables with data have at least one value entered.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCHDR:freshness-fill ── кінець секції ──
-- DOCHDR:freshness-fill ── змінені тексти наявних ключів (12.11, живий прохід W15r: U2 «Freshness unknown», U3 «0 of 49») ──
-- Нове значення вже стоїть у MERGE (D446-stale-undetermined.sql, LUF-fixes.sql); епілог оновлює, лише поки в базі старе.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'documents.staleUndetermined', N'en', N'Freshness unknown', N'Results status hidden'),
    (N'documents.staleUndetermined', N'ru', N'Актуальность не определена', N'Статус результатов скрыт'),
    (N'documents.staleUndetermined', N'kz', N'Өзектілігі анықталмайды', N'Нәтижелер күйі жасырын'),
    (N'documents.staleUndeterminedHint', N'en', N'Some of the data is hidden from you, so result freshness can''t be determined', N'Part of this document is hidden from you, so we can''t show whether its calculated results are up to date. Someone who can see the whole document can check this and recalculate if needed.'),
    (N'documents.staleUndeterminedHint', N'ru', N'Часть данных вам не открыта — актуальность результатов не определяется', N'Часть документа вам не открыта, поэтому нельзя показать, актуальны ли рассчитанные результаты. Проверить это и при необходимости пересчитать может тот, кому открыт весь документ.'),
    (N'documents.staleUndeterminedHint', N'kz', N'Деректердің бір бөлігі сізге ашық емес — нәтижелердің өзектілігі анықталмайды', N'Құжаттың бір бөлігі сізге ашық емес, сондықтан есептелген нәтижелердің өзектілігін көрсету мүмкін емес. Мұны бүкіл құжатқа қолжетімділігі бар адам тексеріп, қажет болса қайта есептей алады.'),
    (N'document.tablesFilledInDocument', N'en', N'Tables filled completely in the whole document: {filled} of {total}', N'Tables with data in the whole document: {withData} of {total} · fully filled: {filled}'),
    (N'document.tablesFilledInDocument', N'ru', N'Полностью заполнено таблиц во всём документе: {filled} из {total}', N'Таблиц с данными во всём документе: {withData} из {total} · полностью заполнено: {filled}'),
    (N'document.tablesFilledInDocument', N'kz', N'Бүкіл құжатта толық толтырылған кестелер: {filled} / {total}', N'Бүкіл құжатта деректері бар кестелер: {withData} / {total} · толық толтырылғаны: {filled}')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- DOCHDR:freshness-fill ── кінець секції ──
