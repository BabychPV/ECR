-- PERMITS: bindings ── ECR-PRM-0422 ── ru/kz: прив'язка аркуша до дозволу некоректна; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0422.bindingTypeNotPublished', N'ru', N'Тип разрешения ещё не опубликован: привязать к нему лист нельзя.'),
    (N'err.ECR-PRM-0422.bindingTypeNotPublished', N'kz', N'Рұқсат түрі әлі жарияланбаған: оған парақты байлауға болмайды.'),
    (N'err.ECR-PRM-0422.bindingSheetNotInVersion', N'ru', N'Таблицы {tableDefId} в этой версии шаблона нет.'),
    (N'err.ECR-PRM-0422.bindingSheetNotInVersion', N'kz', N'Үлгінің осы нұсқасында {tableDefId} кестесі жоқ.'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnknown', N'ru', N'Поля шапки {headerFieldDefId}, из которого берётся разрешение, в этой версии шаблона нет.'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnknown', N'kz', N'Рұқсат алынатын {headerFieldDefId} тақырып өрісі үлгінің осы нұсқасында жоқ.'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldNotLookup', N'ru', N'Поле шапки «{headerField}» не ссылается на справочник и не может хранить разрешение.'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldNotLookup', N'kz', N'«{headerField}» тақырып өрісі анықтамалыққа сілтемейді, сондықтан рұқсатты сақтай алмайды.'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnexpected', N'ru', N'Поле шапки задаётся только для правила «разрешение из поля шапки».'),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnexpected', N'kz', N'Тақырып өрісі тек «рұқсат тақырып өрісінен» ережесі үшін беріледі.'),
    (N'err.ECR-PRM-0422.bindingFacilityFieldMissing', N'ru', N'Чтобы выбирать разрешение по объекту, в шапке шаблона должно быть поле «{fieldCode}».'),
    (N'err.ECR-PRM-0422.bindingFacilityFieldMissing', N'kz', N'Рұқсатты объект бойынша таңдау үшін үлгі тақырыбында «{fieldCode}» өрісі болуы керек.'),
    (N'err.ECR-PRM-0422.bindingFacilityFieldType', N'ru', N'Поле шапки «{fieldCode}» имеет тип {dataType}; оно должно быть текстом или ссылкой на справочник.'),
    (N'err.ECR-PRM-0422.bindingFacilityFieldType', N'kz', N'«{fieldCode}» тақырып өрісінің түрі {dataType}; ол мәтін немесе анықтамалыққа сілтеме болуы керек.'),
    (N'err.ECR-PRM-0422.bindingFormulasHaveColumns', N'ru', N'В режиме формул столбцы не сопоставляются: формулы сами читают разрешение.'),
    (N'err.ECR-PRM-0422.bindingFormulasHaveColumns', N'kz', N'Формула режимінде бағандар сәйкестендірілмейді: формулалар рұқсатты өздері оқиды.'),
    (N'err.ECR-PRM-0422.bindingRowsNeedDynamic', N'ru', N'В таблице «{tableCode}» фиксированные строки: строки разрешения в неё не добавить.'),
    (N'err.ECR-PRM-0422.bindingRowsNeedDynamic', N'kz', N'«{tableCode}» кестесінде бекітілген жолдар бар: оған рұқсат жолдарын қосуға болмайды.'),
    (N'err.ECR-PRM-0422.bindingColumnNotInSheet', N'ru', N'Столбца {columnId} в таблице «{tableCode}» нет.'),
    (N'err.ECR-PRM-0422.bindingColumnNotInSheet', N'kz', N'«{tableCode}» кестесінде {columnId} бағаны жоқ.'),
    (N'err.ECR-PRM-0422.bindingPermitFieldUnknown', N'ru', N'В таблице разрешения нет поля «{fieldCode}».'),
    (N'err.ECR-PRM-0422.bindingPermitFieldUnknown', N'kz', N'Рұқсат кестесінде «{fieldCode}» өрісі жоқ.'),
    (N'err.ECR-PRM-0422.bindingDuplicateField', N'ru', N'Поле разрешения «{fieldCode}» сопоставлено дважды.'),
    (N'err.ECR-PRM-0422.bindingDuplicateField', N'kz', N'«{fieldCode}» рұқсат өрісі екі рет сәйкестендірілген.'),
    (N'err.ECR-PRM-0422.bindingDuplicateColumn', N'ru', N'Столбец «{columnCode}» сопоставлен дважды.'),
    (N'err.ECR-PRM-0422.bindingDuplicateColumn', N'kz', N'«{columnCode}» бағаны екі рет сәйкестендірілген.'),
    (N'err.ECR-PRM-0422.bindingNoRowKey', N'ru', N'Нужен хотя бы один столбец с ролью «ключ строки», иначе строку листа не связать со строкой разрешения.'),
    (N'err.ECR-PRM-0422.bindingNoRowKey', N'kz', N'Кемінде бір баған «жол кілті» рөлінде болуы керек, әйтпесе парақ жолын рұқсат жолымен байланыстыруға болмайды.'),
    (N'err.ECR-PRM-0422.bindingManyLimits', N'ru', N'Роль «лимит» может иметь только один столбец.'),
    (N'err.ECR-PRM-0422.bindingManyLimits', N'kz', N'«Лимит» рөлі тек бір бағанда болуы мүмкін.'),
    (N'err.ECR-PRM-0422.bindingTypeMismatch', N'ru', N'Поле «{fieldCode}» ({permitType}) не подходит столбцу «{columnCode}» ({columnType}): типы не преобразуются.'),
    (N'err.ECR-PRM-0422.bindingTypeMismatch', N'kz', N'«{fieldCode}» өрісі ({permitType}) «{columnCode}» бағанына ({columnType}) сәйкес емес: түрлер түрлендірілмейді.'),
    (N'err.ECR-PRM-0422.bindingLookupMismatch', N'ru', N'Поле «{fieldCode}» и столбец «{columnCode}» ссылаются на разные справочники.'),
    (N'err.ECR-PRM-0422.bindingLookupMismatch', N'kz', N'«{fieldCode}» өрісі мен «{columnCode}» бағаны әртүрлі анықтамалықтарға сілтейді.'),
    (N'err.ECR-PRM-0422.bindingUnitMismatch', N'ru', N'Единицы поля «{fieldCode}» и столбца «{columnCode}» различаются: единицы не пересчитываются.'),
    (N'err.ECR-PRM-0422.bindingUnitMismatch', N'kz', N'«{fieldCode}» өрісі мен «{columnCode}» бағанының бірліктері әртүрлі: бірліктер қайта есептелмейді.'),
    (N'err.ECR-PRM-0422.bindingPrecisionMismatch', N'ru', N'Точность или масштаб поля «{fieldCode}» и столбца «{columnCode}» различаются: значения пришлось бы округлять.'),
    (N'err.ECR-PRM-0422.bindingPrecisionMismatch', N'kz', N'«{fieldCode}» өрісі мен «{columnCode}» бағанының дәлдігі не масштабы әртүрлі: мәндерді дөңгелектеуге тура келер еді.'),
    (N'err.ECR-PRM-0422.bindingNotReadOnly', N'ru', N'Столбец «{columnCode}» должен быть только для чтения: его значение пишет система.'),
    (N'err.ECR-PRM-0422.bindingNotReadOnly', N'kz', N'«{columnCode}» бағаны тек оқуға арналған болуы керек: оның мәнін жүйе жазады.'),
    (N'err.ECR-PRM-0422.bindingTooManyRows', N'ru', N'В разрешении {count} строк, а таблица «{tableCode}» вмещает не больше {max}.'),
    (N'err.ECR-PRM-0422.bindingTooManyRows', N'kz', N'Рұқсатта {count} жол бар, ал «{tableCode}» кестесі ең көбі {max} жолды сыйдырады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── ECR-PRM-0422 ── кінець секції ──

-- PERMITS: bindings ── en: відмови прив'язок аркуша до дозволу (ECR-PRM-0422 binding*, PM-09) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0422.bindingTypeNotPublished', N'en', N'The permit type is not published yet, so a sheet cannot be bound to it.', 1),
    (N'err.ECR-PRM-0422.bindingSheetNotInVersion', N'en', N'Table {tableDefId} does not exist in this template version.', 1),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnknown', N'en', N'The header field {headerFieldDefId} that should hold the permit does not exist in this template version.', 1),
    (N'err.ECR-PRM-0422.bindingHeaderFieldNotLookup', N'en', N'Header field "{headerField}" does not refer to a registry, so it cannot hold a permit.', 1),
    (N'err.ECR-PRM-0422.bindingHeaderFieldUnexpected', N'en', N'A header field is set only for the rule "permit from a header field".', 1),
    (N'err.ECR-PRM-0422.bindingFacilityFieldMissing', N'en', N'To pick a permit by facility the template header must have the field "{fieldCode}".', 1),
    (N'err.ECR-PRM-0422.bindingFacilityFieldType', N'en', N'Header field "{fieldCode}" is {dataType}; it must be text or a registry reference.', 1),
    (N'err.ECR-PRM-0422.bindingFormulasHaveColumns', N'en', N'In formulas mode no columns are mapped: the formulas read the permit themselves.', 1),
    (N'err.ECR-PRM-0422.bindingRowsNeedDynamic', N'en', N'Table "{tableCode}" has fixed rows, so permit rows cannot be added to it.', 1),
    (N'err.ECR-PRM-0422.bindingColumnNotInSheet', N'en', N'Column {columnId} does not exist in table "{tableCode}".', 1),
    (N'err.ECR-PRM-0422.bindingPermitFieldUnknown', N'en', N'The permit table has no field "{fieldCode}".', 1),
    (N'err.ECR-PRM-0422.bindingDuplicateField', N'en', N'Permit field "{fieldCode}" is mapped twice.', 1),
    (N'err.ECR-PRM-0422.bindingDuplicateColumn', N'en', N'Column "{columnCode}" is mapped twice.', 1),
    (N'err.ECR-PRM-0422.bindingNoRowKey', N'en', N'At least one column must be the row key, otherwise a sheet row cannot be tied to a permit row.', 1),
    (N'err.ECR-PRM-0422.bindingManyLimits', N'en', N'Only one column can have the role "limit".', 1),
    (N'err.ECR-PRM-0422.bindingTypeMismatch', N'en', N'Field "{fieldCode}" ({permitType}) does not fit column "{columnCode}" ({columnType}): types are not converted.', 1),
    (N'err.ECR-PRM-0422.bindingLookupMismatch', N'en', N'Field "{fieldCode}" and column "{columnCode}" refer to different registries.', 1),
    (N'err.ECR-PRM-0422.bindingUnitMismatch', N'en', N'Units of field "{fieldCode}" and column "{columnCode}" differ: units are not converted.', 1),
    (N'err.ECR-PRM-0422.bindingPrecisionMismatch', N'en', N'Precision or scale of field "{fieldCode}" and column "{columnCode}" differ: values would have to be rounded.', 1),
    (N'err.ECR-PRM-0422.bindingNotReadOnly', N'en', N'Column "{columnCode}" must be read-only: its value is written by the system.', 1),
    (N'err.ECR-PRM-0422.bindingTooManyRows', N'en', N'The permit has {count} rows but table "{tableCode}" holds at most {max}.', 1),
    (N'err.ECR-PRM-0422.bindingTypeUnknown', N'en', N'The permit type "{typeCode}" does not exist.', 1),
    (N'err.ECR-PRM-0422.bindingPermitTableUnknown', N'en', N'The permit type "{typeCode}" has no table "{tableCode}".', 1),
    (N'err.ECR-PRM-0422.bindingEnumUnknown', N'en', N'Value "{value}" of "{field}" is not known.', 1),
    (N'err.ECR-PRM-0422.bindingIdentityChanged', N'en', N'Binding "{bindingCode}" cannot be pointed at another table or type: delete it and create a new one.', 1),
    (N'err.ECR-PRM-0422.bindingPreviewPeriodRequired', N'en', N'Previewing from a document needs the period (periodKey).', 1),
    (N'err.ECR-PRM-0422.bindingPreviewPeriodUnknown', N'en', N'The project of the document has no period {periodKey}.', 1),
    (N'err.ECR-PRM-0404.binding', N'en', N'Permit binding "{bindingCode}" does not exist in this template version.', 1),
    (N'err.ECR-PRM-4101', N'en', N'Permit binding already exists', 1),
    (N'err.ECR-PRM-4101.sheet', N'en', N'Table {tableDefId} of this version is already bound ("{bindingCode}"): one sheet table takes one permit.', 1),
    (N'err.ECR-PRM-4101.code', N'en', N'Binding code "{bindingCode}" is already taken (codes are unique across the whole system).', 1),
    (N'err.ECR-SCHM-0409.permitBindingBreaking', N'en', N'Deleting the permit binding "{bindingCode}" is a breaking change: documents are already attached to this version and their rows were created from the permit.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── кінець секції ──
