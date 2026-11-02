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
