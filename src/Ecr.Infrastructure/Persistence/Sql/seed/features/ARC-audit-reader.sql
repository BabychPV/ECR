-- ARC:audit-reader ── ru/kz: НФ-8.9.6: позначка «Архів» і вибір періоду в журналі змін комірок; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.fromArchive', N'ru', N'Архив'),
    (N'audit.fromArchive', N'kz', N'Мұрағат'),
    (N'audit.fromArchiveHint', N'ru', N'Прочитано из архива аудита: изменение старше 24 месяцев и перенесено из рабочего журнала. Значения и правила доступа те же.'),
    (N'audit.fromArchiveHint', N'kz', N'Аудит мұрағатынан оқылды: өзгеріс 24 айдан ескі және жұмыс журналынан көшірілген. Мәндер мен қолжетімділік ережелері бірдей.'),
    (N'audit.archiveWindow', N'ru', N'Архив (старше 24 месяцев)'),
    (N'audit.archiveWindow', N'kz', N'Мұрағат (24 айдан ескі)'),
    (N'audit.archiveWindowHint', N'ru', N'Ставит интервал на 90 дней перед границей архива. Более старые изменения читаются из архива аудита и помечаются «Архив».'),
    (N'audit.archiveWindowHint', N'kz', N'Аралықты мұрағат шекарасына дейінгі 90 күнге қояды. Одан ескі өзгерістер аудит мұрағатынан оқылып, «Мұрағат» деп белгіленеді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- ARC:audit-reader ── кінець секції ──
-- ARC:audit-reader ── НФ-8.9.6: журнал змін комірок читає й архів аудиту (arc.AuditCellChange): позначка рядка й вибір періоду старше гарячого вікна; ru/kz — порцією ARC:audit-reader нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.fromArchive', N'en', N'Archive', 1),
    (N'audit.fromArchiveHint', N'en', N'Read from the audit archive: this change is older than 24 months and was moved out of the working journal. Values and access rules are the same.', 1),
    (N'audit.archiveWindow', N'en', N'Archive (older than 24 months)', 1),
    (N'audit.archiveWindowHint', N'en', N'Sets the date range to the 90 days before the archive boundary. Older changes are read from the audit archive and marked "Archive".', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- ARC:audit-reader ── кінець секції ──
