-- COLL:an109-csv-utf8 ── ru/kz: AN-109 / S1-03: импорт CSV переводов не в UTF-8; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.uiStringCsvNotUtf8', N'ru', N'Файл не является текстом в UTF-8 (возможно, он сохранён в кодовой странице Windows). Сохраните его как CSV UTF-8 и повторите. Ничего не импортировано.'),
    (N'err.ECR-REQ-0422.uiStringCsvNotUtf8', N'kz', N'Файл UTF-8 мәтіні емес (Windows кодтау беті болуы мүмкін). Оны CSV UTF-8 ретінде сақтап, қайталап көріңіз. Ештеңе импортталмады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an109-csv-utf8 ── кінець секції ──

