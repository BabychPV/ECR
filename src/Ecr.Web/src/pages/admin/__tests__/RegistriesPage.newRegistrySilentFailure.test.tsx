import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { notifications } from '@mantine/notifications';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '../RegistriesPage';

/**
 * UI-аудит, lane 4: «Creating a registry with an invalid code (spaces/
 * punctuation) fails completely silently — no error shown at all»
 * (`docs/build/audit-drafts/lane4-new-registry-invalid-code-silent-
 * failure.md`, severity High).
 *
 * ⛔ `Select` (`@mantine/core`) — довідник-пікер у шапці сторінки —
 * зависає під jsdom (відтворюваний факт, `Q-299`). Заглушено легким
 * `<select>` тим самим встановленим прийомом, що й `GrantsPanel.
 * resourceName.test.tsx`/`ColumnEditor.registryLookup.test.tsx`.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.newRegistry': 'New registry',
  'registries.newRegistryTitle': 'New registry',
  'registries.code': 'Code',
  'registries.registryCodeHint': 'Latin letters, digits and underscore; cannot be changed later.',
  'registries.name': 'Name',
  'registries.temporalField': 'Temporal',
  'registries.temporalFieldHint': 'Records carry a validity window.',
};

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Registry.EditDefinition'],
  simulatedForUserId: null,
  userId: 1,
  userName: 'bootstrap',
};

const invalidCodeProblem = {
  type: 'https://ecr.ncoc.kz/errors/ECR-CFG-0422',
  title: 'ECR-CFG-0422',
  status: 422,
  detail:
    'Код «LANE4 bad code!» недопустимий: дозволені латинські літери, цифри й підкреслення, ' +
    'перший символ — літера, довжина до 64.',
  instance: '/api/v1/registries',
  errorCode: 'ECR-CFG-0422',
  correlationId: '17aafd06-ea7e-48ac-8be8-212620ab301b',
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(me), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/registries') && method === 'GET') {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/registries') && method === 'POST') {
        return new Response(JSON.stringify(invalidCodeProblem), {
          status: 422,
          headers: { 'Content-Type': 'application/problem+json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <RegistriesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

describe('RegistriesPage: невдале заведення довідника показує помилку (lane4)', () => {
  it('422 ECR-CFG-0422 на "New registry" показує тост, а не мовчить', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show();

    // ⚠ Кнопка відкриття діалогу і кнопка сабміту всередині мають ОДНАКОВИЙ
    // accessible name («New registry») — той самий текст, що й у живому
    // застосунку (репро: "Click 'New registry' (the submit button...)").
    // Перед відкриттям модалки збігається рівно одна — далі шукаємо саме в
    // діалозі.
    fireEvent.click(await screen.findByRole('button', { name: 'New registry' }));

    const dialog = await screen.findByRole('dialog');

    const codeInput = within(dialog).getByLabelText('Code');
    fireEvent.change(codeInput, { target: { value: 'LANE4 bad code!' } });
    fireEvent.change(within(dialog).getByLabelText('Name'), {
      target: { value: '[LANE-4] Bad Code Test' },
    });

    fireEvent.click(within(dialog).getByRole('button', { name: 'New registry' }));

    // ⛔ Мутаційний доказ (RED на невиправленому коді): до фіксу цей запит
    // отримував 422 і НІЧОГО не показував — ані тост, ані інлайн-помилку.
    await waitFor(() => {
      expect(notifications.show).toHaveBeenCalled();
    });

    const call = vi.mocked(notifications.show).mock.calls[0]?.[0] as { message: string };
    expect(call.message).toContain('LANE4 bad code!');
  });
});
