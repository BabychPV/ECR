-- COLL:sec-t102 ── ru/kz імені входу (тестувальник T1-03); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-USR-0422.userNameInvalid', N'ru', N'Имя пользователя должно содержать от 1 до {maxLength} символов: только буквы, цифры и . _ - @ \, без пробелов.'),
    (N'err.ECR-USR-0422.userNameInvalid', N'kz', N'Пайдаланушы аты 1-ден {maxLength} таңбаға дейін болуы керек: тек әріптер, сандар және . _ - @ \, бос орынсыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-t102 ── кінець секції ──

