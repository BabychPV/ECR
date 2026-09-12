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
    // ⚠ Плейсхолдер навмисно НЕ з каталогу: тест перевіряє, що компонент
    // ПОКАЗУЄ код, а не що код правильний. Тут стояв `ECR-PER-0409` —
    // вигаданий, родини `PER` не існує; сторож `ClientErrorCodeTests` таких
    // більше не пропускає. `HTTP-409` — форма, яку `client.ts` породжує сам.
    errorCode: 'HTTP-409',
    correlationId: 'cid-test-1',
  });
}

/** Відмова в праві у формі, у якій її бачить клієнт (`ECR-AUTH-0403`). */
function forbidden(): EcrApiError {
  return new EcrApiError({
    title: '⟦err.ECR-AUTH-0403⟧',
    detail: 'You do not have permission for this action.',
    status: 403,
    errorCode: 'ECR-AUTH-0403',
    correlationId: 'cid-test-403',
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
    expect(alert.textContent).toContain('HTTP-409');
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

/**
 * П'ятий стан (директива B3, `PR nav-arch #6`, `Q-282`): `no-permission` —
 * `403` розпізнається ОКРЕМО від решти помилок і НЕ виглядає ні як порожньо,
 * ні як звичайна відмова сервера (`ФВ-14.22`, розширено директивою).
 */
describe('П\'ятий стан: no-permission (директива B3)', () => {
  it('403 показує текст сервера і код, БЕЗ кнопки «повторити» — навіть коли onRetry передано', () => {
    const retry = vi.fn();
    show(boundary({ isPending: false, error: forbidden(), data: undefined, onRetry: retry }));

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('⟦err.ECR-AUTH-0403⟧');
    expect(alert.textContent).toContain('You do not have permission for this action.');
    expect(alert.textContent).toContain('ECR-AUTH-0403');
    expect(alert.textContent).toContain('cid-test-403');

    // ⛔ Мутаційний доказ: якби `no-permission` мовчки перевикористав
    // `ErrorAlert` (той самий шлях, що й для 500/мережевого збою), кнопка
    // «повторити» з'явилася б тут, бо `onRetry` ПЕРЕДАНО. Право не з'являється
    // від повторного запиту — кнопки нема, попри наявний колбек.
    expect(screen.queryByRole('button')).toBeNull();
    expect(retry).not.toHaveBeenCalled();
  });

  it('403 НЕ виглядає як порожній стан: заголовок порожнечі відсутній, заголовок відмови — присутній', () => {
    show(boundary({ isPending: false, error: forbidden(), data: undefined }));

    // ⛔ Головне твердження розділу, той самий клас, що й `ФВ-14.22` для
    // error/empty: якби `no-permission` мовчки трактувався як `isEmpty`,
    // тут стояв би заголовок порожнього стану цього тесту.
    expect(screen.queryByText('У цьому проєкті ще немає документів')).toBeNull();
    expect(screen.getByText('⟦err.ECR-AUTH-0403⟧')).toBeDefined();
  });

  it('403 без структурованого тіла («HTTP 403») підміняється каталожним заголовком гарда маршруту', () => {
    const raw = new EcrApiError({
      title: 'HTTP 403',
      status: 403,
      errorCode: 'HTTP-403',
      correlationId: 'cid-test-403-raw',
    });

    show(boundary({ isPending: false, error: raw, data: undefined }));

    // ⛔ «HTTP 403» — не «зрозуміле повідомлення» (директива B3): підмінене
    // тим самим ключем, що вже показує `AccessDeniedPage.tsx` (`Q-279`) для
    // відмови МАРШРУТУ — той самий текст для того самого класу відмови.
    expect(screen.queryByText('HTTP 403')).toBeNull();
    expect(screen.getByText('⟦err.ECR-AUTH-0403⟧')).toBeDefined();
  });

  it('403 не перекривається `isEmpty`: дані відсутні (undefined), як і для порожнього стану, але рендер інший', () => {
    // ⚠ І `no-permission`, і `empty` (без даних) проходять через ту саму гілку
    // `data === undefined` МОДЕЛІ, якби перевірка помилки не йшла першою —
    // цей тест доводить, що порядок перевірок (`error` до `isEmpty`/`data`) не
    // зламано: 403 ніколи не доходить до `EmptyState`.
    show(boundary({ isPending: false, error: forbidden(), data: undefined }));

    expect(screen.getByRole('alert')).toBeDefined();
  });

  it('500 (не 403) і далі показує звичайний ErrorAlert із кнопкою «повторити» — регресія не введена', () => {
    const retry = vi.fn();
    show(boundary({ isPending: false, error: refusal(), data: undefined, onRetry: retry }));

    expect(screen.queryByText('⟦err.ECR-AUTH-0403⟧')).toBeNull();
    screen.getByRole('button').click();
    expect(retry).toHaveBeenCalledOnce();
  });
});
