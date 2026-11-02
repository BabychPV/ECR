-- COLL:consistency-summary ── ru/kz: невідома вага знахідки в журналі узгодженості; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.consistencySeverity', N'ru', N'Веса находки «{severity}» не существует: допустимо 1 (информация), 2 (предупреждение) или 3 (ошибка).'),
    (N'err.ECR-REQ-0422.consistencySeverity', N'kz', N'«{severity}» табылым салмағы жоқ: 1 (ақпарат), 2 (ескерту) немесе 3 (қате) болуы тиіс.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:consistency-summary ── кінець секції ──
