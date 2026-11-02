-- COLL:ssrf ── ru/kz політики адреси джерела PI Web API; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointScheme', N'ru', N'Адрес источника данных должен использовать http или https.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointScheme', N'kz', N'Деректер көзінің мекенжайы http немесе https пайдалануы тиіс.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointHostForbidden', N'ru', N'Этот хост не допускается для источника данных: loopback, link-local, metadata и неопределённые адреса отклоняются всегда. Источник без секрета подключается по Windows-аутентификации (Negotiate), и в этом режиме литералы частных IP-адресов тоже запрещены: используйте имя хоста.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointHostForbidden', N'kz', N'Бұл хост деректер көзі үшін рұқсат етілмейді: loopback, link-local, metadata және анықталмаған мекенжайлар әрдайым қабылданбайды. Құпиясы жоқ көз Windows аутентификациясымен (Negotiate) қосылады, және бұл режимде жеке IP-мекенжай литералдары да тыйым салынған: хост атын пайдаланыңыз.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed', N'ru', N'Этого хоста нет в списке разрешённых хостов источников данных (PiWebApi:AllowedHosts).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed', N'kz', N'Бұл хост деректер көздерінің рұқсат етілген хосттар тізімінде жоқ (PiWebApi:AllowedHosts).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointMalformed', N'ru', N'Адрес источника данных пуст или некорректен: укажите полный адрес http(s) без имени пользователя.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointMalformed', N'kz', N'Деректер көзінің мекенжайы бос немесе қате: пайдаланушы атынсыз толық http(s) мекенжайын көрсетіңіз.'),
    (N'err.ECR-INT-0503.piWebApiResponseTooLarge', N'ru', N'PI Web API вернул ответ на {path} размером больше {limitBytes} байт: сбор отклонён.'),
    (N'err.ECR-INT-0503.piWebApiResponseTooLarge', N'kz', N'PI Web API {path} үшін {limitBytes} байттан асатын жауап қайтарды: жинау қабылданбады.'),
    (N'health.sources.negotiateNoAllowlist', N'ru', N'Источники с Windows-аутентификацией без списка разрешённых хостов: {count}. Задайте PiWebApi:AllowedHosts в конфигурации EcrApi и перезапустите EcrApi.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointChangeUnconfirmed', N'ru', N'Источник данных «{code}» использует Windows-аутентификацию (служебную учётную запись): подтвердите смену адреса явно.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointChangeUnconfirmed', N'kz', N'«{code}» деректер көзі Windows аутентификациясын (қызметтік тіркелгіні) пайдаланады: мекенжай ауысуын анық растаңыз.'),
    (N'sources.confirmEndpointChange', N'ru', N'Подтверждаю: служебная учётная запись может подключаться к новому адресу'),
    (N'sources.confirmEndpointChange', N'kz', N'Растаймын: қызметтік тіркелгі жаңа мекенжайға қосыла алады'),
    (N'health.sources.negotiateNoAllowlist', N'kz',N'Windows аутентификациясы бар, рұқсат етілген хосттар тізімі жоқ көздер: {count}. EcrApi конфигурациясында PiWebApi:AllowedHosts орнатып, EcrApi қайта іске қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ssrf ── кінець секції ──

