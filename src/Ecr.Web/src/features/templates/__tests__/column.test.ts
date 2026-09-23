import { describe, it, expect } from 'vitest';
import type { TemplateColumnDto } from '@/api/types';
import {
  columnDraftOf,
  columnBody,
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
 * Живий перегляд (Етап 3, лана "Documents core" — прогін уже готового PR
 * B1, не окрема лана) знайшов реальну шорсткість: `TemplateColumnDto`
 * (`GET …/structure`) НЕ несе `styleId` — той самий клас обмеження, що вже
 * мали `precision`/`lookup`/`unit` (`D-137`, `hasFullData`). Відкриття
 * форми колонки БЕЗ кешу цього сеансу (прямий перехід на сторінку версії)
 * тому завжди бачить чернетку без стилю, і збереження такої форми стерло б
 * наявний стиль колонки мовчки — якби не попередження `hasFullData`, яке
 * вже покриває цей самий клас дефекту для інших полів.
 */
describe('columnDraftOf: styleId — той самий клас "неповних даних", що precision/lookup', () => {
  const templateColumn: TemplateColumnDto = {
    id: 1,
    code: 'C1',
    headerL10n: { values: { en: 'Column 1' } },
    dataType: 'Decimal',
    ordinal: 0,
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    displayFormat: null,
    unitSymbol: null,
    formulaExpression: null,
    formulaDialect: null,
  };

  const fullColumn: ColumnDefDto = {
    id: 1,
    code: 'C1',
    headerL10n: { values: { en: 'Column 1' } },
    ordinal: 0,
    dataType: 'Decimal',
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: null,
    scale: null,
    defaultValue: null,
    displayFormat: null,
    styleId: 42,
    lookupRegistryDefId: null,
    lookupFilter: null,
    unitId: null,
  };

  it('без кешу сеансу (лише TemplateColumnDto) — styleId завжди null, hasFullData: false', () => {
    const draft = columnDraftOf(templateColumn);

    expect(draft.styleId).toBeNull();
    expect(draft.hasFullData).toBe(false);
  });

  it('з кешем сеансу (ColumnDefDto щойно збереженого PUT) — styleId зберігається', () => {
    const draft = columnDraftOf(templateColumn, fullColumn);

    expect(draft.styleId).toBe(42);
    expect(draft.hasFullData).toBe(true);
  });

  it('мутаційний доказ: збереження чернетки БЕЗ кешу шле styleId: null — саме тому попередження hasFullData обов\'язкове', () => {
    const draft = columnDraftOf(templateColumn);

    expect(columnBody(draft).styleId).toBeNull();
  });
});
