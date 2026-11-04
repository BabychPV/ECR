-- COLL:warn-grid ── ru/kz значка «правка поза вікном» на комірці сітки (ФВ-2.16, D-239); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.outOfWindowHint', N'ru', N'Сохранено после закрытия окна доступа (политика Warn); правка отмечена в журнале изменений.'),
    (N'grid.outOfWindowHint', N'kz', N'Қолжетімділік терезесі жабылғаннан кейін сақталды (Warn саясаты); түзету өзгерістер журналында белгіленді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:warn-grid ── кінець секції ──

-- COLL:warn-grid ── ФВ-2.16 (D-239): значок «правка поза вікном» на комірці сітки (`DocumentGrid`, `outOfWindowMarks.ts`).
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.outOfWindowHint', N'en', N'Saved after the access window closed (Warn policy); the change is marked in the change log.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:warn-grid: кінець секції
