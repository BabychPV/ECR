-- COLL:methodology-substances ── ru/kz: склад речовин версії методології; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.substancesRequired', N'ru', N'Перечень веществ не передан: пустой состав записывается пустым перечнем, а не отсутствующим.'),
    (N'err.ECR-CALC-0422.substancesRequired', N'kz', N'Заттар тізімі берілмеген: бос құрам бос тізіммен жазылады, жоқ тізіммен емес.'),
    (N'err.ECR-CALC-0422.substancesTooMany', N'ru', N'В версии не может быть больше {max} веществ (передано: {count}).'),
    (N'err.ECR-CALC-0422.substancesTooMany', N'kz', N'Нұсқада {max} заттан артық болмауы тиіс (жіберілгені: {count}).'),
    (N'err.ECR-CALC-0422.substanceDuplicate', N'ru', N'Вещество {substanceEntryId} указано дважды.'),
    (N'err.ECR-CALC-0422.substanceDuplicate', N'kz', N'{substanceEntryId} заты екі рет көрсетілген.'),
    (N'err.ECR-CALC-0422.substanceNotFound', N'ru', N'Вещество (запись справочника) {substanceEntryId} не существует.'),
    (N'err.ECR-CALC-0422.substanceNotFound', N'kz', N'{substanceEntryId} заты (анықтамалық жазбасы) жоқ.'),
    (N'err.ECR-CALC-0409.substanceInUse', N'ru', N'Вещество нельзя убрать: на него сужены константы {constantCodes} (всего: {count}). Сначала измените эти константы.'),
    (N'err.ECR-CALC-0409.substanceInUse', N'kz', N'Затты алып тастауға болмайды: оған {constantCodes} тұрақтылары тарылтылған (барлығы: {count}). Алдымен осы тұрақтыларды өзгертіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:methodology-substances ── кінець секції ──
-- COLL:methodology-substances ── PUT/GET …/versions/{vid}/substances: склад речовин версії; ru/kz — порцією COLL:methodology-substances нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.substancesRequired', N'en', N'The substance list is missing: an empty composition is written as an empty list, not an absent one.', 1),
    (N'err.ECR-CALC-0422.substancesTooMany', N'en', N'A version cannot have more than {max} substances (sent: {count}).', 1),
    (N'err.ECR-CALC-0422.substanceDuplicate', N'en', N'Substance {substanceEntryId} is listed twice.', 1),
    (N'err.ECR-CALC-0422.substanceNotFound', N'en', N'Substance (registry entry) {substanceEntryId} does not exist.', 1),
    (N'err.ECR-CALC-0409.substanceInUse', N'en', N'The substance cannot be removed: constants {constantCodes} (total: {count}) are narrowed to it. Change those constants first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:methodology-substances ── кінець секції ──
