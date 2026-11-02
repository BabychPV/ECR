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

