-- AUDITUX:filters-time ── ru/kz: P-1124-4 (24.11) фільтри й час журналів аудиту; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.eventTypeAny', N'ru', N'Любое событие'),
    (N'audit.eventTypeAny', N'kz', N'Кез келген оқиға'),
    (N'audit.entityTypeAny', N'ru', N'Любой тип'),
    (N'audit.entityTypeAny', N'kz', N'Кез келген түр'),
    (N'audit.actionAny', N'ru', N'Любое действие'),
    (N'audit.actionAny', N'kz', N'Кез келген әрекет'),
    (N'audit.filterNothing', N'ru', N'За этот период значений нет'),
    (N'audit.filterNothing', N'kz', N'Бұл кезеңде мәндер жоқ'),
    (N'audit.siteTime', N'ru', N'Время площадки ({zone}): {time}'),
    (N'audit.siteTime', N'kz', N'Алаң уақыты ({zone}): {time}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- AUDITUX:filters-time ── кінець секції ──
-- AUDITUX:filters-time ── P-1124-4 (24.11): вибір типу події/дії замість вільного тексту, час журналу в поясі читача; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.eventTypeAny', N'en', N'Any event', 1),
    (N'audit.entityTypeAny', N'en', N'Any type', 1),
    (N'audit.actionAny', N'en', N'Any action', 1),
    (N'audit.filterNothing', N'en', N'No values in this period', 1),
    (N'audit.siteTime', N'en', N'Site time ({zone}): {time}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- AUDITUX:filters-time ── кінець секції ──
