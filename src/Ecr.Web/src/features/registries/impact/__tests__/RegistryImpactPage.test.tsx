import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  documentHref,
  impactPollInterval,
  RegistryImpactPage,
  viaLabel,
} from '../RegistryImpactPage';
import { PollMs } from '@/features/workflow/jobFollow';
import type { JobStatus } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * Сторінка впливу довідника (RT-25): перелік зачеплених документів відкритих періодів і постановка
 * їх перерахунку з показом задачі.
 *
 * ⚠ Каталог рядків не завантажений — підписи приходять ключами в `⟦…⟧`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

interface Server {
  readonly posts: { url: string; body: unknown }[];
  readonly jobReads: () => number;
}

const Impact = {
  items: [
    { documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: ['methodology:HSE301'] },
    {
      documentId: 7,
      businessKey: 'DOC7',
      periodKey: 202608,
      periodState: 'Grace',
      via: ['methodology:FLARE', 'methodology:HSE301'],
    },
  ],
  total: 2,
  truncated: false,
};

function mockServer(
  permissions: string[],
  impact: unknown = Impact,
  job: () => Response = () => json({ jobId: 'IRegistryImpactRecalculationJob#1', state: 'Running', percent: 10, message: 'reading', error: null }),
): Server {
  const posts: { url: string; body: unknown }[] = [];
  let jobReads = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/impact')) return json(impact);

      if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) });
        return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
      }

      if (path.includes('/api/v1/jobs/')) {
        jobReads += 1;
        return job();
      }

      return json(null);
    }),
  );

  return { posts, jobReads: () => jobReads };
}

function show(code = 'COMPONENT'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/registries/${code}/impact`]}>
          <Routes>
            <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function confirmWithReason(reason: string): Promise<void> {
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: reason } });
  fireEvent.click(within(dialog).getByRole('button', { name: /registries\.impact\.recalculateConfirm/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage: зачеплені документи і їх перерахунок', () => {
  it('показує документи з періодом і методологіями, посилання веде в той самий період', async () => {
    mockServer(['Registry.View']);
    show();

    const link = await screen.findByRole('link', { name: 'DOC7' });
    expect(link.getAttribute('href')).toBe('/documents/7?periodKey=202608');

    const row = document.querySelector<HTMLElement>('[data-impact-row="7"]') as HTMLElement;
    expect(row.textContent).toContain('FLARE, HSE301');
    expect(row.textContent).not.toContain('methodology:');
  });

  it('без Calculation.Recalculate кнопки перерахунку й вибору немає', async () => {
    mockServer(['Registry.View']);
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    expect(document.querySelector('[data-impact-recalculate]')).toBeNull();
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
  });

  it('нічого не обрано — ставить усі (documentIds: null) з причиною і показує задачу', async () => {
    const server = mockServer(['Registry.View', 'Calculation.Recalculate']);
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('склад газу оновлено');

    await waitFor(() => expect(server.posts).toHaveLength(1));
    expect(server.posts[0]!.url).toBe('/api/v1/registries/COMPONENT/recalculate-impacted');
    expect(server.posts[0]!.body).toEqual({ documentIds: null, reason: 'склад газу оновлено' });

    await waitFor(() => expect(server.jobReads()).toBeGreaterThan(0));
    const card = await waitFor(() => {
      const found = document.querySelector<HTMLElement>('[data-impact-job]');
      if (found === null) throw new Error('картки задачі ще немає');
      return found;
    });
    await waitFor(() => expect(card.textContent).toContain('reading'));
  });

  it('обрані документи йдуть переліком, а не «усі»', async () => {
    const server = mockServer(['Registry.View', 'Calculation.Recalculate']);
    show();

    fireEvent.click(await screen.findByRole('checkbox', { name: /DOC7/ }));
    fireEvent.click(screen.getByRole('button', { name: /registries\.impact\.recalculateSelected/ }));
    await confirmWithReason('лише DOC7');

    await waitFor(() => expect(server.posts).toHaveLength(1));
    expect(server.posts[0]!.body).toEqual({ documentIds: [7], reason: 'лише DOC7' });
  });

  it('підсумок розкладу показується тим самим рядком, що й у черзі задач', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate'], Impact, () =>
      json({
        jobId: 'IRegistryImpactRecalculationJob#1',
        state: 'Succeeded',
        effectiveState: 'Succeeded',
        percent: 100,
        message: null,
        error: null,
        fanOut: { total: 2, queued: 0, running: 0, succeeded: 2, failed: 0 },
      }),
    );
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('причина');

    await waitFor(() =>
      expect(document.querySelector('[data-impact-fanout]')?.textContent).toContain('jobs.fanOutProgress'),
    );
    expect(document.querySelector('[data-impact-fanout]')?.textContent).toContain('done=2');
  });

  it('бейдж стану — похідний стан розкладу: з помилками не читається як «Succeeded»', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate'], Impact, () =>
      json({
        jobId: 'IRegistryImpactRecalculationJob#1',
        state: 'Succeeded',
        effectiveState: 'SucceededWithErrors',
        percent: 100,
        message: null,
        error: null,
        fanOut: { total: 2, queued: 0, running: 0, succeeded: 1, failed: 1 },
      }),
    );
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('причина');

    // ⛔ Мутація «бейдж за `status.state`» показує тут «Succeeded».
    expect(await screen.findByText(/status\.job\.SucceededWithErrors/)).toBeTruthy();
    expect(screen.queryByText(/status\.job\.Succeeded⟧/)).toBeNull();
  });

  it('стан задачі не прочитати — так і сказано, а не вічний прогрес', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate'], Impact, () =>
      json({ title: 'Forbidden', status: 403 }, 403),
    );
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('причина');

    await waitFor(() => expect(document.querySelector('[data-impact-job-state="unknown"]')).not.toBeNull());
  });

  it('обрізаний сервером перелік попереджає, що документів більше', async () => {
    mockServer(['Registry.View'], { ...Impact, total: 1000, truncated: true });
    show();

    await waitFor(() => expect(document.querySelector('[data-impact-truncated]')).not.toBeNull());
  });

  it('порожній перелік — кнопка перерахунку неактивна', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate'], { items: [], total: 0, truncated: false });
    show();

    await screen.findByText(/registries\.impact\.empty⟧/);
    const button = screen.getByRole('button', { name: /registries\.impact\.recalculateAll/ }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });
});

describe('RegistryImpactPage: чисті функції', () => {
  it('позначка методології стає її кодом, інша лишається як є', () => {
    expect(viaLabel('methodology:HSE301')).toBe('HSE301');
    expect(viaLabel('rule:X')).toBe('rule:X');
  });

  it('адреса документа несе період', () => {
    expect(documentHref({ documentId: 3, periodKey: 202601 })).toBe('/documents/3?periodKey=202601');
  });

  it('опитування триває, поки батько розклав, а дочірні не пораховані', () => {
    const base = { jobId: 'x', percent: 100, message: null, error: null } as unknown as JobStatus;

    expect(impactPollInterval(undefined)).toBe(PollMs);
    expect(impactPollInterval({ ...base, state: 'Running' })).toBe(PollMs);
    expect(impactPollInterval({ ...base, state: 'Succeeded', effectiveState: 'FannedOut' })).toBe(PollMs);
    expect(impactPollInterval({ ...base, state: 'Succeeded', effectiveState: 'Succeeded' })).toBe(false);
    expect(impactPollInterval({ ...base, state: 'Failed' })).toBe(false);
  });
});

/**
 * Фокус клавіатури після «Перерахувати» (WCAG 2.4.3): кнопка на час запиту `loading` (= `disabled`), діалог
 * причини закрився — фокус мав куди піти.
 *
 * Мутаційні докази (перевірено руками 2026-09-30): прибрати `heading.current?.focus()` в `ImpactJob` →
 * червоний «задачу поставлено»; прибрати `recalculateFocus.arm()` в `onConfirm` → червоний «відмова».
 */
describe('RegistryImpactPage: фокус після постановки перерахунку', () => {
  it('задачу поставлено — фокус на заголовку картки задачі', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate']);
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('склад газу оновлено');

    await waitFor(() =>
      expect(document.activeElement?.hasAttribute('data-impact-job-heading')).toBe(true),
    );
  });

  it('відмова постановки — фокус назад на кнопку перерахунку, а не на <body>', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate']);
    const answer = globalThis.fetch;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input).includes('/recalculate-impacted')) {
          // ⚠ Відповідь не миттєва (інакше `Modal` встигає повернути фокус на вже активну кнопку), і
          // браузер знімає фокус із кнопки, що стала `disabled`, — jsdom ні, тож відтворюємо це тут.
          await new Promise((resolve) => setTimeout(resolve, 50));
          (document.activeElement as HTMLElement | null)?.blur();
          return new Response(JSON.stringify({ title: 'no', status: 422, code: 'ECR-X' }), {
            status: 422,
            headers: { 'Content-Type': 'application/problem+json' },
          });
        }
        return answer(input, init);
      }),
    );
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    const button = await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ });
    // ⚠ Клік у браузері ставить фокус на кнопку; `fireEvent.click` у jsdom — ні.
    button.focus();
    fireEvent.click(button);
    await confirmWithReason('причина');

    await waitFor(() => expect(document.activeElement).toBe(button));
  });
});

describe('RegistryImpactPage: відмова сервера на постановці', () => {
  it('422 показується відмовою з текстом сервера, а не карткою чи станом задачі', async () => {
    mockServer(['Registry.View', 'Calculation.Recalculate']);
    const answer = globalThis.fetch;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (String(input).includes('/recalculate-impacted')) {
          return new Response(
            JSON.stringify({
              title: 'Період закрито для перерахунку',
              status: 422,
              code: 'ECR-X',
            }),
            { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }
        return answer(input, init);
      }),
    );
    show();

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    await confirmWithReason('причина');

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Період закрито для перерахунку');
    expect(document.querySelector('[data-impact-job]')).toBeNull();
    expect(document.querySelector('[data-impact-job-state]')).toBeNull();
  });
});
