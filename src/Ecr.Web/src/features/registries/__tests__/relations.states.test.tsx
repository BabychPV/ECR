import type { JSX } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryRelations } from '@/features/registries/RegistryConstructor';

/**
 * Вкладка «Зв'язки» конструктора (`ФВ-8.4`, `ФВ-8.12`) — стани й доступні імена.
 *
 * Мутаційні докази (лише локально): без опції поточної цілі в `RegistryRelations` — поки перелік
 * довідників не прочитався, вибір показує «—» («зв'язку немає»); без перевірки `Composition` —
 * незмінний зв'язок отримує вибір.
 */
const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 1,
  fields: [
    {
      id: 42, code: 'Substance', nameL10n: { values: { en: 'S' } }, dataType: 'Lookup', isRequired: false,
      isScopeField: false, lookupRegistryDefId: 5, unitId: null,
    },
    {
      id: 43, code: 'Lines', nameL10n: { values: { en: 'L' } }, dataType: 'Lookup', isRequired: false,
      isScopeField: false, lookupRegistryDefId: 8, unitId: null,
    },
  ],
  relations: [
    {
      kind: 'Cascade', fieldCode: 'Substance', targetRegistryDefId: 5, targetRegistryCode: 'SUBSTANCE',
      linkKind: null, linkCount: 12,
    },
    {
      kind: 'Composition', fieldCode: 'Lines', targetRegistryDefId: 8, targetRegistryCode: 'PERMIT_LINE',
      linkKind: null, linkCount: null,
    },
  ],
  rules: [],
  mappings: [],
};

function show(node: JSX.Element): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

describe('Зв\'язки: стани', () => {
  it('зв\'язків немає — заголовок і пояснення, без порожньої таблиці', () => {
    show(<RegistryRelations definition={{ ...Definition, relations: [] }} canEdit onChangeLink={vi.fn()} />);

    expect(screen.getByRole('heading', { name: /registries\.tabRelations/ })).toBeDefined();
    expect(screen.getByText(/registries\.noRelations/)).toBeDefined();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('перелік довідників ще не прочитався — вибір показує поточну ціль, а не «—»', () => {
    show(<RegistryRelations definition={Definition} canEdit registryOptions={[]} onChangeLink={vi.fn()} />);

    const select = screen.getByRole('combobox', { name: /registries\.relationTargetFor/ }) as HTMLSelectElement;
    expect(select.value).toBe('5');
    expect(select.selectedOptions[0]?.textContent).toBe('SUBSTANCE');
  });

  it('правка на довідник поза переліком лишається видимою за номером', () => {
    show(
      <RegistryRelations
        definition={Definition}
        canEdit
        registryOptions={[{ value: '5', label: 'Substances (SUBSTANCE)' }]}
        linkEdits={{ 42: 9 }}
        onChangeLink={vi.fn()}
      />,
    );

    const select = screen.getByRole('combobox', { name: /registries\.relationTargetFor/ }) as HTMLSelectElement;
    expect(select.value).toBe('9');
    expect(select.selectedOptions[0]?.textContent).toBe('#9');
  });

  it('вибір — лише для посилання, з ім\'ям поля; композиція незмінна й читається текстом', () => {
    show(<RegistryRelations definition={Definition} canEdit registryOptions={[]} onChangeLink={vi.fn()} />);

    const selects = screen.getAllByRole('combobox');
    expect(selects).toHaveLength(1);
    expect(selects[0]?.getAttribute('aria-label')).toContain('Substance');
    expect(screen.getByText('PERMIT_LINE')).toBeDefined();
    expect(screen.getByText('12')).toBeDefined();
  });
});
