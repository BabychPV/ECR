import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DiffFieldLabel } from '@/features/methodologies/DiffFieldLabel';
import { testTheme } from '@/test/render';

/**
 * Назви полів порівняння версій методології.
 *
 * ⚠ Перелік нижче — копія `MethodologyDiffFields.All` для перевірки розмітки;
 * повноту проти сервера й сіду стереже `MethodologyDiffFieldCatalogTests`
 * (Architecture), а не цей файл.
 */
const Fields = [
  'expression',
  'resultType',
  'outputUnitId',
  'argumentsCsv',
  'kind',
  'value',
  'textValue',
  'unitId',
  'validTo',
  'source',
  'inputJson',
  'expectedJson',
  'tolerance',
] as const;

function labelOf(field: string): HTMLElement {
  const { container } = render(
    <MantineProvider theme={testTheme}>
      <span data-testid="host">
        <DiffFieldLabel field={field} />
      </span>
    </MantineProvider>,
  );

  return container.querySelector('[data-testid="host"]') as HTMLElement;
}

describe('DiffFieldLabel', () => {
  it.each(Fields)('%s — назва з каталогу methodologyDiffField.<поле>', (field) => {
    const host = labelOf(field);

    expect(host.textContent).toBe(`⟦methodologyDiffField.${field}⟧`);
    expect(host.querySelector('code')).toBeNull();
  });

  it('невідоме поле — сире значення в Code, без вигаданої назви', () => {
    const host = labelOf('futureField');

    expect(host.querySelector('code')?.textContent).toBe('futureField');
    expect(host.textContent).not.toContain('⟦');
  });
});
