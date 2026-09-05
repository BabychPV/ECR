import { apiFetchIfChanged } from '@/api/client';
import type { UiStringCatalog } from '@/api/types';

/**
 * Локалізація (D-11, D-95, ФВ-14.9).
 *
 * ⛔ Словників у збірці НЕМАЄ. Рядки приходять із сервера
 * (`GET /api/v1/ui-strings/{lang}`), інакше обіцянка «додати мову = запис у
 * реєстр, не збірка клієнта» невиконувана: назви аркушів приходили б із БД,
 * а меню й кнопки лишалися б у бандлі.
 *
 * З тієї ж причини мова — `string`, а не union: `'en' | 'ru' | 'kz'` не
 * скомпілювався б із четвертою мовою, тобто робив би саме те, що заборонено.
 */
export type Language = string;

/** Область каталогу: до входу доступна лише публічна (D-114). */
export type Scope = 'public' | 'private';

/** Мова, якою показувати, коли в користувача не задано іншої. */
export const DefaultLanguage: Language = 'en';

/**
 * Форма каталогу — **згенерована**, а не описана тут.
 *
 * ⛔ Рукописний інтерфейс чекав поля `language`, а сервер віддає
 * `languageCode` (`A7-16`). Поле не читалося, тож нічого не ламалося — тип
 * просто описував неіснуючу відповідь, і будь-яка спроба ним скористатися
 * дала б `undefined` у місці, де компілятор обіцяв рядок.
 */
type Catalog = UiStringCatalog;

/**
 * Підписка на зміну каталогу.
 *
 * ⛔ Без неї завантажений каталог не потрапляє на екран. Каталог живе в
 * модулі, а не в стані React: `loadCatalog` наповнює `loaded`, і React про це
 * не дізнається ніколи — компонент лишається з тим, що встиг прочитати під час
 * першого рендера, тобто **із самими ключами**.
 *
 * Дефект був живий і видимий кожному: сторінка входу показувала `login.title`
 * і `login.submit`, доки користувач не натискав клавішу в полі — і саме тоді
 * `useState` давав перерендер, а написи «раптом» з'являлися. Знайдено не
 * тестом, а відкриттям сторінки в браузері: жоден компонентний тест цього не
 * ловить, бо всі вони підставляють рядки самі.
 */
const catalogListeners = new Set<() => void>();
let catalogVersion = 0;

/** Позначає, що вміст каталогу змінився. */
function bumpCatalog(): void {
  catalogVersion += 1;
  for (const listener of catalogListeners) listener();
}

/** Підписує слухача на зміни каталогу; повертає відписку. */
export function subscribeCatalog(listener: () => void): () => void {
  catalogListeners.add(listener);

  return () => {
    catalogListeners.delete(listener);
  };
}

/**
 * Версія каталогу.
 *
 * ⚠ Число, а не сам каталог: `useSyncExternalStore` порівнює знімок за
 * посиланням, і повернення об'єкта, що збирається наново, дало б нескінченний
 * цикл рендерів.
 */
export function catalogSnapshot(): number {
  return catalogVersion;
}

const loaded = new Map<string, Catalog>();
let current: Language = DefaultLanguage;

/**
 * Ключ кешу в localStorage.
 *
 * ⚠ Ревізія — ЧИСЛО (`int` на сервері), і в ключі вона рядок лише тому, що
 * ключі localStorage — рядки. Читається вона звідти теж рядком, тому
 * порівнювати їх треба у вигляді рядка, а не числа.
 */
function storageKey(language: Language, scope: Scope, revision: string | number): string {
  return `uiStrings:${language}:${scope}:${revision}`;
}

/**
 * Ключ збереженого `ETag`.
 *
 * ⚠ Окремо від ключа з ревізією: `ETag` — це рядок сервера
 * (`"public-en-1"`), і збирати його на клієнті з ревізії означало б завести
 * друге джерело правди про формат, який сервер може змінити.
 */
function etagKey(language: Language, scope: Scope): string {
  return `uiStrings:${language}:${scope}:etag`;
}

/**
 * Чи розв'язано каталог для мови й області (`D-138`).
 *
 * ⛔ Оболонка не має права малювати текст, доки це не так. Інакше перший кадр
 * складається з позначених ключів — `⟦login.title⟧` замість назви системи, —
 * і користувач бачить його щоразу, поки йде запит. Стан завантаження чесніший
 * і коштує ті самі мілісекунди (`ФВ-14.21`).
 *
 * ⚠ «Розв'язано» означає САМЕ спробу, що завершилася: невдалий запит теж
 * лишає порожній каталог у `loaded` і теж розв'язує цю обіцянку — інакше
 * недоступний сервер вішав би сторінку входу назавжди.
 */
export function isCatalogResolved(lang: Language, scope: Scope): boolean {
  return loaded.has(`${lang}:${scope}`);
}

/** Мова, обрана зараз. */
export function language(): Language {
  return current;
}

/**
 * Мова, яку слід показати до входу.
 *
 * ⚠ Порядок: збережений вибір → мова браузера → мова за замовчуванням.
 * Показати англійську тому, хто щойно перемкнувся на казахську, означає
 * змусити його перемикатися щоразу.
 */
export function preferredLanguage(): Language {
  const stored = safeGet('uiLanguage');
  if (stored !== null && stored.length > 0) return stored;

  const browser = typeof navigator === 'undefined' ? '' : (navigator.language ?? '');

  return browser.length >= 2 ? browser.slice(0, 2).toLowerCase() : DefaultLanguage;
}

/** Запам'ятовує вибір мови. */
export function setLanguage(value: Language): void {
  current = value;
  safeSet('uiLanguage', value);
  bumpCatalog();
}

/**
 * Завантажує каталог.
 *
 * ⚠ Кеш у `localStorage` за ключем із ревізією: сервер віддає `ETag`, ми
 * питаємо з `If-None-Match` і на `304` беремо збережене. Без цього кожне
 * відкриття сторінки тягнуло б кілька тисяч рядків заради того, щоб отримати
 * ті самі.
 */
export async function loadCatalog(lang: Language, scope: Scope): Promise<void> {
  const cacheKey = `${lang}:${scope}`;

  // ⚠ Збережене підставляється ЛИШЕ якщо в пам'яті ще нічого немає. Інакше
  // кожне повторне завантаження тієї самої мови перестворювало б об'єкт і
  // піднімало версію — тобто перемальовувало все дерево дарма ще ДО того, як
  // сервер відповість `304`.
  //
  // ⚠ Свіжіше значення, покладене в сховище іншою вкладкою, при цьому
  // пропускається — і це нормально: мережевий запит нижче однаково принесе
  // актуальне, а `ETag` не збігся б.
  const cached = readCached(lang, scope);
  if (cached !== null && !loaded.has(cacheKey)) {
    loaded.set(cacheKey, cached);
    bumpCatalog();
  }

  try {
    // ⚠ Умовний запит іде ЛИШЕ коли є що лишити при `304`. Без збереженого
    // каталогу відповідь «не змінилося» означала б порожній інтерфейс — тобто
    // рівно ту помилку, від якої кеш і рятує.
    const fresh = await apiFetchIfChanged<Catalog>(
      `/api/v1/ui-strings/${encodeURIComponent(lang)}?scope=${scope}`,
      cached === null ? null : safeGet(etagKey(lang, scope)),
    );

    if (fresh === null) {
      // ⛔ `304`: сервер підтвердив, що збережене — чинне. Вміст НЕ
      // перестворюється, і версія НЕ зростає, якщо мова та сама: інакше
      // React перемалював би все дерево дарма, і виграш умовного запиту
      // зникав би рівно там, де він мав бути — на кожному відкритті
      // сторінки (`D-136`, директива №04 §3.3).
      //
      // ⚠ Ідентичність об'єкта в `loaded` зберігається саме тому, що ми його
      // не чіпаємо: `readCached` уже поклав його на початку, а тут немає
      // жодного `set`.
      if (current !== lang) {
        current = lang;
        bumpCatalog();
      }

      return;
    }

    loaded.set(cacheKey, fresh.body);
    safeSet(storageKey(lang, scope, fresh.body.revision), JSON.stringify(fresh.body));
    safeSet(`uiStrings:${lang}:${scope}:revision`, String(fresh.body.revision));
    if (fresh.etag !== null) safeSet(etagKey(lang, scope), fresh.etag);
    bumpCatalog();
  } catch {
    // ⚠ Недоступний каталог не робить застосунок непридатним: показуємо
    // збережений, а якщо його немає — самі ключі. Порожній екран був би
    // гіршим за екран із технічними назвами.
    if (cached === null) {
      loaded.set(cacheKey, { languageCode: lang, revision: 0, strings: {} });
    }
  }

  // ⚠ Версія зростає лише разом зі зміною мови: усі шляхи, що змінюють ВМІСТ,
  // уже покликали `bumpCatalog` самі. Зайвий виклик тут перетворив би кожне
  // завантаження каталогу на зайвий перерендер усього дерева.
  if (current !== lang) {
    current = lang;
    bumpCatalog();
  }
}

/**
 * Позначка відсутнього рядка (`D-138`).
 *
 * ⛔ Голий ключ показувати НЕ МОЖНА. `login.title` на екрані виглядає майже
 * правдоподібно — як технічна назва, як щось «так і задумано»; саме тому
 * `A7-33` прожила до живого запуску, хоч була видима кожному. Кутові дужки
 * роблять пропуск помітним з першого погляду і з будь-якої відстані.
 */
const Missing = { open: '⟦', close: '⟧' } as const;

/** Ключі, про які вже поскаржилися: щоб не залити консоль на кожному рендері. */
const reported = new Set<string>();

/**
 * Повертає переклад за ключем із завантаженого каталогу.
 *
 * ⚠ Ланцюг запасних варіантів: мова → мова за замовчуванням → **позначений
 * ключ**. Порожнеча не показується ніколи: кнопка без напису виглядає як
 * зламаний інтерфейс.
 */
export function t(key: string, params?: Record<string, string | number>): string {
  const template = lookup(current, key) ?? lookup(DefaultLanguage, key);

  if (template === undefined) {
    report(key);

    // ⛔ Значення параметрів не губляться: `deny.Unknown` без своєї причини
    // перетворюється на те саме «недоступно», проти якого ця підказка й існує.
    const detail =
      params === undefined
        ? key
        : `${key} (${Object.entries(params)
            .map(([name, value]) => `${name}=${String(value)}`)
            .join(', ')})`;

    return `${Missing.open}${detail}${Missing.close}`;
  }

  if (params === undefined) return template;

  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in params ? String(params[name]) : match,
  );
}

/**
 * Скаржиться в консоль — лише в режимі розробки і лише раз на ключ.
 *
 * ⚠ У збірці для розгортання мовчить: користувачеві консоль не показують, а
 * шум у ній заважає діагностувати справжні помилки.
 */
function report(key: string): void {
  if (!import.meta.env.DEV || reported.has(key)) return;

  reported.add(key);
  console.error(`Немає рядка інтерфейсу: ${key} (мова ${current}).`);
}

/** Скидає перелік поскаржених ключів — для тестів. */
export function resetMissingReports(): void {
  reported.clear();
}

function lookup(lang: Language, key: string): string | undefined {
  // ⛔ `?.` і на `strings` теж. Каталог — це ВІДПОВІДЬ СЕРВЕРА, а не наш
  // об'єкт: проксі, шлюз або застаріле збережене значення можуть віддати JSON
  // без цього поля. Без захисту `t()` кидає виняток на першому ж написі, а
  // виняток у рендері — це білий екран замість застосунку.
  return (
    loaded.get(`${lang}:private`)?.strings?.[key] ?? loaded.get(`${lang}:public`)?.strings?.[key]
  );
}

function readCached(lang: Language, scope: Scope): Catalog | null {
  const revision = safeGet(`uiStrings:${lang}:${scope}:revision`);
  if (revision === null) return null;

  const raw = safeGet(storageKey(lang, scope, revision));
  if (raw === null) return null;

  try {
    return JSON.parse(raw) as Catalog;
  } catch {
    return null;
  }
}

/**
 * Читання localStorage, яке не падає.
 *
 * У приватному вікні і при заблокованих даних сайту звернення кидає виняток —
 * і застосунок не піднімався б узагалі через кеш перекладів.
 */
function safeGet(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function safeSet(key: string, value: string): void {
  try {
    globalThis.localStorage?.setItem(key, value);
  } catch {
    // Кеш — оптимізація; його відсутність не змінює поведінки.
  }
}
