import type { ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { PermitListsPage } from '../PermitListsPage';
import { PermitPage } from '../PermitPage';
import { PermitsPage } from '../PermitsPage';
import { PageDescriptionContext } from '@/shared/ui/pageDescription';

/**
 * (Перелік типів — справжня сторінка PM-08a, її тести: `features/permits/types/__tests__/PermitTypesPage.test.tsx`.
 * Прив'язки аркушів — справжні сторінки, тести: `features/permits/bindingsPages/__tests__/`.)
 * Сторінки-каркаси Permits (PM-06): заголовок, смуга розділів, чесний порожній стан, недоступні дії
 * з поясненням і жодного запиту до мережі (ендпоінтів ще немає).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => `text of ${key}`,
}));
// PM-06b: смуга розділів читає права з профілю; ці сторінки показуються редактору типів.
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'] } }),
}));

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function showAt(path: string, route: string, element: ReactElement, descriptionKey?: string): void {
  const router = createMemoryRouter(
    [{ path: route, element: <PageDescriptionContext.Provider value={descriptionKey}>{element}</PageDescriptionContext.Provider> }],
    { initialEntries: [path] },
  );
  render(
    <MantineProvider>
      <RouterProvider router={router} />
    </MantineProvider>,
  );
}

const sectionPages = [
  ['/admin/permits', <PermitsPage key="a" />, 'text of nav.permits', 'text of permits.empty.title', ['text of permits.empty.new', 'text of permits.empty.import'], 0],
  ['/admin/permits/lists', <PermitListsPage key="c" />, 'text of permits.lists.title', 'text of permits.lists.empty.title', [], 2],
] as const;

describe('Permits: сторінки розділів', () => {
  it.each(sectionPages)('%s: заголовок, розділ у смузі, порожній стан', (path, page, title, emptyTitle, buttons, sectionIndex) => {
    const fetchSpy = vi.fn();
    vi.stubGlobal('fetch', fetchSpy);
    showAt(path, path, page);

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe(title);
    expect(screen.getByText(emptyTitle)).toBeTruthy();

    const nav = screen.getByRole('navigation', { name: 'text of permits.nav.aria' });
    const links = within(nav).getAllByRole('link');
    expect(links[sectionIndex]?.getAttribute('aria-current')).toBe('page');
    expect(links.filter((a) => a.getAttribute('aria-current') === 'page')).toHaveLength(1);

    // Жодних запитів до API на цьому кроці.
    expect(fetchSpy).not.toHaveBeenCalled();

    expect(screen.queryAllByRole('button').map((b) => b.textContent)).toEqual(buttons);
  });

  it.each(sectionPages)('%s: дії недоступні, а причина написана поруч і привʼязана до кнопок', (path, page, _title, _empty, buttons) => {
    showAt(path, path, page);
    const reason = screen.getByText('text of permits.unavailable');
    for (const button of screen.queryAllByRole('button')) {
      expect((button as HTMLButtonElement).disabled).toBe(true);
      expect(button.getAttribute('aria-describedby')).toBe(reason.id);
    }
    expect(buttons.length === 0 || screen.queryAllByRole('button').length === buttons.length).toBe(true);
  });

  it('пояснення маршруту показується під заголовком', () => {
    showAt('/admin/permits', '/admin/permits', <PermitsPage />, 'nav.permits.description');
    expect(screen.getByTestId('page-description').textContent).toBe('text of nav.permits.description');
  });

  it('ніякого «скоро буде»: порожні стани не обіцяють майбутнього', () => {
    showAt('/admin/permits', '/admin/permits', <PermitsPage />);
    expect(document.body.textContent?.toLowerCase()).not.toMatch(/coming|soon|next step/);
  });
});

describe('Permits: сторінки з параметром', () => {
  it('перміт: номер із адреси, посилання назад, порожній стан без дій', () => {
    showAt('/admin/permits/P-0001', '/admin/permits/:id', <PermitPage />);
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('text of permits.detail.title');
    expect(screen.getByText('P-0001')).toBeTruthy();
    expect(screen.getByRole('link', { name: '← text of permits.back.permits' }).getAttribute('href')).toBe('/admin/permits');
    expect(screen.getByText('text of permits.detail.emptyTitle')).toBeTruthy();
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });
});
