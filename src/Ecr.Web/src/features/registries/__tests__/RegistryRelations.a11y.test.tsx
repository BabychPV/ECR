import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryRelations } from '@/features/registries/RegistryConstructor';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Вкладка «Зв'язки» конструктора довідника з правкою цілі посилання (`ФВ-8.4`, `ФВ-8.12`) — axe без
 * блокуючих порушень в обох темах (`ФВ-14.16`).
 *
 * ⚠ `RegistryConstructor.a11y.test.tsx` сканує поля й правила, а маршрут `/admin/registries` — без
 * відкритого конструктора: вибір цілі посилання в таблиці зв'язків не бачив жоден прогін. Тут обидва
 * різновиди рядка: незмінний (композиція — текстом) і редагований (`NativeSelect` у комірці).
 *
 * ⛔ Мутаційний доказ (локально, 2026-10-02): прибрати `aria-label` у `NativeSelect` цілі → червоний
 * `critical · select-name` в обох темах — у комірці таблиці видимого підпису немає, ім'я дає лише він.
 */
afterEach(cleanup);

const Definition = {
  id: 1,
  code: 'FUEL',
  fields: [
    { id: 10, code: 'Site', isScopeField: false },
    { id: 11, code: 'Parent', isScopeField: false },
  ],
  rules: [],
  mappings: [],
  relations: [
    { kind: 'Reference', fieldCode: 'Site', linkKind: null, targetRegistryCode: 'SITE', targetRegistryDefId: 5, linkCount: 12 },
    { kind: 'Hierarchy', fieldCode: 'Parent', linkKind: null, targetRegistryCode: 'FUEL', targetRegistryDefId: 1, linkCount: 3 },
    { kind: 'Composition', fieldCode: null, linkKind: 'Parts', targetRegistryCode: null, targetRegistryDefId: null, linkCount: null },
  ],
} as unknown as RegistryDefinitionDto;

describe('Зв\'язки довідника — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: редаговані цілі посилань і незмінна композиція', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <RegistryRelations
          definition={Definition}
          canEdit
          registryOptions={[
            { value: '5', label: 'Sites (SITE)' },
            { value: '1', label: 'Fuel types (FUEL)' },
          ]}
          linkEdits={{ 10: null }}
          onChangeLink={() => undefined}
        />
      </Shell>,
    );

    expect(container.querySelectorAll('select')).toHaveLength(2);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
