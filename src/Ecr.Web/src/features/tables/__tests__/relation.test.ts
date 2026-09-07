import { describe, it, expect } from 'vitest';
import type { TableRelationDto, TemplateStructureDto } from '@/api/types';
import {
  draftOf,
  emptyDraft,
  relationBody,
  tableOptions,
  whyCannotSave,
} from '@/features/tables/relation';

/**
 * Правила чернетки зв'язку між таблицями (`ФВ-2.12`).
 *
 * ⚠ Модуль без React і без мережі: перевіряється те, що форма ВІДПРАВИТЬ, і
 * те, чого вона не відправить. Рендер тієї самої логіки коштував би секунд і
 * доводив би те саме.
 */
describe('Чернетка зв’язку між таблицями', () => {
  it('порожня чернетка не зберігається без коду', () => {
    expect(whyCannotSave(emptyDraft())).toBe('Code');
  });

  it('таблиця не може бути пов’язана сама із собою', () => {
    const draft = {
      ...emptyDraft(),
      code: 'SelfLink',
      sourceTableDefId: 5,
      targetTableDefId: 5,
      matchJson: '{"by":"RowKey"}',
    };

    // ⛔ Те саме, що `CK_Rel_NotSelf` у схемі: без цієї відповіді користувач
    // діставав би не відмову форми, а помилку бази.
    expect(whyCannotSave(draft)).toBe('Self');
  });

  it('зв’язок без зіставлення рядків не зберігається', () => {
    const draft = {
      ...emptyDraft(),
      code: 'Rollup7',
      sourceTableDefId: 5,
      targetTableDefId: 6,
      matchJson: '   ',
    };

    // ⚠ Найтихіша з можливих порожнеч: зв'язок виглядає налаштованим і не
    // з'єднує жодного рядка.
    expect(whyCannotSave(draft)).toBe('Match');
  });

  it('повна чернетка зберігається', () => {
    const draft = {
      ...emptyDraft(),
      code: 'Rollup7',
      sourceTableDefId: 5,
      targetTableDefId: 6,
      matchJson: '{"by":"RowKey"}',
    };

    expect(whyCannotSave(draft)).toBeNull();
  });

  it('порожній mapJson їде як null, а не як порожній рядок', () => {
    const body = relationBody({
      ...emptyDraft(),
      code: 'Rollup7',
      sourceTableDefId: 5,
      targetTableDefId: 6,
      matchJson: ' {"by":"RowKey"} ',
      mapJson: '   ',
    });

    // «Перенесення немає» і «перенесення описане порожнім рядком» — різні
    // стани, і другий не має сенсу.
    expect(body.mapJson).toBeNull();
    expect(body.matchJson).toBe('{"by":"RowKey"}');
  });

  it('чернетка з наявного зв’язку не дозволяє перейменувати код', () => {
    const relation: TableRelationDto = {
      id: 1,
      code: 'Rollup7',
      relationKind: 'Rollup',
      sourceTableDefId: 5,
      sourceTableCode: 'Main',
      targetTableDefId: 6,
      targetTableCode: 'Consolidation',
      matchJson: '{"by":"RowKey"}',
      mapJson: null,
      onSourceChange: 0,
      isActive: true,
      isEditable: true,
    };

    expect(draftOf(relation).isNew).toBe(false);
    expect(draftOf(relation).mapJson).toBe('');
  });

  it('таблиці для вибору беруться зі структури і несуть код аркуша', () => {
    const structure = {
      templateVersionId: 7,
      presentationRevision: 0,
      sheets: [
        {
          id: 1,
          code: 'Water',
          nameL10n: { values: {} },
          ordinal: 1,
          tables: [
            {
              id: 5,
              code: 'Main',
              layoutKind: 'Matrix',
              rowMode: 'Fixed',
              maxDynamicRows: null,
              columns: [],
              rows: [],
            },
          ],
        },
      ],
    } as unknown as TemplateStructureDto;

    // ⚠ Коди таблиць унікальні лише в межах аркуша: два `Main` у списку без
    // коду аркуша не розрізнити.
    expect(tableOptions(structure)).toEqual([{ id: 5, label: 'Water · Main' }]);
  });
});
