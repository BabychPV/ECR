/**
 * Фабрика ключів TanStack Query (`PR nav-arch #1`).
 *
 * ⛔ До цієї фабрики ключі писались рядковими літералами прямо в компоненті:
 * `['template-version', id]` в одному файлі, `['template-versions', id]` (інша
 * форма того самого слова) у сусідньому — і жоден `tsc`/лінт цього не ловить,
 * бо масив рядків типізується як `unknown[]`. Розбіжність написання рахується
 * TanStack Query як РІЗНІ записи кешу: інвалідація за одним ключем мовчки не
 * зачіпає інший, і побічний ефект — засталі дані після мутації — виявляється
 * лише вручну, клацанням, а не тестом.
 *
 * **Форма.** Кожен домен — це вкладений об'єкт, перший елемент масиву завжди
 * рядок домену (`'templates'`, `'registries'`, `'methodologies'`). Це не
 * косметика: `queryClient.invalidateQueries({queryKey})` зіставляє КОЖЕН
 * запит, чий ключ починається з переданого масиву (префіксний збіг). Тобто
 * `templates.all()` (`['templates']`) інвалідовує геть усе під доменом —
 * перелік, версії, матрицю доступу, diff — одним викликом, а вузький
 * `templates.version(id)` не зачіпає інших версій і сусідні домени.
 *
 * ⚠ Кожна функція повертає `as const` кортеж, а не масив: без цього TanStack
 * Query бачить `string[]`, і другий рівень (`invalidateQueries` проти
 * `useQuery`) не звіряється типом узагалі — саме та діра, що дозволяла
 * розбіжне написання вище.
 */

/** Домен `templates`: перелік шаблонів, версії, їх похідні (diff, матриця доступу). */
const templates = {
  /** Префікс усього домену — для «знести все й перечитати» після грубих змін. */
  all: () => ['templates'] as const,

  /** `GET /api/v1/templates` — перелік шаблонів. */
  list: () => ['templates', 'list'] as const,

  /**
   * `GET /api/v1/templates/{templateId}/versions` — версії ОДНОГО шаблону.
   *
   * ⚠ Множина «versionsOf», а не «version»: це геть інша сутність, ніж
   * {@link templates.version} нижче — перелік версій шаблону проти однієї
   * конкретної версії. Змішати їх в один рядок (як робив старий код:
   * `'template-versions'` і `'template-version'`, різниця в одну літеру) —
   * саме та розбіжність, заради якої існує ця фабрика.
   *
   * ⚠ `templateId` приймає й `undefined` (`ExpressionsPage`: перший шаблон
   * переліку ще не завантажився) — той самий стан, що й старий рядковий
   * ключ ніс мовчки. Запит вимкнений (`enabled`), доки значення не відоме;
   * ключ лишається стабільним, а не змінює форму між рендерами.
   */
  versionsOf: (templateId: number | undefined) => ['templates', 'versionsOf', templateId] as const,

  /** Префікс {@link templates.versionsOf} для БУДЬ-ЯКОГО шаблону одразу. */
  allVersionsOf: () => ['templates', 'versionsOf'] as const,

  /** `GET /api/v1/template-versions/{versionId}/structure` — одна версія. */
  version: (versionId: number) => ['templates', 'version', versionId] as const,

  /**
   * `GET /api/v1/template-versions/{versionId}/diff/{otherVersionId}`.
   *
   * ⚠ `otherVersionId` приймає `null` — доки людина не вписала другу версію
   * для порівняння, запит вимкнений (`enabled`), але ключ мусить лишатися
   * стабільним, а не змінювати форму між рендерами.
   */
  versionDiff: (versionId: number, otherVersionId: number | null) =>
    ['templates', 'versionDiff', versionId, otherVersionId] as const,

  /** `GET /api/v1/template-versions/{versionId}/access-matrix`. */
  accessMatrix: (versionId: number) => ['templates', 'accessMatrix', versionId] as const,
};

/** Домен `registries`: перелік довідників, записи, опис (definition), історія. */
const registries = {
  all: () => ['registries'] as const,

  /** `GET /api/v1/registries` — перелік довідників. */
  list: () => ['registries', 'list'] as const,

  /** `GET /api/v1/registries/{code}/entries`. */
  entries: (code: string) => ['registries', 'entries', code] as const,

  /** `GET /api/v1/registries/{code}/definition`. */
  definition: (code: string) => ['registries', 'definition', code] as const,

  /** `GET /api/v1/registries/{code}/history`. */
  history: (code: string) => ['registries', 'history', code] as const,
};

/** Домен `methodologies`: перелік, версії, вміст версії (формули, константи…). */
const methodologies = {
  all: () => ['methodologies'] as const,

  /** `GET /api/v1/methodologies` — перелік методологій. */
  list: () => ['methodologies', 'list'] as const,

  /** `GET /api/v1/methodologies/{methodologyId}/versions`. */
  versionsOf: (methodologyId: number) => ['methodologies', 'versionsOf', methodologyId] as const,

  /** Префікс {@link methodologies.versionsOf} для будь-якої методології. */
  allVersionsOf: () => ['methodologies', 'versionsOf'] as const,

  /**
   * Формули чернетки версії.
   *
   * ⚠ `versionId` може бути `undefined` (доки версію не обрано на екрані) —
   * той самий стан, що ніс старий рядковий ключ `['methodology-formulas',
   * selected?.id]`. Запит із `undefined` не виконується (`enabled`), але
   * КЛЮЧ мусить лишатися стабільним, щоб кеш не плодив запис на кожен рендер.
   */
  formulas: (versionId: number | undefined) => ['methodologies', 'formulas', versionId] as const,

  /** Префікс {@link methodologies.formulas} — для інвалідації без відомого versionId. */
  allFormulas: () => ['methodologies', 'formulas'] as const,

  /** Константи версії (`ФВ-16.1`). */
  constants: (versionId: number) => ['methodologies', 'constants', versionId] as const,

  /** Правила відбору рядків (`ФВ-13.3`). */
  rules: (versionId: number) => ['methodologies', 'rules', versionId] as const,

  /** Оголошені виходи версії (`ФВ-16.6`). */
  outputs: (versionId: number) => ['methodologies', 'outputs', versionId] as const,

  /** Золотий набір тестів версії (`ФВ-13.7`). */
  tests: (versionId: number) => ['methodologies', 'tests', versionId] as const,

  /** Прив'язки методології до колонок документів (`D-69`) — не версії. */
  bindings: (methodologyId: number) => ['methodologies', 'bindings', methodologyId] as const,
};

/**
 * Єдина точка правди для ключів TanStack Query.
 *
 * Охоплює домени, потрібні для першого зрізу навігаційної архітектури —
 * breadcrumbs (`PR #3`) і prefetch за наміром (`PR #5`) резолвитимуть назви й
 * підвантажуватимуть дані саме цими ключами, а не власними рядковими
 * літералами. Інші домени (`documents`, `projects`, `periods`, `units`…)
 * лишаються на попередньому взірці — цей зріз навмисно обмежений маршрутами,
 * що отримають breadcrumbs першими (шаблони/версії, довідники, методології).
 */
export const queryKeys = {
  templates,
  registries,
  methodologies,
};
