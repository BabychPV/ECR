import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * UI-31: майстер створення документа (`KIT.md` §6.9, макет `screens-work.js` `openCreate`):
 * Project → Period → Sheets → Review, підсумок перед створенням, відмова сервера — банером у
 * кроці, без втрати введеного.
 *
 * ⛔ P1 «прихований аркуш»: майстер показує рівно ті аркуші, що віддав
 * `GET /projects/{id}/document-template` (сервер відсіює невидимі, `GetDocumentTemplateHandler`),
 * і нічого не домальовує з інших джерел. Тут роль бачить ОДИН аркуш із двох у версії.
 */

const twoSheets = [
  { id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false },
  { id: 10, code: 'WAT1', nameL10n: { values: { en: 'Water intake' } }, sheetGroup: 'Water', isMandatory: false },
  { id: 11, code: 'WAT2', nameL10n: { values: { en: 'Water discharge' } }, sheetGroup: 'Water', isMandatory: false },
];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let posted: Record<string, unknown> | null = null;

function mockServer(options: { sheets: typeof twoSheets; refuse?: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        // ⚠ Профіль НАВМИСНО називає прихований аркуш (заборона на `Sheet:12`): майстер не має
        // брати аркуші ні звідки, крім відповіді `document-template`.
        return jsonResponse({
          denies: ['Sheet:12'], grants: { 'Project:1': 'Write' }, isSimulation: false, language: 'en',
          mustChangePassword: false, permissions: ['Document.Create'], simulatedForUserId: null,
          userId: 1, userName: 'tester',
        });
      }

      if (url.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posted = JSON.parse(String(init.body)) as Record<string, unknown>;

        return options.refuse === true
          ? jsonResponse(
              {
                title: 'Composition rule violated',
                status: 422,
                detail: 'Group Water requires all of its sheets.',
                errorCode: 'ECR-DOC-0422',
                correlationId: 'c-1',
                messageKey: 'err.ECR-DOC-0422.composition',
              },
              422,
            )
          : jsonResponse({ documentId: 77 }, 201);
      }

      if (url.includes('/document-template')) {
        return jsonResponse({
          templateVersionId: 5, templateCode: 'GEN-UPSTREAM', version: '2.3.0',
          groupRules: [{ sheetGroup: 'Water', ruleKind: 0, targetGroup: null }],
          sheets: options.sheets,
        });
      }

      if (url.includes('/projects/1/periods')) {
        return jsonResponse({
          projectId: 1, currentPeriodMode: 'Auto', periodKind: 'Monthly',
          periods: [{ id: 3, periodKey: 202610, state: 'Open', isCurrent: true }],
        });
      }

      if (url.includes('/api/v1/projects')) {
        return jsonResponse({ items: [{ id: 1, code: 'P07131100', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return jsonResponse(null);
    }),
  );
}

function Where(): null {
  const location = useLocation();
  (globalThis as { __where?: string }).__where = location.pathname + location.search;

  return null;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="*" element={<><Where /><CreateDocumentModal opened onClose={() => {}} /></>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const next = (): HTMLButtonElement => screen.getByRole('button', { name: '⟦wizard.next⟧' });

async function toSheetsStep(): Promise<void> {
  fireEvent.click(await screen.findByLabelText(/documents\.project/, { selector: 'input' }));
  fireEvent.click(await screen.findByRole('option', { name: 'P07131100' }));
  await waitFor(() => expect(next().disabled).toBe(false));
  fireEvent.click(next());
  await screen.findByLabelText('⟦documents.period⟧', { selector: 'input' });
  fireEvent.click(next());
  await screen.findByText('⟦documents.createSheetsHint⟧');
}

afterEach(() => {
  vi.unstubAllGlobals();
  posted = null;
  delete (globalThis as { __where?: string }).__where;
});

describe('UI-31: майстер створення документа', () => {
  it('Scope: роль бачить 1 аркуш із 2 — майстер показує лише його, і лише його надсилає', async () => {
    mockServer({ sheets: [twoSheets[0]!] });
    show();
    await toSheetsStep();

    const body = screen.getByTestId('wizard-step-body');
    expect(within(body).getAllByRole('checkbox')).toHaveLength(1);
    expect(within(body).getByLabelText('Air emissions (AIR)')).toBeTruthy();
    expect(screen.queryByText(/WAT|Sheet:12|\b12\b/)).toBeNull();

    fireEvent.click(next());
    const summary = await screen.findByTestId('wizard-summary');
    expect(summary.textContent).toContain('Air emissions');
    expect(summary.textContent).not.toMatch(/Water|WAT/);

    fireEvent.click(screen.getByRole('button', { name: '⟦documents.createApply⟧' }));
    await waitFor(() => expect(posted).not.toBeNull());
    expect(posted?.['sheetDefIds']).toEqual([9]);
    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/documents/77?periodKey=202610'));
  });

  it('Next вимкнений, доки не обрано проєкт; версію шаблону видно на першому кроці', async () => {
    mockServer({ sheets: twoSheets });
    show();

    await screen.findByLabelText(/documents\.project/, { selector: 'input' });
    expect(next().disabled).toBe(true);

    fireEvent.click(screen.getByLabelText(/documents\.project/, { selector: 'input' }));
    fireEvent.click(await screen.findByRole('option', { name: 'P07131100' }));
    await screen.findByText('GEN-UPSTREAM · v2.3.0');
    await waitFor(() => expect(next().disabled).toBe(false));
  });

  it('усі аркуші типово включені; зняти всі — банер у кроці й фокус на першому чекбоксі', async () => {
    mockServer({ sheets: twoSheets });
    show();
    await toSheetsStep();

    const boxes = within(screen.getByTestId('wizard-step-body')).getAllByRole('checkbox') as HTMLInputElement[];
    expect(boxes.map((box) => box.checked)).toEqual([true, true, true]);

    for (const box of boxes) fireEvent.click(box);
    fireEvent.click(next());

    expect((await screen.findByTestId('wizard-step-error')).textContent).toContain('⟦documents.createNoSheets⟧');
    await waitFor(() => expect(document.activeElement).toBe(boxes[0]));
  });

  it('порушення RequiresAll — live-попередження, але не блок', async () => {
    mockServer({ sheets: twoSheets });
    show();
    await toSheetsStep();

    expect(screen.queryByTestId('create-group-violations')).toBeNull();
    fireEvent.click(screen.getByLabelText('Water discharge (WAT2)'));
    expect(await screen.findByTestId('create-group-violations')).toBeTruthy();
    expect(next().disabled).toBe(false);
  });

  it('відмова сервера — банер у кроці Review, майстер відкритий, вибір аркушів зберігся', async () => {
    mockServer({ sheets: twoSheets, refuse: true });
    show();
    await toSheetsStep();

    fireEvent.click(screen.getByLabelText('Water discharge (WAT2)'));
    fireEvent.click(next());
    fireEvent.click(await screen.findByRole('button', { name: '⟦documents.createApply⟧' }));

    const banner = await screen.findByTestId('wizard-step-error');
    expect(banner.textContent).toContain('Group Water requires all of its sheets.');
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect((globalThis as { __where?: string }).__where).toBe('/');

    fireEvent.click(screen.getByRole('button', { name: '⟦wizard.back⟧' }));
    const kept = within(await screen.findByTestId('wizard-step-body')).getAllByRole('checkbox') as HTMLInputElement[];
    expect(kept.map((box) => box.checked)).toEqual([true, true, false]);
  });
});
