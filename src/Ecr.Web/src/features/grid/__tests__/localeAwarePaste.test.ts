import { afterEach, describe, expect, it } from 'vitest';
import type { ColumnDto } from '@/api/types';
import { formatDecimal, normalizeDecimal } from '@/shared/format';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { cellText } from '../cellValue';
import { parseNumber, planPaste, readNumber } from '../clipboard';
import { parseClipboard, toClipboard } from '../tsvClipboard';
import { decimalTextOf, roundToScale } from '../rounding';

/**
 * U1 (enterprise-аудит, High): «єдина кома» завжди вважалася десятковою.
 * Англійський інтерфейс сам показує `4,242`, тож `1,234` з en-US Excel лягало
 * в `Decimal` як `1.234` — у тисячу разів менше, і сервер це приймав.
 *
 * Очікування нижче виписані руками з правил `readNumber` (`clipboard.ts`), а не
 * зняті з виводу реалізації.
 *
 * Мутації (перевірено вручну у власному worktree, 2026-09-28):
 *   • повернути «кома завжди десяткова» (`,` → `.`, якщо крапки немає) —
 *     червоніють en-блоки: `1,234.5`, `1,234,567`, неоднозначне `1,234`;
 *   • прибрати відмову для неоднозначного (гілка `ambiguous` → `number`) —
 *     червоніють «відхиляє неоднозначне» і `planPaste` в en.
 */

const Nbsp = ' ';

afterEach(() => {
  setLanguage(DefaultLanguage);
});

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'volume',
    header: 'Volume',
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
    precision: 18,
    scale: 2,
    ...overrides,
  };
}

describe('readNumber / parseNumber: en (кома — розряди, крапка — дріб)', () => {
  it('однозначні записи', () => {
    expect(parseNumber('1,234.5', 'en')).toBe(1234.5);
    expect(parseNumber('1234.5', 'en')).toBe(1234.5);
    expect(parseNumber('1,234,567', 'en')).toBe(1234567);
    expect(parseNumber('-1,234.5', 'en')).toBe(-1234.5);
    // Кома, яка НЕ може бути розрядом (не три цифри після неї), — лише дріб.
    expect(parseNumber('12,5', 'en')).toBe(12.5);
    expect(parseNumber('1234,567', 'en')).toBe(1234.567);
    // Крапка в en — її десятковий роздільник, навіть з трьома цифрами.
    expect(parseNumber('1.234', 'en')).toBe(1.234);
  });

  it('відхиляє неоднозначне `1,234`: не число, а обидва прочитання', () => {
    expect(parseNumber('1,234', 'en')).toBeNull();
    expect(readNumber('1,234', 'en')).toEqual({ kind: 'ambiguous', asGroup: '1234', asDecimal: '1.234' });
    expect(readNumber('-12,500', 'en')).toEqual({ kind: 'ambiguous', asGroup: '-12500', asDecimal: '-12.500' });
  });

  it('биті записи — не числа', () => {
    for (const raw of ['1,23,4', '1.2,3', '1,234.5.6', '1.', '1,', ',5', 'н/д', '']) {
      expect(parseNumber(raw, 'en'), raw).toBeNull();
    }
  });

  it('T2-10: `.5` — число, а пробіл усередині — лише розряди тисяч', () => {
    expect(parseNumber('.5', 'en')).toBe(0.5);
    expect(parseNumber('-.5', 'en')).toBe(-0.5);
    expect(parseNumber('1 234', 'en')).toBe(1234);
    // `1 2` мовчки ставав 12 — тепер не число (як і на сервері).
    for (const raw of ['1 2', '12 3', '1  234', '1 2345', '- 5']) {
      expect(parseNumber(raw, 'en'), raw).toBeNull();
    }
  });
});

describe('readNumber / parseNumber: uk (пробіл — розряди, кома — дріб)', () => {
  it('однозначні записи', () => {
    expect(parseNumber('1 234,5', 'uk')).toBe(1234.5);
    expect(parseNumber(`1${Nbsp}234,5`, 'uk')).toBe(1234.5);
    expect(parseNumber('1234,5', 'uk')).toBe(1234.5);
    expect(parseNumber('1234.5', 'uk')).toBe(1234.5);
    // Кома — десятковий роздільник uk, тож три цифри після неї — дріб.
    expect(parseNumber('1,234', 'uk')).toBe(1.234);
    // Крапка в uk не є розрядами — інваріантний запис (так копіює сітка).
    expect(parseNumber('1.234', 'uk')).toBe(1.234);
  });

  it('запис Excel з обома роздільниками читається структурно', () => {
    expect(parseNumber('1,234.5', 'uk')).toBe(1234.5);
    expect(parseNumber('1.234,5', 'uk')).toBe(1234.5);
  });
});

describe('локаль за замовчуванням — мова інтерфейсу (`formatLocale`, як у сітки)', () => {
  it('те саме `1,234` читається за активною мовою', () => {
    setLanguage('uk');
    expect(parseNumber('1,234')).toBe(1.234);
    expect(decimalTextOf('1,234')).toBe('1.234');

    setLanguage('en');
    expect(parseNumber('1,234')).toBeNull();
    expect(decimalTextOf('1,234')).toBeNull();
    expect(decimalTextOf('1,234.5')).toBe('1234.5');
  });
});

describe('rounding: той самий розбір, що й parseNumber', () => {
  it('en: розряди комою округлюються як тисячі, неоднозначне — не число', () => {
    expect(roundToScale('1,234.5678', column({ scale: 2 }), 'en')).toBe('1234.57');
    expect(roundToScale('1,234', column({ scale: 2 }), 'en')).toBeNull();
    expect(decimalTextOf('1,234,567', 'en')).toBe('1234567');
  });

  it('uk: кома — дріб', () => {
    expect(roundToScale('1,2345', column({ scale: 2 }), 'uk')).toBe('1.23');
    expect(decimalTextOf('1 234,5', 'uk')).toBe('1234.5');
    expect(decimalTextOf('1,234', 'uk')).toBe('1.234');
  });
});

describe('planPaste: неоднозначне число відхиляється з поясненням, значення не записане', () => {
  const anchor = { rowIndex: 0, columnIndex: 0 };

  it('en: `1,234` відхиляє весь батч; причина називає значення', () => {
    const plan = planPaste(parseClipboard('1,234\t5\n'), ['R1'], ['C1', 'C2'], anchor, () => null, undefined, 'en');

    expect(plan.targets).toEqual([]);
    expect(plan.rejected).toHaveLength(1);
    expect(plan.rejected[0]?.columnCode).toBe('C1');
    expect(plan.rejected[0]?.reason).toContain('1,234');
    expect(plan.rejected[0]?.reason).toContain('grid.pasteAmbiguousNumber');
  });

  it('uk: той самий буфер вставляється, і значення — 1.234', () => {
    const plan = planPaste(parseClipboard('1,234\t5\n'), ['R1'], ['C1', 'C2'], anchor, () => null, undefined, 'uk');

    expect(plan.rejected).toEqual([]);
    expect(plan.targets.map((target) => target.value)).toEqual(['1,234', '5']);
    expect(decimalTextOf(plan.targets[0]?.value ?? '', 'uk')).toBe('1.234');
  });

  it('en: однозначні записи вставляються', () => {
    const plan = planPaste(parseClipboard('1,234.5\t1234.5\n'), ['R1'], ['C1', 'C2'], anchor, () => null, undefined, 'en');

    expect(plan.rejected).toEqual([]);
    expect(plan.targets.map((target) => decimalTextOf(target.value, 'en'))).toEqual(['1234.5', '1234.5']);
  });

  it('нечислова колонка неоднозначність не перевіряє', () => {
    const plan = planPaste(parseClipboard('1,234\n'), ['R1'], ['T1'], anchor, () => null, () => false, 'en');

    expect(plan.rejected).toEqual([]);
    expect(plan.targets[0]?.value).toBe('1,234');
  });

  it('заборона комірки має пріоритет над неоднозначністю', () => {
    const plan = planPaste(parseClipboard('1,234\n'), ['R1'], ['C1'], anchor, () => 'Комірку рахує система.', undefined, 'en');

    expect(plan.rejected[0]?.reason).toBe('Комірку рахує система.');
  });
});

describe('round-trip у en: копія з сітки → вставка назад дає те саме число', () => {
  const values = ['4242', '1234.5', '1.234', '-0.001', '1234567.25', '5.0000000000', '1234.1234567890123456'];

  it('Ctrl+C (`cellText`) → Ctrl+V', () => {
    const text = toClipboard([values.map((value) => cellText(value))]);
    const codes = values.map((_, index) => `C${index}`);

    const plan = planPaste(parseClipboard(text), ['R1'], codes, { rowIndex: 0, columnIndex: 0 }, () => null, undefined, 'en');

    expect(plan.rejected).toEqual([]);
    expect(plan.targets.map((target) => decimalTextOf(target.value, 'en'))).toEqual(
      values.map((value) => normalizeDecimal(value)),
    );
  });

  it('те, що сітка ПОКАЗУЄ в en (`formatDecimal`), читається назад тим самим числом', () => {
    // ⚠ Лише значення з дробом: ціле `4242` показується як `4,242`, а це
    // рівно неоднозначний запис — його відхиляє вставка (тест вище), і це
    // правильно: з самого тексту `4,242` число не відновити.
    for (const value of ['1234.5', '1234567.25', '1.234', '-9876.54321']) {
      const shown = formatDecimal(value, 'en');
      expect(shown, value).not.toBeNull();
      expect(decimalTextOf(shown ?? '', 'en'), shown ?? '').toBe(normalizeDecimal(value));
    }

    expect(formatDecimal('4242', 'en')).toBe('4,242');
    expect(readNumber('4,242', 'en').kind).toBe('ambiguous');
  });
});
