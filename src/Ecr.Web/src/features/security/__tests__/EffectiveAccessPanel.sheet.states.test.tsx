import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { EffectiveAccessView } from '@/api/types';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { testTheme } from '@/test/render';

/**
 * Розріз на аркуші/таблиці/колонці (`ФВ-6.16`) — стани, яких не тримають `.template` і `.states`:
 * відмова для рівня з проєктом, порожній розріз рівня з застереженням, перемикання на довідник.
 *
 * Мутаційні докази (лише локально): без `needsProject(kind) &&` у `setAsked` — запит довідника несе
 * проєкт, якого поле вже не показує; без `projectId < 1` у `projectMissing` — «Пояснити» доступне з
 * проєктом 0.
 */

const EmptySheet: EffectiveAccessView = {
  userId: 7,
  userName: 'ivanov',
  resource: 'Sheet:4',
  level: 'None',
  isDenied: false,
  denyReason: 'NoGrant',
  groupsFromTicket: true,
  levelMayExceedActual: false,
  caveat: 'DocumentStateNotConsidered',
  projectId: 3,
  contributions: [],
};

function serve(answer: (url: string) => Promise<Response>): string[] {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));

      return answer(String(input));
    }),
  );

  return urls;
}

const ok = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <EffectiveAccessPanel userId={7} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function ask(kind: string, id: string, project?: string): void {
  fireEvent.change(screen.getByLabelText(/effectiveAccess\.kind⟧/), { target: { value: kind } });
  fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: id } });
  if (project !== undefined) {
    fireEvent.change(screen.getByLabelText(/effectiveAccess\.projectId/), { target: { value: project } });
  }
}

const explain = (): HTMLElement => screen.getByRole('button', { name: /effectiveAccess\.explain/ });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EffectiveAccessPanel: аркуш — стани', () => {
  it('404 для аркуша в проєкті — відмова з кодом, а не «внесків немає» і не застереження без рівня', async () => {
    serve(async () =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Not Found', status: 404, errorCode: 'ECR-TMPL-0404', correlationId: 'c-sheet', detail: null }),
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    ask('Sheet', '4', '3');
    fireEvent.click(explain());

    expect(await screen.findByText('ECR-TMPL-0404', { exact: false })).toBeDefined();
    expect(screen.queryByText(/effectiveAccess\.noContributions/)).toBeNull();
    expect(screen.queryByTestId('effective-access-caveat')).toBeNull();
  });

  it('порожній розріз аркуша — рівень, «гранту немає», «внесків немає» і застереження про стан документа', async () => {
    serve(async () => ok(EmptySheet));
    show();

    ask('Sheet', '4', '3');
    fireEvent.click(explain());

    expect(await screen.findByText(/effectiveAccess\.level/)).toBeDefined();
    expect(screen.getByTestId('effective-access-caveat')).toBeDefined();
    expect(screen.getByText(/effectiveAccess\.noGrant/)).toBeDefined();
    expect(screen.getByText(/effectiveAccess\.noContributions/)).toBeDefined();
    // Порожньо — без таблиці: голі заголовки колонок читалка оголосила б як дані.
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('проєкт 0 не дає питати; після перемикання на довідник проєкт у запит не йде', async () => {
    const urls = serve(async () => ok({ ...EmptySheet, resource: 'Registry:4', caveat: null, projectId: null }));
    show();

    ask('Table', '4', '0');
    expect(explain()).toHaveProperty('disabled', true);
    fireEvent.change(screen.getByLabelText(/effectiveAccess\.projectId/), { target: { value: '3' } });
    expect(explain()).toHaveProperty('disabled', false);

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.kind⟧/), { target: { value: 'Registry' } });
    expect(screen.queryByLabelText(/effectiveAccess\.projectId/)).toBeNull();
    fireEvent.click(explain());

    await waitFor(() => expect(urls).toHaveLength(1));
    expect(urls[0]).toContain('resource=Registry%3A4');
    expect(urls[0]).not.toContain('projectId');
  });
});
