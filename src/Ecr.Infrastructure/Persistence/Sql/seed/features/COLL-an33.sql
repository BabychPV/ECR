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
-- COLL:an33 ── збір із джерел (AN-33): політика адреси Sql-джерела (L3-05, D-279); ru/kz — порцією COLL:an33 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption', N'en', N'This connection string option is not allowed for a data source: AttachDBFilename (and its synonyms), User Instance, Enclave Attestation Url, Server SPN, Authentication other than SqlPassword, or a Server Certificate that is not a full local path (X:\…).', 1),
    (N'err.ECR-INT-0503.endpointForbidden', N'en', N'The address of source "{dataSource}" is refused by the address policy (link-local or cloud metadata address, or a forbidden connection string option): fix the Endpoint field of the source.', 1),
    -- L3-07: стеля подій вікна після посторінкового читання.
    (N'coverageEvents.eventsTruncated', N'en', N'The source has more events in the window than were read ({count} in {pages} pages): events after {after} were not synchronized in this run, and removal of missing events was skipped.', 1),
    -- L4-10: однакові імена елементів PI SQL Client.
    (N'err.ECR-INT-0422.elementNameAmbiguous', N'en', N'Several source elements share the name used to read them; their values were not read, because a value read by that name could belong to another element. Give the elements unique names or read them through PI Web API.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an33 ── кінець секції ──
