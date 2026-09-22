import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { AccessMatrixDto } from '@/api/types';
import { AccessMatrix } from '../AccessMatrix';
import { testTheme } from '@/test/render';

/**
 * Матриця доступу: застереження «залежить від даних» і причина блокування
 * клітинки були в Mantine `Tooltip` — лише під мишею. Бейдж і знак клітинки —
 * не фокусовані елементи, тож з клавіатури пояснення не було видно НІКОЛИ, а
 * читач не отримував опису.
 *
 * ⛔ Мутаційний доказ: повернути `Tooltip` у `AccessMatrix.tsx` — тести
 * червоні (немає `tabindex`, немає опису, фокус не дає `role="tooltip"`).
 */
const Matrix: AccessMatrixDto = {
  periodCount: 2,
  presentationRevision: 1,
  sheets: [
    {
      sheetDefId: 11,
      code: 'S1',
      nameL10n: { en: 'Balance' },
      dependsOnData: true,
      cells: [
        { periodSequence: 1, state: 'Editable', reason: 'None', detail: null },
        { periodSequence: 2, state: 'Blocked', reason: 'PeriodWindow', detail: 'Locked by the period window rule' },
      ],
    },
  ],
} as unknown as AccessMatrixDto;

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/access-matrix')) {
        return new Response(JSON.stringify(Matrix), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

async function open(): Promise<HTMLElement> {
  mockFetch();
  const user = userEvent.setup();

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <AccessMatrix templateVersionId={5} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  await user.click(screen.getByRole('button', { name: /version\.accessMatrix/ }));

  return screen.findByRole('table');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AccessMatrix: пояснення доступні з клавіатури', () => {
  it('бейдж «залежить від даних»: у порядку табуляції, з описом, фокус показує підказку', async () => {
    const table = await open();

    const badge = within(table).getByText(/version\.accessMatrixData(?!Hint)/).closest('[aria-describedby]');

    expect(badge).not.toBeNull();
    expect((badge as HTMLElement).tabIndex).toBe(0);
    expect(within(table).getAllByRole('generic', { description: /version\.accessMatrixDataHint/ })).toContain(badge);

    (badge as HTMLElement).focus();
    expect((await screen.findByRole('tooltip')).textContent).toMatch(/version\.accessMatrixDataHint/);
  });

  it('клітинка з причиною: знак названий станом, причина — опис і підказка на фокусі', async () => {
    const table = await open();

    const blocked = within(table).getByRole('img', {
      name: /version\.accessBlocked/,
      description: 'Locked by the period window rule',
    });

    expect(blocked.tabIndex).toBe(0);

    blocked.focus();
    expect((await screen.findByRole('tooltip')).textContent).toContain('Locked by the period window rule');
  });

  it('клітинка без причини не додає зупинки табуляції — назва стану вже в aria-label', async () => {
    const table = await open();

    const editable = within(table).getByRole('img', { name: /version\.accessEditable/ });

    expect(editable.hasAttribute('tabindex')).toBe(false);
  });
});
