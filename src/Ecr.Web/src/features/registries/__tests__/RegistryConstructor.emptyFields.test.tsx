import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryFields } from '@/features/registries/RegistryConstructor';
import { emptyField } from '@/features/registries/definition';
import { testTheme } from '@/test/render';

/**
 * Довідник без полів показує це словами, а не голими заголовками таблиці.
 *
 * Мутаційні докази: прибрати рядок порожнього стану — перший тест червоніє;
 * прибрати умову `newFields.length === 0` — порожній стан стоїть поруч із
 * щойно доданим полем, другий тест червоніє.
 */
afterEach(cleanup);

const Empty = { id: 1, code: 'R', fields: [], rules: [], mappings: [] } as unknown as RegistryDefinitionDto;

function show(newFields: ReturnType<typeof emptyField>[]): void {
  render(
    <MantineProvider theme={testTheme}>
      <RegistryFields
        definition={Empty}
        canEdit
        newFields={newFields}
        registryOptions={[]}
        onAddField={() => undefined}
        onChangeField={() => undefined}
        onRemoveField={() => undefined}
      />
    </MantineProvider>,
  );
}

describe('RegistryFields: порожній стан', () => {
  it('без полів — текст «полів немає» у тілі таблиці', () => {
    show([]);

    const cell = screen.getByText('⟦registries.noFields⟧').closest('td');
    expect(cell?.getAttribute('colspan')).toBe('7');
  });

  it('щойно додане поле прибирає порожній стан', () => {
    show([{ ...emptyField(), code: 'Owner' }]);

    expect(screen.queryByText('⟦registries.noFields⟧')).toBeNull();
  });
});
