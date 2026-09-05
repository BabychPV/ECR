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
}

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

  /** Перелік конфліктів, якщо вони є. */
  get conflicts(): unknown[] {
    return (this.problem.extensions2?.['conflicts'] as unknown[]) ?? [];
  }
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

/** Куди перенаправляти при 401; підміняється в тестах. */
let redirectToLogin: (from: string) => void = (from) => {
  if (typeof window !== 'undefined') {
    window.location.assign(`${LOGIN_PATH}?from=${encodeURIComponent(from)}`);
  }
};

/** Підміняє поведінку при 401 — для тестів і для роутера. */
export function setLoginRedirect(handler: (from: string) => void): void {
  redirectToLogin = handler;
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

  const headers = new Headers(init?.headers);
  headers.set(CORRELATION_HEADER, correlationId);
  if (init?.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(path, { ...init, headers, credentials: 'include' });

  if (response.status === 401) {
    redirectToLogin(typeof window === 'undefined' ? path : window.location.pathname);
    throw new EcrApiError({
      title: 'Потрібна автентифікація',
      status: 401,
      errorCode: 'ECR-AUTH-0401',
      correlationId,
    });
  }

  if (allowNotModified && response.status === 304) {
    return response;
  }

  if (!response.ok) {
    throw new EcrApiError(await problemOf(response, correlationId));
  }

  return response;
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
  const fallback: EcrProblem = {
    title: `HTTP ${response.status}`,
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
