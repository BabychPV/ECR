-- DOC:export-rate-limit ── ru/kz: PT-6 (пентест 2026-11): межа частоти експорту документа й побудови зрізу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyExports', N'ru', N'Слишком много запросов на экспорт за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyExports', N'kz', N'Қысқа уақыт ішінде экспорт сұраулары тым көп. Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOC:export-rate-limit ── кінець секції ──
-- DOC:export-rate-limit ── PT-6: межа частоти експорту й побудови зрізу на користувача; ru/kz — порцією DOC:export-rate-limit вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyExports', N'en', N'Too many export requests in a short time. Wait a moment and try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOC:export-rate-limit ── кінець секції ──
