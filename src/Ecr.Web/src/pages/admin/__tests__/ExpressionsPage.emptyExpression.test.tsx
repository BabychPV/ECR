import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';
import { testTheme } from '@/test/render';

/**
 * `U-07` (`docs/build/UX-PASS-2026-09-23.md`): щойно відкритий
 * `/admin/expressions` показував під ПОРОЖНІМ редактором червоне
 * «Findings: 1» і `ECR-TMPL-0422 Unexpected token ""` — користувач ще нічого
 * не набрав, а екран уже казав, що він помилився.
 *
 * ⛔ Три твердження, і кожне стереже свій бік:
 * 1. порожній редактор — ні запиту перевірки, ні знахідок, лише нейтральна
 *    підказка (`expressions.startTyping`);
 * 2. непорожній хибний вираз — знахідка Є (правка не «вимкнула перевірку»);
 * 3. вираз стерли до порожнього — знахідки зникають, і на їх місці НЕ зелене
 *    «No findings» (порожньо ≠ перевірено, `A7-28`).
 *
 * ⚠ Monaco підмінено фейком (`D1-12`: справжній у jsdom коштує хвилини), але
 * `ExpressionEditor` і сторінка — справжні: предмет тесту саме їхня зв'язка.
 */

// Фейковий редактор, до якого тест «друкує» — як людина в Monaco.
let fakeEditor: { setValue: (next: string) => void } | null = null;

vi.mock('@/features/expressions/monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-template',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: () => {},
    create: (_host: unknown, options: { value: string }) => {
      let value = options.value;
      let changeHandler: (() => void) | null = null;

      const instance = {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          changeHandler?.();
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          changeHandler = handler;
        },
        dispose: () => {},
      };

      fakeEditor = instance;
      return instance;
    },
  };
});

const validateBodies: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }

      if (path.endsWith('/api/v1/expressions/validate')) {
        const body = JSON.parse(String(init?.body ?? '{}')) as { expression?: string };
        validateBodies.push(body.expression ?? '');

        // Сервер поводиться як справжній: на будь-що, зокрема на `""`, що
        // не розбирається, — `ECR-TMPL-0422`.
        return json({
          diagnostics: [
            {
              code: 'ECR-TMPL-0422',
              message: `Unexpected token "${body.expression ?? ''}"`,
              position: 0,
              length: 0,
              severity: 'Error',
              messageKey: null,
              messageParams: null,
            },
          ],
          resultType: null,
          skippedChecks: [],
        });
      }

      if (path.includes('/api/v1/expressions/metadata')) {
        return json({ functions: [], constants: [] });
      }

      if (path.endsWith('/api/v1/templates')) {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ExpressionsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Більше за `ValidateDelay` (400 мс) — щоб перевірка встигла б піти, якби йшла. */
async function pastValidateDelay(): Promise<void> {
  await act(async () => {
    await new Promise((r) => setTimeout(r, 600));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  validateBodies.length = 0;
  fakeEditor = null;
});

describe('ExpressionsPage: порожній вираз не є помилкою (U-07)', () => {
  it(
    'порожньо — ні запиту, ні знахідок; хибний вираз — знахідка; стерли — нейтрально, не зелено',
    async () => {
      mockServer();
      show();

      await waitFor(() => expect(fakeEditor).not.toBeNull(), { timeout: 30_000 });
      await pastValidateDelay();

      // 1. Щойно відкрито.
      expect(validateBodies).toEqual([]);
      expect(screen.queryByText(/expressions\.findings/)).toBeNull();
      expect(screen.queryByText('ECR-TMPL-0422')).toBeNull();
      expect(screen.getByText(/expressions\.startTyping⟧/)).toBeDefined();

      // 2. Людина набрала хибний вираз — перевірка працює.
      act(() => fakeEditor?.setValue('1 +'));

      expect(await screen.findByText('ECR-TMPL-0422', {}, { timeout: 10_000 })).toBeDefined();
      expect(screen.getByText(/expressions\.findings/)).toBeDefined();
      expect(validateBodies).toEqual(['1 +']);

      // 3. Стерла до порожнього (і пробіли — теж «нічого»).
      act(() => fakeEditor?.setValue('   '));
      await pastValidateDelay();

      expect(validateBodies).toEqual(['1 +']);
      expect(screen.queryByText('ECR-TMPL-0422')).toBeNull();
      expect(screen.queryByText(/expressions\.findings/)).toBeNull();
      expect(screen.queryByText(/expressions\.noFindings/)).toBeNull();
      expect(screen.getByText(/expressions\.startTyping⟧/)).toBeDefined();
    },
    120_000,
  );
});
