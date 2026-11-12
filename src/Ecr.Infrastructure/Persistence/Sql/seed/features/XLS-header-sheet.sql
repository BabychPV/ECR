-- XLS:header-sheet ── аудит 2026-11 X-5 (D-477): вкладка шапки документа в xlsx зветься як панель шапки в UI ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з COLL-a212.sql; епілог оновлює наявні бази, лише поки
-- там стоїть САМЕ старе значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'export.header.sheet', N'en', N'Header', N'Document header'),
    (N'export.header.sheet', N'ru', N'Шапка', N'Шапка документа'),
    (N'export.header.sheet', N'kz', N'Тақырып', N'Құжаттың тақырып бөлігі')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- XLS:header-sheet ── кінець секції ──
-- XLS:header-period ── ru/kz аудит 2026-11 X-4: рядок звітного періоду в аркуші шапки xlsx; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'export.header.period', N'ru', N'Отчётный период'),
    (N'export.header.period', N'kz', N'Есепті кезең')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- XLS:header-period ── кінець секції ──
-- XLS:header-period ── аудит 2026-11 X-4: рядок звітного періоду в аркуші шапки xlsx (File Number / Version беруть ключі document.header.contract.*); ru/kz — порцією XLS:header-period вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'export.header.period', N'en', N'Reporting period', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- XLS:header-period ── кінець секції ──
