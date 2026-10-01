import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { RegistryExternalKeysPanel } from '../RegistryExternalKeysPanel';

/**
 * Панель зовнішніх ідентифікаторів запису (`ФВ-8.10`): стани «помилка»,
 * «порожньо», «завантаження» переліку зв'язків (`ФВ-14.22`).
 */
configure({ asyncUtilTimeout: 10_000 });

const Empty = '⟦registries.externalKeysEmpty⟧';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(json({ title: 'Error', status, errorCode, correlationId: 'corr-keys', detail: null }, status));

function serve(links: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/external-keys')) return links();
      if (url.includes('/api/v1/data-sources')) return json([{ id: 3, code: 'PI_MAIN' }]);

      return json(null);
    }),
  );
}

function show(): HTMLElement {
  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RegistryExternalKeysPanel registryCode="Flares" entryId={42} />
      </QueryClientProvider>
    </MantineProvider>,
  ).container;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryExternalKeysPanel — стани', () => {
  it('500: помилка з кодом, а не «не прив\'язано»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(screen.queryByText(Empty)).toBeNull();
  });

  it('403: відмова з кодом, а не «не прив\'язано»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText(Empty)).toBeNull();
  });

  it('порожньо: «не прив\'язано» і жодної таблиці', async () => {
    serve(() => Promise.resolve(json({ items: [], nextCursor: null, totalCount: 0 })));
    show();

    expect(await screen.findByText(Empty)).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('у дорозі: скелет, а не «не прив\'язано»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    const container = show();

    // Панель уже намалювала заголовок — отже стан переліку вже вирішено.
    expect(await screen.findByText('⟦registries.externalKeys⟧')).toBeTruthy();
    expect(container.querySelector('.mantine-Skeleton-root')).not.toBeNull();
    expect(screen.queryByText(Empty)).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });
});
