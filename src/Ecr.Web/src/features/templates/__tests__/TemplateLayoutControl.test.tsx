import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { TemplateLayoutControl } from '@/features/templates/TemplateLayoutControl';

/**
 * ✎ D-474, п. 2: «Layout» версії шаблону — список у чернетці, значок лише для читання в опублікованій.
 *
 * ⛔ Мутаційний доказ: (1) прибрати `PUT` / змінити тіло — тест «чернетка» червоніє на тілі запиту; (2) прибрати
 * інвалідацію `templates.version` — червоніє перевірка кешу; (3) показати список і для `editable=false` — червоніє
 * тест «опублікована»; (4) ковтати відмову — тест 409 не бачить `showApiError`.
 */

const notify = vi.hoisted(() => ({ done: vi.fn(), apiError: vi.fn() }));

vi.mock('@/shared/ui/notify', () => ({
  showDone: notify.done,
  showApiError: notify.apiError,
}));

const Strings: Record<string, string> = {
  'template.layout.label': 'Layout',
  'template.layout.perMonthCopies': 'A copy per month',
  'template.layout.yearWithMonthColumns': 'Year, months in columns',
  'template.layout.hint': 'The layout defines which period a cell belongs to. It can be changed only in a draft.',
  'template.layout.saved': 'Layout saved',
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: unknown;
}

function stubFetch(put: () => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return jsonResponse({ languageCode: 'en', revision: 1, strings: Strings });

      const method = String(init?.method ?? 'GET');
      calls.push({ url, method, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });

      return method === 'PUT' ? put() : jsonResponse(null);
    }),
  );

  return calls;
}

function show(props: { editable: boolean; layout?: 'PerMonthCopies' | 'YearWithMonthColumns' }): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <TemplateLayoutControl templateVersionId={7} layout={props.layout ?? 'PerMonthCopies'} editable={props.editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

beforeEach(() => {
  notify.done.mockReset();
  notify.apiError.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateLayoutControl (D-474)', () => {
  it('чернетка: вибір «Year, months in columns» — PUT …/layout, інвалідація структури й переліків, «Layout saved»', async () => {
    const calls = stubFetch(() => jsonResponse({ templateVersionId: 7, layout: 'YearWithMonthColumns' }));
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = show({ editable: true });
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const user = userEvent.setup();

    const field = screen.getByRole('textbox', { name: /Layout/ });
    expect((field as HTMLInputElement).value).toBe('A copy per month');

    await user.click(field);
    await user.click(await screen.findByRole('option', { name: 'Year, months in columns' }));

    await waitFor(() => expect(notify.done).toHaveBeenCalledWith('Layout saved'));
    expect(calls).toEqual([
      { url: '/api/v1/template-versions/7/layout', method: 'PUT', body: { layout: 'YearWithMonthColumns' } },
    ]);

    const keys = invalidate.mock.calls.map((call) => call[0]?.queryKey);
    expect(keys).toContainEqual(queryKeys.templates.version(7));
    expect(keys).toContainEqual(queryKeys.templates.allVersionsOf());
    expect(keys).toContainEqual(queryKeys.templates.allVersionsBatch());
    expect(notify.apiError).not.toHaveBeenCalled();
  });

  it('чернетка з документами: 409 ECR-SCHM-0409 іде в загальний показ відмови (messageKey), успіху немає', async () => {
    stubFetch(() =>
      jsonResponse(
        {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-SCHM-0409',
          detail: 'The layout cannot be changed: documents are already attached to this template version.',
          messageKey: 'err.ECR-SCHM-0409.templateLayoutBreaking',
        },
        409,
      ),
    );
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show({ editable: true });
    const user = userEvent.setup();

    await user.click(screen.getByRole('textbox', { name: /Layout/ }));
    await user.click(await screen.findByRole('option', { name: 'Year, months in columns' }));

    await waitFor(() => expect(notify.apiError).toHaveBeenCalledTimes(1));
    const error = notify.apiError.mock.calls[0]?.[0] as unknown;
    expect(error).toBeInstanceOf(EcrApiError);
    expect((error as EcrApiError).problem.extensions2?.['messageKey']).toBe('err.ECR-SCHM-0409.templateLayoutBreaking');
    expect(notify.done).not.toHaveBeenCalled();

    // Відмова — поле повертається до значення сервера.
    await waitFor(() =>
      expect((screen.getByRole('textbox', { name: /Layout/ }) as HTMLInputElement).value).toBe('A copy per month'),
    );
  });

  it('опублікована / виведена з обігу: значок лише для читання, без списку і без запитів', async () => {
    const calls = stubFetch(() => jsonResponse(null));
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show({ editable: false, layout: 'YearWithMonthColumns' });

    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.getByText('Layout')).toBeTruthy();
    const badge = screen.getByText('Year, months in columns');
    expect(badge.closest('[data-template-layout-value]')?.getAttribute('data-template-layout-value')).toBe(
      'YearWithMonthColumns',
    );
    expect(calls).toEqual([]);
  });
});
