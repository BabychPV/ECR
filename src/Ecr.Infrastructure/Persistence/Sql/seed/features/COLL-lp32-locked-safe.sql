-- COLL:lp32-locked-safe ── ru/kz: перенос версии с поданными/утверждёнными листами; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.migrateLockedCarried', N'ru', N'Поданные или утверждённые листы, перенесённые без изменений (статус и значения сохраняются, формульные колонки не пересчитываются, пока лист не возвращён в работу): {count}.'),
    (N'documents.migrateLockedCarried', N'kz', N'Өзгеріссіз көшірілген тапсырылған немесе бекітілген парақтар (мәртебе мен мәндер сақталады, формулалы бағандар парақ жұмысқа қайтарылғанға дейін қайта есептелмейді): {count}.'),
    (N'documents.migrateLockedAffected', N'ru', N'Перенос изменил бы поданные или утверждённые листы: {count}. Сначала верните их в работу или выберите версию, которая их не меняет.'),
    (N'documents.migrateLockedAffected', N'kz', N'Көшіру тапсырылған немесе бекітілген парақтарды өзгертер еді: {count}. Алдымен оларды жұмысқа қайтарыңыз немесе оларды өзгертпейтін нұсқаны таңдаңыз.'),
    (N'documents.migrateLockedItem', N'ru', N'Лист {sheet}, период {period}: {status}'),
    (N'documents.migrateLockedItem', N'kz', N'Парақ {sheet}, кезең {period}: {status}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:lp32-locked-safe ── кінець секції ──
-- COLL:lp32-locked-safe ── LP-32: перенос версії з поданими/затвердженими аркушами; ru/kz — порцією COLL:lp32-locked-safe нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.migrateLockedCarried', N'en', N'Submitted or approved sheets carried over unchanged (status and values stay, formula columns are not recalculated until the sheet is returned to work): {count}.', 1),
    (N'documents.migrateLockedAffected', N'en', N'The move would change submitted or approved sheets: {count}. Return them to work first, or choose a version that does not change them.', 1),
    (N'documents.migrateLockedItem', N'en', N'Sheet {sheet}, period {period}: {status}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:lp32-locked-safe ── кінець секції ──
