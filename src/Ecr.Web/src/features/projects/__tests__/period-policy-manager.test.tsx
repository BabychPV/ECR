import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';

/**
 * Перелік політик періодів (`Q-254`).
 *
 * ⛔ `policies.error` не перевірявся ВЗАГАЛІ: `policies.data ?? []` ковтав
 * будь-яку відмову мовчки, і порожня таблиця виглядала так само, як «політик
 * ще не заведено». Форма створення поруч лишалася повністю робочою — адмін
 * міг завести ДРУГУ політику з кодом, що вже зайнятий, бо перша просто не
 * завантажилася (той самий клас дефекту, що й `A7-04`).
 */
function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    ),
  );
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

describe('Перелік політик періодів', () => {
  it('Q-254: невдалий запит показує помилку, а НЕ порожню таблицю', async () => {
    respond(
      {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-policies-1',
      },
      503,
    );
    show();

    screen.getByRole('button', { name: /periods\.managePolicies/ }).click();

    // ⛔ Головне твердження: до виправлення `policies.error` не читався
    // ЗОВСІМ — таблиця мовчки лишалася порожньою.
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('ECR-SYS-0503');
    expect(screen.queryByText('⟦state.emptyTitle⟧')).toBeNull();
  });

  it('Q-254: невдалий запит блокує створення НОВОЇ політики (захист від дублю)', async () => {
    respond(
      {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-policies-2',
      },
      503,
    );
    show();

    screen.getByRole('button', { name: /periods\.managePolicies/ }).click();
    await screen.findByRole('alert');

    // ⚠ Код заповнено НАВМИСНО: інакше кнопка була б вимкнена лише через
    // порожнє поле, і твердження нижче нічого не доводило б.
    fireEvent.change(screen.getByLabelText(/periods\.policyCode/), {
      target: { value: 'NEW_2026' },
    });

    // ⛔ Без цієї заборони адмін міг натиснути «створити» вслiпу і завести
    // ДРУГУ політику з кодом, що вже зайнятий першою — просто тому, що вона
    // не завантажилася, а не тому, що її немає.
    const create = screen.getByRole('button', { name: /periods\.policyCreate/ });
    expect(create.hasAttribute('disabled')).toBe(true);
  });

  it('дійсно порожній перелік пояснюється, а НЕ виглядає як помилка', async () => {
    respond([]);
    show();

    screen.getByRole('button', { name: /periods\.managePolicies/ }).click();

    expect(await screen.findByText('⟦state.emptyTitle⟧')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
