import type { components } from '@/api/schema';

/**
 * Чернетка стилю в редакторі шаблону (директива
 * `docs/build/directive-registry-lookup-and-cell-style.md`, Частина B, PR
 * B1) — за зразком `column.ts` (`W5.2`, `D-137`): псевдоніми згенерованих
 * типів тут само, бо цей файл поза SCOPE, у якому вони вже оголошені.
 */

type Schemas = components['schemas'];

/** Стиль у відповіді `PUT …/styles/{code}` / `GET …/styles`. */
export type StyleDefDto = Schemas['StyleDefDto'];

/** Тіло запиту `PUT …/styles/{code}`. */
export type SaveStyleDefRequest = Schemas['SaveStyleDefRequest'];

/** Товщина рамки: `0` немає, `1` тонка, `2` середня, `3` товста (`StyleMapper.ApplyBorders`). */
export type BorderWeight = 0 | 1 | 2 | 3;

/** `0` Left, `1` Center, `2` Right, `3` Justify (`StyleMapper.Horizontal`). */
export type HorizontalAlign = 0 | 1 | 2 | 3;

/** `0` Top, `1` Center, `2` Bottom (`StyleMapper.Vertical`). */
export type VerticalAlign = 0 | 1 | 2;

/**
 * Чернетка стилю.
 *
 * ⚠ Кольори — `#rrggbb` (порожній рядок — колір теми, немає override) для
 * сумісності з Mantine `ColorInput`; ARGB (те, чого чекає домен) рахується
 * лише при формуванні тіла запиту (`styleBody`), не тут — форма працює з
 * тим форматом, який показує людині колір, а не з тим, який зберігає сервер.
 */
export interface StyleDraft {
  readonly code: string;
  readonly fontName: string;

  /**
   * ⚠ Рядок, не число, і це не формальність: `fontSize` — `decimal` контракту,
   * а `decimal` їде рядком (`e470777a`). Чернетка тримає рівно те, що прийшло
   * з сервера й що піде назад; `Number` тут не викликається взагалі — ані на
   * читанні, ані на запису. Знадобиться число для CSS — перетворювати його
   * має місце рендеру, один раз, а не модель.
   */
  readonly fontSize: string | null;
  readonly isBold: boolean;
  readonly isItalic: boolean;
  readonly foregroundHex: string;
  readonly backgroundHex: string;
  readonly borderTop: BorderWeight;
  readonly borderRight: BorderWeight;
  readonly borderBottom: BorderWeight;
  readonly borderLeft: BorderWeight;
  readonly horizontalAlign: HorizontalAlign;
  readonly verticalAlign: VerticalAlign;
  readonly wrapText: boolean;
  readonly numberFormat: string;
}

/** Порожня чернетка нового стилю. */
export function emptyStyleDraft(code = ''): StyleDraft {
  return {
    code,
    fontName: '',
    fontSize: null,
    isBold: false,
    isItalic: false,
    foregroundHex: '',
    backgroundHex: '',
    borderTop: 0,
    borderRight: 0,
    borderBottom: 0,
    borderLeft: 0,
    horizontalAlign: 0,
    verticalAlign: 0,
    wrapText: false,
    numberFormat: '',
  };
}

/** Чернетка з наявного стилю — для правки. */
export function styleDraftOf(style: StyleDefDto): StyleDraft {
  const borders = parseBorderJson(style.borderJson);

  return {
    code: style.code,
    fontName: style.fontName ?? '',
    fontSize: style.fontSize,
    isBold: style.isBold,
    isItalic: style.isItalic,
    foregroundHex: hexOfArgb(style.foregroundArgb),
    backgroundHex: hexOfArgb(style.backgroundArgb),
    borderTop: borders.top,
    borderRight: borders.right,
    borderBottom: borders.bottom,
    borderLeft: borders.left,
    horizontalAlign: (style.horizontalAlign ?? 0) as HorizontalAlign,
    verticalAlign: (style.verticalAlign ?? 0) as VerticalAlign,
    wrapText: style.wrapText,
    numberFormat: style.numberFormat ?? '',
  };
}

/** Чому чернетку стилю ще не можна зберегти; `null` — можна. */
export type StyleBlocker = 'CodeEmpty' | 'CodeInvalid';

/**
 * Валідація коду — той самий регекс, що й `whyCannotSaveColumn` (`column.ts`):
 * обидва коди йдуть у той самий домен `EcrCode.Create` на сервері
 * (латиниця/цифри/підкреслення, перший символ — літера, довжина до 64).
 */
export function whyCannotSaveStyle(draft: StyleDraft): StyleBlocker | null {
  if (draft.code.trim().length === 0) return 'CodeEmpty';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'CodeInvalid';

  return null;
}

/** Тіло запиту `PUT …/styles/{code}`. */
export function styleBody(draft: StyleDraft): SaveStyleDefRequest {
  return {
    fontName: draft.fontName.trim().length === 0 ? null : draft.fontName.trim(),
    fontSize: draft.fontSize,
    isBold: draft.isBold,
    isItalic: draft.isItalic,
    foregroundArgb: argbOfHex(draft.foregroundHex),
    backgroundArgb: argbOfHex(draft.backgroundHex),
    borderJson: borderJsonOf(draft),
    horizontalAlign: draft.horizontalAlign,
    verticalAlign: draft.verticalAlign,
    wrapText: draft.wrapText,
    numberFormat: draft.numberFormat.trim().length === 0 ? null : draft.numberFormat.trim(),
  };
}

/**
 * `{"top":1,"right":1,"bottom":2,"left":1}` (`StyleMapper.ApplyBorders`);
 * `null` — усі чотири сторони без рамки (нема сенсу нести порожній об'єкт).
 */
function borderJsonOf(draft: StyleDraft): string | null {
  const sides: Record<string, BorderWeight> = {
    top: draft.borderTop,
    right: draft.borderRight,
    bottom: draft.borderBottom,
    left: draft.borderLeft,
  };

  if (Object.values(sides).every((weight) => weight === 0)) return null;

  return JSON.stringify(sides);
}

/** Розбирає `BorderJson`; нерозпізнане чи відсутнє — усі сторони `0` (`StyleMapper`-подібний тихий фолбек). */
function parseBorderJson(
  borderJson: string | null,
): { top: BorderWeight; right: BorderWeight; bottom: BorderWeight; left: BorderWeight } {
  const empty = { top: 0, right: 0, bottom: 0, left: 0 } as const;
  if (borderJson === null || borderJson.trim().length === 0) return empty;

  try {
    const parsed = JSON.parse(borderJson) as Record<string, unknown>;

    return {
      top: weightOf(parsed['top']),
      right: weightOf(parsed['right']),
      bottom: weightOf(parsed['bottom']),
      left: weightOf(parsed['left']),
    };
  } catch {
    return empty;
  }
}

function weightOf(value: unknown): BorderWeight {
  return typeof value === 'number' && value >= 0 && value <= 3 ? (value as BorderWeight) : 0;
}

/** `#rrggbb` → ARGB (альфа завжди `0xFF`, непрозорий — той самий примітив, що бачить `StyleMapper`/`XLColor.FromArgb`). */
function argbOfHex(hex: string): number | null {
  const match = /^#([0-9a-fA-F]{6})$/.exec(hex.trim());
  if (match === null || match[1] === undefined) return null;

  const value = Number.parseInt(match[1], 16);

  // ⚠ `| 0` примушує до 32-бітного знакового цілого — той самий формат, що
  // й C# `int` (`StyleDef.ForegroundArgb`): непрозорий білий (`0xFFFFFFFF`)
  // без цього лишався б додатним числом поза діапазоном `int`.
  return (0xff000000 | value) | 0;
}

/** ARGB → `#rrggbb`; `null`/без кольору — порожній рядок (колір теми). */
function hexOfArgb(argb: number | null): string {
  if (argb === null) return '';

  const rgb = (argb & 0x00ffffff) >>> 0;
  return `#${rgb.toString(16).padStart(6, '0')}`;
}
