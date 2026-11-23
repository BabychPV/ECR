import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectTemplatesSection } from '@/features/projects/ProjectTemplatesSection';
import { testTheme } from '@/test/render';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): налаштування проєкту → «Templates».
 *
 * Проєкт 7: основний `GEN` (3 документи), приєднані `LAND` (0) і `WATER` (2). «Detach» — лише для `LAND`; основний
 * і шаблон із документами недоступні з підказкою чому. Відмова сервера — банер із текстом каталогу (`messageKey`).
 * «Add template» — шаблон із каталогу (без уже приєднаних і архівних) і його опублікована версія.
 */

configure({ asyncUtilTimeout: 30_000 });

const Rows = [
  {
    projectTemplateId: 1, templateId: 4, templateCode: 'GEN', templateNameL10n: { values: { en: 'General' } },
    currentTemplateVersionId: 40, version: '2.3.0', layout: 'PerMonthCopies', isPrimary: true, documentCount: 3,
  },
  {
    projectTemplateId: 2, templateId: 8, templateCode: 'LAND', templateNameL10n: { values: { en: 'Land' } },
    currentTemplateVersionId: 80, version: '1.0', layout: 'YearWithMonthColumns', isPrimary: false, documentCount: 0,
  },
  {
    projectTemplateId: 3, templateId: 9, templateCode: 'WATER', templateNameL10n: { values: { en: 'Water' } },
    currentTemplateVersionId: 90, version: '1.1', layout: 'PerMonthCopies', isPrimary: false, documentCount: 2,
  },
];

interface Call {
  method: string;
  path: string;
  body: unknown;
}

function json(body: unknown, status = 200): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function refusal(messageKey: string, detail: string, errorCode = 'ECR-PRD-0409'): Response {
  return json({ title: 'Conflict', status: 409, detail, errorCode, correlationId: 'c-1', messageKey }, 409);
}

function mockServer(options: { refuseDetach?: Response; refuseAttach?: Response } = {}): { calls: Call[]; listReads: () => number } {
  const calls: Call[] = [];
  let reads = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';

      if (method !== 'GET') {
        calls.push({ method, path, body: init?.body === undefined ? null : JSON.parse(String(init.body)) });

        if (method === 'DELETE') return options.refuseDetach ?? new Response(null, { status: 204 });
        if (method === 'POST') {
          return options.refuseAttach ?? json({ ...Rows[1], projectTemplateId: 9, templateId: 15, templateCode: 'HSE' }, 201);
        }
      }

      if (path.endsWith('/api/v1/projects/7/templates')) {
        reads += 1;

        return json(Rows);
      }

      if (/\/api\/v1\/templates\/15\/versions$/.test(path)) {
        return json({
          items: [
            { id: 150, version: '1.0', status: 'Published', publishedAt: '2026-01-10T00:00:00Z', presentationRevision: 0, clonedFromVersionId: null },
            { id: 151, version: '1.1', status: 'Published', publishedAt: '2026-06-10T00:00:00Z', presentationRevision: 0, clonedFromVersionId: 150 },
            { id: 152, version: '2.0', status: 'Draft', publishedAt: null, presentationRevision: 0, clonedFromVersionId: 151 },
          ],
          nextCursor: null,
          totalCount: 3,
        });
      }

      if (path.endsWith('/api/v1/templates')) {
        return json({
          items: [
            { id: 4, code: 'GEN', nameL10n: { values: { en: 'General' } }, isArchived: false, documentCount: 3 },
            { id: 8, code: 'LAND', nameL10n: { values: { en: 'Land' } }, isArchived: false, documentCount: 0 },
            { id: 15, code: 'HSE', nameL10n: { values: { en: 'Hazard' } }, isArchived: false, documentCount: 0 },
            { id: 16, code: 'OLD', nameL10n: { values: { en: 'Old' } }, isArchived: true, documentCount: 0 },
          ],
          nextCursor: null,
          totalCount: 4,
        });
      }

      return json(null);
    }),
  );

  return { calls, listReads: () => reads };
}

function show(readOnly = false): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ProjectTemplatesSection projectId={7} readOnly={readOnly} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const row = (code: string): HTMLElement => document.querySelector(`[data-project-template="${code}"]`) as HTMLElement;
const detachOf = (code: string): HTMLElement => screen.getByRole('button', { name: `⟦projects.templates.detach⟧ ${code}` });
const hintOf = (button: HTMLElement): string =>
  (button.getAttribute('aria-describedby') ?? '')
    .split(' ')
    .map((id) => document.getElementById(id)?.textContent ?? '')
    .join(' ');

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('B74 P4: вкладка проєкту «Templates»', () => {
  it('перелік: код і назва, версія, документів, «Primary» лише в основного', async () => {
    mockServer();
    show();

    await screen.findByTestId('project-templates');
    expect(within(row('GEN')).getByText('General')).toBeTruthy();
    expect(within(row('GEN')).getByText('2.3.0')).toBeTruthy();
    expect(row('GEN').querySelector('[data-project-template-documents]')?.textContent).toBe('3');
    expect(within(row('GEN')).getByTestId('project-template-primary').textContent).toBe('⟦projects.templates.primary⟧');
    expect(screen.getAllByTestId('project-template-primary')).toHaveLength(1);
    expect(within(row('LAND')).getByText('1.0')).toBeTruthy();
  });

  it('Detach недоступний для основного і для шаблону з документами — з підказкою чому; клік нічого не відкриває', async () => {
    mockServer();
    show();
    await screen.findByTestId('project-templates');

    const primary = detachOf('GEN');
    expect(primary.getAttribute('aria-disabled')).toBe('true');
    expect(hintOf(primary)).toContain('⟦err.ECR-PRD-0409.primaryTemplate (templateCode=GEN)⟧');

    const withDocuments = detachOf('WATER');
    expect(withDocuments.getAttribute('aria-disabled')).toBe('true');
    expect(hintOf(withDocuments)).toContain('⟦projects.templates.detachBlockedDocuments⟧');

    expect(detachOf('LAND').getAttribute('aria-disabled')).toBe('false');

    fireEvent.click(primary);
    fireEvent.click(withDocuments);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('Detach LAND: підтвердження → DELETE …/templates/8, перелік перечитано, повідомлення', async () => {
    const done = vi.spyOn(notifications, 'show');
    const server = mockServer();
    show();
    await screen.findByTestId('project-templates');
    const before = server.listReads();

    fireEvent.click(detachOf('LAND'));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('⟦projects.templates.detachConfirm (templateCode=LAND)⟧');
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦projects.templates.detach⟧' }));

    await waitFor(() => expect(server.calls).toEqual([{ method: 'DELETE', path: '/api/v1/projects/7/templates/8', body: null }]));
    await waitFor(() => expect(server.listReads()).toBeGreaterThan(before));
    await waitFor(() =>
      expect(done.mock.calls.some(([options]) => options.message === '⟦projects.templates.detached⟧')).toBe(true),
    );
  });

  it('відмова сервера при Detach (власний маршрут шаблону) — банер з текстом причини', async () => {
    mockServer({
      refuseDetach: refusal('err.ECR-PRD-0409.templateHasRoute', 'Template «LAND» has its own approval route in this project: remove it first.'),
    });
    show();
    await screen.findByTestId('project-templates');

    fireEvent.click(detachOf('LAND'));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: '⟦projects.templates.detach⟧' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent ?? '').toContain('Template «LAND» has its own approval route in this project: remove it first.');
    expect(alert.textContent ?? '').toContain('ECR-PRD-0409');
  });

  it('Add template: лише не приєднані й не архівні шаблони; типово — остання опублікована версія; POST з нею', async () => {
    const server = mockServer();
    show();
    await screen.findByTestId('project-templates');

    fireEvent.click(screen.getByRole('button', { name: '⟦projects.templates.attach⟧' }));
    const dialog = await screen.findByRole('dialog');
    const apply = within(dialog).getByRole('button', { name: '⟦projects.templates.attachApply⟧' }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);

    fireEvent.click(within(dialog).getByRole('textbox', { name: /projects\.templates\.template/ }));
    await waitFor(() => expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual(['HSE · Hazard']));
    fireEvent.click(screen.getByRole('option', { name: 'HSE · Hazard' }));

    // Остання ОПУБЛІКОВАНА (1.1), а не чернетка 2.0.
    await waitFor(() =>
      expect((within(dialog).getByRole('textbox', { name: /projects\.templates\.version/ }) as HTMLInputElement).value).toBe('1.1'),
    );
    await waitFor(() => expect(apply.disabled).toBe(false));
    fireEvent.click(apply);

    await waitFor(() =>
      expect(server.calls).toEqual([{ method: 'POST', path: '/api/v1/projects/7/templates', body: { templateVersionId: 151 } }]),
    );
  });

  it('відмова сервера при Add (шаблон уже в проєкті) — банер у діалозі, діалог відкритий', async () => {
    mockServer({
      refuseAttach: refusal('err.ECR-PRD-0409.templateAlreadyAttached', 'Template «HSE» is already in this project.'),
    });
    show();
    await screen.findByTestId('project-templates');

    fireEvent.click(screen.getByRole('button', { name: '⟦projects.templates.attach⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('textbox', { name: /projects\.templates\.template/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'HSE · Hazard' }));
    const apply = within(dialog).getByRole('button', { name: '⟦projects.templates.attachApply⟧' }) as HTMLButtonElement;
    await waitFor(() => expect(apply.disabled).toBe(false));
    fireEvent.click(apply);

    const alert = await within(dialog).findByRole('alert');
    expect(alert.textContent ?? '').toContain('Template «HSE» is already in this project.');
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('лише перегляд (архів або немає прав): перелік є, «Add template» і «Detach» немає', async () => {
    mockServer();
    show(true);

    await screen.findByTestId('project-templates');
    expect(row('LAND')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '⟦projects.templates.attach⟧' })).toBeNull();
    expect(screen.queryAllByRole('button', { name: /projects\.templates\.detach/ })).toHaveLength(0);
  });
});
