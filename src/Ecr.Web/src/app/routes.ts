/**
 * Типізоване дерево маршрутів (`PR nav-arch #1`) — єдине джерело правди про
 * шлях, лейбл, право доступу й показ у навбарі кожного маршруту застосунку.
 *
 * ⛔ До цього шлях `'/admin/templates'` жив у ДВОХ місцях одночасно:
 * рядковим літералом у `children` `createBrowserRouter` (`router.tsx`) і
 * окремим рядковим літералом у масиві `Items` навбару (`AppLayout.tsx`).
 * Розійтися їм заважала лише пильність людини — жоден тест і жоден `tsc` не
 * перевіряє, що два незалежні рядки лишаються однаковими. Тепер обидва місця
 * читають `path` з ОДНОГО запису нижче.
 *
 * ⚠ Призначення на майбутнє (щоб не збирати цей реєстр по другому разу):
 * - **breadcrumbs (`PR #3`)** читають `handle` через `useMatches()` —
 *   `handle` тут навмисно сумісний із формою, яку React Router 7 передає в
 *   об'єкт маршруту (`RouteObject.handle: unknown`, довільна прикладна
 *   метадані — читається типізовано лише на споживачі, бо бібліотека його не
 *   типізує).
 * - **рольові гарди (`PR #4`)** читають `handle.permission` — те саме поле,
 *   яким сьогодні керується показ пункту в навбарі (`can(me, permission)`).
 * - **навбар (ця картка)** читає `showInNav`, а не тримає власний перелік:
 *   порядок оголошення нижче — це порядок пунктів у навбарі (той самий
 *   інваріант, що діяв і в старому масиві `Items`: порядок масиву = порядок
 *   на екрані).
 *
 * ⛔ Свідомо НЕ зроблено в цій картці: генерація самого дерева
 * `createBrowserRouter` з цього реєстру (тобто елемент/лінивий імпорт кожного
 * маршруту). Вкладені layout-маршрути (`/admin/*` як окрема секція з власним
 * `Outlet`) — це PR #2 за планом, і саме там з'явиться природна точка, де
 * дерево `router.tsx` перестає бути пласким списком і форма генерації
 * визначиться разом із вкладеністю. Робити це двічі (плаский зараз, вкладений
 * у PR #2) означало б переписувати щойно написане. Натомість `router.tsx`
 * цієї картки бере з реєстру `path` і `handle` для КОЖНОГО наявного
 * маршруту — рядок шляху більше ніде не набирається вручну.
 */

/** Прикладна метадані маршруту, сумісна з `RouteObject.handle` React Router 7. */
export interface RouteHandle {
  /** Ключ каталогу рядків (`shared/i18n`) — назва пункту навбару/breadcrumb. */
  labelKey: string;

  /** Право (`sec.Permission.Code`), потрібне для показу пункту; без нього — доступно всім. */
  permission?: string;

  /** Ключ іконки навбару. Не використовується жодним компонентом ще — навбар
   *  сьогодні не малює іконок узагалі, і додавати бібліотеку іконок заради
   *  порожнього поля тут означало б нову залежність без візуального ефекту.
   *  Поле лишається типізованим, щоб PR, який додасть іконки, не чіпав форму
   *  запису — лише саму бібліотеку рендера. */
  icon?: string;
}

/** Один запис дерева маршрутів. */
export interface RouteEntry {
  /** Стабільний ідентифікатор запису (не адреса) — для пошуку в реєстрі. */
  id: string;

  /** Абсолютний шлях від кореня (`/admin/templates/:id/versions/:versionId`). */
  path: string;

  /** Метадані для навбару/breadcrumbs/гардів. */
  handle: RouteHandle;

  /** Показувати пунктом навбару. За замовчуванням — ні (глибокі/деталь-маршрути). */
  showInNav?: boolean;
}

/**
 * Реєстр маршрутів.
 *
 * ⚠ Порядок оголошення ключів — порядок пунктів навбару (лише записи з
 * `showInNav: true` туди потрапляють, `AppLayout.tsx` більше не тримає
 * власного порядку). Переставити запис тут — і навбар переставиться теж:
 * це навмисно ОДНА дія, а не дві, які треба пам'ятати синхронізувати.
 */
export const routes = {
  home: {
    id: 'home',
    path: '/',
    handle: { labelKey: 'nav.documents' },
    showInNav: true,
  },
  changePassword: {
    id: 'change-password',
    path: '/change-password',
    handle: { labelKey: 'password.title' },
  },
  adminTemplates: {
    id: 'admin-templates',
    path: '/admin/templates',
    handle: { labelKey: 'nav.templates', permission: 'Template.Edit' },
    showInNav: true,
  },
  adminTemplateVersion: {
    id: 'admin-template-version',
    path: '/admin/templates/:id/versions/:versionId',
    handle: { labelKey: 'version.title' },
  },
  adminTemplateVersionRelations: {
    id: 'admin-template-version-relations',
    path: '/admin/templates/:id/versions/:versionId/relations',
    handle: { labelKey: 'tables.relationsTitle' },
  },
  adminRegistries: {
    id: 'admin-registries',
    path: '/admin/registries',
    handle: { labelKey: 'nav.registries', permission: 'Registry.View' },
    showInNav: true,
  },
  adminRegistryDefinition: {
    id: 'admin-registry-definition',
    path: '/admin/registries/:code/definition',
    handle: { labelKey: 'registries.constructor' },
  },
  adminMethodologies: {
    id: 'admin-methodologies',
    path: '/admin/methodologies',
    handle: { labelKey: 'nav.methodologies', permission: 'Calculation.View' },
    showInNav: true,
  },
  adminMethodologyVersions: {
    id: 'admin-methodology-versions',
    path: '/admin/methodologies/:id/versions',
    handle: { labelKey: 'methodologies.versionsTitle' },
  },
  adminExpressions: {
    id: 'admin-expressions',
    path: '/admin/expressions',
    handle: { labelKey: 'nav.expressions', permission: 'Calculation.View' },
    showInNav: true,
  },
  adminUnits: {
    id: 'admin-units',
    path: '/admin/units',
    handle: { labelKey: 'nav.units', permission: 'Calculation.View' },
    showInNav: true,
  },
  adminSecurity: {
    id: 'admin-security',
    path: '/admin/security',
    handle: { labelKey: 'nav.security', permission: 'Security.ManageRoles' },
    showInNav: true,
  },
  adminPeriods: {
    id: 'admin-periods',
    path: '/admin/periods',
    // ⛔ Було `Period.Manage` — права з такою назвою немає в каталозі
    // (`sec.Permission`), той самий факт, що й у старому `AppLayout.tsx`
    // (директива №09 `S-02`) — перенесено без зміни значення.
    handle: { labelKey: 'nav.periods', permission: 'Period.Configure' },
    showInNav: true,
  },
  adminSources: {
    id: 'admin-sources',
    path: '/admin/sources',
    handle: { labelKey: 'nav.sources', permission: 'Integration.Manage' },
    showInNav: true,
  },
  adminMapping: {
    id: 'admin-mapping',
    path: '/admin/mapping',
    handle: { labelKey: 'nav.mapping', permission: 'Integration.Manage' },
    showInNav: true,
  },
  adminJobs: {
    id: 'admin-jobs',
    path: '/admin/jobs',
    handle: { labelKey: 'nav.jobs', permission: 'System.ViewHealth' },
    showInNav: true,
  },
  adminSnapshots: {
    id: 'admin-snapshots',
    path: '/admin/snapshots',
    handle: { labelKey: 'nav.snapshots', permission: 'Report.ViewRegulatory' },
    showInNav: true,
  },
  adminAudit: {
    id: 'admin-audit',
    path: '/admin/audit',
    handle: { labelKey: 'nav.audit', permission: 'Security.ViewAudit' },
    showInNav: true,
  },
  adminUiStrings: {
    id: 'admin-ui-strings',
    path: '/admin/ui-strings',
    handle: { labelKey: 'nav.uiStrings', permission: 'System.ManageLocalization' },
    showInNav: true,
  },
  adminHealth: {
    id: 'admin-health',
    path: '/admin/health',
    handle: { labelKey: 'nav.health', permission: 'System.ViewHealth' },
    showInNav: true,
  },

  // ⛔ БЕЗ права — і це не пропуск. Пункт відповідає на «чому в мене порожні
  // екрани», тобто потрібен саме тому, у кого прав немає (`H-21`). Закрити
  // його правом означало б показувати відповідь лише тим, хто й так знає.
  // (Перенесено з коментаря `AppLayout.tsx` без зміни суті.)
  myGroups: {
    id: 'my-groups',
    path: '/my-groups',
    handle: { labelKey: 'nav.myGroups' },
    showInNav: true,
  },

  documentDetail: {
    id: 'document-detail',
    // ⚠ `documents.title` — загальна назва («Documents»), не бізнес-ключ
    // конкретного документа: цей запис лише заявляє МАРШРУТ у реєстрі.
    // Резолв динамічного сегмента (`:id` → бізнес-ключ із кешу запиту) —
    // задача breadcrumbs-резолвера (`PR #3`), не цієї картки.
    path: '/documents/:id',
    handle: { labelKey: 'documents.title' },
  },
} as const satisfies Record<string, RouteEntry>;

/** Усі записи реєстру як масив (порядок — порядок оголошення вище). */
export const routeList: RouteEntry[] = Object.values(routes);

/** Пункти навбару — підмножина реєстру з `showInNav: true`, у порядку оголошення. */
export const navRoutes: RouteEntry[] = routeList.filter((route) => route.showInNav === true);

/**
 * Шлях запису як дочірній маршрут `createBrowserRouter` (без кореневого `/`).
 *
 * ⚠ Реєстр тримає АБСОЛЮТНІ шляхи (те, що приймає `<Link to>` і чого чекає
 * `useMatches()`), а `router.tsx` монтує кожен запис дитиною маршруту `/`
 * (`AppLayout`), де React Router очікує ВІДНОСНИЙ сегмент без провідного
 * `/`. Одна функція замість ручного `.slice(1)` у кожному місці виклику.
 */
export function childPath(route: RouteEntry): string {
  return route.path.startsWith('/') ? route.path.slice(1) : route.path;
}
