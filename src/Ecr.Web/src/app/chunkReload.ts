import { lazy, type ComponentType, type LazyExoticComponent } from 'react';
import { hasUnsavedChanges } from '@/shared/ui/unsavedSources';

/**
 * Відмова лінивого чанка після оновлення — автоматичне перезавантаження (✎ 25.11, людина: «виправь це»).
 *
 * **Факт.** Після вікна на стенді вкладка, відкрита до оновлення, тримає СТАРИЙ `index.html`; перший перехід на
 * ліниву сторінку (або лінивий шматок каркаса — `IdleSignOut` у `AppLayout`) просить файл, якого вже немає.
 * Межа дочірніх маршрутів (`RouteErrorPage`) це пояснювала, але помилка ЛІНИВОГО ШМАТКА КАРКАСА падала вище —
 * у корінь `/`, де `errorElement` не було, і React Router малював свій розробницький екран
 * («Unexpected Application Error! … Hey developer 👋»). Тепер: межа є на кожному маршруті верхнього рівня
 * (`withRootErrorElement`, `router.tsx`), а відмова чанка лікується тим, чим вона лікується насправді, —
 * одним перезавантаженням.
 *
 * ⛔ Захист від циклу: не більше ОДНОГО автоматичного перезавантаження на збірку (`sessionStorage`, ключ —
 * ім'я вхідного чанка `index-<хеш>.js`, тобто ідентифікатор збірки). Якщо після перезавантаження та сама збірка
 * знову не може взяти свій чанк (зіпсоване розгортання), лишається екран із поясненням і кнопкою — не петля.
 * Немає `sessionStorage` (заборонено політикою браузера) — автоматики немає взагалі: без позначки нічим
 * відрізнити першу спробу від сотої.
 */

/** Тексти відмови завантаження модуля в різних рушіях і в рантаймі Vite. */
const ChunkFailurePatterns: readonly RegExp[] = [
  /Failed to fetch dynamically imported module/i, // Chromium
  /error loading dynamically imported module/i, // Firefox
  /Importing a module script failed/i, // Safari
  /Unable to preload CSS/i, // __vitePreload
  /Loading (CSS )?chunk [\w-]+ failed/i, // форма ChunkLoadError
];

/** Чи кинуте — відмова завантаження чанка (а не помилка рендера). */
export function isChunkLoadError(error: unknown): boolean {
  if (error === null || error === undefined) return false;

  if (error instanceof Error) {
    if (error.name === 'ChunkLoadError') return true;
    return ChunkFailurePatterns.some((pattern) => pattern.test(error.message));
  }

  return typeof error === 'string' && ChunkFailurePatterns.some((pattern) => pattern.test(error));
}

/**
 * Саме перезавантаження — через об'єкт, щоб тест міг його підмінити (`window.location.reload` у jsdom не
 * підмінюється: властивість `location` незмінна).
 */
export const pageReload = { run: (): void => window.location.reload() };

/** Ключ позначки «цю збірку вже перезавантажували». */
export const ChunkReloadStorageKey = 'ecr.chunkReload.build';

/**
 * Ідентифікатор збірки — шлях вхідного чанка з `index.html` (`/assets/index-<хеш>.js`).
 *
 * ⚠ Саме вхідний скрипт сторінки: це той `index.html`, що лежить у пам'яті вкладки. Після перезавантаження
 * браузер бере НОВИЙ `index.html` з іншим хешем — і позначка старої збірки його вже не стримує, а повторна
 * відмова тієї самої (нової) збірки — стримує.
 */
export function currentBuildId(): string {
  const entry = document.querySelector<HTMLScriptElement>('script[type="module"][src]');
  return entry?.getAttribute('src') ?? 'dev';
}

function readMarker(): string | null | undefined {
  try {
    return window.sessionStorage.getItem(ChunkReloadStorageKey);
  } catch {
    return undefined;
  }
}

function writeMarker(value: string): boolean {
  try {
    window.sessionStorage.setItem(ChunkReloadStorageKey, value);
    return true;
  } catch {
    return false;
  }
}

/** Чи автоматичне перезавантаження цієї збірки ще дозволене. */
export function mayAutoReload(): boolean {
  const marker = readMarker();
  return marker !== undefined && marker !== currentBuildId();
}

/**
 * Перезавантажує сторінку, якщо для цієї збірки цього ще не робили. Повертає `true`, коли перезавантаження
 * запущено.
 *
 * ⚠ Позначка пишеться ДО `reload()`: інакше між викликом і вивантаженням сторінки друга межа (або повторний
 * рендер `StrictMode`) запустила б друге перезавантаження.
 */
export function reloadOnceForStaleBuild(): boolean {
  // ⛔ Незбережені правки не викидаються мовчки: їх спершу зберігає `staleVersion.tsx` (`flushUnsaved`), а
  // перезавантаження — після, кнопкою банера або цим самим викликом, коли правок уже немає.
  if (hasUnsavedChanges()) return false;
  if (!mayAutoReload()) return false;
  if (!writeMarker(currentBuildId())) return false;

  pageReload.run();
  return true;
}

/**
 * `React.lazy` для шматків КАРКАСА (не сторінок): відмова чанка запускає перезавантаження і НЕ доходить до межі
 * — поки сторінка вивантажується, `Suspense` показує свій запасний вміст замість екрана помилки на мить.
 *
 * ⚠ Якщо перезавантаження вже було (або неможливе) — помилка йде далі як є, до `errorElement`: там пояснення й
 * кнопка. Інші помилки імпорту (синтаксис, відсутній експорт) не чіпаються взагалі.
 */
// ⚠ Та сама сигнатура, що й у `React.lazy` (`ComponentType<any>`): інакше пропси компонента не виводяться.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function lazyWithReload<T extends ComponentType<any>>(
  importer: () => Promise<{ default: T }>,
): LazyExoticComponent<T> {
  return lazy(() =>
    importer().catch((error: unknown) => {
      if (isChunkLoadError(error) && reloadOnceForStaleBuild()) {
        return new Promise<{ default: T }>(() => {});
      }

      throw error;
    }),
  );
}
