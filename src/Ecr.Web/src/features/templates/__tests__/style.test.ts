import { describe, expect, it } from 'vitest';
import { emptyStyleDraft, styleBody, styleDraftOf, whyCannotSaveStyle, type StyleDefDto } from '../style';

/**
 * Директива `docs/build/directive-registry-lookup-and-cell-style.md`,
 * Частина B, PR B1: чернетка стилю — тіло запиту (кольори з `#rrggbb` в
 * ARGB) і зворотне читання (`styleDraftOf`) для наявного стилю.
 */

function dto(overrides: Partial<StyleDefDto> = {}): StyleDefDto {
  return {
    id: 1,
    code: 'S1',
    fontName: null,
    fontSize: null,
    isBold: false,
    isItalic: false,
    foregroundArgb: null,
    backgroundArgb: null,
    borderJson: null,
    horizontalAlign: null,
    verticalAlign: null,
    wrapText: false,
    numberFormat: null,
    ...overrides,
  };
}

describe('whyCannotSaveStyle', () => {
  it('порожній код — CodeEmpty', () => {
    expect(whyCannotSaveStyle(emptyStyleDraft(''))).toBe('CodeEmpty');
  });

  it('недопустимий код — CodeInvalid', () => {
    expect(whyCannotSaveStyle(emptyStyleDraft('1abc'))).toBe('CodeInvalid');
  });

  it('чинний код — не блокує', () => {
    expect(whyCannotSaveStyle(emptyStyleDraft('BoldStyle'))).toBeNull();
  });
});

describe('styleBody: кольори #rrggbb → ARGB', () => {
  it('чорний текст на білому фоні', () => {
    const body = styleBody({ ...emptyStyleDraft('S1'), foregroundHex: '#000000', backgroundHex: '#ffffff' });

    // 0xFF000000 (непрозорий чорний) — знакове 32-бітне ціле, те саме, що
    // C# `unchecked((int)0xFF000000)`.
    expect(body.foregroundArgb).toBe(0xff000000 | 0);
    expect(body.backgroundArgb).toBe((0xff000000 | 0xffffff) | 0);
  });

  it('порожній колір — null, а не 0', () => {
    const body = styleBody(emptyStyleDraft('S1'));

    expect(body.foregroundArgb).toBeNull();
    expect(body.backgroundArgb).toBeNull();
  });
});

describe('styleBody: рамка', () => {
  it('усі сторони 0 — borderJson: null (нема сенсу нести порожній об\'єкт)', () => {
    expect(styleBody(emptyStyleDraft('S1')).borderJson).toBeNull();
  });

  it('хоч одна сторона ненульова — JSON з усіма чотирма ключами', () => {
    const body = styleBody({ ...emptyStyleDraft('S1'), borderBottom: 2 });

    expect(JSON.parse(body.borderJson ?? '{}')).toEqual({ top: 0, right: 0, bottom: 2, left: 0 });
  });
});

describe('styleBody: порожні текстові поля стають null', () => {
  it('порожнє ім\'я шрифту і формат числа', () => {
    const body = styleBody({ ...emptyStyleDraft('S1'), fontName: '   ', numberFormat: '' });

    expect(body.fontName).toBeNull();
    expect(body.numberFormat).toBeNull();
  });
});

describe('styleDraftOf: зворотне читання наявного стилю', () => {
  it('ARGB → #rrggbb, кругообіг зберігає колір', () => {
    const draft = styleDraftOf(dto({ foregroundArgb: (0xff112233 | 0) }));

    expect(draft.foregroundHex).toBe('#112233');
  });

  it('borderJson розбирається назад у чотири сторони', () => {
    const draft = styleDraftOf(dto({ borderJson: '{"top":1,"right":2,"bottom":3,"left":0}' }));

    expect(draft.borderTop).toBe(1);
    expect(draft.borderRight).toBe(2);
    expect(draft.borderBottom).toBe(3);
    expect(draft.borderLeft).toBe(0);
  });

  it('зіпсований borderJson — усі сторони 0, а не помилка', () => {
    const draft = styleDraftOf(dto({ borderJson: '{not json' }));

    expect(draft.borderTop).toBe(0);
    expect(draft.borderRight).toBe(0);
    expect(draft.borderBottom).toBe(0);
    expect(draft.borderLeft).toBe(0);
  });

  it('кругообіг тіла запиту зберігає значення (upsert-редагування наявного стилю)', () => {
    const original = dto({
      code: 'Header1',
      isBold: true,
      fontSize: '12',
      foregroundArgb: (0xff445566 | 0),
      horizontalAlign: 2,
    });

    const roundTripped = styleBody(styleDraftOf(original));

    expect(roundTripped.isBold).toBe(true);

    // ⛔ `'12'`, а не `12`: кругообіг має зберегти те, що прийшло, БЕЗ
    // перетворення на число — `decimal` контракту їде рядком (`e470777a`), і
    // `Number` по дорозі був би точкою втрати знаків.
    expect(roundTripped.fontSize).toBe('12');
    expect(roundTripped.foregroundArgb).toBe(0xff445566 | 0);
    expect(roundTripped.horizontalAlign).toBe(2);
  });
});
