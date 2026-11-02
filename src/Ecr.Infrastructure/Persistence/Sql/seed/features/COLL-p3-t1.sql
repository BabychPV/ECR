-- COLL:p3-t1 ── ru/kz пакета P3 тестувального проходу №1; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-USR-0422.emailInvalid', N'ru', N'«{email}» не является корректным адресом электронной почты. Используйте вид name@example.com.'),
    (N'err.ECR-USR-0422.emailInvalid', N'kz', N'«{email}» жарамды электрондық пошта мекенжайы емес. name@example.com түрін пайдаланыңыз.'),
    (N'err.ECR-SEC-0409.grantsStale', N'ru', N'Доступы роли изменили после того, как вы их открыли. Перечитайте доступы и повторите изменения.'),
    (N'err.ECR-SEC-0409.grantsStale', N'kz', N'Рөлдің қолжетімділіктері сіз оларды ашқаннан кейін өзгертілді. Қолжетімділіктерді қайта оқып, өзгерістеріңізді қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-t1 ── кінець секції ──
