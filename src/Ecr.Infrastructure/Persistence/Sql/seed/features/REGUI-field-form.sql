-- REG-UI:field-form ── ru/kz UI довідників, коміт 1: форма поля довідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.fieldForm.title', N'ru', N'Новое поле'),
    (N'registries.fieldForm.title', N'kz', N'Жаңа өріс'),
    (N'registries.fieldForm.intro', N'ru', N'Поле — это один столбец каждой записи этого справочника. Код и название определяют его, тип задаёт, что можно ввести. После публикации описания код и тип изменить нельзя.'),
    (N'registries.fieldForm.intro', N'kz', N'Өріс — осы анықтамалықтың әрбір жазбасының бір бағаны. Код пен атау оны анықтайды, түрі нені енгізуге болатынын белгілейді. Сипаттама жарияланғаннан кейін код пен түрді өзгертуге болмайды.'),
    (N'registries.fieldForm.codeHint', N'ru', N'Короткий уникальный идентификатор, например VOLUME. По нему на поле ссылаются формулы, импорт и сопоставления.'),
    (N'registries.fieldForm.codeHint', N'kz', N'Қысқа бірегей идентификатор, мысалы VOLUME. Формулалар, импорт және сәйкестендірулер өріске осы код арқылы сілтейді.'),
    (N'registries.fieldForm.nameHint', N'ru', N'Что люди видят в заголовке столбца и в формах.'),
    (N'registries.fieldForm.nameHint', N'kz', N'Адамдар баған тақырыбында және формаларда көретін атау.'),
    (N'registries.fieldForm.type.String', N'ru', N'Текст произвольного содержания.'),
    (N'registries.fieldForm.type.String', N'kz', N'Кез келген мазмұнды мәтін.'),
    (N'registries.fieldForm.type.Int', N'ru', N'Целое число.'),
    (N'registries.fieldForm.type.Int', N'kz', N'Бүтін сан.'),
    (N'registries.fieldForm.type.Decimal', N'ru', N'Число с дробной частью; можно задать единицу измерения.'),
    (N'registries.fieldForm.type.Decimal', N'kz', N'Бөлшек бөлігі бар сан; өлшем бірлігін беруге болады.'),
    (N'registries.fieldForm.type.Bool', N'ru', N'Да или нет.'),
    (N'registries.fieldForm.type.Bool', N'kz', N'Иә немесе жоқ.'),
    (N'registries.fieldForm.type.Date', N'ru', N'Календарная дата.'),
    (N'registries.fieldForm.type.Date', N'kz', N'Күнтізбелік күн.'),
    (N'registries.fieldForm.type.Lookup', N'ru', N'Выбор записи из другого справочника; ниже укажите, из какого.'),
    (N'registries.fieldForm.type.Lookup', N'kz', N'Басқа анықтамалықтан жазба таңдау; төменде қайсысын көрсетіңіз.'),
    (N'registries.fieldForm.type.Unit', N'ru', N'Выбор единицы измерения для каждой записи.'),
    (N'registries.fieldForm.type.Unit', N'kz', N'Әрбір жазба үшін өлшем бірлігін таңдау.'),
    (N'registries.fieldForm.requiredHint', N'ru', N'У каждой записи должно быть значение. Возможно, пока в справочнике нет записей.'),
    (N'registries.fieldForm.requiredHint', N'kz', N'Әр жазбада мән болуы тиіс. Анықтамалықта жазбалар жоқ кезде ғана мүмкін.'),
    (N'registries.fieldForm.requiredLocked', N'ru', N'Недоступно: в справочнике уже есть записи, и ни в одной нет значения для нового поля.'),
    (N'registries.fieldForm.requiredLocked', N'kz', N'Қолжетімсіз: анықтамалықта жазбалар бар, ал олардың ешқайсысында жаңа өріс үшін мән жоқ.'),
    (N'registries.fieldForm.keyHint', N'ru', N'Часть бизнес-ключа, по которому узнают запись. По ключевым полям можно сужать доступ.'),
    (N'registries.fieldForm.keyHint', N'kz', N'Жазбаны анықтайтын бизнес-кілттің бөлігі. Қолжетімділікті кілттік өрістер бойынша тарылтуға болады.'),
    (N'registries.fieldForm.lookupHint', N'ru', N'Справочник, записи которого предлагаются для выбора в этом поле.'),
    (N'registries.fieldForm.lookupHint', N'kz', N'Осы өріс таңдауға ұсынатын жазбалары бар анықтамалық.'),
    (N'registries.fieldForm.unitHint', N'ru', N'Единица измерения, в которой вводится число. Необязательно.'),
    (N'registries.fieldForm.unitHint', N'kz', N'Сан енгізілетін өлшем бірлігі. Міндетті емес.'),
    (N'registries.fieldForm.errCode', N'ru', N'Укажите код.'),
    (N'registries.fieldForm.errCode', N'kz', N'Кодты енгізіңіз.'),
    (N'registries.fieldForm.errName', N'ru', N'Укажите название.'),
    (N'registries.fieldForm.errName', N'kz', N'Атауды енгізіңіз.'),
    (N'registries.fieldForm.errLookup', N'ru', N'Выберите справочник, из которого поле берёт записи.'),
    (N'registries.fieldForm.errLookup', N'kz', N'Өріс жазба алатын анықтамалықты таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG-UI:field-form ── кінець секції ──

