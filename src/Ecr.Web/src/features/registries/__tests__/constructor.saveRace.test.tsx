import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * L9-39 (AUDIT-2026-10-03, 1E): правки, внесені поки зберігається чернетка, не затираються.
 *
 * Після збереження панель перечитує чернетку, і сторінка засіває нею форму. Сервер повертає рівно
 * НАДІСЛАНЕ, тож усе, що людина дописала, поки запит їхав, мовчки відкочувалось.
 *
 * Мутаційний доказ (перевірено руками 2026-10-04): прибрати ранній `return` за `sameDraft` у засіві
 * `RegistryConstructorPage` → червоний перший тест; другий (без правок — форма засівається
 * збереженим) лишається зеленим. Пропуск засіву без порівняння `rowVersion` (рев'ю AN-35, P3) →
 * червоний третій: новіша чернетка сусіда не потрапляла у форму.
 */

const Code = 'FUEL';
const SlowEnvTimeout = 60_000;

interface Server {
  readonly puts: { reason: string }[];
  release: () => void;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stub(neighbor = false): Server {
  let saved: Record<string, unknown> | null = null;
  const server: Server = { puts: [], release: () => undefined };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (/\/api\/v1\/me(\?|$)/.test(url)) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Registry.View', 'Registry.EditDefinition'], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }

      if (url.includes('/definition/draft')) {
        if (method === 'PUT') {
          const body = JSON.parse(String(init?.body)) as Record<string, unknown> & { reason: string };
          server.puts.push(body);
          // Запит «їде», доки тест його не відпустить.
          await new Promise<void>((resolve) => {
            server.release = resolve;
          });
          saved = { ...body, baseDefinitionVersion: 3, updatedAt: '2026-10-04T10:00:00Z', updatedByUserId: 1, rowVersion: 'AQID' };
          return json(saved);
        }

        // Сусід зберіг свою чернетку між нашим `PUT` і перечитуванням.
        if (neighbor && saved !== null) {
          return json({ definitionVersion: 3, draft: { ...saved, reason: 'причина сусіда', rowVersion: 'NEIGHBOR' } });
        }

        return json({ definitionVersion: 3, draft: saved });
      }

      if (url.includes('/definition')) {
        return json({
          id: 4,
          code: Code,
          nameL10n: { values: { en: 'Fuel types' } },
          isTemporal: false,
          sourceKind: 'Local',
          definitionVersion: 3,
          dataRevision: 11,
          fields: [],
          relations: [],
          rules: [
            {
              id: 101,
              code: 'PublishedRule',
              ruleKind: 'Expression',
              expression: '[Value] > 1',
              severity: 'Error',
              messageL10n: { values: { en: 'Published rule' } },
              parametersJson: null,
              isActive: true,
            },
          ],
          mappings: [],
        });
      }

      if (url.includes('/history')) return json([]);
      if (url.includes('/api/v1/users')) return json({ items: [], total: 0 });
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (/\/api\/v1\/registries(\?|$)/.test(url)) return json([]);

      return json(null);
    }),
  );

  return server;
}

function showPage(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/admin/registries/${Code}/definition`]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/admin/registries/:code/definition" element={<RegistryConstructorPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function reasonInput(): HTMLInputElement {
  const node = document.querySelector('[data-draft-reason]');
  if (node === null) throw new Error('поля причини ще немає');
  return (node instanceof HTMLInputElement ? node : node.querySelector('input')) as HTMLInputElement;
}

function saveButton(): HTMLButtonElement {
  return document.querySelector('[data-save-draft]') as HTMLButtonElement;
}

async function saveWithReason(server: Server, reason: string): Promise<void> {
  const input = await waitFor(reasonInput, { timeout: SlowEnvTimeout });
  fireEvent.change(input, { target: { value: reason } });
  await waitFor(() => expect(saveButton().disabled).toBe(false), { timeout: SlowEnvTimeout });
  fireEvent.click(saveButton());
  await waitFor(() => expect(server.puts).toHaveLength(1), { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryConstructorPage: правки під час збереження чернетки (L9-39)', () => {
  it(
    'дописане, поки запит їхав, лишається у формі після перечитування чернетки',
    async () => {
      const server = stub();
      showPage();

      await saveWithReason(server, 'перша причина');
      fireEvent.change(reasonInput(), { target: { value: 'перша причина і ще дописане' } });

      server.release();

      // Перечитана чернетка вже на екрані (банер), а форма — новіша за неї.
      await waitFor(() => expect(document.querySelector('[data-testid="registry-draft-present"]')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(reasonInput().value).toBe('перша причина і ще дописане');
      expect(server.puts[0]?.reason).toBe('перша причина');
    },
    SlowEnvTimeout,
  );

  it(
    'без правок під час збереження форма засівається збереженою чернеткою',
    async () => {
      const server = stub();
      showPage();

      await saveWithReason(server, 'лише так');
      server.release();

      await waitFor(() => expect(document.querySelector('[data-testid="registry-draft-present"]')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });
      expect(reasonInput().value).toBe('лише так');
    },
    SlowEnvTimeout,
  );

  it(
    'перечитано новішу чернетку сусіда — вона засіває форму, навіть якщо ми правили далі',
    async () => {
      const server = stub(true);
      showPage();

      await saveWithReason(server, 'наша причина');
      fireEvent.change(reasonInput(), { target: { value: 'наша причина і ще дописане' } });

      server.release();

      await waitFor(() => expect(reasonInput().value).toBe('причина сусіда'), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );
});
