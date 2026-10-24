/**
 * Розмір вікна, яке людина може розгорнути чи потягнути за кут (`ResizableModal`, P44).
 *
 * ⚠ Чиста логіка окремо від компонента: рендер Mantine у jsdom дорогий (`D1-12`), а обмеження,
 * крок клавіш і читання сховища — саме те, що ламається мовчки (вікно «стрибає» за екран або
 * забуває розмір). Тут усе перевіряється без жодного рендера.
 *
 * ⛔ `localStorage` — лише зручність одного браузера (як ширини колонок до D-201): його може не
 * бути (приватне вікно, заблоковані дані сайту), а сам доступ — кинути `SecurityError`. Тому
 * КОЖНЕ читання й запис — у `try/catch`, і без сховища вікно просто відкривається звичайним.
 */

/** Звичайний розмір (як задав екран), на весь екран або потягнутий рукою. */
export type ModalSize =
  | { readonly mode: 'normal' }
  | { readonly mode: 'full' }
  | { readonly mode: 'custom'; readonly width: number; readonly height: number };

/** Ширина й висота вікна в пікселях. */
export interface Box {
  readonly width: number;
  readonly height: number;
}

/** Найменше вікно, у якому ще поміщається форма з редактором. */
export const MinWidth = 480;
export const MinHeight = 320;

/** Крок стрілки на ручці і крок зі `Shift`. */
export const KeyStep = 16;
export const KeyStepLarge = 64;

/**
 * Частка екрана, яку може зайняти вікно: Mantine лишає `5vw`/`5dvh` з кожного боку
 * (`--modal-x-offset`, `--modal-y-offset`), і більше вікно однаково обрізав би `max-height`.
 */
const ViewportShare = 0.9;

export const NormalSize: ModalSize = { mode: 'normal' };

/** Межі розміру для поточного екрана; на малому екрані мінімум не більший за максимум. */
export function sizeLimits(viewport: Box): { readonly min: Box; readonly max: Box } {
  const max = {
    width: Math.max(1, Math.floor(viewport.width * ViewportShare)),
    height: Math.max(1, Math.floor(viewport.height * ViewportShare)),
  };

  return {
    min: { width: Math.min(MinWidth, max.width), height: Math.min(MinHeight, max.height) },
    max,
  };
}

/** Розмір у межах екрана й мінімуму. */
export function clampBox(box: Box, viewport: Box): Box {
  const { min, max } = sizeLimits(viewport);

  return {
    width: Math.round(Math.min(max.width, Math.max(min.width, box.width))),
    height: Math.round(Math.min(max.height, Math.max(min.height, box.height))),
  };
}

/**
 * Новий розмір після натиску клавіші на ручці; `null` — клавіша не про розмір.
 *
 * Стрілки вліво/вправо — ширина, вгору/вниз — висота (як тягнути правий нижній кут).
 */
export function keyResize(box: Box, key: string, large: boolean, viewport: Box): Box | null {
  const step = large ? KeyStepLarge : KeyStep;

  switch (key) {
    case 'ArrowRight':
      return clampBox({ width: box.width + step, height: box.height }, viewport);
    case 'ArrowLeft':
      return clampBox({ width: box.width - step, height: box.height }, viewport);
    case 'ArrowDown':
      return clampBox({ width: box.width, height: box.height + step }, viewport);
    case 'ArrowUp':
      return clampBox({ width: box.width, height: box.height - step }, viewport);
    default:
      return null;
  }
}

/**
 * Розмір під час перетягування кута.
 *
 * ⚠ Вікно Mantine стоїть по центру горизонталі: ширшає на обидва боки, тож кут іде за курсором
 * лише тоді, коли ширина росте на ПОДВІЙНИЙ зсув. По вертикалі вікно притиснуте догори і росте
 * вниз, якщо його не відцентровано (`centered`), — тоді теж подвійний.
 */
export function dragResize(start: Box, dx: number, dy: number, centered: boolean, viewport: Box): Box {
  return clampBox(
    { width: start.width + dx * 2, height: start.height + dy * (centered ? 2 : 1) },
    viewport,
  );
}

/** Ключ сховища для виду вікна. */
export function storageKey(sizeKey: string): string {
  return `ecr.modalSize.${sizeKey}`;
}

/** Сховище, якщо воно є і доступ до нього не кидає. */
function storage(): Storage | null {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

/** Розбір збереженого значення; будь-що незрозуміле — звичайний розмір. */
export function parseSize(raw: string | null): ModalSize {
  if (raw === null) return NormalSize;

  try {
    const value: unknown = JSON.parse(raw);
    if (typeof value !== 'object' || value === null) return NormalSize;

    const { mode, width, height } = value as { mode?: unknown; width?: unknown; height?: unknown };
    if (mode === 'full') return { mode: 'full' };

    if (
      mode === 'custom' &&
      typeof width === 'number' &&
      typeof height === 'number' &&
      Number.isFinite(width) &&
      Number.isFinite(height) &&
      width > 0 &&
      height > 0
    ) {
      return { mode: 'custom', width: Math.round(width), height: Math.round(height) };
    }
  } catch {
    // Пошкоджений рядок — те саме, що його відсутність.
  }

  return NormalSize;
}

/** Останній розмір цього виду вікна; без сховища — звичайний. */
export function readSize(sizeKey: string): ModalSize {
  try {
    return parseSize(storage()?.getItem(storageKey(sizeKey)) ?? null);
  } catch {
    return NormalSize;
  }
}

/**
 * Запам'ятовує розмір. Звичайний розмір стирає запис: «Згорнути» — це й є скидання.
 *
 * ⚠ Відмова запису (переповнене сховище, заборона) мовчазна навмисно: розмір — зручність,
 * і помилка про неї посеред роботи з формулою важила б більше за саму зручність.
 */
export function writeSize(sizeKey: string, size: ModalSize): void {
  try {
    const target = storage();
    if (target === null) return;

    if (size.mode === 'normal') target.removeItem(storageKey(sizeKey));
    else target.setItem(storageKey(sizeKey), JSON.stringify(size));
  } catch {
    // Див. коментар над функцією.
  }
}
