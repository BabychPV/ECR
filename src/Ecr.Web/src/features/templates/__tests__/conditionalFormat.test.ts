import { describe, expect, it } from 'vitest';
import {
  emptyRule,
  firstMatchingRule,
  ruleFromWire,
  ruleMatches,
  ruleToWire,
  whyRuleIncomplete,
  type ConditionalRule,
} from '../conditionalFormat';

/**
 * Умовне форматування (`ФВ-2.7`): коли правило спрацьовує і яке з кількох.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): `>` замість `>=` у `ge`
 * — червоніє межа; у `between` прибрати `value <= upper` — червоніє «вище
 * межі»; у `firstMatchingRule` прибрати перевірку `whyRuleIncomplete` —
 * червоніє «неповне правило»; `eq` без числового порівняння — червоніє
 * «1.0 = 1». Перевірено 2026-09-30 (`CONDFMT:client`): прибрати перевірку
 * кольору — червоніє «умови ті самі, що на сервері»; прибрати межу 64 — теж;
 * слати `valueTo` поза `between` — червоніє «операнд, якого оператор не бере».
 */
const rule = (patch: Partial<ConditionalRule>): ConditionalRule => ({
  ...emptyRule('Q'),
  backgroundHex: '#ff0000',
  ...patch,
});

describe('ruleMatches', () => {
  it('порівняння чисел, включно з межами', () => {
    expect(ruleMatches(rule({ operator: 'gt', value: '10' }), '10')).toBe(false);
    expect(ruleMatches(rule({ operator: 'ge', value: '10' }), '10')).toBe(true);
    expect(ruleMatches(rule({ operator: 'lt', value: '0' }), '-0.5')).toBe(true);
    expect(ruleMatches(rule({ operator: 'le', value: '0' }), '0.1')).toBe(false);
  });

  it('між — обидві межі включно, поза ними ні', () => {
    const between = rule({ operator: 'between', value: '1', valueTo: '5' });
    expect(ruleMatches(between, '1')).toBe(true);
    expect(ruleMatches(between, '5')).toBe(true);
    expect(ruleMatches(between, '5.01')).toBe(false);
    expect(ruleMatches(between, '0.99')).toBe(false);
  });

  it('дорівнює / не дорівнює: лише числа, як на сервері', () => {
    expect(ruleMatches(rule({ operator: 'eq', value: '1' }), '1.0')).toBe(true);
    // ⛔ Текст не число — ні `eq`, ні `ne` (2026-10-01: `ne 100` фарбував «abc»).
    expect(ruleMatches(rule({ operator: 'ne', value: '100' }), 'abc')).toBe(false);
    expect(ruleMatches(rule({ operator: 'eq', value: 'Так' }), 'Так')).toBe(false);
    expect(ruleMatches(rule({ operator: 'ne', value: 'Так' }), 'Ні')).toBe(false);
  });

  it('порожня комірка: лише «порожньо» спрацьовує', () => {
    expect(ruleMatches(rule({ operator: 'empty' }), null)).toBe(true);
    expect(ruleMatches(rule({ operator: 'empty' }), '  ')).toBe(true);
    expect(ruleMatches(rule({ operator: 'notEmpty' }), null)).toBe(false);
    expect(ruleMatches(rule({ operator: 'lt', value: '1' }), null)).toBe(false);
  });

  it('текст на числовому порівнянні не спрацьовує', () => {
    expect(ruleMatches(rule({ operator: 'gt', value: '1' }), 'abc')).toBe(false);
  });
});

describe('whyRuleIncomplete', () => {
  it('називає, чого бракує', () => {
    expect(whyRuleIncomplete(rule({ columnCode: '' }))).toBe('Column');
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: 'abc' }))).toBe('Value');
    expect(whyRuleIncomplete(rule({ operator: 'between', value: '1', valueTo: '' }))).toBe('ValueTo');
    expect(whyRuleIncomplete(rule({ operator: 'between', value: '5', valueTo: '1' }))).toBe('Range');
    expect(whyRuleIncomplete(rule({ operator: 'empty', backgroundHex: '' }))).toBe('Style');
    expect(whyRuleIncomplete(rule({ operator: 'eq', value: '1' }))).toBeNull();
  });

  it('умови ті самі, що на сервері: «дорівнює» — теж число, колір — #rrggbb, операнд ≤ 64', () => {
    // ⛔ Сервер відхиляє текстовий операнд і для eq/ne (`condFormatOperand`).
    expect(whyRuleIncomplete(rule({ operator: 'eq', value: 'Так' }))).toBe('Value');
    expect(whyRuleIncomplete(rule({ operator: 'ne', value: 'Так' }))).toBe('Value');
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '1'.repeat(65) }))).toBe('Value');
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: `0.${'1'.repeat(62)}` }))).toBeNull();
    // ⛔ 64 знаки, але поза `decimal` — сервер не розбере (`decimal.TryParse`).
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '1'.repeat(64) }))).toBe('Value');
    // Кома — як крапка, як на сервері.
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '5,5' }))).toBeNull();
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '1', backgroundHex: '#fff' }))).toBe('Color');
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '1', foregroundHex: 'red' }))).toBe('Color');
    expect(whyRuleIncomplete(rule({ operator: 'gt', value: '1', foregroundHex: '#00AAff' }))).toBeNull();
  });
});

describe('firstMatchingRule', () => {
  const red = rule({ operator: 'gt', value: '100', backgroundHex: '#ff0000' });
  const yellow = rule({ operator: 'gt', value: '50', backgroundHex: '#ffff00' });

  it('перше за порядком перемагає', () => {
    expect(firstMatchingRule([red, yellow], 'Q', '150')).toBe(red);
    expect(firstMatchingRule([red, yellow], 'Q', '70')).toBe(yellow);
    expect(firstMatchingRule([red, yellow], 'Q', '10')).toBeNull();
  });

  it('лише своя колонка', () => {
    expect(firstMatchingRule([red], 'OTHER', '150')).toBeNull();
  });

  it('неповне правило не застосовується, навіть якщо умова збігається', () => {
    const noStyle = rule({ operator: 'gt', value: '0', backgroundHex: '' });
    expect(firstMatchingRule([noStyle, yellow], 'Q', '70')).toBe(yellow);
  });
});

describe('ruleFromWire / ruleToWire', () => {
  it('null з сервера — порожньо на клієнті, і назад', () => {
    const wire = {
      columnCode: 'Q',
      operator: 'between',
      value: '1',
      valueTo: '5',
      backgroundHex: null,
      foregroundHex: '#112233',
      isBold: true,
    };

    const local = ruleFromWire(wire);
    expect(local).toEqual({
      columnCode: 'Q',
      operator: 'between',
      value: '1',
      valueTo: '5',
      backgroundHex: '',
      foregroundHex: '#112233',
      isBold: true,
    });
    expect(ruleToWire(local!)).toEqual(wire);
  });

  it('невідомий оператор не вгадується', () => {
    expect(ruleFromWire({ columnCode: 'Q', operator: 'contains', isBold: false })).toBeNull();
  });

  it('операнд, якого оператор не бере, не шлеться', () => {
    // Людина перемкнула «між» на «більше» — `valueTo` лишився в стані, але не на екрані.
    const wire = ruleToWire(rule({ operator: 'gt', value: ' 10 ', valueTo: '20' }));
    expect(wire.value).toBe('10');
    expect(wire.valueTo).toBeNull();
    expect(ruleToWire(rule({ operator: 'empty', value: '3' })).value).toBeNull();
  });
});
