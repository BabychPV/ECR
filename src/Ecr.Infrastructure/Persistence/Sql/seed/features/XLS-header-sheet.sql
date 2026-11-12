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
