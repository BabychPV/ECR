import { describe, expect, it } from 'vitest';
import { groupRuleViolations } from '../groupRuleViolations';
import type { TemplateStructureDto } from '@/api/types';

/**
 * Директива "live-попередження про порушення SheetGroupRule": до цього
 * `CreateDocumentModal.tsx` не мав жодної клієнтської перевірки складу за
 * `SheetGroupRule`, і порушення виявлялося лише після відхиленого
 * `POST /documents`. `groupRuleViolations` — та сама логіка, що серверний
 * `DocumentStore.ValidateCompositionAsync`, продубльована на клієнті для
 * live-попередження ДО збереження (сервер лишається останньою лінією правди).
 *
 * ⚠ Чиста функція — рендер `CreateDocumentModal` у jsdom іде хвилинами через
 * Mantine `Select` (`create-document-modal.render.test.tsx`, Q-274/Q-275).
 * Логіка попередження перевіряється тут окремо від рендеру: швидко і без
 * жодної залежності від DOM чи мережі. Ключі `t()` без завантаженого
 * каталогу повертають позначений фолбек `⟦ключ (parm=val, …)⟧`
 * (`shared/i18n/index.ts`), який усе одно несе передані параметри — тому
 * перевірки нижче не залежать від завантаження перекладів.
 */
describe('groupRuleViolations', () => {
  function structure(
    sheets: { id: number; sheetGroup: string | null }[],
    groupRules: { sheetGroup: string; ruleKind: number; targetGroup: string | null }[],
  ): Pick<TemplateStructureDto, 'sheets' | 'groupRules'> {
    return {
      sheets: sheets.map((s) => ({
        id: s.id,
        code: `S${s.id}`,
        nameL10n: { values: { en: `Sheet ${s.id}` } },
        ordinal: s.id,
        sheetGroup: s.sheetGroup,
        isMandatory: false,
        isVisible: true,
        tables: [],
      })) as unknown as TemplateStructureDto['sheets'],
      groupRules: groupRules as unknown as TemplateStructureDto['groupRules'],
    };
  }

  it('без структури (ще не завантажена) не повертає жодного попередження', () => {
    expect(groupRuleViolations(undefined, [1, 2])).toEqual([]);
  });

  it('RequiresAll: часткова вибірка групи дає попередження', () => {
    const s = structure(
      [
        { id: 1, sheetGroup: 'Water' },
        { id: 2, sheetGroup: 'Water' },
      ],
      [{ sheetGroup: 'Water', ruleKind: 0, targetGroup: null }],
    );

    const messages = groupRuleViolations(s, [1]);

    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain('Water');
  });

  it('RequiresAll: вся група обрана — жодного попередження', () => {
    const s = structure(
      [
        { id: 1, sheetGroup: 'Water' },
        { id: 2, sheetGroup: 'Water' },
      ],
      [{ sheetGroup: 'Water', ruleKind: 0, targetGroup: null }],
    );

    expect(groupRuleViolations(s, [1, 2])).toEqual([]);
  });

  it('RequiresAll: жодного аркуша групи не обрано — жодного попередження (як на сервері)', () => {
    // ⚠ Той самий інваріант, що `DocumentStore.ValidateCompositionAsync`:
    // `picked > 0 && picked < inGroup.Count` — нуль вибраних не порушує
    // RequiresAll, бо групу можна не чіпати взагалі.
    const s = structure(
      [
        { id: 1, sheetGroup: 'Water' },
        { id: 2, sheetGroup: 'Water' },
      ],
      [{ sheetGroup: 'Water', ruleKind: 0, targetGroup: null }],
    );

    expect(groupRuleViolations(s, [])).toEqual([]);
  });

  it('RequiresOne: жодного аркуша групи не обрано дає попередження', () => {
    const s = structure(
      [
        { id: 1, sheetGroup: 'Air' },
        { id: 2, sheetGroup: 'Air' },
      ],
      [{ sheetGroup: 'Air', ruleKind: 1, targetGroup: null }],
    );

    const messages = groupRuleViolations(s, []);

    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain('Air');
  });

  it('RequiresOne: один аркуш групи обрано — жодного попередження', () => {
    const s = structure(
      [
        { id: 1, sheetGroup: 'Air' },
        { id: 2, sheetGroup: 'Air' },
      ],
      [{ sheetGroup: 'Air', ruleKind: 1, targetGroup: null }],
    );

    expect(groupRuleViolations(s, [1])).toEqual([]);
  });

  it('Excludes (ruleKind 2) ігнорується — сервер його теж не перевіряє', () => {
    // ⛔ Мутаційний доказ навпаки: якби функція перевіряла Excludes так само,
    // як RequiresOne/RequiresAll, цей тест впав би. Сервер
    // (`DocumentStore.ValidateCompositionAsync`) не має гілки для
    // `RuleKind == 2`, і клієнт навмисно дзеркалить рівно це, а не більше.
    const s = structure(
      [
        { id: 1, sheetGroup: 'Water' },
        { id: 2, sheetGroup: 'Air' },
      ],
      [{ sheetGroup: 'Water', ruleKind: 2, targetGroup: 'Air' }],
    );

    expect(groupRuleViolations(s, [1, 2])).toEqual([]);
  });

  it('групи без жодного присутнього в структурі аркуша ігноруються', () => {
    const s = structure(
      [{ id: 1, sheetGroup: 'Water' }],
      [{ sheetGroup: 'Ghost', ruleKind: 0, targetGroup: null }],
    );

    expect(groupRuleViolations(s, [1])).toEqual([]);
  });

  it('кілька правил одночасно дають кілька повідомлень', () => {
    const s = structure(
      [
        { id: 1, sheetGroup: 'Water' },
        { id: 2, sheetGroup: 'Water' },
        { id: 3, sheetGroup: 'Air' },
      ],
      [
        { sheetGroup: 'Water', ruleKind: 0, targetGroup: null },
        { sheetGroup: 'Air', ruleKind: 1, targetGroup: null },
      ],
    );

    const messages = groupRuleViolations(s, [1]);

    expect(messages).toHaveLength(2);
  });
});
