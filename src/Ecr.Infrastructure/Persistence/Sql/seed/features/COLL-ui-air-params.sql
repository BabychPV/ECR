-- COLL:ui-air-params ── ru/kz: крок «Region and area» майстра «Import parameters…» дозволу AIR (AIR-ON-AIR); власна порція; en — у MERGE 00-prelude.sql ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.air.import.step.scope', N'ru', N'Регион и участок'),
    (N'permits.air.import.step.scope', N'kz', N'Аймақ және учаске'),
    (N'permits.air.import.hint.scope', N'ru', N'Выберите, к чему относятся параметры: регион (и участок) расположения разрешения.'),
    (N'permits.air.import.hint.scope', N'kz', N'Параметрлер неге жататынын таңдаңыз: рұқсат орналасқан аймақ (және учаске).'),
    (N'permits.air.import.permitNumber', N'ru', N'Номер разрешения'),
    (N'permits.air.import.permitNumber', N'kz', N'Рұқсат нөмірі'),
    (N'permits.air.import.text.placeholder', N'ru', N'Введите значение'),
    (N'permits.air.import.text.placeholder', N'kz', N'Мәнді енгізіңіз'),
    (N'permits.air.import.scope.nothingFound', N'ru', N'Ничего не найдено'),
    (N'permits.air.import.scope.nothingFound', N'kz', N'Ештеңе табылмады'),
    (N'permits.air.import.scope.notInTreeTitle', N'ru', N'Региона или участка нет в дереве НАЕ'),
    (N'permits.air.import.scope.notInTreeTitle', N'kz', N'Аймақ немесе учаске НАЕ ағашында жоқ'),
    (N'permits.air.import.scope.notInTreeText', N'ru', N'Коды НАЕ из файла будут сопоставлены без учёта региона. Проверьте регион или добавьте его в дерево НАЕ.'),
    (N'permits.air.import.scope.notInTreeText', N'kz', N'Файлдағы НАЕ кодтары аймақсыз сәйкестендіріледі. Аймақты тексеріңіз немесе оны НАЕ ағашына қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-params ── кінець секції ──
