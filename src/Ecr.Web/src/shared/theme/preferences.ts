import { useSyncExternalStore } from 'react';
import { theme } from './theme';

/**
 * Щільність подання (`ФВ-14.14`, `D-131`).
 *
 * ⚠ Дві, а не «налаштовуваний масштаб»: три значення довелося б перевіряти в
 * кожній таблиці, а користь від третього нульова. `compact` за замовчуванням —
 * це відповідь на «скільки рядків я бачу без прокручування», а не економія.
 */
export type Density = 'compact' | 'comfortable';

const DensityKey = 'ecr.density';

/**
 * Ім'я CSS-змінної висоти рядка — рівно те, що оголошує `tokens.css`.
 *
 * ⚠ Константа, а не літерал на місці читання: змінну оголошує CSS, читає
 * TypeScript, і перевіряє тест. Три написання того самого рядка розійшлися б
 * мовчки — а наслідок «змінної немає» виглядає як «висота просто така».
 */
export const RowHeightVar = '--ecr-row-height';

/** Висота рядка таблиці для щільності, у пікселях. */
export function rowHeight(density: Density): number {
  const other = theme.other as { rowHeightCompact: number; rowHeightComfortable: number };

  return density === 'compact' ? other.rowHeightCompact : other.rowHeightComfortable;
}

/**
 * Обрана щільність.
 *
 * ⚠ Читання не падає ніколи: у приватному вікні і при заблокованих даних сайту
 * звернення до `localStorage` кидає виняток, і застосунок не піднявся б через
 * налаштування вигляду.
 */
export function density(): Density {
  try {
    return globalThis.localStorage?.getItem(DensityKey) === 'comfortable'
      ? 'comfortable'
      : 'compact';
  } catch {
    return 'compact';
  }
}

/**
 * Щільність, яку користувач колись обирав сам; `null` — вибору не було.
 *
 * ⚠ Окремо від `density()`: та повертає дефолт, і «обрав compact» від «нічого
 * не обирав» за нею не відрізнити — а від цього залежить, чи переносити
 * значення на сервер (`BE-20`).
 */
export function storedDensity(): Density | null {
  try {
    const raw = globalThis.localStorage?.getItem(DensityKey);
    return raw === 'compact' || raw === 'comfortable' ? raw : null;
  } catch {
    return null;
  }
}

/**
 * Ті, кому треба знати про ВИБІР щільності (`BE-20`: синхронізація з сервером).
 *
 * ⚠ Реєстр, а не імпорт фічі: `shared` не знає, хто слухає (той самий прийом,
 * що й `shared/ui/unsavedSources`).
 */
const chosenListeners = new Set<(value: Density) => void>();

/** Підписує на вибір щільності; повертає відписку. */
export function onDensityChosen(listener: (value: Density) => void): () => void {
  chosenListeners.add(listener);

  return () => {
    chosenListeners.delete(listener);
  };
}

/** Запам'ятовує вибір щільності. */
export function setDensity(value: Density): void {
  try {
    globalThis.localStorage?.setItem(DensityKey, value);
  } catch {
    // Налаштування вигляду — не привід ламати роботу.
  }

  for (const notify of [...chosenListeners]) notify(value);
}

/** Ті, кому треба перемалюватися, коли щільність змінилася. */
const listeners = new Set<() => void>();

/**
 * Застосовує щільність до документа.
 *
 * ⛔ Через CSS-змінну, а не через перерендер кожної таблиці. Щільність зачіпає
 * геть усі подання; пропустити її в одному з п'ятнадцяти означало б, що екран
 * «майже» перемкнувся — і це помітно гірше, ніж якби не перемкнувся зовсім.
 *
 * ⛔ `UI-03`: тут стояв ЩЕ Й інлайновий запис `--ecr-row-height` на `<html>`
 * зі значення `rowHeight()`. Його прибрано навмисно, і це не спрощення. Інлайн
 * виграє каскад у будь-якому разі, тобто справжнім джерелом висоти було число
 * з `theme.ts`, а три змінні `tokens.css` лишалися декорацією: `--ecr-ctl-height`
 * і `--ecr-rail-item` перемикалися атрибутом, а `--ecr-row-height` — ні.
 * Наслідок гірший за неохайність: прибери оголошення з `tokens.css` — і нічого
 * не зміниться, тобто жоден тест не побачив би зниклої змінної.
 *
 * ⚠ Тепер джерело рівно одне — `tokens.css`, а `rowHeight()` лишається
 * запасним значенням для середовища, у яке таблицю стилів не завантажено
 * (модульний тест без `main.tsx`). Те, що ці два джерела не розходяться,
 * звіряє окремий тест, а не сподівання.
 */
export function applyDensity(value: Density): void {
  document.documentElement.dataset['ecrDensity'] = value;

  // ⚠ Копія набору: підписник має право відписатися прямо з обробника
  // (`useSyncExternalStore` робить саме це при розмонтуванні).
  for (const notify of [...listeners]) notify();
}

/**
 * Висота рядка, ЯК ЇЇ ПОРАХУВАВ БРАУЗЕР; `null` — змінної немає зовсім.
 *
 * ⛔ `null`, а не «розумний дефолт». Відсутня змінна і правильна висота — два
 * різні факти, і функція, яка на перший відповідає другим, робить перевірку
 * сліпою: тест міряв би число, яке сам собі й підказав. Рівно на цьому вже
 * обпікся `expectFocusRing` (різниця 0.00 % читалася як «кільце є»).
 * Запасне значення — рішення ВИКЛИКАЧА, і воно видно на місці виклику.
 */
export function measuredRowHeight(): number | null {
  if (typeof document === 'undefined') return null;

  const raw = getComputedStyle(document.documentElement).getPropertyValue(RowHeightVar).trim();
  const px = /^(\d+(?:\.\d+)?)px$/.exec(raw);

  return px === null ? null : Number(px[1]);
}

function subscribeDensity(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

function rowHeightSnapshot(): number {
  // ⚠ Запасне значення саме тут: у модульному тесті без `tokens.css` змінної
  // немає, і сітка з `rowSize={NaN}` не намалювала б жодного рядка. У
  // застосунку таблиця стилів є завжди (`main.tsx`), тож у продукті працює
  // перша половина виразу.
  return measuredRowHeight() ?? rowHeight(density());
}

/**
 * Висота рядка для компонента, що мусить перемалюватися при перемиканні.
 *
 * ⛔ Потрібна не всім, а тим, хто бере висоту ЧИСЛОМ, а не CSS-ом: RevoGrid
 * рахує віртуалізацію сам і приймає `rowSize` пропом. Звичайна таблиця читає
 * ту саму змінну стилем і перемальовується браузером без участі React —
 * підписувати її на це було б зайвим рендером на кожному екрані.
 *
 * ⚠ Знімок — число (примітив), тому `useSyncExternalStore` не зациклиться на
 * порівнянні за посиланням.
 */
export function useRowHeight(): number {
  return useSyncExternalStore(subscribeDensity, rowHeightSnapshot, rowHeightSnapshot);
}

/**
 * Обрана щільність — підписка, що сама перемальовує компонент.
 *
 * ⚠ Той самий реєстр (`subscribeDensity`), що й у `useRowHeight()`: обидва
 * читають ту саму подію `applyDensity()`, лише знімок різний (значення проти
 * пікселів). Компонент, якому потрібне САМЕ значення (`Density`) —
 * перемикач у `UserMenu` — раніше не мав способу підписатися і замість
 * цього перечитував стан через форсований ремонт (`key={generation}`) від
 * батька: той хак розмонтовував усе піддерево `UserMenu` щоразу, коли
 * щільність приходила ззовні (сервер, `BE-20`), і губив відкритий стан
 * `Menu`. Ця підписка читає значення напряму, без ремонту.
 */
export function useDensity(): Density {
  return useSyncExternalStore(subscribeDensity, density, density);
}
