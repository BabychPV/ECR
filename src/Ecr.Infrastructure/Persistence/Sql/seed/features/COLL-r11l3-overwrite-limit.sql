-- COLL:r11l3-overwrite-limit ── ru/kz: R11-L3 / R1-05: застосування імпорту .xlsx з понад 5000 рядків для перезапису - 422, а не 500; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.importTooManyOverwriteRows', N'ru', N'Для перезаписи отмечено больше {maxRows} строк. Ничего не применено: перезапишите строки несколькими партиями.'),
    (N'err.ECR-IMP-0422.importTooManyOverwriteRows', N'kz', N'Қайта жазу үшін {maxRows} жолдан көп белгіленген. Ештеңе қолданылмады: жолдарды бірнеше бөлікпен қайта жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l3-overwrite-limit ── кінець секції ──

