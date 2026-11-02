-- COLL:an35b ── ru/kz AN-35 (L5-11): імпорт CSV; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.entriesCsvUnterminatedQuote', N'ru', N'Файл заканчивается внутри кавычек: не хватает закрывающей кавычки. Ничего не импортировано.'),
    (N'err.ECR-REG-0422.entriesCsvNotUtf8', N'ru', N'Файл не является текстом в UTF-8 (возможно, он сохранён в кодовой странице Windows). Сохраните его как CSV UTF-8 и повторите.'),
    (N'err.ECR-REQ-0422.uiStringCsvUnterminatedQuote', N'ru', N'Файл заканчивается внутри кавычек: не хватает закрывающей кавычки. Ничего не импортировано.'),
    (N'err.ECR-REG-0422.entriesCsvUnterminatedQuote', N'kz', N'Файл тырнақша ішінде аяқталады: жабатын тырнақша жоқ. Ештеңе импортталмады.'),
    (N'err.ECR-REG-0422.entriesCsvNotUtf8', N'kz', N'Файл UTF-8 мәтіні емес (Windows кодтау беті болуы мүмкін). Оны CSV UTF-8 ретінде сақтап, қайталап көріңіз.'),
    (N'err.ECR-REQ-0422.uiStringCsvUnterminatedQuote', N'kz', N'Файл тырнақша ішінде аяқталады: жабатын тырнақша жоқ. Ештеңе импортталмады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an35b ── кінець секції ──
