import { describe, it, expect, vi, afterEach, beforeEach, beforeAll } from 'vitest';
import { render, screen, cleanup, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import { EcrApiError } from '@/api/client';
import { AsyncBoundary, EmptyState, ErrorState, ForbiddenState } from '@/shared/ui/AsyncBoundary';

/**
 * Три стани подання, винесені з `AsyncBoundary` окремими компонентами
 * (директива №15, §2, Шар 2; правило `L10`: «немає прав» ≠ «порожньо» ≠
 * «фільтр нічого не знайшов»).
 *
 * ⛔ Чинний `AsyncBoundary.test.tsx` не змінено жодним рядком — і це головний
 * критерій приймання цієї зміни. Останній `describe` тут доводить, чому він
 * і НЕ МАВ змінитися: шлях помилки всередині обгортки малює рівно те саме, що
 * й малював.
 */
function show(node: ReactNode): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

function refusal(correlationId = 'cid-state-1'): EcrApiError {
  return new EcrApiError({
    title: 'Період закрито',
    detail: 'Період закрито: зміни потребують окремого погодження.',
    status: 409,
    errorCode: 'HTTP-409',
    correlationId,
  });
}

function forbidden(): EcrApiError {
  return new EcrApiError({
    title: 'Немає права на цей ресурс',
    detail: 'You do not have permission for this action.',
    status: 403,
    errorCode: 'ECR-AUTH-0403',
    correlationId: 'cid-state-403',
  });
}

/**
 * ⚠ `navigator.clipboard` у jsdom НЕМАЄ зовсім. Mantine це переживає
 * (`useClipboard` ставить собі помилку і мовчить), тож без заглушки тест на
 * копіювання був би ХИБНОЗЕЛЕНИМ: кнопка на місці, натискається, а
 * скопійованого значення ніхто не бачить. Перевіряти треба саме ЗНАЧЕННЯ —
 * інакше доказ зводиться до «кнопка існує».
 */
let written: string[] = [];

beforeEach(() => {
  written = [];

  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    value: {
      writeText: (value: string) => {
        written.push(value);

        return Promise.resolve();
      },
    },
  });
});

/**
 * ⛔ Прогрів `React.lazy`, і без нього половина перевірок нижче була б
 * ХИБНОЗЕЛЕНОЮ.
 *
 * Кнопка копіювання вантажиться за `import()` (бюджет `D-132`, див. коментар
 * у `AsyncBoundary.tsx`), тобто в першому кадрі її немає НІКОЛИ — ні коли
 * `copyLabel` не передано, ні коли передано. Тоді «кнопки копіювання немає» і
 * «кнопок рівно одна» зелені завжди, тобто не перевіряють нічого.
 *
 * Після першого розв'язаного `import()` React запам'ятовує модуль, і наступні
 * рендери малюють кнопку СИНХРОННО — твердження «є» і «немає» знову можна
 * робити в тому самому кадрі.
 */
beforeAll(async () => {
  render(
    <MantineProvider>
      <ErrorState error={refusal('cid-прогрів')} copyLabel="Прогрів копіювання" />
    </MantineProvider>,
  );

  await screen.findByRole('button', { name: 'Прогрів копіювання' });
  cleanup();
});

afterEach(() => {
  cleanup();
});

describe('EmptyState — окремий компонент', () => {
  it('пояснює і пропонує дію; без даних нічого зайвого не малює', () => {
    show(
      <EmptyState
        title="Фільтр нічого не знайшов"
        hint="Спробуйте зняти фільтр за періодом."
        action={<button type="button">Зняти фільтри</button>}
      />,
    );

    expect(screen.getByText('Фільтр нічого не знайшов')).toBeDefined();
    expect(screen.getByText('Спробуйте зняти фільтр за періодом.')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Зняти фільтри' })).toBeDefined();

    // ⛔ `L10`: це НЕ помилка. Порожньо й помилка не сміють виглядати
    // однаково, і `role="alert"` — саме те, чим вони розрізняються для читалки.
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('без hint і без action лишається сам заголовок', () => {
    show(<EmptyState title="Порожньо" />);

    expect(screen.getByText('Порожньо')).toBeDefined();
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('ForbiddenState — окремий компонент', () => {
  it('показує відмову з кодом і кореляцією, але БЕЗ кнопки «повторити»', () => {
    show(<ForbiddenState error={forbidden()} />);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Немає права на цей ресурс');
    expect(alert.textContent).toContain('ECR-AUTH-0403');
    expect(alert.textContent).toContain('cid-state-403');

    // Право не з'являється від повторного запиту того самого користувача.
    expect(screen.queryByRole('button')).toBeNull();
  });
});

/**
 * ⛔ `ErrorState` — обгортка навколо `ErrorAlert`, а не друга його редакція.
 * Доводиться і те, і те: що код із кореляцією показує САМЕ `ErrorAlert`
 * (розмітка `Alert` із `role="alert"`, кнопка «повторити» його ж), і що
 * `ErrorState` додає рівно одне — копіювання.
 */
describe('ErrorState — код, кореляція і Copy через ErrorAlert', () => {
  it('без copyLabel рендерить ДОСЛІВНО ErrorAlert: одна кнопка, і та — «повторити»', () => {
    const retry = vi.fn();
    show(<ErrorState error={refusal()} onRetry={retry} />);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Період закрито');
    expect(alert.textContent).toContain('HTTP-409');
    expect(alert.textContent).toContain('cid-state-1');

    // ⛔ Мутаційний доказ у бік «зробили копіювання завжди»: тоді тут було б
    // ДВІ кнопки, а безіменна кнопка копіювання ще й завалила б `button-name`
    // у гейті a11y.
    expect(screen.getAllByRole('button')).toHaveLength(1);

    screen.getByRole('button').click();
    expect(retry).toHaveBeenCalledOnce();
  });

  it('із copyLabel з’являється кнопка копіювання — і копіює САМЕ correlation id', async () => {
    show(<ErrorState error={refusal('cid-copy-42')} copyLabel="Скопіювати кореляцію" />);

    // Показ коду й кореляції лишається за `ErrorAlert` — обидва в тому ж alert.
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('HTTP-409');
    expect(alert.textContent).toContain('cid-copy-42');

    const copy = screen.getByRole('button', { name: 'Скопіювати кореляцію' });

    await act(async () => {
      copy.click();
    });

    /*
     * ⛔ Найважливіше твердження файлу — саме ЗНАЧЕННЯ, а не факт натискання.
     * Мутаційний доказ: замініть у `ErrorState` `value={correlationId}` на
     * `value={error.problem.errorCode}` — кнопка лишиться на місці, тест
     * падає. Приберіть кнопку зовсім — падає інакше, на пошуку за іменем.
     */
    expect(written).toEqual(['cid-copy-42']);
  });

  it('кнопка копіювання НЕ підміняє «повторити» — доступні обидві дії', () => {
    const retry = vi.fn();
    show(<ErrorState error={refusal()} onRetry={retry} copyLabel="Скопіювати кореляцію" />);

    expect(screen.getByRole('button', { name: 'Скопіювати кореляцію' })).toBeDefined();

    screen.getByRole('button', { name: '⟦common.retry⟧' }).click();
    expect(retry).toHaveBeenCalledOnce();
  });

  it('помилка без кореляції (не відповідь сервера) кнопки копіювання не отримує', () => {
    // ⚠ Мережевий збій до сервера не дійшов — кореляції не існує, і кнопка
    // «скопіювати» обіцяла б підтримці ідентифікатор, якого немає (`D15-06`).
    show(<ErrorState error={new Error('Failed to fetch')} copyLabel="Скопіювати кореляцію" />);

    expect(screen.queryByRole('button', { name: 'Скопіювати кореляцію' })).toBeNull();
    expect(screen.getByRole('alert')).toBeDefined();
  });

  it('без помилки не рендерить нічого', () => {
    const { container } = render(
      <MantineProvider>
        <ErrorState error={null} copyLabel="Скопіювати кореляцію" />
      </MantineProvider>,
    );

    expect(container.querySelector('[role="alert"]')).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
  });
});

/**
 * ⛔ Сторож сумісності: обгортка тепер ходить через `ErrorState`/
 * `ForbiddenState`, і саме тут доводиться, що це переливання, а не зміна
 * поведінки.
 */
describe('AsyncBoundary через винесені стани — поведінка та сама', () => {
  it('помилка всередині обгортки лишається однією кнопкою «повторити»', () => {
    const retry = vi.fn();

    show(
      <AsyncBoundary<string[]>
        isPending={false}
        error={refusal()}
        data={undefined}
        onRetry={retry}
      >
        {() => <div />}
      </AsyncBoundary>,
    );

    // ⛔ Рівно ОДНА кнопка. Якби обгортка почала просити копіювання, чинний
    // `AsyncBoundary.test.tsx` упав би на `getByRole('button')` — «знайдено
    // дві», тобто не на своїй причині.
    expect(screen.getAllByRole('button')).toHaveLength(1);

    screen.getByRole('button').click();
    expect(retry).toHaveBeenCalledOnce();
  });

  it('403 усередині обгортки — той самий ForbiddenState', () => {
    show(
      <AsyncBoundary<string[]> isPending={false} error={forbidden()} data={undefined}>
        {() => <div />}
      </AsyncBoundary>,
    );

    expect(screen.getByRole('alert').textContent).toContain('cid-state-403');
    expect(screen.queryByRole('button')).toBeNull();
  });
});
