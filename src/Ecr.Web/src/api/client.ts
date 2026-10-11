import type { paths } from './schema';

/**
 * Шляхи OpenAPI — основа типізованого клієнта.
 * Заповнюються `npm run api:types` із живого бекенда; до першої генерації
 * `schema.d.ts` є заглушкою (Q-017).
 */
export type ApiPaths = paths;

/**
 * Помилка API у форматі EcrProblemDetails.
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
export interface EcrProblem {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  errorCode: string;
  correlationId: string;
  extensions2?: Record<string, unknown>;
  /**
   * Лише для `429`: через скільки секунд сервер дозволяє повтор (`Retry-After`).
   * Поля немає, якщо заголовка немає або він не є цілим невід'ємним числом
   * секунд (HTTP-date свідомо не розбирається: сервер ECR шле секунди).
   */
  retryAfterSeconds?: number;
}

/** Ключ минущої відмови «дані зайняті» (`LockWaitGuard.MessageKey` на сервері). */
export const LockTimeoutMessageKey = 'err.ECR-DOC-4091.lockTimeout';

/**
 * Ключ відмови «аркуш зараз подається» (`SheetEditGate.Busy`, спільне блокування
 * не дочекалось подання цього аркуша).
 */
export const SheetBeingSubmittedMessageKey = 'err.ECR-DOC-4091.sheetBeingSubmitted';

/**
 * Ключ минущої відмови «база тимчасово зайнята» (`503 ECR-SYS-0503`, E1-04 на сервері:
 * дедлок 1205 після повторів EF, тайм-аут, обрив з'єднання). Транзакцію відкочено,
 * нічого не записано; сервер радить строк повтору в `Retry-After`.
 */
export const DatabaseBusyMessageKey = 'err.ECR-SYS-0503.databaseBusy';

/** Виняток клієнта API. */
export class EcrApiError extends Error {
  constructor(readonly problem: EcrProblem) {
    super(problem.detail ?? problem.title);
    this.name = 'EcrApiError';
  }

  /** Чи це конфлікт паралельного редагування. */
  get isConflict(): boolean {
    return this.problem.errorCode === 'ECR-CELL-0409';
  }

  /**
   * Чи це минуща відмова «дані зайняті» (`409 ECR-DOC-4091` з ключем
   * `err.ECR-DOC-4091.lockTimeout`, `LockWaitGuard.Busy`): очікування блокування
   * на сервері вичерпалось, нічого не записано, і той самий запит пройде, щойно
   * довга операція (перенос версії, великий імпорт) відпустить блокування.
   *
   * ⛔ AN-123 (`R1-03`/`R2-01`): НЕ остаточна відмова — правки не утримуються, а
   * повторюються автозбереженням із відступом (`scheduleBusyRetry`).
   *
   * ⚠ Саме за `messageKey`, а не за всім кодом: той самий `ECR-DOC-4091` несе й
   * «структуру змінено» (`structureChanged`), де повтор того самого запиту
   * нічого не вилікує.
   *
   * ⛔ X6-02: `sheetBeingSubmitted` — теж минуще. Збереження прочекало (до 30 с)
   * подання аркуша й нічого не записало; утримати правки до ручного повтору
   * означало б, що вони не доїдуть самі, навіть коли подання впало. Подання
   * пройшло — повтор дістане `ECR-DOC-0409`/`403`, і тоді утримання справедливе.
   */
  get isTransientBusy(): boolean {
    const messageKey = this.problem.extensions2?.['messageKey'];

    // ⛔ X8-06 (R6): `503 ECR-SYS-0503 databaseBusy` — той самий клас «нічого не
    // записано, повтор пройде»: без нього правки чекали ручного «Retry save», а
    // `Retry-After` сервера ніхто не читав. ⚠ Лише за ключем: інші 503 (шлюз,
    // `ECR-INT-0503` інтеграції) сюди не належать.
    return (
      (this.problem.errorCode === 'ECR-DOC-4091' &&
        (messageKey === LockTimeoutMessageKey || messageKey === SheetBeingSubmittedMessageKey)) ||
      (this.problem.errorCode === 'ECR-SYS-0503' && messageKey === DatabaseBusyMessageKey)
    );
  }

  /** Перелік конфліктів, якщо вони є. */
  get conflicts(): unknown[] {
    return (this.problem.extensions2?.['conflicts'] as unknown[]) ?? [];
  }

  /**
   * Чи це відмова gate-у обов'язкових вхідних колонок методології
   * (директива «обов'язкові вхідні колонки методології»).
   */
  get isRequiredInputMissing(): boolean {
    return this.problem.errorCode === 'ECR-CALC-0437';
  }

  /** Незаповнені обов'язкові колонки, якщо відмова саме про них. */
  get requiredInputCells(): RequiredInputCell[] {
    return (this.problem.extensions2?.['cells'] as RequiredInputCell[]) ?? [];
  }
}

/** Одна незаповнена обов'язкова вхідна колонка з відмови `ECR-CALC-0437`. */
export interface RequiredInputCell {
  rowKey: string;
  columnCode: string;
  ruleCode: string;
  message: string;
}

/** Прийняте в роботу завдання: сервер відповів 202. */
export interface AcceptedJob {
  jobId: string;
  statusUrl: string;
}

/** Куди вести користувача при 401. */
export const LOGIN_PATH = '/login';

/**
 * Заголовок кореляції.
 *
 * ⚠ Ідентифікатор генерує КЛІЄНТ і показує його в повідомленні про помилку.
 * Це єдиний спосіб звірити скаргу «у мене не зберігається» з серверним
 * журналом: без нього довелося б шукати за часом і логіном серед тисяч
 * записів того ж хвилинного піку в останній день періоду.
 */
export const CORRELATION_HEADER = 'X-Correlation-Id';

/** Генерує ідентифікатор кореляції запиту. */
export function newCorrelationId(): string {
  const uuid = globalThis.crypto?.randomUUID?.();
  if (uuid !== undefined) return uuid;

  // jsdom старих версій і небезпечний контекст (http без TLS) не дають
  // randomUUID. Кореляція потрібна навіть там: без неї скарга користувача
  // непорівнянна з логом.
  return `cid-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * Причина, з якої людину повели на вхід.
 *
 * `session-invalidated` — сервер обірвав ЧИННУ сесію штампом безпеки
 * (`SecurityStampMiddleware`: змінилися гранти, пароль, блокування) і
 * відповів `401` з тілом `ECR-AUTH-0401`. Без причини — звичайний `401`
 * cookie-схеми без тіла: людина просто не входила.
 */
export type LoginReason = 'session-invalidated';

/** Параметр адреси входу, що несе причину. */
export const LOGIN_REASON_PARAM = 'reason';

/** Адреса сторінки входу з поверненням і (необов'язковою) причиною. */
export function loginUrl(from: string, reason?: LoginReason): string {
  const base = `${LOGIN_PATH}?from=${encodeURIComponent(from)}`;
  return reason === undefined ? base : `${base}&${LOGIN_REASON_PARAM}=${reason}`;
}

/** Куди перенаправляти при 401; підміняється в тестах. */
let redirectToLogin: (from: string, reason?: LoginReason) => void = (from, reason) => {
  if (typeof window !== 'undefined') {
    // ⛔ AN-108 / S2-03: `replace`, не `assign` — сторінка з даними сеансу не лишається в історії (і в bfcache)
    // під кнопкою «Назад» на формі входу. Повернення після входу несе `?from=`.
    window.location.replace(loginUrl(from, reason));
  }
};

/**
 * Чи це обрив чинної сесії, а не «не входив».
 *
 * ⚠ Розрізняє ТІЛО: cookie-схема на анонімний запит віддає `401` без тіла
 * (`OnRedirectToLogin`), а штамп безпеки кидає `AccessDeniedException` з
 * `ECR-AUTH-0401`, і `ExceptionHandlingMiddleware` пише `problem+json`.
 * Читається `clone()` — оригінальне тіло лишається недоторканим.
 */
async function loginReasonOf(response: Response): Promise<LoginReason | undefined> {
  try {
    const body = (await response.clone().json()) as { errorCode?: unknown };
    return body.errorCode === 'ECR-AUTH-0401' ? 'session-invalidated' : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Чи `401` — це відповідь ФОРМИ, а не кінець сеансу.
 *
 * ⛔ V-16 (UX-прохід, третій раунд): хибний ПОТОЧНИЙ пароль на
 * `/change-password` сервер відмовляє `401` («The current password is
 * incorrect.»), і транспорт виводив із системи з «session has ended» — хоча
 * сеанс живий, а помилився користувач у полі форми. Сеанс, що справді
 * скінчився, на тому самому ендпоінті дає інший ключ (`signInRequired`), і
 * його перенаправлення лишається.
 *
 * ⚠ Розрізнення — за `messageKey`, а не за текстом: текст локалізований.
 */
async function isFormAnswer401(path: string, response: Response): Promise<boolean> {
  if (path === '/api/v1/login/local' || path === '/api/v1/login/windows') return true;
  if (path !== '/api/v1/auth/change-password') return false;

  try {
    const body = (await response.clone().json()) as { messageKey?: unknown };
    return body.messageKey === 'err.ECR-AUTH-0401.currentPasswordWrong';
  } catch {
    return false;
  }
}

/** Підміняє поведінку при 401 — для тестів і для роутера. */
export function setLoginRedirect(handler: (from: string, reason?: LoginReason) => void): void {
  redirectToLogin = handler;
}

/**
 * Хто має щось зробити ПЕРЕД перенаправленням на вхід.
 *
 * ⚠ Перенаправлення — повне перезавантаження сторінки, і все, що жило лише в
 * пам'яті (незбережені правки сітки), зникає разом із ним. Хук — останній
 * момент, коли про це ще можна залишити слід (`features/grid/lostEdits.ts`).
 * Транспорт не знає, ЩО саме записується: напрямок залежності той самий, що й
 * у `setRequestLanguageTag`.
 */
const beforeLoginRedirect = new Set<(from: string) => void>();

/** Реєструє дію перед перенаправленням на вхід; повертає зняття. */
export function onBeforeLoginRedirect(hook: (from: string) => void): () => void {
  beforeLoginRedirect.add(hook);

  return () => {
    beforeLoginRedirect.delete(hook);
  };
}

function runBeforeLoginRedirect(from: string): void {
  for (const hook of beforeLoginRedirect) {
    try {
      hook(from);
    } catch {
      // Збій хука не має зірвати перенаправлення.
    }
  }
}

/**
 * Слід незбереженого перед ЯВНИМ виходом (F6-01).
 *
 * ⛔ Вихід — теж повне перезавантаження сторінки, як і `401`: усе, що жило лише
 * в пам'яті, зникає. Людина вже бачила питання й обрала «Вийти», але слід
 * (`lostEdits`) лишається, щоб після наступного входу можна було відновити
 * введене. Викликати ДО `beginSignOut()`.
 */
export function recordBeforeSignOut(): void {
  runBeforeLoginRedirect(window.location.pathname + window.location.search);
}

/** Адреса виходу — єдиний запит, який ще йде після `beginSignOut()`. */
export const LOGOUT_PATH = '/api/v1/logout';

/**
 * Чи людина вже натиснула «Вийти» в цій вкладці.
 *
 * ⛔ A2-06: між кліком «Вийти» і перезавантаженням сторінки на `/login` живий
 * застосунок встигав піти ще кількома запитами (опитування «My tasks», фонові
 * перезапити) — уже без cookie, тобто `401`. Кожен такий `401` ще й запускав
 * `redirectToLogin` з `?from=…&reason=…`, перебиваючи чистий перехід виходу на
 * `/login`. Після виходу сеансу немає за визначенням, тож запит навіть не
 * надсилається: викликач отримує той самий `401`, що й від сервера, але без
 * мережі й без повторного перенаправлення (вихід уже веде на `/login` сам).
 *
 * ⚠ Прапорець живе до перезавантаження сторінки, і це навмисно: новий сеанс
 * починається лише після входу, а вхід — це завжди нове завантаження.
 */
let signedOut = false;

/** Перевірка власника cookie, що триває (`verifySessionOwner`): одночасні сповіщення зливаються в одну. */
let ownerCheck: Promise<void> | null = null;

/** Позначає вихід: далі в мережу йде лише сам `POST /api/v1/logout`. */
export function beginSignOut(): void {
  signedOut = true;
}

/** Скидає позначку виходу — лише для тестів. */
export function resetSignOutForTests(): void {
  signedOut = false;
  sessionSwitched = false;
  sessionUserId = null;
  ownerCheck = null;
}

/**
 * Чи сеанс цієї вкладки змінився деінде (AN-108 / S2-05): в іншій вкладці вийшли або увійшов ІНШИЙ користувач.
 *
 * ⛔ Cookie сеансу спільна для всіх вкладок. Після входу B у сусідній вкладці кожен запит цієї вкладки (зокрема
 * автозбереження і маячок `beforeunload` з правками A) пішов би вже з cookie B — і журнал правок приписав би B
 * чужі значення. Тому з цієї миті мережа для вкладки закрита так само, як після власного виходу.
 */
let sessionSwitched = false;

/** Чи сеанс вкладки закрито: власний вихід або зміна сеансу в іншій вкладці. */
export function isSessionClosed(): boolean {
  return signedOut || sessionSwitched;
}

/** Перезавантаження сторінки; підміняється в тестах. */
let reloadPage: () => void = () => {
  window.location.reload();
};

/** Підміняє перезавантаження — лише для тестів. */
export function setReloadPageForTests(reload: () => void): void {
  reloadPage = reload;
}

/**
 * Покидає сеанс, що змінився деінде (AN-108 / S2-05): закриває мережу, лишає слід незбережених правок ЇХНЬОГО
 * власника (той самий шлях, що й `401`, — `onBeforeLoginRedirect`) і перезавантажує вкладку, щоб на екрані не
 * лишилось даних попереднього користувача. Ідемпотентно.
 */
export function abandonSwitchedSession(): void {
  if (sessionSwitched) return;
  sessionSwitched = true;
  if (typeof window === 'undefined') return;
  runBeforeLoginRedirect(window.location.pathname + window.location.search);
  reloadPage();
}

/**
 * Реакція на сповіщення сусідньої вкладки «сеанс змінився» (R2-05): покидає сеанс лише коли власник cookie ІНШИЙ.
 *
 * ⛔ Сповіщення не каже, ХТО увійшов. Повторний вхід ТОГО САМОГО користувача (cookie спливла, вкладка 1 перейшла
 * на `/login`) перезавантажував усі інші вкладки й переносив їхні незбережені правки в слід `lostEdits` — хоча
 * автозбереження під новим cookie того самого користувача було б безпечним. Тут вкладка питає `/me`: той самий
 * `userId` — нічого не робить; інший, `401`, збій мережі чи невідомий власник вкладки — `abandonSwitchedSession`
 * (невідомо — безпечніше покинути).
 *
 * ⚠ Сирий `fetch`, а не `apiFetch`: `401` не має запускати перенаправлення з `?from=` (вкладка й так піде на вхід
 * через `abandonSwitchedSession`), а чужий cookie не має ПІДПИСУВАТИСЬ `X-Ecr-User` цієї вкладки. Одночасні
 * сповіщення (канал + `storage`) зливаються в одну перевірку.
 *
 * ⚠ Вихід (`UserMenu.signOut`) безумовний сам по собі: після виходу `/me` дає `401`.
 */
export function verifySessionOwner(): Promise<void> {
  if (isSessionClosed()) return Promise.resolve();
  if (ownerCheck !== null) return ownerCheck;

  ownerCheck = (async () => {
    const known = sessionUserId;
    if (known === null) {
      abandonSwitchedSession();
      return;
    }

    try {
      const response = await fetch('/api/v1/me', {
        credentials: 'include',
        cache: 'no-store',
        headers: { [CORRELATION_HEADER]: newCorrelationId() },
      });
      const owner = response.ok ? ((await response.json()) as { userId?: unknown }) : null;

      if (owner === null || owner.userId !== known) abandonSwitchedSession();
    } catch {
      abandonSwitchedSession();
    }
  })().finally(() => {
    ownerCheck = null;
  });

  return ownerCheck;
}

/**
 * Заголовок з id користувача, якого бачила ця вкладка (AN-108 / S2-05, серверний рубіж `SessionUserMiddleware`).
 *
 * ⛔ Клієнтські рубежі (`sessionChannel`, звірка `/me`) можуть не встигнути: сповіщення не дійшло, маячок
 * `beforeunload` іде в мить вивантаження. Тому небезпечні запити несуть id власника вкладки, і сервер, бачачи
 * cookie ІНШОГО користувача, відповідає `409 ECR-AUTH-0409`, а не записує правки під чужим іменем.
 */
export const SESSION_USER_HEADER = 'X-Ecr-User';

/** Код відмови «вкладка вважає себе іншим користувачем, ніж власник cookie». */
export const SESSION_USER_MISMATCH = 'ECR-AUTH-0409';

/** Id користувача з `/me`, якого бачила вкладка; `null` — ще не бачила (заголовок не шлеться). */
let sessionUserId: number | null = null;

/** Запам'ятовує користувача вкладки (`checkSessionUser`); `null` — забути. */
export function setSessionUserId(id: number | null): void {
  sessionUserId = id;
}

/** Заголовок користувача вкладки для запитів повз `apiFetch` (маячок `sendPatchBeacon`). */
export function sessionUserHeaders(): Record<string, string> {
  return sessionUserId === null ? {} : { [SESSION_USER_HEADER]: String(sessionUserId) };
}

/** Чи змінює метод стан (те саме правило, що в `SessionUserMiddleware`/`CsrfOriginMiddleware`). */
function isStateChanging(method: string | undefined): boolean {
  const m = (method ?? 'GET').toUpperCase();
  return m === 'POST' || m === 'PUT' || m === 'PATCH' || m === 'DELETE';
}

/**
 * Тег мови інтерфейсу, який іде на сервер заголовком `Accept-Language`.
 *
 * ⛔ Дефект, який це закриває: вибір мови в застосунку до сервера НЕ ДОХОДИВ
 * узагалі. Мова користувача живе лише в `localStorage` браузера
 * (`uiLanguage`), у профілі її немає, а `ICurrentUser.Language` на сервері
 * (`CurrentUser.Language`) береться саме з `Accept-Language` — claim-а мови
 * (`ecr:lang`) у токені немає, і сервер його не читає. Отже мова серверних
 * текстів визначалася заголовком браузера, який клієнт не виставляв: оператор
 * перемикав застосунок на англійську, а відмови приходили російською, бо
 * такою була системна мова машини. Перемикач у шапці на них не впливав ніяк.
 *
 * ⚠ Значення штовхає сюди шар i18n, а не навпаки. Імпортувати `shared/i18n`
 * із цього модуля не можна: i18n сам тягне `apiFetch` для
 * `/api/v1/ui-strings`, і вийшов би цикл імпортів. Тому напрямок один —
 * i18n знає про транспорт, транспорт про i18n не знає.
 *
 * ⚠ Саме ТЕГ BCP-47 (`kk`), а не внутрішній код реєстру (`kz`):
 * `Accept-Language` — стандартний заголовок, і класти в нього власний
 * словник було б виглядом сумісності без сумісності. Переведення назад у код
 * реєстру робить сервер (`LanguageCodes.FromTag`).
 */
let requestLanguageTag: string | null = null;

/**
 * Оголошує мову, якою клієнт хоче отримувати серверні тексти.
 *
 * @param tag Тег BCP-47; `null` — не надсилати заголовок узагалі.
 */
export function setRequestLanguageTag(tag: string | null): void {
  requestLanguageTag = tag;
}

/**
 * Базовий HTTP-клієнт.
 *
 * ⚠ `credentials: 'include'` — автентифікація на **cookie**, не на токені:
 * токен у localStorage читається будь-яким скриптом на сторінці, а cookie з
 * `HttpOnly` — ні.
 *
 * ⛔ На 401 клієнт **не намагається мовчки увійти знову**. Мовчазний повторний
 * вхід приховує причину: користувач бачить, що дані «іноді не зберігаються», і
 * не пов'язує це з тим, що його сесія закінчилася.
 */
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await apiFetchRaw(path, init, false);

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/**
 * Сирий `Response` для тіл, що не є JSON (файли: CSV-вивантаження аудиту).
 *
 * ⚠ Той самий шлях, що й `apiFetch`: кореляція, `Accept-Language`, cookie,
 * перенаправлення на 401 і розбір `problem+json` у `EcrApiError`. Тіло
 * успішної відповіді НЕ читається — його читає викликач (`blob()`/`text()`).
 */
export async function apiFetchResponse(path: string, init?: RequestInit): Promise<Response> {
  return apiFetchRaw(path, init, false);
}

/**
 * Спільна частина: кореляція, cookie, розбір відмови.
 *
 * ⚠ Виділена не заради стислості, а тому, що інакше умовний запит довелося б
 * писати повз неї — і він єдиний з усього клієнта не перенаправляв би на вхід
 * при `401` і не мав би кореляції в журналі.
 *
 * ⛔ `allowNotModified` за замовчуванням НЕ вмикається: `304` без заголовка
 * `If-None-Match` — це або кеш проксі, або помилка викликача, і мовчазне
 * «даних немає» тут гірше за гучну відмову.
 */
async function apiFetchRaw(
  path: string,
  init: RequestInit | undefined,
  allowNotModified: boolean,
): Promise<Response> {
  const correlationId = newCorrelationId();

  if ((signedOut && path !== LOGOUT_PATH) || sessionSwitched) {
    throw new EcrApiError({
      title: 'err.ECR-AUTH-0401.signInRequired',
      status: 401,
      errorCode: 'ECR-AUTH-0401',
      correlationId,
    });
  }

  const headers = new Headers(init?.headers);
  headers.set(CORRELATION_HEADER, correlationId);

  // ⚠ `set`, але лише коли викликач НЕ задав свого: єдине місце, яке
  // надсилає власну `Accept-Language`, — завантаження каталогу конкретної
  // мови, і перебити його активною мовою означало б попросити каталог не тієї
  // мови, яку щойно обрали. Заголовок ставиться тут, а не в кожному виклику,
  // бо пропустити його в одному місці означало б, що частина відмов приходить
  // однією мовою, а частина іншою, — і причину такого не знайти.
  if (requestLanguageTag !== null && !headers.has('Accept-Language')) {
    headers.set('Accept-Language', requestLanguageTag);
  }

  // ⛔ `FormData` НЕ отримує `application/json`. Тип multipart несе межу
  // (`boundary`), яку генерує сам браузер, і задати його заголовком
  // неможливо: підписаний вручну `Content-Type` лишає тіло без межі, а
  // сервер відповідає 415 на кожен файл. Імпорт із перегляду diff — єдине
  // місце системи, яке надсилає файл, і саме тому помилка тут була б
  // одноразовою і назавжди.
  // ⛔ AN-108 / S2-05: небезпечний запит несе id користувача вкладки — сервер звірить його з cookie.
  if (sessionUserId !== null && isStateChanging(init?.method) && !headers.has(SESSION_USER_HEADER)) {
    headers.set(SESSION_USER_HEADER, String(sessionUserId));
  }

  const body = init?.body;
  if (body !== undefined && body !== null && !headers.has('Content-Type') && !isMultipart(body)) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(path, { ...init, headers, credentials: 'include' });

  // ⛔ Аудит-пас 5: невірний пароль на самій формі входу — теж `401`, і до
  // цього фіксу він так само викликав `redirectToLogin` (реальний
  // `window.location.assign` у продакшні): сторінка входу починала
  // перезавантажувати САМУ СЕБЕ в момент невдалої спроби, і `LoginPage.tsx`
  // не встигав показати відповідь сервера (`setError` у `submit()` —
  // код, що вже правильно ловить і показує помилку, просто ніколи не
  // отримував шансу спрацювати до навігації). Ендпоінти входу відповідають
  // за власний `401` самі — тут перенаправляти нема куди й нема чого.
  if (response.status === 401 && !(await isFormAnswer401(path, response))) {
    const from = typeof window === 'undefined' ? path : window.location.pathname;
    runBeforeLoginRedirect(from);
    redirectToLogin(from, await loginReasonOf(response));
    /*
     * ⛔ Заголовок — із КАТАЛОГУ, не літералом. Тут стояло «Потрібна
     * автентифікація» українською — мовою, якої в продукті немає (`D-95`:
     * en, ru, kz). Заголовки відповідей сервер локалізує сам (сторож
     * `ErrorTitleCatalogTests`), і саме цей кидок був винятком з-під нього:
     * він народжується на КЛІЄНТІ, коли сервер відповів `401`, а тіла з
     * заголовком ще немає.
     *
     * ⚠ Тут стоїть КЛЮЧ, а не готовий текст, і це не півзаходу заради
     * зручності. `shared/i18n` сам імпортує `apiFetchIfChanged` із цього
     * файлу (`i18n/index.ts:1`), тож виклик `t()` звідси замкнув би цикл
     * модулів. Ключ розв'язує його: рядок залишає цей файл без мови, а
     * перетворює його на текст шар ПОКАЗУ (`problemText`), який каталог і
     * так уже імпортує.
     *
     * ⚠ Ключ `err.ECR-AUTH-0401.signInRequired` уже є в каталозі — нового
     * рядка сіду не потрібно.
     */
    throw new EcrApiError({
      title: 'err.ECR-AUTH-0401.signInRequired',
      status: 401,
      errorCode: 'ECR-AUTH-0401',
      correlationId,
    });
  }

  if (allowNotModified && response.status === 304) {
    return response;
  }

  if (!response.ok) {
    const problem = await problemOf(response, correlationId);
    // ⛔ AN-108 / S2-05: сервер бачить cookie ІНШОГО користувача — та сама реакція, що й на сповіщення
    // сусідньої вкладки: мережа закривається, незбережені правки лишаються в сліді їхнього власника.
    if (response.status === 409 && problem.errorCode === SESSION_USER_MISMATCH) abandonSwitchedSession();
    throw new EcrApiError(problem);
  }

  return response;
}

/**
 * Чи формує браузер тип тіла сам.
 *
 * ⚠ `FormData` тут головний випадок, решта — та сама родина: для них
 * підставлений заголовок або зайвий, або шкідливий.
 */
function isMultipart(body: BodyInit): boolean {
  return (
    (typeof FormData !== 'undefined' && body instanceof FormData) ||
    (typeof Blob !== 'undefined' && body instanceof Blob) ||
    (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams)
  );
}

/** Відповідь умовного запиту: тіло і `ETag`, яким його позначив сервер. */
export interface Conditional<T> {
  body: T;
  etag: string | null;
}

/**
 * Умовний запит: `If-None-Match` і `304` як «не змінилося».
 *
 * ⛔ Окремий метод, а не гілка всередині `apiFetch`. `304` — не помилка і не
 * дані: для `apiFetch` він `!response.ok`, тобто перетворився б на
 * `EcrApiError('HTTP-304')`, і екран показав би червоне там, де сервер сказав
 * «усе гаразд, у тебе вже є». Тип `| null` змушує викликача обробити цей
 * випадок, а не дізнатися про нього з журналу.
 *
 * ⚠ Написаний тому, що обіцянка вже була: коментар у каталозі рядків описував
 * `If-None-Match` і `304`, сервер їх реалізував і віддавав `ETag` — а клієнт
 * жодного разу заголовка не надіслав. Кожне відкриття сторінки тягнуло повний
 * каталог, і помітити це можна було лише в мережевій панелі.
 */
export async function apiFetchIfChanged<T>(
  path: string,
  etag: string | null,
): Promise<Conditional<T> | null> {
  const init: RequestInit = etag === null ? {} : { headers: { 'If-None-Match': etag } };

  const response = await apiFetchRaw(path, init, true);

  if (response.status === 304) return null;

  return { body: (await response.json()) as T, etag: response.headers.get('ETag') };
}

/**
 * Ставить довгу операцію і повертає її ідентифікатор.
 *
 * ⚠ Окремий метод, а не гілка всередині `apiFetch`: 202 — це не «успіх із
 * тілом», а обіцянка. Тип, який іноді містить дані, а іноді ідентифікатор
 * задачі, змушував би кожного викликача це розбирати — і хтось забув би.
 */
export async function apiEnqueue(path: string, body?: unknown): Promise<AcceptedJob> {
  const init: RequestInit =
    body === undefined ? { method: 'POST' } : { method: 'POST', body: JSON.stringify(body) };

  const result = await apiFetch<{ jobId: string }>(path, init);

  return { jobId: result.jobId, statusUrl: `/api/v1/jobs/${encodeURIComponent(result.jobId)}` };
}

/**
 * Розбирає тіло помилки.
 *
 * ⚠ Якщо сервер відповів не `problem+json` (проксі, балансувальник, 502 від
 * шлюзу), клієнт усе одно віддає структуру з кодом: екран, який уміє показати
 * лише `EcrProblem`, інакше показав би «щось пішло не так» — тобто рівно те,
 * що заборонено (`07-checkpoints` Етап 6).
 */
async function problemOf(response: Response, correlationId: string): Promise<EcrProblem> {
  const retryAfterSeconds = retryAfterOf(response);
  const problem = await problemBodyOf(response, correlationId);

  // ⚠ Лише за наявності — та сама причина, що й для `type`/`detail` нижче.
  if (retryAfterSeconds !== undefined) problem.retryAfterSeconds = retryAfterSeconds;

  return problem;
}

/**
 * `Retry-After` відповіді `429` або `503` у секундах, або `undefined`.
 *
 * ⚠ Заголовок — єдине місце, де сервер передає строк: у тілі `problem+json`
 * його немає. Без цього поля клієнт міг би лише вгадувати, коли повторити.
 *
 * ✎ X8-06 (R6): і `503` — сервер ставить `Retry-After` на `ECR-SYS-0503` (E1-04).
 */
function retryAfterOf(response: Response): number | undefined {
  if (response.status !== 429 && response.status !== 503) return undefined;

  const raw = response.headers.get('Retry-After')?.trim();
  if (raw === undefined || !/^\d+$/.test(raw)) return undefined;

  const seconds = Number(raw);
  return Number.isSafeInteger(seconds) ? seconds : undefined;
}

/**
 * Заголовок відповіді БЕЗ тіла `problem+json` — ключем каталогу (`R-19`/`X-09`).
 *
 * ⛔ Тут стояв `HTTP ${status}`: будь-яка відповідь без тіла (проксі, шлюз,
 * статичний сервер, неіснуючий маршрут) показувала людині «HTTP 502» чи
 * «HTTP 404 · HTTP-404» — число протоколу замість пояснення.
 *
 * ⚠ Ключ, а не текст, — з тієї самої причини, що й `401` вище: виклик `t()`
 * звідси замкнув би цикл модулів із `shared/i18n`. Розв'язує ключ шар показу
 * (`problemText`, `CatalogKey`). Ключі — у ПУБЛІЧНІЙ області каталогу: шлюз
 * може відповісти 502 і на сторінці входу.
 *
 * ⚠ Групи, а не кожен статус: людині важить, ЩО робити (повторити за
 * хвилину, перевірити адресу, звернутися по доступ), а не номер статусу —
 * він і так лишається в `errorCode` (`HTTP-502`) для підтримки.
 */
function transportTitleKey(status: number): string {
  if (status === 404 || status === 410) return 'err.http.notFound';
  if (status === 403) return 'err.http.forbidden';
  if (status === 408 || status === 504) return 'err.http.timeout';
  if (status === 502 || status === 503) return 'err.http.unavailable';
  if (status >= 500) return 'err.http.serverError';

  return 'err.http.requestFailed';
}

/** Чи код склав транспорт (`HTTP-502`), а не сервер ECR із каталогу. */
export function isTransportErrorCode(code: string): boolean {
  return code.startsWith('HTTP-');
}

async function problemBodyOf(response: Response, correlationId: string): Promise<EcrProblem> {
  const fallback: EcrProblem = {
    title: transportTitleKey(response.status),
    status: response.status,
    errorCode: `HTTP-${response.status}`,
    correlationId: response.headers.get(CORRELATION_HEADER) ?? correlationId,
  };

  try {
    const body = (await response.json()) as Partial<EcrProblem> & Record<string, unknown>;

    const problem: EcrProblem = {
      title: body.title ?? fallback.title,
      status: body.status ?? response.status,
      errorCode: body.errorCode ?? fallback.errorCode,
      correlationId: body.correlationId ?? fallback.correlationId,
    };

    // ⚠ Необов'язкові поля додаються ЛИШЕ за наявності: під
    // `exactOptionalPropertyTypes` явний `undefined` — це не «немає поля»,
    // а «поле є і воно порожнє», і серіалізація в лог показала б різницю.
    if (body.type !== undefined) problem.type = body.type;
    if (body.detail !== undefined) problem.detail = body.detail;

    // ⛔ Розширення лежать ПЛОСКО у верхньому рівні тіла — так вимагає
    // `application/problem+json` (RFC 9457 §3.2: члени-розширення є полями
    // верхнього рівня), і саме так їх пише сервер:
    // `problem.Extensions["conflicts"] = …`.
    //
    // Тут був дефект тієї самої родини, що й `A7-34`…`A7-36`: клієнт шукав їх
    // у полі `extensions2`, якого в тілі немає взагалі. Наслідок мовчазний і
    // дорогий — `EcrApiError.conflicts` ЗАВЖДИ повертав порожній масив, тому
    // при конфлікті паралельного редагування grid показував «хтось змінив ці
    // комірки: 0» або не показував нічого. Користувач бачив відмову без
    // жодної підказки, ЩО саме розійшлося (`ФВ-14.24`).
    //
    // ⚠ Беремо все, що не належить самому формату: перелік стандартних полів
    // закритий (RFC 9457 §3.1), решта — розширення за визначенням.
    const standard = new Set(['type', 'title', 'status', 'detail', 'instance']);
    const extensions: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(body)) {
      if (!standard.has(key)) extensions[key] = value;
    }

    if (Object.keys(extensions).length > 0) problem.extensions2 = extensions;

    return problem;
  } catch {
    return fallback;
  }
}

/**
 * Чи має сенс повторювати запит.
 *
 * ⛔ 4xx не повторюється: 403 і 422 повторення не виправить, а 409 повторений
 * мовчки затер би чужу правку — саме те, від чого захищає `baseVersion`.
 */
export function isRetryable(error: unknown): boolean {
  if (!(error instanceof EcrApiError)) return true;

  return error.problem.status >= 500;
}
