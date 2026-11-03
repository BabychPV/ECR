-- NAE:node-types ── ECR-REG-4094 ── ru/kz: типи вузлів НАЕ (людина 03.11): номер НАЕ в області, назва en, поле іншого рівня; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-4094', N'ru', N'Номер НАЕ уже занят'),
    (N'err.ECR-REG-4094', N'kz', N'НАЕ нөмірі бос емес'),
    (N'err.ECR-REG-4094.naeCodeTaken', N'ru', N'Номер НАЕ {naeCode} уже есть в {regionCode}/{areaCode} (узел {entryCode}). Номер уникален в пределах региона и района.'),
    (N'err.ECR-REG-4094.naeCodeTaken', N'kz', N'{naeCode} НАЕ нөмірі {regionCode}/{areaCode} ішінде бар ({entryCode} түйіні). Нөмір аймақ пен аудан шегінде бірегей.'),
    (N'err.ECR-REG-0422.naeNameEnRequired', N'ru', N'Нужно английское название узла; русское и казахское — необязательны.'),
    (N'err.ECR-REG-0422.naeNameEnRequired', N'kz', N'Түйіннің ағылшынша атауы қажет; орысша және қазақша атаулар міндетті емес.'),
    (N'err.ECR-REG-0422.naeMoveChangesScope', N'ru', N'Перенос узла {entryCode} изменил бы регион или район источника НАЕ {sourceCode}. Создайте источник заново в нужном месте.'),
    (N'err.ECR-REG-0422.naeMoveChangesScope', N'kz', N'{entryCode} түйінін көшіру {sourceCode} НАЕ көзінің аймағын немесе ауданын өзгертер еді. Көзді қажетті жерде қайта жасаңыз.'),
    (N'err.ECR-REG-0422.fieldLevelMismatch', N'ru', N'Поля {fields} относятся к другому типу узла справочника «{registryCode}».'),
    (N'err.ECR-REG-0422.fieldLevelMismatch', N'kz', N'{fields} өрістері «{registryCode}» анықтамалығының басқа түйін түріне жатады.'),
    (N'err.ECR-REG-0409.levelHasFields', N'ru', N'К уровню «{levelCode}» привязаны поля ({fields}): сначала отвяжите их.'),
    (N'err.ECR-REG-0409.levelHasFields', N'kz', N'«{levelCode}» деңгейіне өрістер байланған ({fields}): алдымен оларды ажыратыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:node-types ── кінець секції ──
-- NAE:node-types ── ECR-REG-4094 ── en: типи вузлів НАЕ; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-4094', N'en', N'The NAE number is already taken', 1),
    (N'err.ECR-REG-4094.naeCodeTaken', N'en', N'NAE number {naeCode} already exists in {regionCode}/{areaCode} (node {entryCode}). The number is unique within its region and area.', 1),
    (N'err.ECR-REG-0422.naeNameEnRequired', N'en', N'The node needs an English name; Russian and Kazakh names are optional.', 1),
    (N'err.ECR-REG-0422.naeMoveChangesScope', N'en', N'Moving node {entryCode} would change the region or area of NAE source {sourceCode}. Create the source again in the right place.', 1),
    (N'err.ECR-REG-0422.fieldLevelMismatch', N'en', N'Fields {fields} belong to another node type of registry «{registryCode}».', 1),
    (N'err.ECR-REG-0409.levelHasFields', N'en', N'Fields are bound to level «{levelCode}» ({fields}): unbind them first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:node-types ── кінець секції ──
