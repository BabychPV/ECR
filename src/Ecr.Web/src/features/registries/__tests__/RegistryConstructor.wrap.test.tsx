import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryFields } from '@/features/registries/RegistryConstructor';
import { testTheme } from '@/test/render';

/**
 * CL-26 (B-12): на ширині 500 px таблиця Конструктора довідника не має розпирати сторінку.
 *
 * ⚠ jsdom не застосовує зовнішні стилі й не міряє розкладку, тому доказ двоскладовий: таблиця
 * несе клас, а файл стилів містить правило переносу для цього класу (як `UiStringsTable.wrap.test.tsx`).
 * Сам вимір (`scrollWidth <= innerWidth` на 500) - візуальний прогін на стенді/CI.
 */
const css = readFileSync(
  path.resolve(process.cwd(), 'src/features/registries/registryConstructor.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

afterEach(cleanup);

const Empty = { id: 1, code: 'R', fields: [], rules: [], mappings: [] } as unknown as RegistryDefinitionDto;

describe('RegistryFields: перенос у вузькому вікні', () => {
  it('таблиця має клас, а правило дозволяє перенос усередині слова без обгортки з прокруткою', () => {
    render(
      <MantineProvider theme={testTheme}>
        <RegistryFields
          definition={Empty}
          canEdit
          newFields={[]}
          registryOptions={[]}
          onAddField={() => undefined}
          onChangeField={() => undefined}
          onRemoveField={() => undefined}
        />
      </MantineProvider>,
    );

    const table = screen.getByRole('table');

    expect(table.classList.contains('ecr-registry-table')).toBe(true);
    expect(css).toMatch(/\.ecr-registry-table td[^{]*\{[^}]*overflow-wrap:\s*anywhere/);
    expect(css).toMatch(/\.ecr-registry-table th[^{]*,/);
    expect(css).not.toMatch(/overflow-x\s*:\s*(auto|scroll)/);
  });
});
