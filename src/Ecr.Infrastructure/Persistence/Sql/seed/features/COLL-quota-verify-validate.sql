-- COLL:quota-verify-validate ── ru/kz: L1-05/P1-08: межа частоти перевірки зрізу й документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyVerifications', N'ru', N'Слишком много запросов на проверку среза за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyVerifications', N'kz', N'Қысқа уақыт ішінде кесіндіні тексеруге сұраныстар тым көп. Біраз күтіп, қайталап көріңіз.'),
    (N'err.ECR-REQ-0429.tooManyValidations', N'ru', N'Слишком много запросов на проверку документа за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyValidations', N'kz', N'Қысқа уақыт ішінде құжатты тексеруге сұраныстар тым көп. Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:quota-verify-validate ── кінець секції ──
-- COLL:quota-verify-validate ── L1-05/P1-08: межа частоти «Перевірити зріз» і «Перевірити документ» на користувача; ru/kz — порцією COLL:quota-verify-validate нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyVerifications', N'en', N'Too many snapshot verification requests in a short time. Wait a moment and try again.', 1),
    (N'err.ECR-REQ-0429.tooManyValidations', N'en', N'Too many document validation requests in a short time. Wait a moment and try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:quota-verify-validate ── кінець секції ──
