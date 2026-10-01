import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';
import type { RowWindowMap } from '../rowWindowApi';
import { RowWindowMapsPanel } from '../RowWindowMapsPanel';

/**
 * Розділ «Прив'язки PI за вікном рядка» вкладки «Події з PI» (HSE301 A1) проти заглушки `fetch`: що пішло на
 * сервер (метод, адреса, тіло) і що з відповіді доїхало до людини — перелік із кодами колонок, пауза без зміни
 * решти, відмова видалення з текстом сервера, форма наявної й нової прив'язки.
 */

interface Sent {
  readonly method: string;
  readonly path: string;
  readonly search: string;
  readonly body: unknown;
}

let sent: Sent[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function problem(status: number, errorCode: string, messageKey: string, extra: Record<string, unknown> = {}): Response {
  return json({ title: `err.${errorCode}`, status, errorCode, correlationId: 'c-1', detail: 'server detail', messageKey, ...extra }, status);
}

const Active: RowWindowMap = {
  id: 5,
  tableDefId: 200,
  targetColumnDefId: 3,
  targetColumnCode: 'Volume',
  startColumnDefId: 1,
  startColumnCode: 'Start',
  endColumnDefId: 2,
  endColumnCode: 'End',
  selectorColumnDefId: 4,
  selectorColumnCode: 'Key',
  summary: 'Total',
  isStep: false,
  maxGapSeconds: null,
  targetUnitId: 9,
  minPercentGood: '95.00',
  refetchWithinDays: 7,
  isActive: true,
  rowVersion: '00000000000007D1',
  sources: [{ id: 1, selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
};

const Paused: RowWindowMap = {
  ...Active,
  id: 6,
  targetColumnDefId: 7,
  targetColumnCode: 'Mass',
  selectorColumnDefId: null,
  selectorColumnCode: null,
  summary: 'Average',
  isActive: false,
  sources: [],
};

const Entity = { id: 42, code: 'FLARE_EVENTS', displayName: 'Flare events' };

interface Options {
  maps?: RowWindowMap[];
  remove?: () => Response;
}

function respond({ maps = [Active, Paused], remove = () => new Response(null, { status: 204 }) }: Options = {}): void {
  sent = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = init?.method ?? 'GET';
      const body: unknown = init?.body === undefined || init.body === null ? undefined : JSON.parse(String(init.body));
      sent.push({ method, path, search: url.search, body });

      if (path === '/api/v1/row-window-maps' && method === 'GET') return json(maps);
      if (path === '/api/v1/row-window-maps' && method === 'POST') return json({ ...Active, id: 9, ...(body as object) }, 201);
      if (path === '/api/v1/row-window-maps/5' && method === 'PUT') return json({ ...Active, ...(body as object) });
      if (path === '/api/v1/row-window-maps/6' && method === 'PUT') return json({ ...Paused, ...(body as object) });
      if (path === '/api/v1/row-window-maps/5' && method === 'DELETE') return remove();
      if (path === '/api/v1/documents/100/tables') {
        return json([
          { tableDefId: 200, tableInstanceId: 300, allowsDynamicRows: true, tableCode: 'FLARE_RECORD', sheetCode: 'S1' },
          { tableDefId: 201, tableInstanceId: 301, allowsDynamicRows: false, tableCode: 'FIXED', sheetCode: 'S1' },
        ]);
      }
      if (path === '/api/v1/documents/100/tables/300') {
        return json({
          columns: [
            { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
            { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
            { id: 3, code: 'Volume', header: 'Volume', dataType: 'Decimal', lookupRegistryDefId: null, unitId: null },
            { id: 4, code: 'Key', header: 'Key', dataType: 'String', lookupRegistryDefId: null, unitId: null },
          ],
        });
      }
      if (path === '/api/v1/units') return json([{ id: 9, code: 'Sm3' }, { id: 8, code: 'kg' }]);

      return json(null);
    }),
  );
}

function show(canManage = true): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <RowWindowMapsPanel
            sourceEntityId={42}
            entities={[Entity as never]}
            documents={[{ id: 100, businessKey: 'DOC-000123' } as never]}
            canManage={canManage}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function row(id: number): Promise<HTMLElement> {
  return waitFor(() => {
    const found = document.querySelector<HTMLElement>(`[data-row-window-map="${String(id)}"]`);
    expect(found).not.toBeNull();
    return found as HTMLElement;
  });
}

async function pick(input: HTMLElement | null, option: string): Promise<void> {
  expect(input).not.toBeNull();
  fireEvent.click(input as HTMLElement);
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('RowWindowMapsPanel', () => {
  it('перелік: коди колонок, вікно «початок → кінець», селектор чи «один атрибут», згортка, джерела, стан; запит за сутністю', async () => {
    respond();
    show();

    const active = await row(5);
    expect(active.textContent).toContain('Volume');
    expect(active.textContent).toContain('Start → End');
    expect(active.textContent).toContain('Key');
    expect(active.textContent).toContain('⟦rowWindow.summaryTotal⟧');
    expect(active.textContent).toContain('⟦rowWindow.stateActive⟧');

    const paused = await row(6);
    expect(paused.textContent).toContain('⟦rowWindow.noSelector⟧');
    expect(paused.textContent).toContain('⟦rowWindow.statePaused⟧');

    const list = sent.find((s) => s.method === 'GET' && s.path === '/api/v1/row-window-maps');
    expect(list?.search).toBe('?sourceEntityId=42');
  });

  it('без Integration.Manage кнопок немає (а не вимкнені)', async () => {
    respond();
    show(false);

    await row(5);

    expect(document.querySelector('[data-row-window-create]')).toBeNull();
    expect(document.querySelector('[data-row-window-edit]')).toBeNull();
    expect(document.querySelector('[data-row-window-toggle]')).toBeNull();
    expect(document.querySelector('[data-row-window-delete]')).toBeNull();
  });

  it('пауза: PUT лише з isActive навпаки, решта й rowVersion — як є; перелік перечитується', async () => {
    respond();
    show();

    fireEvent.click((await row(5)).querySelector<HTMLElement>('[data-row-window-toggle="5"]') as HTMLElement);

    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.path).toBe('/api/v1/row-window-maps/5');
    expect(put?.body).toEqual({
      startColumnDefId: 1,
      endColumnDefId: 2,
      selectorColumnDefId: 4,
      summary: 'Total',
      isStep: false,
      targetUnitId: 9,
      minPercentGood: '95.00',
      refetchWithinDays: 7,
      maxGapSeconds: null,
      isActive: false,
      rowVersion: '00000000000007D1',
      sources: [{ selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
    });
    await waitFor(() => expect(sent.filter((s) => s.method === 'GET' && s.path === '/api/v1/row-window-maps').length).toBeGreaterThan(1));
  });

  it('видалення з підтягнутими значеннями (409 rowWindowMapHasValues) — причина сервера в панелі, прив\'язка лишається', async () => {
    respond({ remove: () => problem(409, 'ECR-INT-0409', 'err.ECR-INT-0409.rowWindowMapHasValues', { values: 3 }) });
    show();

    fireEvent.click((await row(5)).querySelector<HTMLElement>('[data-row-window-delete="5"]') as HTMLElement);
    const confirm = await screen.findByRole('dialog');
    fireEvent.click(within(confirm).getByRole('button', { name: '⟦rowWindow.delete⟧' }));

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
    expect(sent.some((s) => s.method === 'DELETE' && s.path === '/api/v1/row-window-maps/5')).toBe(true);
    expect(document.querySelector('[data-row-window-map="5"]')).not.toBeNull();
  });

  it('наявна прив\'язка: «Зберегти» вимкнено, доки не обрано документ; після вибору — PUT без ключа (таблиця, ціль)', async () => {
    respond();
    show();

    fireEvent.click((await row(5)).querySelector<HTMLElement>('[data-row-window-edit="5"]') as HTMLElement);
    const form = await screen.findByRole('dialog');
    const save = within(form).getByRole('button', { name: '⟦common.save⟧' });

    expect(save.hasAttribute('disabled')).toBe(true);
    expect(form.querySelector('[data-row-window-problem="documentRequired"]')).not.toBeNull();
    // Таблиця й колонка-ціль у наявній прив'язці не змінюються.
    expect(form.querySelector<HTMLInputElement>('[data-row-window-table]')?.disabled).toBe(true);
    expect(form.querySelector<HTMLInputElement>('[data-row-window-target]')?.disabled).toBe(true);

    await pick(form.querySelector('[data-row-window-document]'), 'DOC-000123');
    await waitFor(() => expect(sent.some((s) => s.path === '/api/v1/documents/100/tables/300')).toBe(true));
    await waitFor(() => expect(save.hasAttribute('disabled')).toBe(false));

    fireEvent.click(save);

    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.path).toBe('/api/v1/row-window-maps/5');
    expect(put?.body).toMatchObject({ startColumnDefId: 1, endColumnDefId: 2, selectorColumnDefId: 4, isActive: true, rowVersion: '00000000000007D1' });
    expect(put?.body).not.toHaveProperty('tableDefId');
    expect(put?.body).not.toHaveProperty('targetColumnDefId');
  });

  it('нова прив\'язка: усе обирається з колонок таблиці; POST несе таблицю, колонки, одиницю й джерело', async () => {
    respond({ maps: [] });
    show();

    fireEvent.click(await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-row-window-create]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    }));
    const form = await screen.findByRole('dialog');
    const save = within(form).getByRole('button', { name: '⟦common.save⟧' });
    expect(save.hasAttribute('disabled')).toBe(true);

    await pick(form.querySelector('[data-row-window-document]'), 'DOC-000123');
    await pick(form.querySelector('[data-row-window-table]'), 'FLARE_RECORD · S1');
    await waitFor(() => expect(sent.some((s) => s.path === '/api/v1/documents/100/tables/300')).toBe(true));
    await pick(form.querySelector('[data-row-window-target]'), 'Volume · Volume (Decimal)');
    await pick(form.querySelector('[data-row-window-start]'), 'Start · Start (Date)');
    await pick(form.querySelector('[data-row-window-end]'), 'End · End (Date)');
    await pick(form.querySelector('[data-row-window-unit]'), 'Sm3');

    await waitFor(() => expect(save.hasAttribute('disabled')).toBe(false));

    // Джерело без атрибута — зберегти не можна, доки воно не повне.
    fireEvent.click(form.querySelector<HTMLElement>('[data-row-window-source-add]') as HTMLElement);
    expect(form.querySelector('[data-row-window-problem="sourceIncomplete"]')).not.toBeNull();
    expect(save.hasAttribute('disabled')).toBe(true);

    const source = form.querySelector<HTMLElement>('[data-row-window-source]') as HTMLElement;
    fireEvent.change(within(source).getByLabelText('⟦rowWindow.sourceField⟧'), { target: { value: 'Flare.Total' } });
    await pick(within(source).getByLabelText('⟦rowWindow.sourceUnit⟧'), 'kg');
    await waitFor(() => expect(save.hasAttribute('disabled')).toBe(false));

    fireEvent.click(save);

    await waitFor(() => expect(sent.some((s) => s.method === 'POST')).toBe(true));
    const post = sent.find((s) => s.method === 'POST');
    expect(post?.path).toBe('/api/v1/row-window-maps');
    expect(post?.body).toEqual({
      tableDefId: 200,
      targetColumnDefId: 3,
      startColumnDefId: 1,
      endColumnDefId: 2,
      selectorColumnDefId: null,
      summary: 'Total',
      isStep: false,
      targetUnitId: 9,
      minPercentGood: null,
      refetchWithinDays: null,
      maxGapSeconds: null,
      sources: [{ selectorValue: null, sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
    });
  });
});
