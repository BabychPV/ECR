-- B54:no-expectations ── (підказка очікуваного JSON — НОВИЙ ключ expectedJsonEmptyHint: текст наявного expectedJsonHint лежить у прологу, який не чіпаємо, а сторож тримає «нове» рівним каталогу) ru/kz: окремий ключ «нічого не перевірено» (випадок без очікувань), підказка на екрані прогону, новий текст підказки очікуваного JSON; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.goldenNoExpectations', N'ru', N'Версию {version} нельзя опубликовать: у случаев ({tests}) нет ожидаемых значений, поэтому ничего не проверено.'),
    (N'err.ECR-CALC-0422.goldenNoExpectations', N'kz', N'{version} нұсқасын жариялауға болмайды: ({tests}) жағдайларында күтілетін мәндер жоқ, сондықтан ештеңе тексерілмеді.'),
    (N'expressions.noExpectationsHint', N'ru', N'Набор красный не из-за расхождений: ни у одного случая нет ожидаемых значений, поэтому ничего не проверено. Задайте ожидаемые числа выходов.'),
    (N'expressions.noExpectationsHint', N'kz', N'Жиынтық сәйкессіздікке байланысты емес, қызыл: бірде-бір жағдайдың күтілетін мәні жоқ, сондықтан ештеңе тексерілмеді. Шығыс мәндердің күтілетін сандарын көрсетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B54:no-expectations ── кінець секції ──

-- B54:no-expectations ── en-порція ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.goldenNoExpectations', N'en', N'Version {version} cannot be published: the cases ({tests}) have no expected values, so nothing was checked.', 1),
    (N'expressions.noExpectationsHint', N'en', N'The set is red not because of divergences: no case has expected values, so nothing was checked. Set the expected output numbers.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B54:no-expectations ── кінець секції ──
