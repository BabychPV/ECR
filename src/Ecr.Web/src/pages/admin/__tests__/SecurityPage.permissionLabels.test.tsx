import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * `U-11`: матриця `/admin/security` підписувала 41 колонку сирим кодом права.
 *
 * ⚠ Тест тримає ОБИДВА боки рішення, і саме в цьому його сенс:
 * 1) підпис — людський, із каталогу (інакше це знову код сервера на екрані);
 * 2) код ЛИШАЄТЬСЯ на екрані (інакше адміністратор безпеки, який читає коди в
 *    журналі й у відмовах `Requires permission {permission}`, втрачає зв'язок
 *    між колонкою і тим, що написано в журналі).
 * Перевірка лише першого пункту пройшла б і тоді, коли код зник зовсім.
 */

const SeededStrings: Record<string, string> = {
  'security.role': 'Role',
  'security.roles': 'Roles',
  'security.users': 'Users',
  'security.grants': 'Grants',
  'security.title': 'Security',
  'security.noRoles': 'No roles',
  'security.noRolesHint': 'No roles hint',
  'security.builtIn': 'built-in',
  'security.inactive': 'inactive',
  'security.dangerous': '{count} dangerous permission(s)',
  'security.noPermissions': 'no permissions',

  // ⚠ Рядок є лише під ДВА коди з трьох — третій навмисно лишається без назви.
  'permission.Calculation.EditConstant': 'Edit methodology constants',
  'permission.Document.View': 'View documents',
};

const RolesResponse = [
  {
    id: 1,
    code: 'DataEntry',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: ['Document.View'],
  },
];

const PermissionsResponse = [
  { code: 'Calculation.EditConstant', group: 'Calculation', isDangerous: false },
  { code: 'Document.View', group: 'Document', isDangerous: false },
  // ⛔ Право, якого в `09-seed.sql` іще немає: рівно той випадок, заради якого
  // `permissionLabel` не користується голим `t()`.
  { code: 'Future.NotSeededYet', group: 'Future', isDangerous: false },
];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }
      if (url.includes('/permissions')) return json(PermissionsResponse);
      if (url.includes('/roles')) return json(RolesResponse);
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: [],
          isSimulation: false,
        });
      }

      return json([]);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function renderRoles(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=roles']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Заголовок колонки, у якому стоїть саме цей код права. */
async function headerOf(code: string): Promise<HTMLElement> {
  const cell = (await screen.findByText(code)).closest('th');

  expect(cell).not.toBeNull();

  return cell as HTMLElement;
}

describe('SecurityPage: матриця прав підписана назвами, а не кодами (U-11)', () => {
  it('колонка названа рядком каталогу, і код лишається другим рядком', async () => {
    await renderRoles();

    const header = await headerOf('Calculation.EditConstant');

    // ⛔ Це й ламається поверненням `<Text>{permission.code}</Text>`.
    expect(within(header).getByText('Edit methodology constants')).not.toBeNull();

    // ⚠ А це ламається, якщо код «сховати зовсім»: другий рядок підпису.
    expect(
      header.querySelector('[data-two-line-secondary=""]')?.textContent,
    ).toBe('Calculation.EditConstant');

    // Повний код доступний і як підказка — колонок 41, вони вузькі.
    expect(header.querySelector('[data-two-line=""]')?.getAttribute('title')).toBe(
      'Calculation.EditConstant',
    );
  });

  it('право без рядка в каталозі показує СЕБЕ, а не позначений ключ', async () => {
    await renderRoles();

    const header = await headerOf('Future.NotSeededYet');

    expect(header.textContent).not.toContain('⟦');
    expect(header.textContent).not.toContain('permission.Future');

    // ⚠ Верхній рядок — сам код: нижнього не буде, бо він той самий.
    expect(header.querySelector('[data-two-line-primary=""]')?.textContent).toBe(
      'Future.NotSeededYet',
    );
  });

  it('жоден заголовок матриці не лишився голим кодом', async () => {
    await renderRoles();

    const named = await headerOf('Document.View');

    expect(within(named).getByText('View documents')).not.toBeNull();
  });
});
