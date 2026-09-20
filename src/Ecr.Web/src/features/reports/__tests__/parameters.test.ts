import { describe, expect, it } from 'vitest';
import {
  defaultDraft,
  missingRequired,
  parseDay,
  readReportParameters,
  toParametersBody,
  type ParameterDraft,
  type ReportParameterDeclaration,
} from '@/features/reports/parameters';

/**
 * Розбір оголошень параметрів звіту (`R6`).
 *
 * ⛔ Головна вісь — ТРИ стани, а не два. «Параметрів немає» і «прочитати не
 * вдалося» виглядають однаково (порожня форма), але означають протилежне:
 * перше дозволяє побудову, друге її забороняє. Саме тому кожне твердження тут
 * перевіряє `kind`, а не лише довжину переліку: `items: []` з правильним
 * `kind` — це знання, а не брак знання.
 */

function declared(json: string): readonly ReportParameterDeclaration[] {
  const result = readReportParameters(json);

  if (result.kind !== 'declared') throw new Error(`очікували declared, отримали ${result.kind}`);

  return result.items;
}

describe('readReportParameters: три стани', () => {
  it('схема 1 — поля parameters немає взагалі, і це «параметрів немає»', () => {
    const result = readReportParameters('{"rowSource":"CalculationResults"}');

    expect(result.kind).toBe('declared');
    expect(result.kind === 'declared' ? result.items : null).toEqual([]);
  });

  it('parameters: null — те саме «параметрів немає»', () => {
    expect(readReportParameters('{"rowSource":"X","parameters":null}').kind).toBe('declared');
  });

  it('порожній перелік — «параметрів немає», а не «не знаємо»', () => {
    expect(readReportParameters('{"parameters":[]}').kind).toBe('declared');
  });

  it('невалідний JSON — «прочитати не вдалося», а НЕ «параметрів немає»', () => {
    // ⛔ Саме та підміна, заради якої цей модуль існує: `catch → []` тут дав би
    // `declared`, і побудова пішла б наосліп.
    expect(readReportParameters('{"parameters":[').kind).toBe('unreadable');
    expect(readReportParameters('не json зовсім').kind).toBe('unreadable');
  });

  it('рядка немає зовсім (версію не знайшли) — «прочитати не вдалося»', () => {
    expect(readReportParameters(undefined).kind).toBe('unreadable');
    expect(readReportParameters(null).kind).toBe('unreadable');
    expect(readReportParameters('   ').kind).toBe('unreadable');
  });

  it('parameters не масив — «прочитати не вдалося»', () => {
    expect(readReportParameters('{"parameters":{"Year":"Number"}}').kind).toBe('unreadable');
  });

  it('невідомий тип або елемент без імені роблять нечитабельним УВЕСЬ перелік', () => {
    // ⚠ Не «пропустити поганий елемент»: форма без поля, яке сервер вимагає,
    // мовчки відправила б побудову без параметра.
    expect(readReportParameters('{"parameters":[{"code":"Y","type":"Money"}]}').kind).toBe(
      'unreadable',
    );
    expect(
      readReportParameters('{"parameters":[{"code":"Y","type":"Number"},{"type":"Text"}]}').kind,
    ).toBe('unreadable');
  });

  it('тип читається без урахування регістру — як і на сервері', () => {
    const items = declared('{"parameters":[{"code":"Y","type":"nUmBeR","required":true}]}');

    expect(items).toEqual([{ code: 'Y', type: 'Number', required: true, defaultValue: undefined }]);
  });

  it('масив верхнього рівня читається як «параметрів немає» — так само, як на сервері', () => {
    // ⚠ Це рішення, а не недогляд: `ReportRules.Parse` ловить `JsonException`
    // і бере правила за замовчуванням, тож сервер такий опис будує без питань.
    expect(readReportParameters('[]').kind).toBe('declared');
  });
});

const items: readonly ReportParameterDeclaration[] = declared(
  JSON.stringify({
    parameters: [
      { code: 'Year', type: 'Number', required: true },
      { code: 'Site', type: 'Text', required: false, default: 'ALL' },
      { code: 'Draft', type: 'Boolean', required: false },
      { code: 'Since', type: 'Date', required: true, default: '2026-03-01' },
    ],
  }),
);

describe('чернетка значень і те, що з неї виходить', () => {
  it('замовчування підставлені, а дата стала МІСЦЕВОЮ датою того ж дня', () => {
    const draft = defaultDraft(items);

    expect(draft['Site']).toBe('ALL');
    expect(draft['Year']).toBe('');
    // ⚠ Тумблер не має стану «не обрано»: це справжнє значення, а не порожнеча.
    expect(draft['Draft']).toBe(false);

    const since = draft['Since'];

    expect(since instanceof Date).toBe(true);
    expect(since instanceof Date ? since.getFullYear() : 0).toBe(2026);
    expect(since instanceof Date ? since.getMonth() : -1).toBe(2);
    expect(since instanceof Date ? since.getDate() : 0).toBe(1);
  });

  it('обов\'язковий без значення й без замовчування блокує — решта ні', () => {
    expect(missingRequired(items, defaultDraft(items))).toEqual(['Year']);

    // Обов'язковий із замовчуванням — законна пара: сервер підставить сам.
    expect(missingRequired(items, { ...defaultDraft(items), Year: 2026 })).toEqual([]);
  });

  it('очищене поле з замовчуванням не блокує, а очищене без нього — блокує', () => {
    const draft: ParameterDraft = { ...defaultDraft(items), Year: 2026, Since: null };

    expect(missingRequired(items, draft)).toEqual([]);
    expect(missingRequired(items, { ...draft, Year: '' })).toEqual(['Year']);
  });

  it('тіло запиту несе значення ПОТРІБНИХ типів, а дату — рядком того ж дня', () => {
    const body = toParametersBody(items, { ...defaultDraft(items), Year: 2026, Draft: true });

    expect(body).toEqual({ Year: 2026, Site: 'ALL', Draft: true, Since: '2026-03-01' });
    expect(typeof body?.['Year']).toBe('number');
    expect(typeof body?.['Draft']).toBe('boolean');
  });

  it('незаповнене не надсилається зовсім — інакше стерло б замовчування сервера', () => {
    const body = toParametersBody(items, { ...defaultDraft(items), Year: 2026, Site: '  ' });

    expect(body).toBeDefined();
    expect(body !== undefined && 'Site' in body).toBe(false);
  });

  it('параметрів немає — поля parameters в тілі немає зовсім', () => {
    // ⚠ `undefined`, а не `{}`: запит до звіту без параметрів має лишитися
    // побайтно таким, яким був до R6.
    expect(toParametersBody([], {})).toBeUndefined();
  });
});

describe('parseDay: дата з замовчування', () => {
  it('читає YYYY-MM-DD як місцеву дату, а не як опівніч UTC', () => {
    const value = parseDay('2026-03-01');

    // ⛔ `new Date('2026-03-01')` дає опівніч UTC, і на захід від Гринвіча
    // `getDate()` повернув би 28 лютого — тобто запит поїхав би іншим днем.
    expect(value?.getDate()).toBe(1);
    expect(value?.getMonth()).toBe(2);
  });

  it('неіснуючий день — null, а не мовчки наступний місяць', () => {
    // `new Date(2026, 1, 31)` — це третє березня, і без перевірки складниками
    // форма повернула б інший день, ніж прийшов.
    expect(parseDay('2026-02-31')).toBeNull();
    expect(parseDay('позавчора')).toBeNull();
    expect(parseDay(5)).toBeNull();
  });
});
