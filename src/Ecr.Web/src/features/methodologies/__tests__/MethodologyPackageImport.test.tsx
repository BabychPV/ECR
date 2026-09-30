import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyPackageImport } from '@/features/methodologies/MethodologyPackageImport';
import { testTheme } from '@/test/render';

/**
 * «Імпорт пакета» методологій (крок V, FEATURE-HSE301-VIEW §11.6).
 *
 * ⛔ Сервер — `fetch`-стаб: адреса з `dryRun` і відмова 422 проходять той самий
 * розбір (`apiFetch` → `EcrApiError` → `report`), що й у продукті.
 *
 * Мутаційні докази: звести умову кнопки «Імпортувати» до `pkg === null`
 * → червоний «запис недоступний до перевірки»; прибрати
 * `reportFromError` в `onError` → червоний «блокери з відмови 422 видно».
 */
const Report = {
  dryRun: true,
  applied: false,
  outcome: 'created',
  methodologies: [],
  blockers: [],
  conflicts: [],
  warnings: [],
  totals: { methodologiesToCreate: 2, versionsToCreate: 2, versionsUnchanged: 0, formulas: 2, constants: 1, imports: 1 },
};

const sent: { url: string; method: string }[] = [];

function mockServer(respond: (url: string) => { status: number; body: unknown; problem?: boolean }): void {
  sent.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });
      const answer = respond(String(url));

      return new Response(JSON.stringify(answer.body), {
        status: answer.status,
        headers: { 'Content-Type': answer.problem === true ? 'application/problem+json' : 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyPackageImport />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function choosePackage(): Promise<void> {
  fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importPackage⟧' }));
  const input = await waitFor(() => {
    const found = document.querySelector('input[type="file"]');
    if (found === null) throw new Error('немає поля файлу');
    return found;
  });
  const file = new File([JSON.stringify({ format: 'ecr-methodology-package', version: 1 })], 'package.json', {
    type: 'application/json',
  });
  fireEvent.change(input, { target: { files: [file] } });
  await waitFor(() =>
    expect(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }).hasAttribute('disabled')).toBe(false),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyPackageImport', () => {
  it('запис недоступний до перевірки; перевірка шле dryRun=true і показує підсумки', async () => {
    mockServer(() => ({ status: 200, body: Report }));
    show();
    await choosePackage();

    const apply = screen.getByRole('button', { name: '⟦methodologies.importApply⟧' });
    expect(apply.hasAttribute('disabled')).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));

    await screen.findByText('⟦methodologies.importOutcomeCreated⟧');
    expect(sent).toEqual([{ url: '/api/v1/methodologies/import?dryRun=true', method: 'POST' }]);
    expect(screen.getByRole('button', { name: '⟦methodologies.importApply⟧' }).hasAttribute('disabled')).toBe(false);
  });

  it('блокери з відмови 422 видно в діалозі', async () => {
    mockServer((url) =>
      url.endsWith('dryRun=true')
        ? { status: 200, body: Report }
        : {
            status: 422,
            problem: true,
            body: {
              title: 'Unprocessable',
              status: 422,
              errorCode: 'ECR-CALC-0422',
              messageKey: 'err.ECR-CALC-0422.methodologyImportBlocked',
              report: {
                ...Report,
                dryRun: false,
                outcome: 'blocked',
                blockers: [{ kind: 'unresolvedFormula', methodology: 'M1', version: 'V1', subject: 'F1', detail: '!Nowhere' }],
              },
            },
          },
    );
    show();
    await choosePackage();

    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' }));
    await screen.findByText('⟦methodologies.importOutcomeCreated⟧');
    fireEvent.click(screen.getByRole('button', { name: '⟦methodologies.importApply⟧' }));

    await screen.findByText('!Nowhere');
    expect(screen.getByText('⟦methodologies.importOutcomeBlocked⟧')).toBeDefined();
    expect(sent.at(-1)).toEqual({ url: '/api/v1/methodologies/import?dryRun=false', method: 'POST' });
  });
});

/**
 * Фокус після перевірки (WCAG 2.4.3): «Перевірити» на час запиту `loading` (= `disabled`) і втрачає фокус —
 * звіт забирає його собі, щоб читач почув підсумок, а `Tab` вів до блокерів.
 *
 * Мутаційний доказ (перевірено руками 2026-09-30): прибрати `reportRef.current?.focus()` → червоний.
 */
describe('MethodologyPackageImport: фокус', () => {
  it('звіт перевірки отримує фокус і має доступне ім’я — підсумок', async () => {
    mockServer(() => ({ status: 200, body: Report }));
    show();
    await choosePackage();

    const check = screen.getByRole('button', { name: '⟦methodologies.importCheck⟧' });
    check.focus();
    fireEvent.click(check);

    const report = await waitFor(() => {
      const found = document.querySelector<HTMLElement>('[data-import-report]');
      if (found === null) throw new Error('звіту ще немає');
      return found;
    });
    await waitFor(() => expect(document.activeElement).toBe(report));
    expect(report.getAttribute('aria-label')).toBe('⟦methodologies.importOutcomeCreated⟧');
  });
});
