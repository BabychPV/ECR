import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { visibleNavGroups } from '@/app/visibleNavGroups';
import { t } from '@/shared/i18n';
import { useNavbarCollapsed } from '@/shared/theme/navbarCollapse';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { testTheme } from '@/test/render';
import { SearchLauncher } from '@/features/search/SearchLauncher';
import { matchCommands, type CommandItem } from '@/features/search/commandItems';

/**
 * UI-30: командна палітра — екрани (лише з правами), дії оболонки, пошук даних.
 * Макет: `docs/design/hybrid/kit.js` `openPalette`.
 *
 * Що доводиться: (1) порожній запит показує екрани з меню користувача і дії;
 * (2) запит фільтрує екрани й окремо шукає дані; (3) Enter на екрані веде на
 * нього, на дії — виконує її, закриває палітру й повертає фокус; (4) вузька
 * роль (область — один аркуш) бачить лише свої екрани і лише те, що повернув
 * сервер, без жодних інших запитів; (5) axe в обох темах.
 */

beforeAll(async () => {
  await import('@/features/search/DataSearchPalette');
});

const original = globalThis.fetch;

afterEach(() => {
  cleanup();
  globalThis.fetch = original;
  localStorage.clear();
});

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

/** Підміняє мережу; повертає УСІ шляхи запитів у порядку надходження. */
function serve(hits: unknown[]): { paths: () => string[] } {
  const paths: string[] = [];
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), 'http://x');
    paths.push(url.pathname + url.search);
    return json(url.pathname === '/api/v1/search' ? hits : {});
  }) as typeof globalThis.fetch;
  return { paths: () => paths };
}

function me(permissions: string[]): CurrentUserDto {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    isSimulation: false,
    mustChangePassword: false,
  } as unknown as CurrentUserDto;
}

function Location(): JSX.Element {
  return <span data-testid="location">{useLocation().pathname}</span>;
}

function MenuState(): JSX.Element {
  return <span data-testid="menu-collapsed">{String(useNavbarCollapsed())}</span>;
}

function mount(user: CurrentUserDto, scheme: 'light' | 'dark' = 'light'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme} defaultColorScheme={scheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/units']}>
          <SearchLauncher groups={visibleNavGroups(user)} />
          <Location />
          <MenuState />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openPalette(): Promise<{ user: ReturnType<typeof userEvent.setup>; input: HTMLElement }> {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: t('search.open') }));
  const input = await screen.findByRole('combobox', { name: t('search.open') });
  await waitFor(() => expect(document.activeElement).toBe(input));
  return { user, input };
}

function optionsIn(groupName: string): string[] {
  const group = screen.queryByRole('group', { name: groupName });
  if (group === null) return [];
  return within(group)
    .getAllByRole('option')
    .map((option) => option.textContent ?? '');
}

const Admin = me(['Template.View', 'Registry.View', 'Calculation.View', 'System.ViewHealth', 'Period.Configure']);

describe('UI-30: командна палітра', () => {
  it('діалог має ім\'я, порожній запит — екрани з меню (з групою) і дії', async () => {
    serve([]);
    mount(Admin);
    await openPalette();

    expect(screen.getByRole('dialog', { name: t('palette.title') })).toBeTruthy();

    const expected = visibleNavGroups(Admin).flatMap((group) =>
      group.items.map((route) => `${t(route.handle.labelKey)}${t(group.labelKey)}`),
    );
    expect(optionsIn(t('palette.screens'))).toEqual(expected);
    expect(optionsIn(t('palette.actions'))).toEqual([
      t('palette.action.themeDark'),
      t('palette.action.densityComfortable'),
      t('palette.action.collapseMenu'),
    ]);
    // Порожній запит — не пошук: рядок стану мовчить, у мережу нічого не йде.
    expect(screen.getByRole('status').textContent).toBe('');
  });

  it('запит фільтрує екрани й окремо шукає дані', async () => {
    const net = serve([{ kind: 'document', id: 42, code: 'DOC-42', title: 'Units report' }]);
    mount(Admin);
    const { user, input } = await openPalette();

    await user.type(input, 'units');

    expect(optionsIn(t('palette.screens'))).toEqual([`${t('nav.units')}${t('nav.group.configure')}`]);
    await waitFor(() => expect(optionsIn(t('nav.documents'))).toEqual(['Units reportDOC-42']));
    expect(net.paths()).toEqual(['/api/v1/search?q=units']);
    // Курсор — на першому рядку, екрані.
    const [first] = screen.getAllByRole('option');
    expect(first?.textContent).toContain(t('nav.units'));
    expect(first?.getAttribute('aria-selected')).toBe('true');
  });

  it('Enter на екрані веде на нього й закриває палітру', async () => {
    serve([]);
    mount(Admin);
    const { user, input } = await openPalette();

    await user.type(input, 'jobs');
    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/admin/jobs'));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('дія виконується, палітра закривається, фокус — на кнопку, що її відкрила', async () => {
    serve([]);
    mount(Admin);
    const { user, input } = await openPalette();
    const launcher = screen.getByRole('button', { name: t('search.open'), hidden: true });

    await user.type(input, 'collapsemenu');
    expect(optionsIn(t('palette.actions'))).toEqual([t('palette.action.collapseMenu')]);
    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByTestId('menu-collapsed').textContent).toBe('true');
    expect(screen.getByTestId('location').textContent).toBe('/admin/units');
    await waitFor(() => expect(document.activeElement).toBe(launcher));
  });

  it('тема: дія перемикає на темну, і наступного разу пропонує світлу', async () => {
    serve([]);
    mount(Admin);
    const { user } = await openPalette();

    await user.click(screen.getByRole('option', { name: t('palette.action.themeDark') }));
    await waitFor(() =>
      expect(document.documentElement.getAttribute('data-mantine-color-scheme')).toBe('dark'),
    );

    await openPalette();
    expect(optionsIn(t('palette.actions'))[0]).toBe(t('palette.action.themeLight'));
  });

  it('Scope: роль з областю в один аркуш бачить лише свої екрани й лише те, що повернув сервер', async () => {
    // Оператор вводу: жодного адміністративного права; область даних звужує
    // СЕРВЕР — він і повертає один документ. Палітра нічого не дописує.
    const narrow = me([]);
    const net = serve([{ kind: 'document', id: 7, code: 'DOC-7', title: 'Sheet A only' }]);
    mount(narrow);
    const { user, input } = await openPalette();

    const allowed = visibleNavGroups(narrow).flatMap((group) => group.items.map((route) => route.handle.labelKey));
    expect(allowed).not.toContain('nav.templates');
    expect(optionsIn(t('palette.screens'))).toHaveLength(allowed.length);
    const shown = screen.getAllByRole('option').map((option) => option.textContent ?? '');
    for (const key of ['nav.templates', 'nav.registries', 'nav.jobs', 'nav.security', 'nav.periods', 'nav.audit']) {
      expect(shown.some((text) => text.includes(t(key)))).toBe(false);
    }

    await user.type(input, 'Sheet');
    await waitFor(() => expect(optionsIn(t('nav.documents'))).toEqual(['Sheet A onlyDOC-7']));
    // Даних — рівно стільки, скільки повернув сервер; інших видів і лічильників немає.
    expect(optionsIn(t('nav.templates'))).toEqual([]);
    expect(optionsIn(t('nav.registries'))).toEqual([]);
    const dataOptions = within(screen.getByRole('listbox'))
      .getAllByRole('option')
      .filter((option) => option.hasAttribute('data-route'));
    expect(dataOptions.map((option) => option.getAttribute('data-route'))).toEqual(['/documents/7']);
    // Жодного запиту, крім пошуку: ні переліку документів, ні аркушів, ні лічильників.
    expect(net.paths().every((path) => path.startsWith('/api/v1/search?'))).toBe(true);
  });

  it.each(['light', 'dark'] as const)('axe, тема %s: екрани й дії без порушень', async (scheme) => {
    serve([]);
    mount(Admin, scheme);
    await openPalette();
    await screen.findByRole('listbox');

    const violations = await findViolations(document.body);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});

describe('matchCommands', () => {
  const item = (id: string, section: CommandItem['section'], title: string, hint = ''): CommandItem => ({
    id,
    section,
    title,
    hint,
    run: () => undefined,
  });

  it('кожне слово запиту має бути в назві або підписі групи', () => {
    const items = [item('a', 'screens', 'Units', 'Configure'), item('b', 'screens', 'Jobs', 'Operate')];
    expect(matchCommands(items, 'conf un').map((x) => x.id)).toEqual(['a']);
    expect(matchCommands(items, '  ').map((x) => x.id)).toEqual(['a', 'b']);
  });

  it('під час набору — не більше 6 рядків на розділ', () => {
    const items = Array.from({ length: 9 }, (_, i) => item(`s${String(i)}`, 'screens', `Screen ${String(i)}`));
    expect(matchCommands(items, 'screen')).toHaveLength(6);
  });
});
