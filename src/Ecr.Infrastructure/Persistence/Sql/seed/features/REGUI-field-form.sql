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

-- REG-UI:field-form ── UI довідників, коміт 1: форма поля довідника (підписи й підказки до властивостей); ru/kz — порцією REG-UI:field-form у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.fieldForm.title', N'en', N'New field', 1),
    (N'registries.fieldForm.intro', N'en', N'A field is one column of every entry in this registry. The code and the name identify it, the type decides what can be entered. After the description is published, the code and the type cannot be changed.', 1),
    (N'registries.fieldForm.codeHint', N'en', N'Short unique identifier, for example VOLUME. Formulas, imports and mappings refer to the field by it.', 1),
    (N'registries.fieldForm.nameHint', N'en', N'What people see in the column header and in forms.', 1),
    (N'registries.fieldForm.type.String', N'en', N'Text of any content.', 1),
    (N'registries.fieldForm.type.Int', N'en', N'Whole number.', 1),
    (N'registries.fieldForm.type.Decimal', N'en', N'Number with a fractional part; a unit of measure can be set.', 1),
    (N'registries.fieldForm.type.Bool', N'en', N'Yes or no.', 1),
    (N'registries.fieldForm.type.Date', N'en', N'A calendar date.', 1),
    (N'registries.fieldForm.type.Lookup', N'en', N'A choice of an entry from another registry; choose which one below.', 1),
    (N'registries.fieldForm.type.Unit', N'en', N'A choice of a unit of measure for each entry.', 1),
    (N'registries.fieldForm.requiredHint', N'en', N'Every entry must have a value. Possible only while the registry has no entries.', 1),
    (N'registries.fieldForm.requiredLocked', N'en', N'Not available: the registry already has entries and none of them has a value for a new field.', 1),
    (N'registries.fieldForm.keyHint', N'en', N'Part of the business key that identifies an entry. Access rights can be narrowed by key fields.', 1),
    (N'registries.fieldForm.lookupHint', N'en', N'The registry whose entries this field offers to choose from.', 1),
    (N'registries.fieldForm.unitHint', N'en', N'Unit of measure in which the number is entered. Optional.', 1),
    (N'registries.fieldForm.errCode', N'en', N'Enter a code.', 1),
    (N'registries.fieldForm.errName', N'en', N'Enter a name.', 1),
    (N'registries.fieldForm.errLookup', N'en', N'Choose the registry this field picks from.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG-UI:field-form ── кінець секції ──
