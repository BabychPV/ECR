using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecr.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// B179 L7 (D-574): знімає ручний перелік споживачів зв'язки <c>cfg.DocumentSourceBindingConsumer</c> (разом із
    /// PK, FK на зв'язку й шаблон та індексом за шаблоном). Споживачів з L6 називає граф залежностей; L6 на live з
    /// вікна W15y (a9a3c0067), тож відкат на попередній реліз таблиці вже не потребує.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Перед зняттям — перевірка «дані ніхто не потребує» на боці бази: жоден SQL-модуль (подання, процедура,
    /// функція, тригер), синонім чи чужий FK не посилається на таблицю; інакше <c>THROW 50574</c> і транзакція міграції
    /// відкочується, нічого не знято. Боку коду — сторож <c>SourceBindingConsumerTableReadGuardTests</c> (ім'я таблиці
    /// поза історією міграцій не трапляється, модель EF її не відображає).
    /// </para>
    /// <para>
    /// Архів: рядки (з кодами зв'язки й шаблону) переносяться ОДНИМ записом у незмінний журнал
    /// <c>aud.SecurityEvent</c> (<c>EventType = DocumentSourceBindingConsumerRetired</c>, <c>ChangedByUserId = 0</c> —
    /// система), у тій самій транзакції, що й зняття: це той самий журнал, де лежать усі зміни зв'язок
    /// (<c>DocumentSourceBindingChanged</c>), з тригером незмінності і штатним переносом у <c>arc.AuditSecurityEvent</c>.
    /// Порожня таблиця (свіжа база) — без запису.
    /// </para>
    /// <para>
    /// Онлайн: таблиця — одиниці рядків конфігурації; <c>DROP TABLE</c> — коротка зміна метаданих (Sch-M на неї і на
    /// мить на <c>cfg.Template</c>/<c>cfg.DocumentSourceBinding</c> для FK). <c>LOCK_TIMEOUT</c> 60 с: якщо довге
    /// читання тримає шаблони, міграція падає з 1222 і відкочується, а не вибудовує чергу блокувань за собою.
    /// Ідемпотентний скрипт: крім запису в історії міграцій, кожен крок має власну перевірку <c>OBJECT_ID</c>.
    /// </para>
    /// </remarks>
    public partial class B179L7DropSourceBindingConsumer : Migration
    {
        /// <summary>Тип події архіву в <c>aud.SecurityEvent</c>.</summary>
        public const string RetiredEventType = "DocumentSourceBindingConsumerRetired";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                SET LOCK_TIMEOUT 60000;
                IF OBJECT_ID(N'cfg.DocumentSourceBindingConsumer', N'U') IS NOT NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies AS d
                               WHERE d.referenced_entity_name = N'DocumentSourceBindingConsumer'
                                 AND ISNULL(d.referenced_schema_name, N'cfg') = N'cfg')
                       OR EXISTS (SELECT 1 FROM sys.synonyms AS s
                                  WHERE s.base_object_name LIKE N'%DocumentSourceBindingConsumer%')
                       OR EXISTS (SELECT 1 FROM sys.foreign_keys AS f
                                  WHERE f.referenced_object_id = OBJECT_ID(N'cfg.DocumentSourceBindingConsumer'))
                        THROW 50574, N'B179 L7: на cfg.DocumentSourceBindingConsumer ще посилається модуль, синонім чи FK бази - зняття зупинено (D-574).', 1;

                    IF EXISTS (SELECT 1 FROM cfg.DocumentSourceBindingConsumer)
                    BEGIN
                        IF OBJECT_ID(N'aud.SecurityEvent', N'U') IS NULL
                            THROW 50574, N'B179 L7: немає aud.SecurityEvent - рядки споживачів нікуди архівувати, зняття зупинено (D-574).', 1;

                        INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
                        SELECT SYSUTCDATETIME(), N'{RetiredEventType}', NULL, NULL,
                               (SELECT N'D-574' AS decision,
                                       N'cfg.DocumentSourceBindingConsumer' AS [table],
                                       (SELECT c.BindingId AS bindingId, b.Code AS bindingCode,
                                               c.TemplateId AS templateId, t.Code AS templateCode
                                          FROM cfg.DocumentSourceBindingConsumer AS c
                                          LEFT JOIN cfg.DocumentSourceBinding AS b ON b.Id = c.BindingId
                                          LEFT JOIN cfg.Template AS t ON t.Id = c.TemplateId
                                         ORDER BY c.BindingId, c.TemplateId
                                           FOR JSON PATH) AS consumers
                                   FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
                               0, N'B179L7DropSourceBindingConsumer';
                    END

                    DROP TABLE cfg.DocumentSourceBindingConsumer;
                END
                SET LOCK_TIMEOUT -1;
                """);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Відновлює таблицю в схемі B80 і її рядки з останнього запису архіву (лише пари, чиї зв'язка й шаблон ще є).
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                IF OBJECT_ID(N'cfg.DocumentSourceBindingConsumer', N'U') IS NULL
                BEGIN
                    CREATE TABLE cfg.DocumentSourceBindingConsumer
                    (
                        BindingId  int NOT NULL,
                        TemplateId int NOT NULL,
                        CONSTRAINT PK_DocumentSourceBindingConsumer PRIMARY KEY (BindingId, TemplateId),
                        CONSTRAINT FK_DocSrcBindingConsumer_Binding FOREIGN KEY (BindingId) REFERENCES cfg.DocumentSourceBinding (Id),
                        CONSTRAINT FK_DocSrcBindingConsumer_Template FOREIGN KEY (TemplateId) REFERENCES cfg.Template (Id)
                    );
                    CREATE INDEX IX_DocSrcBindingConsumer_Template ON cfg.DocumentSourceBindingConsumer (TemplateId);

                    IF OBJECT_ID(N'aud.SecurityEvent', N'U') IS NOT NULL
                        INSERT INTO cfg.DocumentSourceBindingConsumer (BindingId, TemplateId)
                        SELECT DISTINCT j.bindingId, j.templateId
                          FROM (SELECT TOP (1) e.DetailsJson
                                  FROM aud.SecurityEvent AS e
                                 WHERE e.EventType = N'{RetiredEventType}'
                                 ORDER BY e.ChangedAt DESC, e.Id DESC) AS last
                         CROSS APPLY OPENJSON(last.DetailsJson, N'$.consumers')
                               WITH (bindingId int N'$.bindingId', templateId int N'$.templateId') AS j
                         WHERE EXISTS (SELECT 1 FROM cfg.DocumentSourceBinding AS b WHERE b.Id = j.bindingId)
                           AND EXISTS (SELECT 1 FROM cfg.Template AS t WHERE t.Id = j.templateId);
                END
                """);
        }
    }
}
