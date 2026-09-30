import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import { RegistryEntryEditor } from '@/features/registries/RegistryEntryEditor';
import { testTheme } from '@/test/render';

/**
 * Обов'язкове поле запису довідника — обов'язкове й для читача екрана (WCAG 1.3.1, 3.3.2).
 *
 * ⛔ Раніше ознакою була лише « *» у тексті підпису: читач озвучував «зірочка», а поле для нього не було
 * обов'язковим. Мутаційний доказ (перевірено руками 2026-09-30): прибрати `required={field.isRequired}` →
 * червоний.
 */
const Registry = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [
    { id: 1, code: 'LIMIT', dataType: 'Decimal', isRequired: true, isScopeField: false, lookupRegistryDefId: null, nameL10n: { values: { en: 'Limit' } }, unitId: null },
    { id: 2, code: 'NOTE', dataType: 'String', isRequired: false, isScopeField: false, lookupRegistryDefId: null, nameL10n: { values: { en: 'Note' } }, unitId: null },
  ],
} as RegistryDefDto;

describe('RegistryEntryEditor: обов’язкові поля', () => {
  it('обов’язкове поле має required, необов’язкове — ні; зірочка не входить у доступне ім’я', () => {
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient()}>
          <RegistryEntryEditor registry={Registry} entry={null} opened onClose={() => undefined} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    // ⚠ Точне ім'я: зірочку Mantine малює з `aria-hidden`, а колишня « *» у тексті підпису входила в ім'я.
    const limit = screen.getByRole('textbox', { name: 'Limit' }) as HTMLInputElement;
    const note = screen.getByRole('textbox', { name: 'Note' }) as HTMLInputElement;

    expect(limit.required).toBe(true);
    expect(note.required).toBe(false);
  });
});
