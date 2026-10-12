import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateProjectModal } from '@/features/projects/CreateProjectModal';
import { testTheme } from '@/test/render';

/**
 * ФВ-1.2 (рішення людини 12.10): форма створення проєкту ПРОПОНУЄ останню опубліковану версію шаблону.
 *
 * Проводка «перелік сервера -> `pickDefaultVersionId` -> значення поля» (сам вибір - у
 * `defaultTemplateVersion.test.ts`). Мокається реалістична пагінована відповідь (`{items, nextCursor}`).
 */
function respond(routes: Record<string, { status?: number; body: unknown }>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      const path = url.split('?')[0] ?? url;
      const route = routes[path];
      if (route === undefined) throw new Error(`Немає мока для ${url}`);

      return new Response(JSON.stringify(route.body), {
        status: route.status ?? 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function page<T>(items: T[]): { status: number; body: unknown } {
  return { status: 200, body: { items, nextCursor: null, totalCount: items.length } };
}

function version(id: number, name: string, status: string, publishedAt: string | null) {
  return { id, version: name, status, presentationRevision: 0, clonedFromVersionId: null, publishedAt };
}

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CreateProjectModal opened onClose={() => {}} onCreated={async () => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateProjectModal: версія шаблону за замовчуванням (ФВ-1.2)', () => {
  it('після публікації v2 поле вже містить v2; чернетка новіша за Id не пропонується', async () => {
    respond({
      '/api/v1/templates': page([{ id: 1, code: 'GEN', versionCount: 3, isArchived: false }]),
      '/api/v1/templates/1/versions': page([
        version(10, '1.0', 'Published', '2026-09-01T10:00:00Z'),
        version(11, '2.0', 'Published', '2026-10-01T10:00:00Z'),
        version(12, '3.0-draft', 'Draft', null),
      ]),
      '/api/v1/projects/period-policies': { body: [{ id: 7, code: 'STD', graceOffsetDays: 5, hardCloseOffsetDays: 10 }] },
    });

    const rendered = show();
    const field = await screen.findByRole('textbox', { name: /periods\.templateVersion/ });

    // ⛔ Без жодного кліку: значення з'являється саме з відповіді сервера.
    await waitFor(() => expect((field as HTMLInputElement).value).toBe('GEN · 2.0'), { timeout: 20_000 });

    rendered.unmount();
  });

  it('відмова завантаження версій — пропозиції немає (остання з неповного переліку була б хибною)', async () => {
    respond({
      '/api/v1/templates': page([
        { id: 1, code: 'GEN', versionCount: 1, isArchived: false },
        { id: 2, code: 'OIL', versionCount: 1, isArchived: false },
      ]),
      '/api/v1/templates/1/versions': page([version(10, '1.0', 'Published', '2026-09-01T10:00:00Z')]),
      '/api/v1/templates/2/versions': { status: 500, body: { code: 'ECR-SYS-0500', message: 'boom' } },
      '/api/v1/projects/period-policies': { body: [] },
    });

    const rendered = show();
    const field = await screen.findByRole('textbox', { name: /periods\.templateVersion/ });

    // Дочекатися обох: версія GEN уже в переліку (тобто пропозиція БУЛА б можлива) і відмова дійшла до форми
    // (банер помилки) — лише тоді дивитися на поле, інакше перевірка порожня.
    await screen.findByRole('alert', {}, { timeout: 20_000 });
    await userEvent.setup().click(field);
    await screen.findByRole('option', { name: 'GEN · 1.0' }, { timeout: 20_000 });
    expect((field as HTMLInputElement).value).toBe('');

    rendered.unmount();
  });
});
