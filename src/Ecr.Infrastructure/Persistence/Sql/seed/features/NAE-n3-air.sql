-- NAE:n3-air ── ru/kz дозволів "Air Limits Import" (N3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.regValueEmpty', N'ru', N'«{field}» не заполнено. Строка импортируется без него.'),
    (N'err.ECR-IMP-0422.regValueEmpty', N'kz', N'«{field}» толтырылмаған. Жол онсыз импортталады.'),
    (N'err.ECR-IMP-0422.airRequestInvalid', N'ru', N'Запрос импорта не читается. Обновите страницу и повторите.'),
    (N'err.ECR-IMP-0422.airRequestInvalid', N'kz', N'Импорт сұрауын оқу мүмкін емес. Бетті жаңартып, қайталаңыз.'),
    (N'err.ECR-IMP-0422.airNotProvisioned', N'ru', N'Тип разрешения «Air Limits Import» ещё не настроен. Попросите администратора выполнить настройку.'),
    (N'err.ECR-IMP-0422.airNotProvisioned', N'kz', N'«Air Limits Import» рұқсат түрі әлі бапталмаған. Әкімшіден баптауды сұраңыз.'),
    (N'err.ECR-IMP-0422.airScopeInvalid', N'ru', N'Выберите регион импорта.'),
    (N'err.ECR-IMP-0422.airScopeInvalid', N'kz', N'Импорт аймағын таңдаңыз.'),
    (N'err.ECR-IMP-0422.airNotAirPermit', N'ru', N'Это разрешение не относится к типу «Air Limits Import».'),
    (N'err.ECR-IMP-0422.airNotAirPermit', N'kz', N'Бұл рұқсат «Air Limits Import» түріне жатпайды.'),
    (N'err.ECR-IMP-0422.airCreateUnderInvalid', N'ru', N'НАЕ {key} нельзя создать под выбранным узлом. Выберите площадку или группу оборудования.'),
    (N'err.ECR-IMP-0422.airCreateUnderInvalid', N'kz', N'НАЕ {key} таңдалған түйін астында жасалмайды. Алаңды немесе жабдық тобын таңдаңыз.'),
    (N'err.ECR-IMP-0422.airWriteRejected', N'ru', N'Импорт отклонён на {key} ({reason}). Ничего не импортировано.'),
    (N'err.ECR-IMP-0422.airWriteRejected', N'kz', N'Импорт {key} жолында қабылданбады ({reason}). Ештеңе импортталмады.'),
    (N'err.ECR-IMP-0422.airProfileUnknown', N'ru', N'Профиль импорта {code} не существует.'),
    (N'err.ECR-IMP-0422.airProfileUnknown', N'kz', N'{code} импорт профилі жоқ.'),
    (N'err.ECR-REG-0409.profileChanged', N'ru', N'Профиль импорта {code} изменили после открытия. Загрузите его заново.'),
    (N'err.ECR-REG-0409.profileChanged', N'kz', N'{code} импорт профилін ашқаннан кейін өзгертті. Оны қайта жүктеңіз.'),
    (N'err.ECR-REG-0404.importProfile', N'ru', N'Профиль импорта {code} не существует.'),
    (N'err.ECR-REG-0404.importProfile', N'kz', N'{code} импорт профилі жоқ.'),
    (N'registryAudit.airLimitsImport', N'ru', N'Импортированы лимиты: источников {sources}, строк веществ {substances}.'),
    (N'registryAudit.airLimitsImport', N'kz', N'Лимиттер импортталды: көздер {sources}, заттар жолдары {substances}.'),
    (N'err.ECR-REG-0409.profileCodeTaken', N'ru', N'Профиль импорта с кодом {code} уже существует.'),
    (N'err.ECR-REG-0409.profileCodeTaken', N'kz', N'{code} коды бар импорт профилі бұрыннан бар.'),
    (N'err.ECR-REG-0409.profileBuiltIn', N'ru', N'Профиль импорта {code} встроенный, его нельзя перезаписать. Сохраните сопоставление под другим кодом.'),
    (N'err.ECR-REG-0409.profileBuiltIn', N'kz', N'{code} импорт профилі кірістірілген, оны қайта жазуға болмайды. Сәйкестікті басқа кодпен сақтаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:n3-air ── кінець секції ──

-- NAE:n3-air ── "Air Limits Import" permits (provisioning, profiles, wizard backend); ru/kz in the section NAE:n3-air below.
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.airRequestInvalid',     N'en', N'The import request cannot be read. Reload the page and try again.', 1),
    (N'err.ECR-IMP-0422.airNotProvisioned',     N'en', N'The Air Limits Import permit type is not set up yet. Ask an administrator to provision it.', 1),
    (N'err.ECR-IMP-0422.airScopeInvalid',       N'en', N'Choose the region of the import.', 1),
    (N'err.ECR-IMP-0422.airNotAirPermit',       N'en', N'This permit is not an Air Limits Import permit.', 1),
    (N'err.ECR-IMP-0422.airCreateUnderInvalid', N'en', N'NAE {key} cannot be created under the chosen node. Choose a site or equipment group.', 1),
    (N'err.ECR-IMP-0422.airWriteRejected',      N'en', N'The import was refused at {key} ({reason}). Nothing was imported.', 1),
    (N'err.ECR-IMP-0422.airProfileUnknown',     N'en', N'The import profile {code} does not exist.', 1),
    (N'err.ECR-REG-0409.profileChanged',        N'en', N'The import profile {code} was changed after you opened it. Reload it and try again.', 1),
    (N'err.ECR-REG-0404.importProfile',         N'en', N'The import profile {code} does not exist.', 1),
    (N'registryAudit.airLimitsImport',          N'en', N'Air limits imported: {sources} sources, {substances} substance rows.', 1),
    (N'err.ECR-REG-0409.profileCodeTaken',      N'en', N'An import profile with code {code} already exists.', 1),
    (N'err.ECR-REG-0409.profileBuiltIn',        N'en', N'The import profile {code} is built in and cannot be overwritten. Save the mapping under another code.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:n3-air ── кінець секції ──
