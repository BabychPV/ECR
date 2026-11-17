import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { YearMonthReopen } from '../YearMonthReopen';

/** ✎ LAND-YEAR L7a: вибір місяця «Reopen a month for this document…» — axe без порушень у обох темах (кнопка і меню). */
const Months = Array.from({ length: 12 }, (_, i) => ({
  periodKey: 209901 + i,
  state: i === 8 || i === 9 ? ('Closed' as const) : ('Open' as const),
}));

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('YearMonthReopen — axe', () => {
  it.each(Themes.flatMap((scheme) => [false, true].map((menuOpen) => [scheme, menuOpen] as const)))(
    'тема %s, меню відкрите: %s',
    async (scheme, menuOpen) => {
      const { container } = render(
        <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } })}>
            <main>
              <h1>Document</h1>
              <YearMonthReopen documentId={5} months={Months} timeZoneId="UTC" />
            </main>
          </QueryClientProvider>
        </MantineProvider>,
      );

      if (menuOpen) {
        fireEvent.click(screen.getByTestId('year-month-reopen'));
        await waitFor(() => expect(document.querySelectorAll('[data-year-reopen-month]')).toHaveLength(12));
      }
      // Меню Mantine — у порталі (поза `container`), тож axe дивиться на весь документ.
      // ⚠ Те саме вузьке виключення, що в PermitsPage.a11y: службовий `<div data-autofocus>` Mantine у `role="menu"`
      // дає `aria-required-children`; приймається лише на вузлах меню і лише коли решта дітей меню — `menuitem`.
      const violations = (await findViolations(menuOpen ? document.body : container)).filter(
        (violation) => !(violation.id === 'aria-required-children' && violation.nodes.every((html) => html.includes('role="menu"'))),
      );

      expect(violations, describeViolations(violations)).toHaveLength(0);
      for (const menu of document.querySelectorAll('[role="menu"]')) {
        const foreign = [...menu.children].filter(
          (child) => child.getAttribute('role') !== 'menuitem' && child.getAttribute('data-autofocus') !== 'true',
        );
        expect(foreign, 'у меню лише пункти й службовий елемент фокуса Mantine').toHaveLength(0);
      }
    },
  );
});
