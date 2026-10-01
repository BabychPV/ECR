import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
import { testTheme } from '@/test/render';

/**
 * Маршрут погодження: стани, яких бракувало `approval-route-editor.test`
 * (там — `503` і «порожньо»): «завантаження» і `403` (`ФВ-14.22`).
 *
 * ⚠ Тут «порожньо» небезпечніше, ніж деінде: PUT — повна заміна, і
 * збереження з екрана «кроків немає» стерло б наявний маршрут.
 *
 * ⚠ Постійна сіра підказка (`workflow.routeHint`) — теж `role="alert"`
 * (Mantine `Alert`), тож відмову шукаємо за КОДОМ, а не за роллю.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(route: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('approval-route')) return route();

      // Ролі приходять нормально: перевіряється саме запит маршруту.
      return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ApprovalRouteEditor projectId={1} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: /workflow\.route/ }));
}

const saveButton = (): HTMLElement => screen.getByRole('button', { name: '⟦common.save⟧' });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ApprovalRouteEditor — стани', () => {
  it('у дорозі: «завантаження», а не «кроків немає»; зберегти не можна', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦workflow.routeNone⟧')).toBeNull();
    expect(saveButton().hasAttribute('disabled')).toBe(true);
  });

  it('403: стан «немає права» з кодом, а не «кроків немає»; зберегти не можна', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            title: 'Forbidden',
            status: 403,
            errorCode: 'ECR-AUTH-0403',
            correlationId: 'corr-route',
            detail: null,
          }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect(await screen.findByText('ECR-AUTH-0403')).toBeTruthy();
    expect(screen.queryByText('⟦workflow.routeNone⟧')).toBeNull();
    expect(saveButton().hasAttribute('disabled')).toBe(true);
  });
});
