import type { CellStyleDto, TableSliceDto } from '@/api/types';

/**
 * Умовне форматування на живій сітці документа (`ФВ-2.7`).
 *
 * Сервер рахує правила версії сам (`ConditionalFormatEvaluator`, та сама
 * функція, що в Excel-експорті) і віддає ГОТОВИЙ результат у зрізі:
 * `TableSliceDto.cellFormats`, ключ `"{rowKey}:{columnCode}"` (`02-contracts.md`,
 * ✎ 2026-09-30). Клієнт правил не обчислює — лише фарбує комірки з ключем.
 * ⚠ Результат — зі ЗБЕРЕЖЕНОГО стану: ще не збережена правка перефарбується
 * після повторного читання зрізу.
 *
 * ⛔ Формат — ШАР ПОВЕРХ стилю автора (`cellAppearance.ts`), а не третій
 * механізм поруч: він підміняє у `CellStyleDto` колонки лише те, що задає
 * (заливку, колір тексту, жирність), і далі все йде тим самим шляхом —
 * контраст під обидві теми, заливка лише на комірці без стану (`X-10`). Тому
 * незбережена чи заблокована комірка лишається впізнаваною і з правилом.
 *
 * ⚠ Модуль живе в лінивому чанку сітки: статично його бере лише
 * `DocumentGrid`, тож вхідний чанк `DocumentPage` (`D-132`) він не розширює.
 */

export type CellFormat = NonNullable<TableSliceDto['cellFormats']>[string];

/** Результат правил для комірки; `null` — жодне правило не спрацювало. */
export function cellFormatOf(slice: Pick<TableSliceDto, 'cellFormats'>, key: string): CellFormat | null {
  return slice.cellFormats?.[key] ?? null;
}

/**
 * Стиль автора з накладеним результатом правила. Правило задає лише те, що
 * задає: порожній колір лишає колір автора, жирність додається.
 */
export function withCellFormat(style: CellStyleDto | null | undefined, format: CellFormat): CellStyleDto {
  const base: CellStyleDto = style ?? {
    isBold: false,
    isItalic: false,
    foregroundArgb: null,
    backgroundArgb: null,
    horizontalAlign: null,
    verticalAlign: null,
    wrapText: false,
  };

  return {
    ...base,
    isBold: base.isBold || format.isBold,
    foregroundArgb: argbOfHex(format.foregroundHex) ?? base.foregroundArgb,
    backgroundArgb: argbOfHex(format.backgroundHex) ?? base.backgroundArgb,
  };
}

/**
 * `#rrggbb` → ARGB зі знаком .NET `int` (непрозорий), дзеркало `hexOfArgb` у
 * `cellAppearance.ts`: `| 0` дає те саме від'ємне число, що й `StyleDef`.
 * Порожньо або не `#rrggbb` — `null` (колір теми).
 */
function argbOfHex(hex: string | null | undefined): number | null {
  if (hex === null || hex === undefined || !/^#[0-9a-fA-F]{6}$/.test(hex)) return null;

  return (0xff000000 | Number.parseInt(hex.slice(1), 16)) | 0;
}
