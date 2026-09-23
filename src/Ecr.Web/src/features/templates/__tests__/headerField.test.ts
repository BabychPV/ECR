import { describe, expect, it } from 'vitest';
import {
  emptyHeaderFieldDraft,
  headerFieldBody,
  headerFieldDraftOf,
  whyCannotSaveHeaderField,
  type HeaderFieldDraft,
} from '../headerField';

/**
 * Поле шапки документа версії шаблону (`W5.2`-подібний зріз, рівень усього
 * документа, не таблиці) — за зразком `column.test.ts`-подібних наборів для
 * `ColumnDraft`. Три твердження нижче — саме ті, що явно вимагав контракт
 * задачі як мутаційний доказ: валідація обов'язковості/типу коду й підпису,
 * і те, що `lookupRegistryDefId` живе виключно в тілі запиту типу `Lookup`.
 */

const validDraft: HeaderFieldDraft = {
  code: 'Contractor',
  labelL10n: { en: 'Contractor' },
  ordinal: 0,
  dataType: 'String',
  isRequired: false,
  lookupRegistryDefId: null,
  isNew: true,
};

describe('whyCannotSaveHeaderField', () => {
  it('порожній код блокує збереження', () => {
    expect(whyCannotSaveHeaderField({ ...validDraft, code: '' })).toBe('CodeEmpty');
    expect(whyCannotSaveHeaderField({ ...validDraft, code: '   ' })).toBe('CodeEmpty');
  });

  it('код із недопустимими символами блокує збереження окремою причиною від порожнього', () => {
    expect(whyCannotSaveHeaderField({ ...validDraft, code: '1Area' })).toBe('CodeInvalid');
    expect(whyCannotSaveHeaderField({ ...validDraft, code: 'Area Code' })).toBe('CodeInvalid');
    expect(whyCannotSaveHeaderField({ ...validDraft, code: 'Area-Code' })).toBe('CodeInvalid');
  });

  it('код без жодного тексту підпису в жодній мові блокує збереження', () => {
    expect(whyCannotSaveHeaderField({ ...validDraft, labelL10n: {} })).toBe('Label');
    expect(whyCannotSaveHeaderField({ ...validDraft, labelL10n: { en: '   ' } })).toBe('Label');
  });

  it('тип Lookup без обраного довідника блокує збереження', () => {
    expect(
      whyCannotSaveHeaderField({ ...validDraft, dataType: 'Lookup', lookupRegistryDefId: null }),
    ).toBe('LookupRequired');
  });

  it('тип Lookup з обраним довідником — не блокує', () => {
    expect(
      whyCannotSaveHeaderField({ ...validDraft, dataType: 'Lookup', lookupRegistryDefId: 7 }),
    ).toBeNull();
  });

  it('коректна чернетка не блокує збереження', () => {
    expect(whyCannotSaveHeaderField(validDraft)).toBeNull();
  });
});

describe('headerFieldBody: lookupRegistryDefId лише для типу Lookup', () => {
  it('для НЕ-Lookup типу тіло запиту несе null, навіть якщо в чернетці лишилось стале значення', () => {
    // ⚠ Саме цей сценарій — перемкнули тип з Lookup на String, форма ще не
    // встигла (чи не мусить) обнулити вибір довідника в самій чернетці.
    const stale: HeaderFieldDraft = { ...validDraft, dataType: 'String', lookupRegistryDefId: 42 };

    expect(headerFieldBody(stale).lookupRegistryDefId).toBeNull();
  });

  it('для типу Lookup тіло запиту несе обраний довідник', () => {
    const draft: HeaderFieldDraft = { ...validDraft, dataType: 'Lookup', lookupRegistryDefId: 42 };

    expect(headerFieldBody(draft).lookupRegistryDefId).toBe(42);
  });

  it('решта полів тіла відповідає чернетці', () => {
    const body = headerFieldBody(validDraft);

    expect(body).toEqual({
      labelL10n: { en: 'Contractor' },
      ordinal: 0,
      dataType: 'String',
      isRequired: false,
      lookupRegistryDefId: null,
    });
  });
});

describe('emptyHeaderFieldDraft / headerFieldDraftOf', () => {
  it('порожня чернетка — нова, без довідника, порядок як переданий (null — «стане останнім»)', () => {
    const draft = emptyHeaderFieldDraft(null);

    expect(draft.isNew).toBe(true);
    expect(draft.ordinal).toBeNull();
    expect(draft.lookupRegistryDefId).toBeNull();
    expect(draft.dataType).toBe('String');
  });

  it('чернетка з наявного поля відтворює всі поля відповіді сервера', () => {
    const draft = headerFieldDraftOf({
      id: 9,
      code: 'Region',
      labelL10n: { values: { en: 'Region' } },
      ordinal: 3,
      dataType: 'Lookup',
      isRequired: true,
      lookupRegistryDefId: 5,
    });

    expect(draft).toEqual({
      code: 'Region',
      labelL10n: { en: 'Region' },
      ordinal: 3,
      dataType: 'Lookup',
      isRequired: true,
      lookupRegistryDefId: 5,
      isNew: false,
    });
  });
});
