import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { JSX, ReactNode } from 'react';
import { EcrApiError } from '@/api/client';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';

function show(node: ReactNode): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

/** Відмова сервера у формі, у якій її бачить клієнт. */
function refusal(): EcrApiError {
  return new EcrApiError({
    title: 'Період закрито',
    detail: 'Період закрито: зміни потребують окремого погодження.',
    status: 409,
    errorCode: 'ECR-PER-0409',
    correlationId: 'cid-test-1',
  });
}

interface Page {
  items: string[];
}

function boundary(props: {
  isPending: boolean;
  error: unknown;
  data: Page | undefined;
  onRetry?: () => void;
}): JSX.Element {
  return (
    <AsyncBoundary<Page>
      isPending={props.isPending}
      error={props.error}
      data={props.data}
      isEmpty={(d) => d.items.length === 0}
      emptyTitle="У цьому проєкті ще немає документів"
      emptyHint="Документи з'являються після відкриття періоду."
      skeleton="table"
      {...(props.onRetry === undefined ? {} : { onRetry: props.onRetry })}
    >
      {(d) => <ul>{d.items.map((i) => <li key={i}>{i}</li>)}</ul>}
    </AsyncBoundary>
  );
}

describe('Чотири стани подання', () => {
  it('ФВ-14.25: очікування показує скелет, а не порожній екран', () => {
    show(boundary({ isPending: true, error: null, data: undefined }));

    expect(screen.getByRole('status', { busy: true })).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ФВ-14.22: помилка НІКОЛИ не рендериться як порожній стан (A7-04)', () => {
    show(boundary({ isPending: false, error: refusal(), data: undefined }));

    const alert = screen.getByRole('alert');

    // Текст сервера, а не власне «щось пішло не так».
    expect(alert.textContent).toContain('Період закрито');

    // Стабільний код і кореляція — те, з чим ідуть у підтримку.
    expect(alert.textContent).toContain('ECR-PER-0409');
    expect(alert.textContent).toContain('cid-test-1');

    // ⛔ Головне твердження розділу: порожній стан не показано.
    expect(screen.queryByText('У цьому проєкті ще немає документів')).toBeNull();
  });

  it('помилка перекриває застарілі дані, а не ховається за ними', () => {
    // ⚠ `react-query` лишає попередні дані, коли повторний запит упав. Для
    // системи введення це найгірший з можливих станів: оператор правив би
    // числа, не знаючи, що бачить учорашній зріз.
    show(boundary({ isPending: false, error: refusal(), data: { items: ['Аркуш 1'] } }));

    expect(screen.getByRole('alert')).toBeDefined();
    expect(screen.queryByText('Аркуш 1')).toBeNull();
  });

  it('ФВ-14.23: порожній стан пояснює і пропонує дію', () => {
    show(boundary({ isPending: false, error: null, data: { items: [] } }));

    expect(screen.getByText('У цьому проєкті ще немає документів')).toBeDefined();
    expect(screen.getByText("Документи з'являються після відкриття періоду.")).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('дія порожнього стану відсутня, коли права немає', () => {
    show(
      <MantineProvider>
        <AsyncBoundary<Page>
          isPending={false}
          error={null}
          data={{ items: [] }}
          isEmpty={(d) => d.items.length === 0}
          emptyTitle="Немає документів"
        >
          {() => <div />}
        </AsyncBoundary>
      </MantineProvider>,
    );

    // ⚠ Кнопку створення показує ВИКЛИКАЧ і лише за наявності права: обгортка
    // не має власного уявлення про доступ і не може його вигадати.
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('ФВ-14.21: дані показуються, коли вони є', () => {
    show(boundary({ isPending: false, error: null, data: { items: ['Аркуш 1', 'Аркуш 2'] } }));

    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  it('стан помилки веде до виходу: «повторити» викликає запит наново', () => {
    const retry = vi.fn();
    show(boundary({ isPending: false, error: refusal(), data: undefined, onRetry: retry }));

    screen.getByRole('button').click();

    expect(retry).toHaveBeenCalledOnce();
  });
});
