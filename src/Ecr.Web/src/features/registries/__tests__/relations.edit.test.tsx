import type { JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryRelations } from '@/features/registries/RegistryConstructor';
import { buildSaveRequest, draftLinkEdits } from '@/features/registries/definition';

/**
 * Редагування зв'язків полів у конструкторі (`ФВ-8.12`, порція 1).
 *
 * Мутаційні докази: без `linkEdits` у `buildSaveRequest` червоні тести запиту; без перевірки
 * `canEdit` у `RegistryRelations` червоний тест «лише читання».
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
      id: 41, code: 'Number', nameL10n: { values: { en: 'N' } }, dataType: 'String', isRequired: true,
      isScopeField: true, lookupRegistryDefId: null, unitId: null,
    },
    {
      id: 42, code: 'Substance', nameL10n: { values: { en: 'S' } }, dataType: 'Lookup', isRequired: false,
      isScopeField: false, lookupRegistryDefId: 5, unitId: null,
    },
  ],
  relations: [
    {
      kind: 'Cascade', fieldCode: 'Substance', targetRegistryDefId: 5, targetRegistryCode: 'SUBSTANCE',
      linkKind: null, linkCount: null,
    },
  ],
  rules: [],
  mappings: [],
};

const Options = [
  { value: '5', label: 'Substances (SUBSTANCE)' },
  { value: '6', label: 'Sites (SITE)' },
];

function show(node: JSX.Element): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

describe('Зв\'язки полів: редагування', () => {
  it('зміна цілі повідомляє поле і нову ціль; зняти ціль («—») не можна', () => {
    const onChange = vi.fn();
    show(
      <RegistryRelations definition={Definition} canEdit registryOptions={Options} onChangeLink={onChange} />,
    );

    const select = screen.getByRole('combobox');

    // ⛔ ent6 R1: порожнього варіанта немає — поле Lookup без цілі сервер не приймає.
    expect(Array.from(select.querySelectorAll('option')).map((o) => o.getAttribute('value'))).toEqual(['5', '6']);

    fireEvent.change(select, { target: { value: '6' } });
    fireEvent.change(select, { target: { value: '' } });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith(42, 6);
  });

  it('без права на опис — лише читання', () => {
    show(<RegistryRelations definition={Definition} canEdit={false} registryOptions={Options} onChangeLink={vi.fn()} />);

    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.getByText('SUBSTANCE')).toBeDefined();
  });

  it('правка зв\'язку потрапляє в запит збереження, без правки — ціль не змінюється', () => {
    const edited = buildSaveRequest(Definition, [], [], 'зв\'язок', 'en', { 42: 6 });
    const unlinked = buildSaveRequest(Definition, [], [], 'зв\'язок', 'en', { 42: null });
    const plain = buildSaveRequest(Definition, [], [], 'зв\'язок', 'en');

    expect(edited.fields.find((f) => f.id === 42)?.lookupRegistryDefId).toBe(6);
    expect(unlinked.fields.find((f) => f.id === 42)?.lookupRegistryDefId).toBeNull();
    expect(plain.fields.find((f) => f.id === 42)?.lookupRegistryDefId).toBe(5);
  });

  it('чернетка відновлює лише відмінні від опублікованого зв\'язки', () => {
    const draft = buildSaveRequest(Definition, [], [], 'зв\'язок', 'en', { 42: 6 });

    expect(draftLinkEdits(draft.fields, Definition)).toEqual({ 42: 6 });
    expect(draftLinkEdits(buildSaveRequest(Definition, [], [], 'x', 'en').fields, Definition)).toEqual({});
  });
});