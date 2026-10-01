import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReportDefinitionsModal } from '@/features/reports/ReportDefinitionsModal';
import { testTheme } from '@/test/render';

const apiFetch = vi.hoisted(() => vi.fn((_path: string, _init?: RequestInit) => Promise.resolve([])));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch,
}));

/**
 * Недоступні дії форми опису звіту пояснюють, ЧОМУ вони недоступні.
 *
 * Раніше «Add» і «×» колонки були просто `disabled`: без фокуса, без наведення,
 * без жодного слова, яке з полів заважає.
 *
 * Мутаційні докази: повернути `disabled={cannotCreate}` без `DisabledReason` —
 * кнопка втрачає опис причини, перший тест червоніє; прибрати гасіння кліку в
 * `DisabledReason` — POST іде з порожньою формою, перший тест червоніє; дати
 * `×` причину завжди (`reason` без умови) — другий тест червоніє на другій колонці.
 */
const SlowEnvTimeout = 400_000;

function renderModal(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ReportDefinitionsModal opened onClose={() => undefined} definitions={[]} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Текст опису за `aria-describedby` — те, що читач озвучить разом із назвою. */
function description(element: Element | undefined): string {
  const ids = element?.getAttribute('aria-describedby')?.split(' ') ?? [];
  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ').trim();
}

function postCalls(): unknown[][] {
  return apiFetch.mock.calls.filter(([, init]) => init?.method === 'POST');
}

beforeEach(() => {
  apiFetch.mockClear();
});

describe('ReportDefinitionsModal: причина недоступної дії', () => {
  it(
    'порожня форма: «Add» у фокусі, описаний причиною, клік не шле запиту',
    async () => {
      renderModal();

      const add = await screen.findByRole('button', { name: '⟦reportDefs.add⟧' }, { timeout: SlowEnvTimeout });
      expect(add.getAttribute('aria-disabled')).toBe('true');
      expect((add as HTMLButtonElement).disabled).toBe(false);
      expect(description(add)).toBe('⟦reportDefs.addBlocked⟧');

      fireEvent.click(add);
      expect(postCalls()).toHaveLength(0);
    },
    SlowEnvTimeout,
  );

  it(
    '«×» єдиної колонки пояснює, чому її не прибрати; з двома колонками — звичайна дія',
    async () => {
      renderModal();

      const [only] = await screen.findAllByRole(
        'button',
        { name: '⟦reportDefs.removeColumn⟧' },
        { timeout: SlowEnvTimeout },
      );
      expect(only?.getAttribute('aria-disabled')).toBe('true');
      expect(description(only)).toBe('⟦reportDefs.removeColumnBlocked⟧');

      fireEvent.click(only as HTMLElement);
      expect(screen.getAllByRole('button', { name: '⟦reportDefs.removeColumn⟧' })).toHaveLength(1);

      fireEvent.click(screen.getByRole('button', { name: '⟦reportDefs.addColumn⟧' }));
      const both = screen.getAllByRole('button', { name: '⟦reportDefs.removeColumn⟧' });
      expect(both).toHaveLength(2);
      for (const button of both) {
        expect(button.getAttribute('aria-disabled')).toBeNull();
        expect(description(button)).toBe('');
      }

      fireEvent.click(both[1] as HTMLElement);
      expect(screen.getAllByRole('button', { name: '⟦reportDefs.removeColumn⟧' })).toHaveLength(1);
    },
    SlowEnvTimeout,
  );
});

/**
 * reports-walk: «New version» теж пояснює недоступність.
 *
 * Мутаційний доказ: повернути голий `disabled` замість `DisabledReason` —
 * тест червоний (немає `aria-describedby` з причиною).
 */
describe('ReportDefinitionsModal: причина недоступної нової версії', () => {
  it(
    'звіт не обрано: «New version» у фокусі, описана «pick a report», клік не шле запиту',
    async () => {
      renderModal();

      const buttons = await screen.findAllByRole('button', { name: '⟦reportDefs.newVersion⟧' }, { timeout: SlowEnvTimeout });
      const next = buttons[buttons.length - 1];
      expect(next?.getAttribute('aria-disabled')).toBe('true');
      expect(description(next)).toBe('⟦snapshots.pickReport⟧');

      if (next !== undefined) fireEvent.click(next);
      expect(postCalls()).toHaveLength(0);
    },
    SlowEnvTimeout,
  );
});
