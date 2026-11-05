import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { mantineProviderProps } from '@/shared/theme/provider';
import { Catalog, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { FrozenColumnsSelect } from '../FrozenColumnsSelect';

/**
 * «Freeze columns» у панелі сітки: axe без блокуючих порушень у обох темах; підпис - справжній `<label for>`;
 * тексти - СПРАВЖНІЙ каталог із сіду (`UI-grid-frozen-columns.sql`), `⟦key⟧` означало б ключ без рядка в сіді.
 * ⚠ jsdom без розкладки: контраст реальних кольорів дивиться e2e `gridFrozenColumns.spec.ts`.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(() => {
  cleanup();
});

describe('FrozenColumnsSelect: a11y', () => {
  it.each(Themes)('%s: без порушень, з підписом і пунктами з сіду', async (scheme) => {
    const onChange = vi.fn();
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <main>
          <FrozenColumnsSelect columnNames={['Row', 'Region', 'Plant', 'Q1']} override={undefined} onChange={onChange} />
        </main>
      </MantineProvider>,
    );

    expect(container.textContent).not.toContain('⟦');

    const select = screen.getByRole('combobox', { name: 'Freeze columns' });
    expect(Array.from(select.querySelectorAll('option')).map((o) => o.textContent)).toEqual([
      'Default',
      'None',
      'Up to Row',
      'Up to Region',
      'Up to Plant',
    ]);

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);

    fireEvent.change(select, { target: { value: '2' } });
    expect(onChange).toHaveBeenCalledWith(2);
    fireEvent.change(select, { target: { value: 'default' } });
    expect(onChange).toHaveBeenLastCalledWith(undefined);
  });
});
