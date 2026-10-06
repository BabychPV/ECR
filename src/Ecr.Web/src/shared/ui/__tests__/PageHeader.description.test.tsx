import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PageDescriptionContext } from '@/shared/ui/pageDescription';

/**
 * UI-11: рядок-пояснення під заголовком сторінки (`KIT.md` §6.4 `subtitle`).
 *
 * Що доводиться: (1) пояснення маршруту приходить контекстом і стоїть ПІСЛЯ
 * заголовка; (2) проп перемагає контекст, `null` прибирає пояснення; (3) без
 * контексту й пропа розмітка без пояснення (`D15-06`); (4) фокус лишається на
 * заголовку — пояснення його не перехоплює.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => `text of ${key}`,
}));

afterEach(cleanup);

function show(node: ReactNode, routeKey?: string): void {
  render(
    <MantineProvider>
      <PageDescriptionContext.Provider value={routeKey}>{node}</PageDescriptionContext.Provider>
    </MantineProvider>,
  );
}

describe('PageHeader: пояснення екрана (UI-11)', () => {
  it('пояснення маршруту — під заголовком, фокус лишається на заголовку', () => {
    show(<PageHeader title="Jobs" />, 'nav.jobs.description');

    const heading = screen.getByRole('heading', { name: 'Jobs' });
    const description = screen.getByTestId('page-description');
    expect(description.textContent).toBe('text of nav.jobs.description');
    // ⚠ Порядок у розмітці = порядок читання: спершу назва, потім пояснення.
    expect(heading.compareDocumentPosition(description) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(document.activeElement).toBe(heading);
  });

  it('проп перемагає маршрут; null прибирає пояснення', () => {
    show(<PageHeader title="Jobs" description="Own words" />, 'nav.jobs.description');
    expect(screen.getByTestId('page-description').textContent).toBe('Own words');
    cleanup();

    show(<PageHeader title="Jobs" description={null} />, 'nav.jobs.description');
    expect(screen.queryByTestId('page-description')).toBeNull();
  });

  it('без маршруту й пропа пояснення немає зовсім (D15-06)', () => {
    show(<PageHeader title="Jobs" />);
    expect(screen.queryByTestId('page-description')).toBeNull();
  });
});
