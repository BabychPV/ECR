import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefinitionDto, RegistryHistoryEntryDto } from '@/api/types';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10 — той самий клас, що #444/#446/#449/#451/#452
 * і #454. На цій сторінці він коштує двічі.
 *
 * ⛔ Історія. `entries={history.data ?? []}`, а `RegistryHistory` на нулі
 * записів каже «змін не було». Відмова `GET …/history` читалася як
 * ТВЕРДЖЕННЯ про журнал змін — саме там, куди приходять із питанням «хто і
 * навіщо це змінив». Найгірший різновид мовчазної порожнечі: не «нічого не
 * показали», а «показали неправду про минуле».
 *
 * ⛔ Довідники. `(registries.data ?? []).map(...)` → перелік цілей для поля
 * `Lookup` складався з самого «—». Зберегти таке поле не можна
 * (`isFieldComplete` вимагає цілі), і єдина підказка на екрані казала «поле
 * неповне» — тобто називала не ту причину.
 *
 * ⚠ `users.data?.items ?? []` на цій самій сторінці лишається як є, і це не
 * недогляд: право читати перелік користувачів — інше (`Security.ManageUsers`),
 * `403` на ньому не повинен ховати історію, і `RegistryHistory` показує в
 * такому рядку голий ідентифікатор із бейджем «нерозв'язано». Порожнеча там
 * НАЗВАНА, а не мовчазна.
 */

const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: true,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 11,
  fields: [
    {
      id: 41,
      code: 'Number',
      nameL10n: { values: { en: 'Permit number' } },
      dataType: 'String',
      isRequired: true,
      isScopeField: true,
      lookupRegistryDefId: null,
      unitId: null,
    },
  ],
  relations: [],
  rules: [],
  mappings: [],
};

const History: RegistryHistoryEntryDto[] = [
  {
    changedAt: '2026-05-01T10:00:00Z',
    entityType: 'cfg.RegistryDef',
    operation: 'SaveDefinition',
    oldJson: '{}',
    newJson: '{}',
    changeReason: 'ліміт перенесено з Configuration!J3',
    changedByUserId: 9,
  },
];

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
function refusal(detail: string, errorCode: string, correlationId: string): unknown {
  return {
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail,
    errorCode,
    correlationId,
    messageKey: `err.${errorCode}.unexpected`,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(fail: { registries: boolean; history: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }

      // ⚠ `/api/v1/me` — ТОЧНО: `includes` збігся б і з `/api/v1/methodologies`.
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Registry.EditDefinition'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/users')) {
        return json({ items: [{ id: 9, userName: 'tester', displayName: 'Tester' }], totalCount: 1 });
      }

      // ⚠ Вужчі маршрути — ПЕРЕД ширшим: обидва починаються з `/api/v1/registries`.
      if (path.endsWith('/definition')) {
        return json(Definition);
      }

      if (path.endsWith('/history')) {
        return fail.history
          ? json(refusal('журнал змін прочитати не вдалося', 'ECR-SYS-0500', 'cid-hist-1'), 500)
          : json(History);
      }

      if (path.endsWith('/api/v1/registries')) {
        return fail.registries
          ? json(refusal('перелік довідників прочитати не вдалося', 'ECR-SYS-0503', 'cid-reg-1'), 500)
          : json([
              { id: 5, code: 'SUBSTANCE', nameL10n: { values: { en: 'Substances' } }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master' },
            ]);
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries/PERMIT/definition']}>
          <Routes>
            <Route
              path="/admin/registries/:code/definition"
              element={<RegistryConstructorPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * ⚠ Каталог рядків тут не завантажений — підписи приходять ключами в `⟦…⟧`
 * (той самий вибір, що в `features/registries/__tests__/constructor.test.tsx`).
 * Тому вкладка шукається за ключем, а не за перекладом.
 */
async function openHistory(): Promise<void> {
  fireEvent.click(await screen.findByRole('tab', { name: /registries\.tabHistory/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryConstructorPage: відмова журналу змін ≠ «змін не було»', () => {
  it('історія не приїхала — причина з кодом, і НЕ «змін не було»', async () => {
    mockServer({ registries: false, history: true });
    show();
    await openHistory();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('журнал змін прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    // ⛔ Головне твердження: відмова більше не говорить від імені журналу.
    expect(screen.queryByText(/registries\.noHistory/)).toBeNull();
  });

  it('історія приїхала — записи на місці, банера немає', async () => {
    mockServer({ registries: false, history: false });
    show();
    await openHistory();

    // ⚠ Спершу дочекатися самого запису: запит у дорозі зробив би твердження
    // про відсутність банера зеленим на будь-якому коді.
    await screen.findByText('ліміт перенесено з Configuration!J3');

    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('RegistryConstructorPage: відмова переліку довідників названа', () => {
  it('довідники не приїхали — причина з кодом просто над полями', async () => {
    mockServer({ registries: true, history: false });
    show();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік довідників прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0503');
  });

  it('довідники приїхали — банера немає', async () => {
    mockServer({ registries: false, history: false });
    show();

    // ⚠ Ознака, що опис і перелік уже в стані: поле довідника намальоване.
    await screen.findByText('Permit number');
    await waitFor(() => {
      expect(screen.queryByRole('alert')).toBeNull();
    });
  });
});
