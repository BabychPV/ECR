import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ScopedApprovalRouteEditor } from '@/features/approvalRoutes/ScopedApprovalRouteEditor';
import type { ApprovalRouteImpactDto, ScopedApprovalRouteDto } from '@/features/approvalRoutes/approvalRoutesApi';
import { testTheme } from '@/test/render';

/**
 * ✎ B155 (варіант C): редактор маршруту області (тут — шаблон 7 в усіх проєктах).
 *
 * ⛔ Перевіряються ЗАПИТИ, а не лише вигляд: тіло `preview`/`PUT`, їх кількість і порядок. «Кнопка є» не доводить,
 * що `PUT` іде лише після підтвердження, а другий клік не шле другої повної заміни маршруту.
 */
configure({ asyncUtilTimeout: 10_000 });

const RouteUrl = '/api/v1/templates/7/approval-route';

const Roles = [
  { id: 11, code: 'Reviewer', nameL10n: null, isBuiltIn: false, isGeneralApprover: false },
  { id: 12, code: 'Controller', nameL10n: null, isBuiltIn: false, isGeneralApprover: false },
  { id: 13, code: 'Approver', nameL10n: null, isBuiltIn: true, isGeneralApprover: true },
];

function route(overrides: Partial<ScopedApprovalRouteDto>): ScopedApprovalRouteDto {
  return {
    projectId: null,
    templateId: 7,
    hasRoute: false,
    steps: [],
    effectiveSource: 'none',
    effectiveSteps: [],
    generalApproverAlsoApproves: false,
    generalApproverRoleId: 13,
    canEdit: true,
    workflowModeNone: false,
    ...overrides,
  };
}

function own(roleIds: number[], overrides: Partial<ScopedApprovalRouteDto> = {}): ScopedApprovalRouteDto {
  const steps = roleIds.map((roleId, index) => ({ ordinal: index + 1, roleId, isOptional: false }));

  return route({
    hasRoute: true,
    steps,
    effectiveSource: 'own',
    effectiveSteps: steps,
    generalApproverAlsoApproves: true,
    ...overrides,
  });
}

const NoImpact: ApprovalRouteImpactDto = { total: 0, visible: 0, sheets: [], truncated: false };

interface Call {
  method: string;
  url: string;
  body: unknown;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

/** Стенд запитів: маршрут, ролі, preview і PUT; записує метод, адресу й тіло кожного запиту. */
function serve(options: {
  route?: () => Promise<Response>;
  impact?: ApprovalRouteImpactDto;
  change?: { steps: number };
}): Call[] {
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
      calls.push({ method, url, body });

      if (url.includes('/roles/approvers')) return json(Roles);
      if (url.endsWith('/approval-route/preview') && method === 'POST') return json(options.impact ?? NoImpact);
      if (url.endsWith(RouteUrl) && method === 'PUT') {
        return json({ steps: options.change?.steps ?? 1, restarted: options.impact ?? NoImpact });
      }
      if (url.endsWith(RouteUrl)) return options.route === undefined ? json(route({})) : options.route();

      return json([]);
    }),
  );

  return calls;
}

function show(node: JSX.Element = <ScopedApprovalRouteEditor scope={{ kind: 'template', templateId: 7 }} />): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {node}
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const writes = (calls: Call[]): Call[] => calls.filter((call) => call.method !== 'GET');
const puts = (calls: Call[]): Call[] => calls.filter((call) => call.method === 'PUT');
const previews = (calls: Call[]): Call[] => calls.filter((call) => call.method === 'POST');

async function startEditing(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: '⟦approvalRoutes.edit⟧' }));
  await screen.findByTestId('approval-route-editor');
  // Ролі приїхали — поле вибору кроку змонтоване.
  await screen.findByLabelText(/workflow\.routeAddStep/);
}

async function pick(code: string): Promise<void> {
  fireEvent.click(screen.getByLabelText(/workflow\.routeAddStep/));
  fireEvent.click(await screen.findByRole('option', { name: code }));
}

const saveButton = (): HTMLButtonElement => screen.getByTestId('approval-route-save');

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ScopedApprovalRouteEditor — показ', () => {
  it('успадкований маршрут: позначка джерела, кроки успадкованого і пояснення «власного немає»', async () => {
    const steps = [{ ordinal: 1, roleId: 12, isOptional: false }];
    serve({ route: () => Promise.resolve(json(route({ effectiveSource: 'project', effectiveSteps: steps }))) });
    show();

    const badge = await screen.findByTestId('approval-route-source');
    expect(badge.textContent).toBe('⟦approvalRoutes.source.project⟧');
    expect(screen.getByTestId('scoped-approval-route').getAttribute('data-source')).toBe('project');
    expect(screen.getByText('⟦approvalRoutes.inheritedHint⟧')).toBeDefined();
    await waitFor(() => expect(within(screen.getByTestId('approval-route-steps')).getByText('Controller')).toBeDefined());
    expect(screen.queryByTestId('approval-route-general-approver')).toBeNull();
  });

  it('власний маршрут шаблону: позначка «власний» і підказка «загальний Approver теж затверджує»', async () => {
    serve({ route: () => Promise.resolve(json(own([11, 12]))) });
    show();

    expect((await screen.findByTestId('approval-route-source')).textContent).toBe('⟦approvalRoutes.source.own⟧');
    expect(screen.getByTestId('approval-route-general-approver').textContent).toBe('⟦approvalRoutes.generalApproverHint⟧');
    expect(screen.queryByText('⟦approvalRoutes.inheritedHint⟧')).toBeNull();
    await waitFor(() => {
      const items = within(screen.getByTestId('approval-route-steps')).getAllByRole('listitem');
      expect(items.map((item) => item.textContent)).toEqual(['Reviewer', 'Controller']);
    });
  });

  it('без canEdit — «лише читання», кнопки зміни немає', async () => {
    serve({ route: () => Promise.resolve(json(own([11], { canEdit: false }))) });
    show();

    await screen.findByTestId('approval-route-read-only');
    expect(screen.queryByRole('button', { name: '⟦approvalRoutes.edit⟧' })).toBeNull();
  });

  it('workflowModeNone — пояснення, що маршрут не діє, і зміна недоступна', async () => {
    serve({ route: () => Promise.resolve(json(own([11], { workflowModeNone: true }))) });
    show();

    await screen.findByTestId('approval-route-mode-none');
    expect((screen.getByRole('button', { name: '⟦approvalRoutes.edit⟧' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('маршрут у дорозі — ні «кроків немає», ні кнопки зміни; зберегти нема з чого', async () => {
    serve({ route: () => new Promise<Response>(() => undefined) });
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByTestId('approval-route-no-steps')).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦approvalRoutes.edit⟧' })).toBeNull();
    expect(screen.queryByTestId('approval-route-save')).toBeNull();
  });

  it('невдалий запит маршруту — помилка з кодом, а не «кроків немає»; редагувати нема з чого', async () => {
    serve({
      route: () =>
        Promise.resolve(
          json({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'cid-b155' }, 403),
        ),
    });
    show();

    expect(await screen.findByText('ECR-AUTH-0403')).toBeDefined();
    expect(screen.queryByTestId('approval-route-no-steps')).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦approvalRoutes.edit⟧' })).toBeNull();
    expect(screen.queryByTestId('approval-route-save')).toBeNull();
  });
});

describe('ScopedApprovalRouteEditor — правила кроків', () => {
  it('та сама роль двічі поспіль не додається; інша — додається, і preview отримує саме цей набір', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))) });
    show();
    await startEditing();

    await pick('Reviewer');
    expect(screen.getByTestId('approval-route-problem').textContent).toBe('⟦approvalRoutes.consecutiveRole⟧');
    expect(within(screen.getByTestId('approval-route-draft')).getAllByRole('listitem')).toHaveLength(1);

    await pick('Controller');
    expect(screen.queryByTestId('approval-route-problem')).toBeNull();
    expect(within(screen.getByTestId('approval-route-draft')).getAllByRole('listitem')).toHaveLength(2);

    fireEvent.click(saveButton());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(previews(calls).map((call) => call.body)).toEqual([{ roleIds: [11, 12] }]);
    expect(puts(calls)[0]?.body).toEqual({ roleIds: [11, 12] });
  });

  it('видалення кроку, що зводить ту саму роль поспіль, блокує збереження', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11, 12, 11]))) });
    show();
    await startEditing();

    fireEvent.click(screen.getByRole('button', { name: '⟦approvalRoutes.removeStep (step=2)⟧' }));
    expect(
      within(screen.getByTestId('approval-route-draft'))
        .getAllByRole('listitem')
        .map((item) => item.querySelector('p')?.textContent),
    ).toEqual(['Reviewer', 'Reviewer']);

    expect(screen.getByTestId('approval-route-problem').textContent).toBe('⟦approvalRoutes.consecutiveRole⟧');
    expect(saveButton().disabled).toBe(true);
    fireEvent.click(saveButton());
    expect(writes(calls)).toEqual([]);
  });

  it('20 кроків — більше не додати (поле вимкнене, пояснення поруч)', async () => {
    serve({ route: () => Promise.resolve(json(own(Array.from({ length: 20 }, (_, i) => (i % 2 === 0 ? 11 : 12))))) });
    show();
    await startEditing();

    expect((screen.getByLabelText(/workflow\.routeAddStep/) as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText('⟦approvalRoutes.tooLong (max=20)⟧')).toBeDefined();
  });
});

describe('ScopedApprovalRouteEditor — збереження (варіант C)', () => {
  const Impact: ApprovalRouteImpactDto = {
    total: 5,
    visible: 2,
    truncated: true,
    sheets: [
      { projectId: 1, documentId: 101, sheetDefId: 7, sheetCopyId: null, periodKey: 202609 },
      { projectId: 2, documentId: 202, sheetDefId: 7, sheetCopyId: 9, periodKey: 202610 },
    ],
  };

  it('є подані аркуші — підтвердження з видимими/усіма й переліком; PUT лише після підтвердження', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))), impact: Impact, change: { steps: 2 } });
    show();
    await startEditing();
    await pick('Controller');

    fireEvent.click(saveButton());

    const consequences = await screen.findByTestId('confirm-consequences');
    expect(previews(calls).map((call) => call.body)).toEqual([{ roleIds: [11, 12] }]);
    // ⛔ Головне: доки людина не підтвердила, заміни маршруту немає.
    expect(puts(calls)).toEqual([]);

    const lines = within(consequences).getAllByRole('listitem').map((item) => item.textContent);
    expect(lines).toEqual([
      '⟦approvalRoutes.restartVisible (visible=2, total=5)⟧',
      '⟦approvalRoutes.restartHidden (hidden=3)⟧',
      '⟦approvalRoutes.restartTruncated (count=2)⟧',
    ]);
    expect(screen.getByRole('dialog', { name: '⟦approvalRoutes.restartTitle (total=5)⟧' })).toBeDefined();
    expect(within(screen.getByTestId('approval-route-restart-sheets')).getAllByRole('listitem')).toHaveLength(2);

    fireEvent.click(screen.getByTestId('confirm-verb'));
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]).toMatchObject({ url: RouteUrl, body: { roleIds: [11, 12] } });
  });

  it('скасування підтвердження — PUT не йде', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))), impact: Impact });
    show();
    await startEditing();
    await pick('Controller');

    fireEvent.click(saveButton());
    fireEvent.click(await screen.findByTestId('confirm-cancel'));

    await waitFor(() => expect(screen.queryByTestId('confirm-consequences')).toBeNull());
    expect(puts(calls)).toEqual([]);
  });

  it('усі подані видимі — без рядка «в проєктах, яких не видно»', async () => {
    serve({
      route: () => Promise.resolve(json(own([11]))),
      impact: { ...Impact, total: 2, truncated: false },
    });
    show();
    await startEditing();
    await pick('Controller');
    fireEvent.click(saveButton());

    const consequences = await screen.findByTestId('confirm-consequences');
    expect(within(consequences).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      '⟦approvalRoutes.restartVisible (visible=2, total=2)⟧',
    ]);
  });

  it('поданих немає — PUT одразу, без підтвердження', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))) });
    show();
    await startEditing();
    await pick('Controller');

    fireEvent.click(saveButton());

    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(screen.queryByTestId('confirm-consequences')).toBeNull();
    expect(writes(calls).map((call) => call.method)).toEqual(['POST', 'PUT']);
  });

  it('«прибрати власний маршрут» — порожній набір і в preview, і в PUT', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11, 12]))), change: { steps: 0 } });
    show();
    await startEditing();

    fireEvent.click(screen.getByTestId('approval-route-remove-own'));

    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(previews(calls).map((call) => call.body)).toEqual([{ roleIds: [] }]);
    expect(puts(calls)[0]?.body).toEqual({ roleIds: [] });
  });

  it('усі кроки видалено вручну — збереження теж шле порожній набір', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))), change: { steps: 0 } });
    show();
    await startEditing();

    fireEvent.click(screen.getByRole('button', { name: '⟦approvalRoutes.removeStep (step=1)⟧' }));
    fireEvent.click(saveButton());

    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]?.body).toEqual({ roleIds: [] });
  });

  it('набір не змінився — зберігати нічого (кнопка вимкнена)', async () => {
    serve({ route: () => Promise.resolve(json(own([11]))) });
    show();
    await startEditing();

    expect(saveButton().disabled).toBe(true);
  });

  it('B145b: подвійний клік «Зберегти» — один preview і ОДИН PUT', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))) });
    show();
    await startEditing();
    await pick('Controller');

    fireEvent.click(saveButton());
    fireEvent.click(saveButton());

    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    // Дати другому ланцюжку (якби він був) час дійти до сервера.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(previews(calls)).toHaveLength(1);
    expect(puts(calls)).toHaveLength(1);
  });

  it('B145b: подвійний клік підтвердження — ОДИН PUT', async () => {
    const calls = serve({ route: () => Promise.resolve(json(own([11]))), impact: Impact });
    show();
    await startEditing();
    await pick('Controller');

    fireEvent.click(saveButton());
    const verb = await screen.findByTestId('confirm-verb');
    fireEvent.click(verb);
    fireEvent.click(verb);

    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(puts(calls)).toHaveLength(1);
  });
});
