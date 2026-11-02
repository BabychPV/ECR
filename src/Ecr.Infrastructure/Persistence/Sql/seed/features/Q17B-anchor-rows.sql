-- Q17b: anchorRows ── ru/kz: Q-17/D-457, рядки додаються й видаляються в періоді-якорі; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-ACCS-0403.addRowAnchorPeriod', N'ru', N'Строки этой таблицы добавляются в периоде {anchorPeriod}; у вашей роли нет доступа к нему.'),
    (N'err.ECR-ACCS-0403.addRowAnchorPeriod', N'kz', N'Бұл кестенің жолдары {anchorPeriod} кезеңінде қосылады; сіздің рөліңіздің оған қолжетімділігі жоқ.'),
    (N'err.ECR-ACCS-0403.deleteRowAnchorPeriod', N'ru', N'Строки этой таблицы удаляются в периоде {anchorPeriod}; у вашей роли нет доступа к нему.'),
    (N'err.ECR-ACCS-0403.deleteRowAnchorPeriod', N'kz', N'Бұл кестенің жолдары {anchorPeriod} кезеңінде жойылады; сіздің рөліңіздің оған қолжетімділігі жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- Q17b: anchorRows ── кінець секції ──
