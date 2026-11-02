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

