-- COLL:an33 ── ru/kz збору з джерел (AN-33); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption', N'ru', N'Этот параметр строки подключения для источника не допускается: AttachDBFilename (и его синонимы), User Instance, Enclave Attestation Url, Server SPN, Authentication, отличный от SqlPassword, или Server Certificate не в виде полного локального пути (X:\…).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption', N'kz', N'Қосылу жолының бұл параметрі көз үшін рұқсат етілмейді: AttachDBFilename (және оның синонимдері), User Instance, Enclave Attestation Url, Server SPN, SqlPassword-тан басқа Authentication немесе толық жергілікті жол (X:\…) емес Server Certificate.'),
    (N'err.ECR-INT-0503.endpointForbidden', N'ru', N'Адрес источника «{dataSource}» отклонён политикой адресов (link-local или облачный metadata-адрес либо запрещённый параметр строки подключения): исправьте поле Endpoint источника.'),
    (N'err.ECR-INT-0503.endpointForbidden', N'kz', N'«{dataSource}» көзінің мекенжайы мекенжай саясатымен қабылданбады (link-local немесе бұлттық metadata мекенжайы не қосылу жолының тыйым салынған параметрі): көздің Endpoint өрісін түзетіңіз.'),
    (N'coverageEvents.eventsTruncated', N'ru', N'В окне источника событий больше, чем прочитано ({count} за {pages} страниц): события после {after} в этом прогоне не синхронизированы, удаление исчезнувших пропущено.'),
    (N'coverageEvents.eventsTruncated', N'kz', N'Көз терезесінде оқылғаннан көп оқиға бар ({pages} бетте {count}): {after} кейінгі оқиғалар бұл өткізуде синхрондалмады, жоғалғандарды жою өткізіп жіберілді.'),
    (N'err.ECR-INT-0422.elementNameAmbiguous', N'ru', N'Несколько элементов источника имеют одно имя, по которому они читаются; их значения не прочитаны, потому что значение, прочитанное по этому имени, может принадлежать другому элементу. Дайте элементам уникальные имена или читайте их через PI Web API.'),
    (N'err.ECR-INT-0422.elementNameAmbiguous', N'kz', N'Көздің бірнеше элементінің оқылатын аты бірдей; олардың мәндері оқылмады, себебі осы атпен оқылған мән басқа элементке тиесілі болуы мүмкін. Элементтерге бірегей атау беріңіз немесе оларды PI Web API арқылы оқыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an33 ── кінець секції ──
