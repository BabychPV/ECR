-- COLL:p3-ent7c ── ru/kz: число грантів, що блокують перенос версії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.migrateGrantsNotMappedCount', N'ru', N'Заблокировано: прав на листы, таблицы или колонки без соответствия в новой версии — {count}. Снимите их или создайте заново.'),
    (N'documents.migrateGrantsNotMappedCount', N'kz', N'Бұғатталған: жаңа нұсқада баламасы жоқ парақтарға, кестелерге немесе бағандарға құқықтар саны — {count}. Оларды алып тастаңыз немесе қайта жасаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-ent7c ── кінець секції ──

-- COLL:p3-ent7c ── число грантів, що блокують перенос версії (ent7 P3-4); лише кількість, без переліку ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.migrateGrantsNotMappedCount', N'en', N'Blocked: {count} permission(s) on sheets, tables or columns have no counterpart in the new version. Remove them or recreate them afterwards.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p3-ent7c ── кінець секції ──
