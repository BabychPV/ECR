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
