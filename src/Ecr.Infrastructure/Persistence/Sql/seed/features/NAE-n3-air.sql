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
    (N'registryAudit.airLimitsImport', N'kz', N'Лимиттер импортталды: көздер {sources}, заттар жолдары {substances}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:n3-air ── кінець секції ──

