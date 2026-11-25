---
name: bootstrap-admin-is-not-a-superuser
description: "BootstrapAdministrator — навмисно мінімальна роль (лише Security.ManageUsers), не SystemAdministrator; без проєктних ResourceGrant навіть SystemAdministrator не бачить жодного проєкту — це задум, не дефект"
metadata: 
  node_type: memory
  type: project
  originSessionId: 7a8e1359-5171-48b8-a08d-8b2542a7a9f1
  modified: 2026-09-23T15:54:17.343Z
---

2026-09-23: під час ручного огляду на свіжій БД (`setup-dev-db.ps1 -Documents
N`) вхід під `bootstrap` показував "No projects yet" на `/admin/periods` і
порожній `GET /api/v1/projects`, хоча документи й проєкт реально є в базі.
Спершу сприйняв це як прогалину інструменту — виявилось, що це навмисний
дизайн у двох шарах одразу:

1. `BootstrapAdmin.RoleCode = "BootstrapAdministrator"` — **окрема** від
   `SystemAdministrator` роль, коментар у `Ecr.Application/Security/
   BootstrapAdmin.cs` прямо пояснює чому: жодна вбудована роль не отримує
   небезпечних прав (ФВ-6.12, D-40), а `Security.ManageUsers` — саме таке.
   Задача bootstrap — створити першого реального адміна й піти, не бути
   робочим акаунтом.
2. Навіть `SystemAdministrator` (роль із правами) не бачить жоден проєкт без
   ОКРЕМОГО `sec.ResourceGrant` (`ResourceKind=Project`) — `ListProjectsHandler`
   (`Ecr.Application/Projects/ProjectQueryHandlers.cs:83-85`) явно фільтрує:
   право на дію ≠ видимість ресурсу. Коментар у коді: перелік проєктів без
   доступу — вже розвідка структури підприємства.

**Why:** обидва рівні — узгоджений принцип найменших прав, не недогляд.
`setup-dev-db.ps1` НЕ повинен автоматично видавати bootstrap видимість у
проєкт — це суперечило б самому принципу, який bootstrap втілює.

**How to apply:** щоб побачити щойно засіяні документи на свіжому стенді —
увійти bootstrap → створити (Security → New user) реального адміністратора з
роллю `SystemAdministrator` → видати йому `ResourceGrant` на потрібний проєкт
через Security → Grants (або SQL для одноразового огляду). Не витрачати час
на пошук "чому bootstrap нічого не бачить" як на баг.

Пов'язане: [[dev-integration-direct-push-not-pr]].
