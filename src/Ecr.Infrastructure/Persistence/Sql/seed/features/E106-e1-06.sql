-- E1-06 ── ru/kz: тіло запиту завелике; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.requestTooLarge', N'ru', N'Тело запроса больше, чем принимает сервер. Отправляйте меньше данных за один запрос.'),
    (N'err.ECR-REQ-0422.requestTooLarge', N'kz', N'Сұраныс денесі сервер қабылдайтын көлемнен үлкен. Бір сұраныста азырақ деректер жіберіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- E1-06 ── кінець секції ──

