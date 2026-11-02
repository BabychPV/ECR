-- REGSRV:rt15-16 ── ru/kz серверних ендпоінтів довідників (RT-15, RT-16, пакет, фільтр id); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.registryRowsIdsTooMany', N'ru', N'В фильтре id можно указать не больше {max} записей, а указано {count}.'),
    (N'err.ECR-REQ-0422.registryRowsIdsTooMany', N'kz', N'id сүзгісінде {max} жазбадан артық көрсетуге болмайды, ал көрсетілгені — {count}.'),
    (N'err.ECR-REQ-0422.batchItemNewOnly', N'ru', N'Строка пакета «{clientRowId}»: название и срок действия можно задать только новой записи. У существующей записи их меняют в её форме.'),
    (N'err.ECR-REQ-0422.batchItemNewOnly', N'kz', N'Пакеттің «{clientRowId}» жолы: атау мен әрекет ету мерзімін тек жаңа жазбаға беруге болады. Бар жазбада оларды жазба пішінінде өзгертеді.'),
    (N'err.ECR-REQ-0422.registryExportFormatUnknown', N'ru', N'Формата экспорта справочника «{format}» не существует: используйте csv или xlsx.'),
    (N'err.ECR-REQ-0422.registryExportFormatUnknown', N'kz', N'«{format}» анықтамалық экспорты пішімі жоқ: csv немесе xlsx пайдаланыңыз.'),
    (N'err.ECR-REQ-0422.registryExportTooLarge', N'ru', N'В справочнике «{registryCode}» для экспорта {total} записей, а ограничение — {max}.'),
    (N'err.ECR-REQ-0422.registryExportTooLarge', N'kz', N'«{registryCode}» анықтамалығында экспортқа {total} жазба бар, ал шектеу — {max}.'),
    (N'err.ECR-REG-0422.unitCodeUnknown', N'ru', N'Единицы измерения с таким кодом нет.'),
    (N'err.ECR-REG-0422.unitCodeUnknown', N'kz', N'Мұндай коды бар өлшем бірлігі жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REGSRV:rt15-16 ── кінець секції ──

