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
 * ⛔ `PR #1` свідомо НЕ генерував саме дерево `createBrowserRouter` вкладеним
 * — лишав `router.tsx` пласким списком дітей `AppLayout`, обіцяючи, що форма
 * вкладеності визначиться в `PR #2`. **`PR #2` (ця картка) це зробив**:
 * `router.tsx` тепер вкладає секцію `/admin/*` (`AdminLayout`) і секцію
 * `admin/templates/:id/*` (`TemplateVersionLayout`, версія + зв'язки одного
 * шаблону) під власні `<Route>` з `Outlet`. `relativePath()` нижче — форма
 * генерації дочірнього `path` для таких вкладених рівнів (`childPath()`
 * лишається для рівня одразу під `AppLayout`, де предок — корінь `/`).
 * Самі шляхи (`routes.X.path`) і так лишаються АБСОЛЮТНИМИ й НЕ змінились —
 * вкладеність міняє лише те, ЯК `router.tsx` монтує дерево `<Route>`, не
 * контракт цього реєстру.
 */

/**
 * Налаштування breadcrumb-крихти запису (`PR nav-arch #3`).
 *
 * ⚠ Не кожен рівень фізичної вкладеності `router.tsx` — це той рівень, який
 * людина хоче бачити у breadcrumbs. `admin-template-version-relations`,
 * наприклад, — фізичний СУСІД `admin-template-version` (`PR #2` свідомо не
 * вклав "зв'язки" під "версію" — обидва належать одному layout'у
 * `TemplateVersionLayout`, але як два незалежні листові маршрути), тому
 * `useMatches()` сам по собі НЕ дає ланцюжка «Шаблон / Версія / Зв'язки» —
 * лише «Шаблон / Зв'язки». `ancestorIds` — явний спосіб сказати «встав сюди
 * крихту ЦИХ записів реєстру перед моєю власною», не змінюючи саму
 * вкладеність маршрутів заради breadcrumbs.
 */
export interface RouteCrumbConfig {
  /**
   * Ids інших записів цього ж реєстру, чиї крихти вставляються ПЕРЕД
   * власною крихтою цього запису, у вказаному порядку. Кожен резолвиться
   * (статично чи динамічно) тими самими правилами, що й звичайний матч.
   */
  ancestorIds?: readonly string[];

  /**
   * Ім'я параметра `useParams()` цього маршруту (`id`, `versionId`, `code`),
   * чиє значення резолвер (`breadcrumbResolvers.ts`) перетворює на
   * людиночитну назву замість статичного `labelKey`. Без цього поля крихта
   * завжди статична (`t(labelKey)`).
   */
  resolveParam?: string;

  /** Який резолвер `breadcrumbResolvers.ts` читає кеш TanStack Query для {@link resolveParam}. */
  resolveWith?: 'templateName' | 'templateVersionLabel' | 'registryName';

  /**
   * Явна ціль посилання крихти, якщо вона НЕ збігається з власним `pathname`
   * збігу — синтетичний вузол вкладеності (`admin-template-section` нижче)
   * не має власної сторінки (голий `/admin/templates/:id` рендерить лише
   * порожній `Outlet`), тож крихта веде на найближчий чинний маршрут (перелік
   * шаблонів), а не в глухий кут.
   */
  linkTo?: string;
}

/** Прикладна метадані маршруту, сумісна з `RouteObject.handle` React Router 7. */
export interface RouteHandle {
  /** Ключ каталогу рядків (`shared/i18n`) — назва пункту навбару/breadcrumb. */
  labelKey: string;

  /** Право (`sec.Permission.Code`), потрібне для показу пункту; без нього — доступно всім. */
  permission?: string;

  /** Ключ іконки навбару (`navIcons`, `src/app/navIcons.tsx`) — резолвиться в
   *  компонент inline SVG на споживачі (`AppLayout.tsx`, `NavLink leftSection`).
   *  Рядковий ключ, не сама іконка чи компонент: реєстр маршрутів і далі не
   *  залежить від форми рендера — заміна бібліотеки рендера не чіпає цей файл. */
  icon?: string;

  /** Breadcrumb-специфічні налаштування (`PR nav-arch #3`). Без цього поля
   *  крихта — просто статичний `t(labelKey)`, без резолву й без ін'єкції
   *  логічних предків. */
  crumb?: RouteCrumbConfig;
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
    handle: { labelKey: 'nav.documents', icon: 'documents' },
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
    handle: { labelKey: 'nav.templates', permission: 'Template.Edit', icon: 'templates' },
    showInNav: true,
  },
  // ⚠ Синтетичний вузол вкладеності (`PR #2`, `TemplateVersionLayout`) —
  // не окрема сторінка (голий `/admin/templates/:id` рендерить лише
  // `Outlet`, без листа). Існує в реєстрі РІВНО заради breadcrumbs (`PR #3`):
  // `router.tsx` передає цей `handle` самому layout-маршруту, і саме тут
  // резолвиться людиночитна НАЗВА ШАБЛОНУ для сегмента `:id`, спільного для
  // версії й зв'язків таблиць нижче.
  adminTemplateSection: {
    id: 'admin-template-section',
    path: '/admin/templates/:id',
    handle: {
      labelKey: 'nav.templates',
      crumb: {
        ancestorIds: ['admin-templates'],
        resolveParam: 'id',
        resolveWith: 'templateName',
        linkTo: '/admin/templates',
      },
    },
  },
  adminTemplateVersion: {
    id: 'admin-template-version',
    path: '/admin/templates/:id/versions/:versionId',
    handle: {
      labelKey: 'version.title',
      crumb: { resolveParam: 'versionId', resolveWith: 'templateVersionLabel' },
    },
  },
  adminTemplateVersionRelations: {
    id: 'admin-template-version-relations',
    path: '/admin/templates/:id/versions/:versionId/relations',
    // ⚠ `ancestorIds: ['admin-template-version']` — цей лист є ФІЗИЧНИМ
    // СУСІДОМ `admin-template-version` у `router.tsx` (обидва — прямі діти
    // `TemplateVersionLayout`), не його нащадком, тож `useMatches()` сам не
    // дає крихти версії для цього маршруту. Без цього поля людина бачила б
    // «Шаблон / Зв'язки» замість «Шаблон / Версія / Зв'язки» — саме той
    // четвертий рівень, на якому директива вимагає перевірити усічення.
    handle: { labelKey: 'tables.relationsTitle', crumb: { ancestorIds: ['admin-template-version'] } },
  },
  adminRegistries: {
    id: 'admin-registries',
    path: '/admin/registries',
    handle: { labelKey: 'nav.registries', permission: 'Registry.View', icon: 'registries' },
    showInNav: true,
  },
  adminRegistryDefinition: {
    id: 'admin-registry-definition',
    path: '/admin/registries/:code/definition',
    handle: {
      labelKey: 'registries.constructor',
      crumb: {
        ancestorIds: ['admin-registries'],
        resolveParam: 'code',
        resolveWith: 'registryName',
      },
    },
  },
  adminMethodologies: {
    id: 'admin-methodologies',
    path: '/admin/methodologies',
    handle: { labelKey: 'nav.methodologies', permission: 'Calculation.View', icon: 'methodologies' },
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
    handle: { labelKey: 'nav.expressions', permission: 'Calculation.View', icon: 'expressions' },
    showInNav: true,
  },
  adminUnits: {
    id: 'admin-units',
    path: '/admin/units',
    handle: { labelKey: 'nav.units', permission: 'Calculation.View', icon: 'units' },
    showInNav: true,
  },
  adminSecurity: {
    id: 'admin-security',
    path: '/admin/security',
    handle: { labelKey: 'nav.security', permission: 'Security.ManageRoles', icon: 'security' },
    showInNav: true,
  },
  adminPeriods: {
    id: 'admin-periods',
    path: '/admin/periods',
    // ⛔ Було `Period.Manage` — права з такою назвою немає в каталозі
    // (`sec.Permission`), той самий факт, що й у старому `AppLayout.tsx`
    // (директива №09 `S-02`) — перенесено без зміни значення.
    handle: { labelKey: 'nav.periods', permission: 'Period.Configure', icon: 'periods' },
    showInNav: true,
  },
  adminSources: {
    id: 'admin-sources',
    path: '/admin/sources',
    handle: { labelKey: 'nav.sources', permission: 'Integration.Manage', icon: 'sources' },
    showInNav: true,
  },
  adminMapping: {
    id: 'admin-mapping',
    path: '/admin/mapping',
    handle: { labelKey: 'nav.mapping', permission: 'Integration.Manage', icon: 'mapping' },
    showInNav: true,
  },
  adminJobs: {
    id: 'admin-jobs',
    path: '/admin/jobs',
    handle: { labelKey: 'nav.jobs', permission: 'System.ViewHealth', icon: 'jobs' },
    showInNav: true,
  },
  adminSnapshots: {
    id: 'admin-snapshots',
    path: '/admin/snapshots',
    handle: { labelKey: 'nav.snapshots', permission: 'Report.ViewRegulatory', icon: 'snapshots' },
    showInNav: true,
  },
  adminAudit: {
    id: 'admin-audit',
    path: '/admin/audit',
    handle: { labelKey: 'nav.audit', permission: 'Security.ViewAudit', icon: 'audit' },
    showInNav: true,
  },
  adminUiStrings: {
    id: 'admin-ui-strings',
    path: '/admin/ui-strings',
    handle: { labelKey: 'nav.uiStrings', permission: 'System.ManageLocalization', icon: 'uiStrings' },
    showInNav: true,
  },
  adminHealth: {
    id: 'admin-health',
    path: '/admin/health',
    handle: { labelKey: 'nav.health', permission: 'System.ViewHealth', icon: 'health' },
    showInNav: true,
  },

  // ⛔ БЕЗ права — і це не пропуск. Пункт відповідає на «чому в мене порожні
  // екрани», тобто потрібен саме тому, у кого прав немає (`H-21`). Закрити
  // його правом означало б показувати відповідь лише тим, хто й так знає.
  // (Перенесено з коментаря `AppLayout.tsx` без зміни суті.)
  myGroups: {
    id: 'my-groups',
    path: '/my-groups',
    handle: { labelKey: 'nav.myGroups', icon: 'myGroups' },
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

/**
 * Шлях запису відносно СЕГМЕНТА-ПРЕДКА, для вкладених `<Route>` (`PR nav-arch #2`).
 *
 * ⚠ Це форма генерації, яку `PR #1` свідомо залишив невирішеною (див.
 * коментар вище: «саме там форма генерації природно визначиться разом із
 * вкладеністю»). `childPath()` знімає лише кореневий `/` (шлях лишається
 * плоским, монтується одразу під `AppLayout`); тут — те саме, але відносно
 * будь-якого проміжного layout-маршруту (`admin`, `admin/templates/:id`),
 * бо React Router 7 очікує, що `path` дочірнього `<Route>` НЕ повторює
 * сегмент, який уже зіставив батько (`admin/templates` дитиною `admin` —
 * подвійне зіставлення й неробочий маршрут, а не просто зайвий рядок).
 *
 * ⛔ Не приймає рядок «як є» без перевірки префікса: якщо шлях запису не
 * починається з `${parentPath}/`, це ознака, що запис змонтовано під не тим
 * предком (помилка виклику, не рантайм користувача) — кидає одразу, а не
 * мовчки повертає повний шлях (мовчазна поведінка тут замаскувала б саме ту
 * розбіжність реєстру й дерева, від якої весь реєстр і рятує).
 */
export function relativePath(route: RouteEntry, parentPath: string): string {
  const full = childPath(route);
  const prefix = parentPath.endsWith('/') ? parentPath : `${parentPath}/`;

  if (!full.startsWith(prefix)) {
    throw new Error(
      `relativePath: шлях '${route.path}' не починається з предка '${parentPath}' — запис змонтовано не там, де його оголошено.`,
    );
  }

  return full.slice(prefix.length);
}
