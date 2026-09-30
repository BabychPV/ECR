import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryFields, RegistryRules } from '@/features/registries/RegistryConstructor';
import { emptyField, emptyRule } from '@/features/registries/definition';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Конструктор довідника (`ФВ-8.12`, `ФВ-8.16`) із НОВИМИ полями й правилами — axe без блокуючих порушень в
 * обох темах (`ФВ-14.16`).
 *
 * ⚠ Маршрут `/admin/registries` у наборі маршрутів сканується без відкритого конструктора, тобто без
 * редагованих рядків — а саме в них інпути, чекбокс ключа й кнопки «Прибрати».
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `aria-label` у чекбокса «Ключове» → червоний
 * `critical · label` в обох темах.
 */
afterEach(cleanup);

const Definition = { id: 1, code: 'R', fields: [], rules: [], mappings: [] } as unknown as RegistryDefinitionDto;

describe('Конструктор довідника — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: нові поля (зокрема Lookup) і нове правило', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <RegistryFields
          definition={Definition}
          canEdit
          newFields={[{ ...emptyField(), code: 'Owner' }, { ...emptyField('Lookup'), code: 'Site' }]}
          registryOptions={[{ value: '5', label: 'Sites (SITE)' }]}
          onAddField={() => undefined}
          onChangeField={() => undefined}
          onRemoveField={() => undefined}
        />
        <RegistryRules rules={[emptyRule('Expression')]} canEdit onChange={() => undefined} onAdd={() => undefined} />
      </Shell>,
    );

    expect(container.querySelectorAll('[data-focus-row]')).toHaveLength(3);

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
