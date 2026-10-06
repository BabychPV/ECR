import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import type { RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

/**
 * UI-37: смуга показників, пошук і шкала рівня над грантами ролі (макет
 * `screens-ops.js` `secGrants`: `stats`, `search`, `ladder()`).
 *
 * ⛔ Мутаційні докази в тестах нижче:
 *  - `matchesStat` завжди `true` → «deny» не ховає дозволи (червоний 1-й);
 *  - `visible` без гілки `resourceId <= 0` → щойно доданий рядок зникає під
 *    пошуком (червоний 3-й);
 *  - індекс поля з відфільтрованого переліку → «Level 3» стає «Level 1»
 *    (червоний 1-й).
 */

const SeededStrings: Record<string, string> = {
  'security.role': 'Role',
  'grants.add': 'Add grant',
  'grants.kind': 'Resource kind',
  'grants.level': 'Level',
  'grants.deny': 'Deny',
  'grants.remove': 'Remove',
  'grants.empty': 'Empty',
  'grants.stats': 'Grants of the role',
  'grants.stat.all': 'grants',
  'grants.stat.deny': 'deny rules',
  'grants.stat.manage': 'at Manage level',
  'grants.stat.unresolved': 'resources not found',
  'grants.search': 'Search',
  'grants.noMatch': 'No grants match the filter.',
  'grants.levelHint.Manage': 'Can change the settings of the resource.',
  'common.save': 'Save',
  'common.loading': 'Loading',
};

const Roles: RoleView[] = [
  { id: 10, code: 'Auditor', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

const GrantsResponse = [
  { resourceKind: 'Project', resourceId: 1, level: 'Approve', isDeny: false, resourceName: 'ATR' },
  { resourceKind: 'Project', resourceId: 2, level: 'Write', isDeny: false, resourceName: 'KAR' },
  { resourceKind: 'Sheet', resourceId: 17, level: 'Read', isDeny: true, resourceName: 'AIR' },
  { resourceKind: 'Registry', resourceId: 4, level: 'Manage', isDeny: false, resourceName: 'EmissionSources' },
];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/grants')) return json(GrantsResponse);
      return json([]);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function openAuditor(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GrantsPanel roles={Roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(await screen.findByLabelText('Role'));
  fireEvent.click(await screen.findByRole('option', { name: 'Auditor' }));
  await screen.findByText('EmissionSources');
}

/** Рядки таблиці грантів (без заголовка). */
function grantRows(): HTMLElement[] {
  const table = screen.getByText('Resource kind').closest('table') as HTMLElement;
  return within(table).getAllByRole('row').slice(1);
}

describe('GrantsPanel: смуга, пошук і шкала рівня (UI-37)', () => {
  it('смуга рахує набір, а «deny rules» лишає лише заборони — з тими самими номерами полів', async () => {
    await openAuditor();

    const strip = screen.getByRole('group', { name: 'Grants of the role' });
    expect(strip.textContent).toContain('4');
    expect(grantRows()).toHaveLength(4);

    fireEvent.click(within(strip).getByRole('button', { name: /deny rules/ }));

    const rows = grantRows();
    expect(rows).toHaveLength(1);
    expect(within(rows[0] as HTMLElement).getByText('AIR')).not.toBeNull();
    // ⚠ Номер у підписі поля — з усієї чернетки: заборона третя.
    expect(within(rows[0] as HTMLElement).getByRole('textbox', { name: 'Level 3' })).not.toBeNull();
  });

  it('пошук звужує перелік; без збігів — пояснення, а не порожня таблиця мовчки', async () => {
    await openAuditor();

    const search = screen.getByRole('searchbox', { name: 'Search' });
    fireEvent.change(search, { target: { value: 'emission' } });
    expect(grantRows()).toHaveLength(1);

    fireEvent.change(search, { target: { value: 'nothing-like-this' } });
    expect(grantRows()).toHaveLength(0);
    expect(screen.getByTestId('grants-no-match').textContent).toBe('No grants match the filter.');
  });

  it('щойно доданий рядок видно й під активним пошуком', async () => {
    await openAuditor();

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search' }), { target: { value: 'nothing-like-this' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));

    expect(grantRows()).toHaveLength(1);
    expect(screen.getByRole('textbox', { name: 'Level 5' })).not.toBeNull();
  });

  it('шкала рівня: рисок стільки, який рівень; заборона позначена', async () => {
    await openAuditor();

    const ladders = document.querySelectorAll<HTMLElement>('.ecr-ladder');
    expect(ladders).toHaveLength(4);

    const manage = document.querySelector<HTMLElement>('.ecr-ladder[data-level="Manage"]');
    expect(manage?.querySelectorAll('i[data-on]')).toHaveLength(5);
    expect(manage?.getAttribute('title')).toBe('Can change the settings of the resource.');

    const write = document.querySelector<HTMLElement>('.ecr-ladder[data-level="Write"]');
    expect(write?.querySelectorAll('i[data-on]')).toHaveLength(2);

    const deny = document.querySelector<HTMLElement>('.ecr-ladder[data-level="Read"]');
    expect(deny?.hasAttribute('data-deny')).toBe(true);
    expect(write?.hasAttribute('data-deny')).toBe(false);
  });
});
