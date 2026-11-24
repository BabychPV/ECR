# B74 P3 + P4 — план (D-576, lane `lane/integ-b74-p3p4`)

База: `origin/main` ed2b9f138 (P1 PT01 + P2 вже злиті). ADR: `docs/build/ADR-B74-PROJECT-TEMPLATES.md`.
Рішення людини 13.11: проєкт = «рік + об'єкт» з документами РІЗНИХ шаблонів; варіант A (`doc.ProjectTemplate`);
нова версія шаблону переносить усі документи ЦЬОГО шаблону в проєкті; бізнес-ключ несе код шаблону; грант на
проєкт діє на всі шаблони; старі проєкти лишаються як є.

**Міграція схеми: НІ.** Достатньо таблиць PT01 (`doc.ProjectTemplate`, `doc.ProjectTemplateVersion`,
`doc.Document.TemplateId`). `EcrDbContextModelSnapshot.cs` не змінюється.
**Нових кодів помилок: НІ** — нові `messageKey` під наявними кодами (`ECR-PRD-0409`, `ECR-DOC-0422`,
`ECR-TMPL-0404`, `ECR-TMPL-0422`).

## Ендпоінти й контракт (лише додаванням)

| # | Що | Контракт | Право |
|---|---|---|---|
| E1 | `GET /projects/{id}/templates` — шаблони проєкту | `ProjectTemplateDto[]` (projectTemplateId, templateId, templateCode, templateNameL10n, currentTemplateVersionId, version, layout, isPrimary, documentCount) | `Document.View` у проєкті + видимий проєкт (S17: невидимий = 404) |
| E2 | `POST /projects/{id}/templates` `{templateVersionId}` — приєднати | 201 `ProjectTemplateDto`; 404 версії; 409 `templateAlreadyAttached` / архівований проєкт / архівований шаблон; 422 річний проєкт × «копія на місяць» | `Project.Manage` у проєкті + грант `Manage` |
| E3 | `DELETE /projects/{id}/templates/{templateId}` — від'єднати | 204; 404 `templateNotInProject`; 409 `templateHasDocuments` (з кількістю), `primaryTemplate`, `templateHasRoute` | те саме |
| E4 | `GET /projects/{id}/document-template?templateId=` | без параметра — основний шаблон (як доти); з параметром — чинна версія ЦЬОГО шаблону в проєкті; не в проєкті — 422 `templateNotInProject` | як доти |
| E5 | `POST /documents` + `templateId?` | версія = чинна версія шаблону в проєкті під блоком рядка проєкту; `templateVersionId`, якщо задано, мусить збігатися | як доти |
| E6 | `POST /projects` + `templateVersionIds?[]` | перша — основний шаблон (`Project.TemplateVersionId`), решта — приєднані; два однакові шаблони — 422 | як доти |
| E7 | перелік і картка документа: `templateCode` | рядок/картка | — |

Бізнес-ключ: у проєкті з ≥ 2 шаблонами — `{КОД_ШАБЛОНУ}-P{проєкт}-{NNNN}` (нумерація на пару проєкт×шаблон,
номер лише зростає, видалені/перейменовані ключі з журналу не повторюються); одношаблонний проєкт — старий
`P{проєкт}-V{версія}-{NNNN}` (старі проєкти лишаються як є).

Проєкт «без шаблону» (нуль шаблонів) — НЕ в P3: `doc.Project.TemplateVersionId` NOT NULL до P6, і ~40 читачів
основного шаблону; створення з одним шаблоном + приєднання решти покриває сценарій «2026 · об'єкт». Питання людині.
Звуження гранту до шаблону (`ResourceKind.ProjectTemplate`) — НЕ в P3: у фазах ADR і плану його немає (лише
коментар сутності); безпекова зміна `AccessDecisionService` — окрема lane з мутаціями. Типово — грант на проєкт
покриває всі шаблони (рішення людини 3), це перевіряється тестом.

## Підзадачі

| # | Підзадача | Файли запису | DoD |
|---|---|---|---|
| 0 | перенос версії на пару проєкт×шаблон (агент, `wip/b74-p3-migration` → cherry-pick) | `MigrateDocumentVersionHandler`, `DocumentVersionMigrationStore`, порт, тести | 6 тестів B74, 10 мутацій |
| 1 | порт `IProjectTemplateStore` + сховище + E1–E3 | нові файли `Projects/ProjectTemplateHandlers.cs`, `Ports/IProjectTemplateStore.cs`, `Persistence/ProjectTemplateStore.cs`, `ProjectsController.cs`, DI | Api-тести |
| 2 | E6 створення проєкту з кількома шаблонами | `ProjectQueryHandlers.cs` (CreateProjectHandler), `ProjectsController.cs` | Api-тести |
| 3 | E4/E5 + бізнес-ключ з кодом | `GetDocumentTemplateHandler`, `CreateDocumentHandler`, `DocumentStore` (ключ), `IDocumentStore`, `DocumentsController` | Api/Infra-тести |
| 4 | читання версії документа другого шаблону (залишок P2, на шляху P3) | `DocumentStore` (картка, перелік, `GetTemplateVersionIdAsync`), `99-epilogue.sql` крок 4 | тести нижче |
| 5 | рядки UI (en/ru/kz) | новий `seed/features/B74-project-templates.sql` | `SeedTextUpdateTests`/каталог |
| 6 | контракт | `openapi.snapshot.json`, `schema.d.ts` — останнім кроком у коміті контракту | `OpenApiSnapshotTests` |
| 7 | P4 UI: вкладка «Templates», крок «Template» майстра, колонка шаблону | `features/projects/**`, `CreateDocumentModal.tsx`, перелік документів, e2e spec | vitest + e2e spec (CI) |
| 8 | D-576 у `docs/tz/10-decisions.md` + `docs/CHECKSUMS.txt` | — | `SpecificationBaselineTests` |

## Залежні шляхи → тест

| Шлях | Тест |
|---|---|
| S17 видимість: невидимий проєкт — 404 на E1–E4 | `ProjectTemplatesApiTests.Невидимий_проєкт_404` |
| Грант на проєкт покриває документи всіх шаблонів | `ProjectTemplatesApiTests.Грант_проєкту_відкриває_документ_другого_шаблону` |
| Маршрути погодження проєкт×шаблон (B155/D2): від'єднання з маршрутом — 409; документ другого шаблону бере версію свого шаблону | `ProjectTemplatesApiTests.Відєднання_з_маршрутом_409` |
| Нічний перерахунок / F5/B80 / rpt-назви / календар періодів: версія документа другого шаблону — його пари, не основна | `ProjectTemplatesApiTests.Картка_документа_другого_шаблону_несе_його_версію`, `…GetTemplateVersionId…` |
| B75: шаблон без робочого процесу — режим за шаблоном документа | `…Картка…` (WorkflowMode/Layout з версії шаблону документа) |
| B165: `ECR-DOC-4092` — «один документ» рахується на шаблон, не на проєкт | `ProjectTemplatesApiTests.Один_документ_на_шаблон_у_спільному_проєкті` |
| Перенос версії на пару | `DocumentVersionMigrationTests.ProjectTemplates` (6) |
| Від'єднання при документах шаблону — 409 під блоком проєкту | `ProjectTemplatesApiTests.Відєднання_з_документами_409` |
| Сторож B74 (храповик читань) | `ProjectTemplateVersionB74GuardTests` |

## ✎ 24.11: закриття відкритих питань (координатор: «без жодних боргів»)

- Питання 1, 2, 3, 5, 6 — прийняті типові рішення, записані в D-576 п. 8.
- Q4: фільтр/сортування/варіанти версій переліку — за зрізом «проєкт × шаблон документа» (DocumentStore.ProjectVersionLabelsAsync, SliceKey). Тест: ProjectTemplatesApiTests.Фільтр_і_сортування_за_версією_групують_за_шаблоном_документа.
- Q7: шапка документа — рядок «Template» (код шаблону); огляд майстра — «Template» і «Version» окремо. Тести: DocumentHeaderPanel.templateCode.test.tsx, CreateDocumentModal.templateStep.test.tsx, e2e projectTemplates.spec.ts.
- Окремі тести на двошаблонний проєкт: якір року нічного перерахунку (YearLayoutCalculatedRoutingSqlTests.B74_…), маршрутизація Q-17 і замок правки без періоду (B74DocumentTemplateVersionTests), нічна перевірка узгодженості (ConsistencyCheckJobDetectionTests.B74_…); мутація на кожен шлях.
