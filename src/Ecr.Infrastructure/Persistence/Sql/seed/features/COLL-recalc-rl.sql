-- COLL:recalc-rl ── ru/kz межі частоти перерахунку документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyRecalculations', N'ru', N'Слишком много запросов на пересчёт за короткое время (предел: 6 в минуту на один документ от одного пользователя). Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyRecalculations', N'kz', N'Қысқа уақыт ішінде қайта есептеуге сұраныстар тым көп (шек: бір пайдаланушыдан бір құжат бойынша минутына 6). Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:recalc-rl ── кінець секції ──

