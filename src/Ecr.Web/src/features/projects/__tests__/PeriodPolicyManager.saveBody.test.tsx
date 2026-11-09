import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';

/**
 * UAT 08.11 (лінія f/g): «Save» на рядку політики, якого НЕ редагували.
 *
 * ⛔ Тіло PUT бралося з `editing[id]`, а на невідредагованому рядку його немає: `JSON.stringify(undefined)`
 * — `undefined`, і запит ішов без тіла (сервер — 415). Тепер тіло — поточні значення рядка.
 */
const policy = {
  id: 7,
  code: 'MONTHLY_STD',
  openOffsetDays: -3,
  graceOffsetDays: 15,
  hardCloseOffsetDays: 45,
  yearGraceOffsetDays: 60,
};

function stubFetch(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (_url: string, init?: RequestInit) =>
    Promise.resolve(
      new Response(JSON.stringify(init?.method === 'PUT' ? policy : [policy]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <PeriodPolicyManager />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Збереження рядка політики', () => {
  it('Save на невідредагованому рядку шле поточні значення рядка, а не порожнє тіло', async () => {
    const fetchMock = stubFetch();
    show();

    fireEvent.click(screen.getByRole('button', { name: /periods\.managePolicies/ }));
    await screen.findByText('MONTHLY_STD');

    fireEvent.click(screen.getByRole('button', { name: /common\.save/ }));

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')).toBe(true);
    });

    const [url, init] = fetchMock.mock.calls.find(([, i]) => (i as RequestInit | undefined)?.method === 'PUT') as [
      string,
      RequestInit,
    ];
    expect(String(url)).toContain('/api/v1/projects/period-policies/7');
    expect(typeof init.body).toBe('string');
    expect(JSON.parse(init.body as string)).toEqual({
      openOffsetDays: -3,
      graceOffsetDays: 15,
      hardCloseOffsetDays: 45,
      yearGraceOffsetDays: 60,
    });
  });
});
