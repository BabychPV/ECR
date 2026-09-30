import { describe, it, expect } from 'vitest';
import {
  columnDraftOf,
  columnBody,
  columnTakesUnit,
  EditableColumnDataTypes,
  EditableDataTypes,
  emptyColumnDraft,
  whyCannotSaveColumn,
  type ColumnDefDto,
} from '../column';
import { emptyStyleDraft } from '../style';

/**
 * Дефект, знайдений живим переглядом: `ColumnEditor.tsx` пропонував лише
 * `EditableDataTypes` — масив без `Formula`/`Calculated`. Тип колонки
 * незмінний після створення (`disabled={!draft.isNew}`), а прикріпити
 * формулу до колонки, заведеної іншим типом, сервер відхиляє
 * (`err.ECR-TMPL-4227.formulaOnManualColumn`) — колонку типу Formula чи
 * Calculated через форму завести було НЕМОЖЛИВО.
 *
 * ⛔ Мутаційний доказ: якби `EditableColumnDataTypes` знову дорівнював
 * `EditableDataTypes` (стара поведінка), перший `expect` нижче був би
 * червоним — `'Formula'` і `'Calculated'` не входили б у перелік.
 *
 * ⚠ `EditableDataTypes` (без змін) далі використовує `HeaderFieldEditor.tsx`
 * — поле шапки документа обчислюваних типів не підтримує взагалі
 * (`HeaderFieldDef` відхиляє їх конструктором, `ECR-TMPL-0422`). Другий тест
 * — запобіжник: якби хтось додав `Formula`/`Calculated` у СПІЛЬНИЙ масив
 * замість нового, форма поля шапки почала б пропонувати вибір, який сервер
 * ніколи не прийме.
 */
describe('EditableColumnDataTypes / EditableDataTypes: Formula й Calculated лише для колонки', () => {
  it('форма колонки пропонує Formula і Calculated', () => {
    expect(EditableColumnDataTypes).toContain('Formula');
    expect(EditableColumnDataTypes).toContain('Calculated');
  });

  it('форма поля шапки документа їх НЕ пропонує — сервер відхиляє обидва типи для HeaderFieldDef', () => {
    expect(EditableDataTypes).not.toContain('Formula');
    expect(EditableDataTypes).not.toContain('Calculated');
  });

  it('EditableColumnDataTypes несе решту типів EditableDataTypes без втрат', () => {
    for (const type of EditableDataTypes) {
      expect(EditableColumnDataTypes).toContain(type);
    }
  });
});

/**
 * Той самий дефект, що й `table.test.ts` (Q-336, `table.ts`): форма колонки
 * казала «дай коду код» навіть ПІСЛЯ введення коду, якщо той містив
 * недопустимі символи (`[`, `]`, `-`) — `CodeEmpty` і `CodeInvalid` були
 * ОДНИМ значенням блокувальника (`'Code'`).
 *
 * ⛔ Мутаційний доказ: повернення `whyCannotSaveColumn` для порожнього коду і
 * для недопустимого коду мають РІЗНИТИСЯ. Злиття їх назад в одне значення (як
 * було до фіксу) зробить перший `expect` нижче червоним — обидва виклики
 * повернуть те саме.
 */
describe('whyCannotSaveColumn (ColumnDraft)', () => {
  it('порожній код і недопустимий код — РІЗНІ причини', () => {
    const empty = whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '' });
    const invalid = whyCannotSaveColumn({ ...emptyColumnDraft(1), code: 'ab-[cd]' });

    expect(empty).toBe('CodeEmpty');
    expect(invalid).toBe('CodeInvalid');
    expect(empty).not.toBe(invalid);
  });

  it('код лише з пробілів — теж «порожній», а не «недопустимий»', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '   ' })).toBe('CodeEmpty');
  });

  it('код, що починається з цифри — недопустимий', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: '1abc' })).toBe('CodeInvalid');
  });

  it('чинний код і порожній заголовок — блокує заголовок, а не код', () => {
    expect(whyCannotSaveColumn({ ...emptyColumnDraft(1), code: 'VALID_CODE' })).toBe('Header');
  });

  it('чинний код і заголовок — нічого не блокує', () => {
    expect(
      whyCannotSaveColumn({
        ...emptyColumnDraft(1),
        code: 'VALID_CODE',
        headerL10n: { en: 'Column' },
      }),
    ).toBeNull();
  });

  // ⛔ Директива registry-lookup / cell-style, PR B1: «власний стиль» —
  // ДРУГА причина, за якою колонку не можна зберегти, поверх решти форми.
  // Без цієї гілки недійсний код стилю пройшов би повз клієнтську валідацію
  // і впав би сирою відмовою сервера на ОКРЕМОМУ запиті (`PUT …/styles/…`,
  // `columnApi.ts`), який форма шле РАНІШЕ за сам запис колонки.
  it('увімкнено власний стиль з порожнім кодом — блокує StyleCode', () => {
    expect(
      whyCannotSaveColumn({
        ...emptyColumnDraft(1),
        code: 'VALID_CODE',
        headerL10n: { en: 'Column' },
        style: emptyStyleDraft(''),
      }),
    ).toBe('StyleCode');
  });

  it('увімкнено власний стиль з чинним кодом — не блокує', () => {
    expect(
      whyCannotSaveColumn({
        ...emptyColumnDraft(1),
        code: 'VALID_CODE',
        headerL10n: { en: 'Column' },
        style: emptyStyleDraft('BoldStyle'),
      }),
    ).toBeNull();
  });
});

/**
 * X-02 (четвертий раунд UX, critical): повторне збереження колонки стирало
 * одиницю, точність, довідник, фільтр, значення за замовчуванням і стиль —
 * чернетку наявної колонки будував бідний `TemplateColumnDto` структури
 * (`unitId: null`, `precision: null`…), а `PUT` — заміна цілком.
 *
 * ⛔ Тепер чернетка береться з повної відповіді `GET …/columns/{code}`, і тіло
 * `PUT` без правок у формі — рівно те, що сервер уже зберігає. Мутаційний
 * доказ: повернути в `columnBody` `lookupFilter: null` (як доти) або скинути
 * `unitId` у `columnDraftOf` — і тест нижче червоніє на відповідному полі.
 */
describe('columnDraftOf → columnBody: збереження без правок нічого не стирає (X-02)', () => {
  const full: ColumnDefDto = {
    id: 1,
    code: 'LIMIT',
    headerL10n: { values: { en: 'Limit', ru: 'Лимит', kz: 'Шек' } },
    ordinal: 3,
    dataType: 'Decimal',
    isRequired: true,
    isReadOnly: false,
    isHidden: false,
    precision: 18,
    scale: 4,
    defaultValue: '0',
    displayFormat: 'N2',
    styleId: 42,
    lookupRegistryDefId: null,
    lookupFilter: null,
    unitId: 12,
  };

  it('кожне розширене поле їде назад незмінним', () => {
    const body = columnBody(columnDraftOf(full));

    expect(body).toMatchObject({
      headerL10n: { en: 'Limit', ru: 'Лимит', kz: 'Шек' },
      ordinal: 3,
      dataType: 'Decimal',
      isRequired: true,
      precision: 18,
      scale: 4,
      defaultValue: '0',
      displayFormat: 'N2',
      styleId: 42,
      unitId: 12,
    });
  });

  it('довідник і його фільтр колонки Lookup теж не губляться', () => {
    const body = columnBody(
      columnDraftOf({
        ...full,
        dataType: 'Lookup',
        precision: null,
        scale: null,
        unitId: null,
        lookupRegistryDefId: 5,
        lookupFilter: 'kind=gas',
      }),
    );

    expect(body.lookupRegistryDefId).toBe(5);
    expect(body.lookupFilter).toBe('kind=gas');
  });

  it('чернетка наявної колонки — не нова: код і тип не редагуються', () => {
    expect(columnDraftOf(full).isNew).toBe(false);
  });
});

/**
 * R-07: одиниця колонки — лише для числових типів; `Unit` несе одиницю на
 * рядок, і сервер відхиляє одиницю колонки такого типу.
 */
describe('columnTakesUnit (R-07)', () => {
  it('числові типи приймають одиницю колонки', () => {
    for (const type of ['Decimal', 'Int', 'Formula', 'Calculated'] as const) {
      expect(columnTakesUnit(type)).toBe(true);
    }
  });

  it('Unit, текст, дата, довідник — ні', () => {
    for (const type of ['Unit', 'String', 'Date', 'Lookup', 'Bool'] as const) {
      expect(columnTakesUnit(type)).toBe(false);
    }
  });

  it('тип без одиниці не везе її в PUT', () => {
    expect(columnBody({ ...emptyColumnDraft(0), dataType: 'String', unitId: 7 }).unitId).toBeNull();
  });
});
