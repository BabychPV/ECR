import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * Аудит 2026-09-16, §10.8: невдалий запит структури шаблону показувався як
 * «аркушів немає».
 *
 * ⛔ `available = (structure.data?.sheets ?? [])` — рівно той взірець, проти
 * якого існує `AsyncBoundary`: «`data?.items ?? []` у п'ятнадцяти областях —
 * це п'ятнадцять місць, де невдалий запит перетворюється на "даних немає"»
 * (`AsyncBoundary.tsx`). Тут це означало: 500 від сервера чи обрив мережі
 * давали порожній перелік чекбоксів під заголовком «Аркуші», і людина робила
 * висновок, що у версії шаблону аркушів НЕМАЄ — тобто що винна конфігурація,
 * а не збій. Кнопка «Save» при цьому лишалася заблокованою (`sheets.length ===
 * 0`) без жодного пояснення чому.
 *
 * ⚠ `Select` Mantine підмінено: дійти до переліку аркушів у jsdom через ДВА
 * справжні `Select` неможливо — клік по опції такого списку тут висить
 * назавжди (задокументовано в `CreateDocumentModal.checkboxSource.test.tsx`).
 * Підмінюється лише спосіб обрати значення; уся логіка запиту структури й
 * показу результату — справжня.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubSelect(props: {
    label?: string;
    value?: string | null;
    onChange?: (value: string | null) => void;
    data?: readonly { value: string; label: string }[];
  }): JSX.Element {
    return (
      <select
        aria-label={props.label ?? ''}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.currentTarget.value || null)}
      >
        <option value="">—</option>
        {(props.data ?? []).map((item) => (
          <option key={item.value} value={item.value}>
            {item.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

/** Чи відповідати на запит структури відмовою. */
let structureFails = true;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/structure')) {
        return structureFails
          ? jsonResponse(
              {
                title: 'Внутрішня помилка',
                status: 500,
                detail: 'структуру шаблону прочитати не вдалося',
                errorCode: 'ECR-SYS-0500',
                correlationId: 'corr-1',
              },
              500,
            )
          : jsonResponse({
              templateVersionId: 5,
              presentationRevision: 1,
              isEditable: false,
              groupRules: [],
              sheets: [
                { id: 9, code: 'GEN', nameL10n: { values: { en: 'General' } }, sheetGroup: null },
              ],
            });
      }

      if (url.includes('/versions')) {
        return jsonResponse({
          items: [{ id: 5, version: '1.0', status: 'Published' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/templates')) {
        return jsonResponse({
          items: [{ id: 3, code: 'AIR', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/projects')) {
        return jsonResponse({
          items: [{ id: 1, code: 'PRJ', status: 'Active' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <CreateDocumentModal opened onClose={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Обирає опубліковану версію шаблону — саме після цього йде запит структури. */
async function pickVersion(): Promise<void> {
  const select = await screen.findByLabelText('⟦documents.version⟧');
  await waitFor(() => expect(select.querySelectorAll('option')).toHaveLength(2));

  fireEvent.change(select, { target: { value: '5' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateDocumentModal: збій запиту структури не виглядає як «аркушів немає» (§10.8)', () => {
  it('відмова сервера показується як помилка з текстом і кодом, а не порожнім переліком', async () => {
    structureFails = true;
    mockServer();
    show();

    await pickVersion();

    // ⛔ Мутаційний доказ (RED до фіксу): `structure.data?.sheets ?? []`
    // давав порожній перелік чекбоксів, і на екрані НЕ БУЛО нічого з
    // `role="alert"` — жодного натяку, що запит відмовив.
    const alert = await screen.findByRole('alert');

    expect(alert.textContent ?? '').toContain('структуру шаблону прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
  });

  it('успішний запит показує аркуші, а не помилку', async () => {
    structureFails = false;
    mockServer();
    show();

    await pickVersion();

    await waitFor(() => expect(screen.getByText('General (GEN)')).toBeTruthy());
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
