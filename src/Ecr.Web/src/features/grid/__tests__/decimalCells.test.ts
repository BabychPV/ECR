import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { normalizeDecimal } from '@/shared/format';
import type { ColumnDto, RegistryEntryDto, TableSliceDto } from '@/api/types';
import { cellText, sameCellValue } from '../cellValue';
import { captureEdit, coerce } from '../edits';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { lookupCellDisplay } from '../LookupCellEditor';
import { decimalTextOf, roundToScale } from '../rounding';
import {
  cellKey,
  discardPendingRows,
  pendingSlice,
  putPendingEdit,
  resetPending,
} from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * `decimal` приходить із сервера РЯДКОМ (коміт `e470777a`).
 *
 * ⛔ Чому це найдорожча частина переходу саме для сітки: `RowDto.cells`
 * типізовані як `{ [key: string]: unknown }`, тож `tsc` не бачить НІЧОГО —
 * значення `5` і значення `"5.0000000000"` компілюються однаково. Усі
 * твердження нижче рантаймні й свідомо БЕЗ рендера: сітка в jsdom важка, а
 * рівність значень, формат показу і склад тіла запиту від неї не залежать.
 */

/** Нерозривні пробіли ICU → звичайний: інакше падіння нечитабельне. */
function norm(text: string): string {
  return text.replace(/[   ]/g, ' ');
}

/**
 * Значення, яке `double` ГАРАНТОВАНО псує, і те, на що він його перетворює.
 *
 * ⛔ Не будь-які «16 знаків». `String(Number('1.2345678901234567'))` повертає
 * той самий рядок — на такому вході мутація «пустити через `Number`» лишилася
 * б ЗЕЛЕНОЮ, тобто тест нічого не доводив би. Тут двадцять значущих цифр, і
 * `Number` зрізає три останні; окреме твердження нижче це і фіксує.
 */
const Twenty = '1234.1234567890123456';
const TwentyThroughDouble = '1234.1234567890124';

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Обсяг',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(cells: Record<string, unknown>, columns: ColumnDto[] = [column()]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Item',
        label: null,
        rowVersion: '0x2A',
        cells,
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

/** Текст, який намалює сітка для комірки `R1`/`C1`. */
function shownValue(data: TableSliceDto, value: unknown): string {
  const columns = gridColumns(data, false, NoLocalFlags, {}, noRequiredInput);
  const found = columns.find((c) => c.prop === 'C1');

  if (found === undefined) throw new Error('Немає колонки C1');
  if (typeof found.cellTemplate !== 'function') throw new Error('cellTemplate відсутній');

  // ⚠ RevoGrid кличе шаблон саме так: гіпер-функція і властивості комірки.
  // Шаблон десяткової колонки повертає РЯДОК, тож гіпер-функція йому не
  // потрібна — і це навмисно: показ перевіряється без тіньового DOM.
  return String(found.cellTemplate((() => null) as never, { value } as never, undefined as never));
}

beforeEach(() => {
  setLanguage('en');
  resetPending();
});

afterEach(() => {
  resetPending();
  setLanguage(DefaultLanguage);
  localStorage.clear();
});

describe('А: усі знаки доходять із відповіді до екрана', () => {
  it('замір: Number(...) на цьому вході ТАКИ псує значення', () => {
    // ⛔ Без цього твердження наступні два доводили б менше, ніж обіцяють.
    expect(String(Number(Twenty))).toBe(TwentyThroughDouble);
  });

  it('значення зрізу малюється з усіма знаками — порівняння РЯДКІВ, не чисел', () => {
    // ⛔ Мутаційна межа: `Number` дорогою дав би `1,234.1234567890124`.
    // `toBeCloseTo` тут був би зелений і на зламаному коді.
    const data = slice({ C1: Twenty }, [column({ scale: 16 })]);

    expect(norm(shownValue(data, data.rows[0]?.cells.C1))).toBe('1,234.1234567890123456');
  });

  it('те саме казахською — роздільники локалі продукту, знаки на місці', () => {
    setLanguage('kz');
    const data = slice({ C1: Twenty }, [column({ scale: 16 })]);

    expect(norm(shownValue(data, data.rows[0]?.cells.C1))).toBe('1 234,1234567890123456');
  });
});

describe('Г (дзеркало): звичайні числа показуються як раніше', () => {
  it('масштаб 0 — жодних хвостових нулів', () => {
    const data = slice({ C1: '5.0000000000' }, [column({ scale: 0 })]);

    // ⛔ До `e470777a` `JSON.parse` прибирав ці нулі сам. Показ зобов'язаний
    // лишитися таким самим — інакше кожна ціла комірка аркуша стала б
    // `5.0000000000`.
    expect(shownValue(data, '5.0000000000')).toBe('5');
  });

  it('масштаб 2 — рівно те, що ввели', () => {
    const data = slice({ C1: '12.3400000000' }, [column({ scale: 2 })]);

    expect(shownValue(data, '12.3400000000')).toBe('12.34');
  });

  it('нечислове значення показується текстом, а не зникає', () => {
    // Так у сітку потрапляє те, що `coerce` не розпізнав як число: сервер
    // відповість `ECR-CELL-0422`, і до того оператор має бачити свій ввід.
    const data = slice({ C1: 'н/д' });

    expect(shownValue(data, 'н/д')).toBe('н/д');
    expect(shownValue(data, '')).toBe('');
  });

  it('текстова колонка шаблону не отримує взагалі', () => {
    const data = slice({ C1: '5.0000000000' }, [column({ dataType: 'String' })]);
    const columns = gridColumns(data, false, NoLocalFlags, {}, noRequiredInput);

    expect(columns.find((c) => c.prop === 'C1')?.cellTemplate).toBeUndefined();
  });
});

describe('Д: формат комірки — доповнення нулями до scale (лише показ)', () => {
  it('1.5 при scale = 4 → 1.5000; без scale — 1.5 (дзеркало)', () => {
    expect(shownValue(slice({ C1: '1.5' }, [column({ scale: 4 })]), '1.5')).toBe('1.5000');
    expect(shownValue(slice({ C1: '1.5' }, [column({ scale: null })]), '1.5')).toBe('1.5');
  });

  it('20 значущих цифр доповнюються без втрати — не Number().toFixed()', () => {
    // ⛔ Мутаційна межа: `Number(x).toFixed(18)` дав би інші останні цифри.
    const data = slice({ C1: Twenty }, [column({ scale: 18 })]);

    expect(norm(shownValue(data, Twenty))).toBe('1,234.123456789012345600');
  });

  it('від\'ємне, нуль, мова з комою', () => {
    const data = slice({ C1: '-2.5' }, [column({ scale: 3 })]);

    expect(shownValue(data, '-2.5')).toBe('-2.500');
    expect(shownValue(data, '0.0000000000')).toBe('0.000');
    setLanguage('kz');
    expect(norm(shownValue(data, '-1234.5'))).toBe('-1 234,500');
  });

  it('порожня комірка лишається порожньою; нечислове — як є', () => {
    const data = slice({ C1: null }, [column({ scale: 4 })]);

    expect(shownValue(data, null)).toBe('');
    expect(shownValue(data, undefined)).toBe('');
    expect(shownValue(data, 'н/д')).toBe('н/д');
  });

  it('експонентний запис не ламається: показується як є', () => {
    // `normalizeDecimal` експоненти не приймає (сервер її не друкує).
    expect(shownValue(slice({ C1: '1e3' }, [column({ scale: 2 })]), '1e3')).toBe('1e3');
  });

  it('довший дріб за scale не зрізається показом', () => {
    expect(shownValue(slice({ C1: '1.23456' }, [column({ scale: 2 })]), '1.23456')).toBe('1.23456');
  });

  it('буфер обміну нулів показу не отримує', () => {
    expect(cellText('1.5')).toBe('1.5');
  });
});

describe('Б: комірка не лишається брудною', () => {
  it('ввід «5» поверх серверного «5.0000000000» не є правкою', () => {
    /*
     * ⛔ Мутаційна межа й суть вимоги. Сервер віддає значення в масштабі
     * колонки, оператор бачить `5` і набирає `5`, а RevoGrid повідомляє
     * `afteredit` на кожен вихід із редактора. Порівняння ТЕКСТУ (`'5'` проти
     * `'5.0000000000'`) назвало б це правкою — і комірка отримала б позначку
     * незбереженої на порожньому місці.
     */
    const data = slice({ C1: '5.0000000000' });

    expect(captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '5' })).toBeNull();
  });

  it('справжня зміна значення правкою лишається', () => {
    // ⚠ Дзеркало: без нього попереднє твердження було б зелене й на коді, який
    // не захоплює ЖОДНОЇ правки.
    const data = slice({ C1: '5.0000000000' });
    const captured = captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '5.1' });

    expect(captured?.pending.value).toBe('5.1');
  });

  it('число зі старішого сервера й рядок — те саме значення', () => {
    expect(captureEdit(slice({ C1: 5 }), { columnCode: 'C1', rowKey: 'R1', raw: '5' })).toBeNull();
  });

  it('після збереження позначку знято, хоч запис і надісланий текст різні', () => {
    /*
     * ⛔ Тут `discardPendingRows` звіряв ПОСИЛАННЯ на об'єкт правки. Патч
     * будують і шляхи, які в сховище не пишуть (вставка, undo/redo,
     * безхазяйний зріз), тож у знімку `sent` лежать нові об'єкти — і правка
     * сховища «виглядала новішою» назавжди. Порівняння тексту дало б те саме:
     * `'5'` і `'5.0000000000'` — різні рядки, одне число.
     */
    const stored: PendingEdit = {
      rowKey: 'R1',
      columnCode: 'C1',
      value: '5',
      isEmpty: false,
      baseVersion: '0x2A',
    };

    putPendingEdit(1, 202609, stored);

    const sent = new Map([
      [cellKey(stored), { ...stored, value: '5.0000000000' } satisfies PendingEdit],
    ]);

    discardPendingRows(1, 202609, ['R1'], sent);

    expect(pendingSlice(1, 202609).size).toBe(0);
  });

  it('правка, зроблена ПОКИ патч летів, позначку зберігає', () => {
    // ⚠ Дзеркало попереднього: інакше «звіряємо значення» перетворилося б на
    // «стираємо все, що було в рядку» — тобто на тиху втрату введеного.
    const stored: PendingEdit = {
      rowKey: 'R1',
      columnCode: 'C1',
      value: '7',
      isEmpty: false,
      baseVersion: '0x2A',
    };

    putPendingEdit(1, 202609, stored);

    const sent = new Map([[cellKey(stored), { ...stored, value: '5' } satisfies PendingEdit]]);
    discardPendingRows(1, 202609, ['R1'], sent);

    expect(pendingSlice(1, 202609).size).toBe(1);
  });

  it('явна порожнеча і стирання не зливаються в одну рівність', () => {
    // `R-B4`: обидва несуть `value: null`, але це різні наміри.
    const stored: PendingEdit = {
      rowKey: 'R1',
      columnCode: 'C1',
      value: null,
      isEmpty: true,
      baseVersion: '0x2A',
    };

    putPendingEdit(1, 202609, stored);

    const sent = new Map([[cellKey(stored), { ...stored, isEmpty: false } satisfies PendingEdit]]);
    discardPendingRows(1, 202609, ['R1'], sent);

    expect(pendingSlice(1, 202609).size).toBe(1);
  });
});

describe('sameCellValue — рівність ЗНАЧЕННЯ, не тексту й не посилання', () => {
  it('одне число трьома записами — рівні', () => {
    expect(sameCellValue(5, '5')).toBe(true);
    expect(sameCellValue('5', '5.0000000000')).toBe(true);
    expect(sameCellValue('-2.50', -2.5)).toBe(true);
    expect(sameCellValue('0', '-0.000')).toBe(true);
  });

  it('різні числа — різні', () => {
    expect(sameCellValue('5', '5.0000000001')).toBe(false);
    expect(sameCellValue('5', '50')).toBe(false);
  });

  it('порожнеча зведена, але не дорівнює нулю', () => {
    expect(sameCellValue(null, undefined)).toBe(true);
    expect(sameCellValue(null, 0)).toBe(false);
    expect(sameCellValue('', 0)).toBe(false);
  });

  it('нечислове не зводиться до тексту числа', () => {
    // ⛔ `String(0) === String('0')` — пастка, у яку впало б текстове
    // порівняння: `'н/д'` і `0` мусять лишитися різними.
    expect(sameCellValue('н/д', 0)).toBe(false);
    expect(sameCellValue(true, 1)).toBe(false);
    expect(sameCellValue('так', 'так')).toBe(true);
  });
});

describe('coerce: Decimal їде РЯДКОМ, Int лишається числом', () => {
  it('ручний ввід доживає до значення правки з усіма знаками', () => {
    // ⛔ Мутаційна межа: тут стояв `parseNumber(raw)`, тобто `Number`, і він
    // віддавав `1234.1234567890124` — див. замір вище.
    expect(coerce(Twenty, 'Decimal')).toBe(Twenty);
    expect(coerce(Twenty, 'Decimal')).not.toBe(TwentyThroughDouble);
  });

  it('кома й розрядні пробіли вводу нормалізуються, як і доти', () => {
    expect(coerce('12,5', 'Decimal')).toBe('12.5');
    expect(coerce('1 234,56', 'Decimal')).toBe('1234.56');
  });

  it('експонента з буфера Excel розгортається, а не їде рядком на відмову', () => {
    // ⛔ `normalizeDecimal` (`shared/format/decimal.ts`) експоненту свідомо
    // відхиляє — і має рацію щодо ДРОТУ. Але Excel кладе її в буфер щодня, а
    // `roundToScale` її розгортає, тож розбір вводу лишається тут
    // (`decimalTextOf`). Без цього `1.5e-7` поїхав би на сервер текстом і
    // дістав `ECR-CELL-0422` там, де доти проходив.
    expect(coerce('1.5e-7', 'Decimal')).toBe('0.00000015');
    expect(coerce('1e3', 'Decimal')).toBe('1000');
  });

  it('Int лишається числом — на дроті це інший контракт', () => {
    expect(coerce('42', 'Int')).toBe(42);
  });

  it('нерозпізнане число лишається текстом, порожнє — null', () => {
    expect(coerce('н/д', 'Decimal')).toBe('н/д');
    expect(coerce('', 'Decimal')).toBeNull();

    // ⚠ Ворота лишилися ті самі, що й доти (`parseNumber`): значення поза
    // діапазоном `double` не стає 400-значним рядком, а їде текстом і дістає
    // `ECR-CELL-0422` — так само, як до переходу на рядок.
    expect(coerce('1e400', 'Decimal')).toBe('1e400');
  });

  it('канон `decimalTextOf` і канон дроту не розходяться', () => {
    // ⛔ Два розбори існують навмисно (один знає кому й експоненту, другий —
    // ні), але КАНОН у них зобов'язаний бути один. Це твердження, а не
    // домовленість: розходження дало б комірку, яка «змінилася» одразу після
    // збереження.
    for (const text of ['5.0000000000', '012.3400', '-0.000', '-2.50', Twenty]) {
      expect(decimalTextOf(text)).toBe(normalizeDecimal(text));
    }
  });
});

describe('cellText — буфер обміну й діалоги', () => {
  it('Ctrl+C віддає число без хвостових нулів', () => {
    // ⛔ `String('5.0000000000')` поклав би в кожну комірку аркуша Excel
    // десять зайвих нулів.
    expect(cellText('5.0000000000')).toBe('5');
    expect(cellText('1234.5')).toBe('1234.5');
  });

  it('але НЕ форматує локаллю — Excel прочитав би «1 234,5» як текст', () => {
    expect(cellText('1234.5')).not.toContain(',');
    expect(cellText('1234.5')).not.toContain(' ');
  });

  it('порожнє — порожньо, нечислове — як є', () => {
    expect(cellText(null)).toBe('');
    expect(cellText(undefined)).toBe('');
    expect(cellText('н/д')).toBe('н/д');
  });
});

describe('roundToScale віддає РЯДОК, і він же їде на сервер', () => {
  it('округлений результат не переживає double — і саме тому не перетворюється', () => {
    const fixed = roundToScale('1234.12345678901234567', column({ scale: 16 }));

    expect(fixed).toBe('1234.1234567890123457');

    // ⛔ Саме цей `Number(...)` стояв на межі відправлення до 2026-09-21: він
    // давав `1234.1234567890124`, тобто інше число, ніж показане оператору.
    expect(String(Number(fixed))).toBe(TwentyThroughDouble);
  });
});

describe('Д: Lookup-комірка й далі читається — і числом, і рядком', () => {
  /*
   * ⛔ Єдине `typeof value === 'number'` у `features/grid/**`, що дивиться на
   * ЗНАЧЕННЯ комірки, — `LookupCellEditor.currentEntryId`. Воно лишилося як є,
   * і це висновок, а не недогляд: `Lookup`-комірка несе `ValueRegistryEntryId`
   * — `int`, а не `decimal`, тож конвертер `e470777a` її не чіпає. Твердження
   * нижче фіксує обидві гілки, щоб «прибирання зайвого» не зламало ту, яка
   * зараз і працює.
   */
  const entries: RegistryEntryDto[] = [
    { id: 3, code: 'A', display: 'Вугілля', parentEntryId: null, validFrom: null, validTo: null },
  ];

  it('число (як приходить із сервера) і рядок дають той самий показ', () => {
    expect(lookupCellDisplay(3, entries)).toBe('Вугілля');
    expect(lookupCellDisplay('3', entries)).toBe('Вугілля');
  });

  it('невідомий запис показується ідентифікатором, а не порожнечею', () => {
    expect(lookupCellDisplay(99, entries)).toBe('99');
    expect(lookupCellDisplay(null, entries)).toBe('');
  });
});
