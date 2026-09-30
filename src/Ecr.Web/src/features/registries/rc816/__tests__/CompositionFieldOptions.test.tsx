import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { emptyField, type FieldDraft } from '../../definition';
import { draftNewFields } from '../../registryDraft';
import { CompositionFieldOptions } from '../CompositionFieldOptions';
import { testTheme } from '@/test/render';

/**
 * Перемикач «Частина батька» нового поля конструктора (`ФВ-8.16`).
 */

const Definition = { id: 7, isTemporal: false, relations: [] };

function show(draft: FieldDraft, onChange = vi.fn(), definition: typeof Definition = Definition): typeof onChange {
  render(
    <MantineProvider theme={testTheme}>
      <CompositionFieldOptions definition={definition} draft={draft} otherNew={[draft]} canEdit onChange={onChange} />
    </MantineProvider>,
  );
  return onChange;
}

const Lookup: FieldDraft = { ...emptyField('Lookup'), code: 'CASE', name: 'Case', lookupRegistryDefId: 6 };

describe('CompositionFieldOptions', () => {
  it('позначка робить поле композицією з типовою політикою Restrict', () => {
    const onChange = show(Lookup);
    fireEvent.click(screen.getByRole('checkbox', { name: /registries\.rc816\.partOfParent/ }));

    expect(onChange).toHaveBeenCalledWith({ ...Lookup, relationKind: 'Composition', onParentDelete: 'Restrict' });
  });

  it('політику видалення батька можна змінити на Cascade', () => {
    const onChange = show({ ...Lookup, relationKind: 'Composition', onParentDelete: 'Restrict' });
    fireEvent.change(screen.getByRole('combobox', { name: /registries\.rc816\.onParentDelete/ }), {
      target: { value: 'Cascade' },
    });

    expect(onChange).toHaveBeenCalledWith({ ...Lookup, relationKind: 'Composition', onParentDelete: 'Cascade' });
  });

  it('темпоральний довідник — попередження до збереження', () => {
    show({ ...Lookup, relationKind: 'Composition' }, vi.fn(), { ...Definition, isTemporal: true });

    expect(screen.getByRole('alert').textContent).toContain('registries.rc816.issueTemporal');
  });

  it('поле не-Lookup перемикача не має', () => {
    show({ ...emptyField('String'), code: 'X', name: 'X' });
    expect(screen.queryByRole('checkbox')).toBeNull();
  });
});

describe('draftNewFields', () => {
  it('чернетка сервера повертає композицію нового поля у форму', () => {
    const [restored] = draftNewFields(
      {
        baseDefinitionVersion: 1,
        fields: [
          {
            id: null,
            code: 'CASE',
            dataType: 'Lookup',
            isKey: false,
            isRequired: true,
            lookupRegistryDefId: 6,
            nameL10n: { values: { en: 'Case' } },
            ordinal: 1,
            unitId: null,
            relationKind: 'Composition',
            onParentDelete: 'Cascade',
          },
        ],
        reason: 'склад',
        rowVersion: 'r1',
        rules: [],
        savedAt: '2026-09-30T10:00:00Z',
        savedByUserId: 9,
      } as unknown as Parameters<typeof draftNewFields>[0],
      'en',
    );

    expect(restored).toMatchObject({ code: 'CASE', relationKind: 'Composition', onParentDelete: 'Cascade' });
  });
});
