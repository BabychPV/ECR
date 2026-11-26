import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145: майстер створення документа шле рівно ОДИН `POST /documents`, хоч скільки разів натиснуто «Create».
 *
 * ⛔ Діра (зонд на базі b746a5add): сервер дублі документів приймає (`UQ_Document` — за `(ProjectId,
 * BusinessKey)`, а ключ видає сам), тож кожен зайвий `POST` — зайвий документ. Зайвий `POST` ішов у ТРЬОХ
 * вікнах, і лише останнє — анімація закриття:
 *   1. два кліки поспіль до першого перемальовування (зайнятість кнопки ще не намальована);
 *   2. після відповіді сервера, поки перечитується перелік документів (`await invalidateQueries` в
 *      `onSuccess` виклику): мутація вже не `isPending`, майстер ще ВІДКРИТИЙ, кнопка активна — вікно
 *      завдовжки з мережевий запит, а не 200 мс;
 *   3. після `onClose`, поки `Modal` майстра ще в DOM.
 * Зонд цим самим сценарієм: `{ sync: 2, refreshing: 3, closed: 3 }` (третій `POST` ще в польоті — кнопка
 * зайнята); лише клік по закритому майстру: `{ sync: 1, refreshing: 1, closed: 2 }`.
 *
 * Захист — у самому `CreateDocumentModal` (майстер — `Wizard` на власному `Modal`; кореневий захист
 * `shared/ui/ConfirmModal` сюди не дістає): одна постановка на відкриття (`creating`, знімається лише
 * відмовою сервера чи новим відкриттям) і зайнятість кнопки аж до закриття.
 *
 * Мутаційні докази (`CreateDocumentModal.tsx`):
 * - прибрати `if (creating.current) return;` → червоний перший тест
 *   (`expected { sync: 2, refreshing: 2, closed: 2 } to deeply equal { sync: 1, refreshing: 1, closed: 1 }`);
 * - `isApplying={create.isPending}` (без `|| created`) → червоний перший тест: кнопка під час перечитування
 *   переліку не зайнята (`expected false to be true`);
 * - прибрати `creating.current = false` у `onError` → червоний другий тест (повтор після відмови не йде:
 *   `expected 1 to be 2`).
 *
 * ⚠ Без залежності від часу: `Wizard` справжній, тема `keepMountedModalsTheme` лишає закритий діалог у DOM
 * назавжди; відповіді сервера (`POST` і перечитування переліку) віддає тест.
 */

const sheets = [{ id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false }];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Тіла `POST /documents`, що дійшли до «сервера». */
const posts: unknown[] = [];
/** Відпускає відповідь на останній `POST`; `null` — запиту ще не було. */
let respondPost: (() => void) | null = null;
/** Відпускає перечитування переліку документів після створення; `null` — воно ще не почалося. */
let respondList: (() => void) | null = null;
/** Відмовити на `POST` (422) — для шляху «відмова → виправив → повторив». */
let refuse = false;

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';

      if (path.endsWith('/api/v1/documents') && init?.method === 'POST') {
        posts.push(JSON.parse(String(init.body)));
        const refused = refuse;
        await new Promise<void>((resolve) => {
          respondPost = resolve;
        });

        return refused
          ? json(
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
          : json({ documentId: 77 }, 201);
      }

      if (path.endsWith('/api/v1/documents')) {
        // Перелік документів проєкту. Після першого прийнятого `POST` — тоді, коли скаже тест.
        if (posts.length > 0 && !refuse) {
          await new Promise<void>((resolve) => {
            respondList = resolve;
          });
        }

        return json({ items: [], nextCursor: null });
      }

      if (path.includes('/document-template')) {
        return json({ templateVersionId: 5, templateCode: 'GEN-UPSTREAM', version: '2.3.0', groupRules: [], sheets });
      }

      // ✎ B74 P4: єдиний шаблон проєкту — майстер обирає його сам.
      // ✎ B74 (24.11): `documentCount: 1` — перелік документів шаблону майстер питає лише тоді (попередження «уже є»),
      // а саме його перечитування після створення тримає майстер відкритим у кроці 2 нижче.
      if (path.endsWith('/api/v1/projects/1/templates')) {
        return json([
          {
            projectTemplateId: 1, templateId: 4, templateCode: 'GEN-UPSTREAM', templateNameL10n: { values: {} },
            currentTemplateVersionId: 5, version: '2.3.0', layout: 'PerMonthCopies', isPrimary: true, documentCount: 1,
          },
        ]);
      }

      if (path.endsWith('/api/v1/projects')) {
        return json({ items: [{ id: 1, code: 'P07131100', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

let where = '';
let closes = 0;

function Where(): null {
  const location = useLocation();
  where = location.pathname + location.search;

  return null;
}

/** Як `DocumentsPage`: майстер лишається змонтованим, `onClose` лише знімає `opened`. */
function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <CreateDocumentModal
      opened={opened}
      onClose={() => {
        closes += 1;
        setOpened(false);
      }}
    />
  );
}

function show(): void {
  render(
    <MantineProvider theme={keepMountedModalsTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="*" element={<><Where /><Host /></>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const next = (): HTMLButtonElement => screen.getByTestId('wizard-next') as HTMLButtonElement;

/** Проєкт → аркуші → підсумок; повертає кнопку «Create». */
async function toReview(): Promise<HTMLButtonElement> {
  fireEvent.click(await screen.findByLabelText(/documents\.project/, { selector: 'input' }));
  fireEvent.click(await screen.findByRole('option', { name: 'P07131100' }));
  await waitFor(() => expect(next().disabled).toBe(false));
  fireEvent.click(next()); // ✎ B74 P4: → Template
  await screen.findByText('⟦documents.createTemplateStep⟧');
  await waitFor(() => expect(next().disabled).toBe(false));
  fireEvent.click(next());
  await screen.findByText('⟦documents.createSheetsHint⟧');
  fireEvent.click(next());
  await screen.findByTestId('wizard-summary');

  return screen.getByTestId('wizard-apply') as HTMLButtonElement;
}

/** Незахищена мутація стартує одразу: кілька тіків циклу подій — і зайвий `POST` уже був би в журналі. */
async function settle(): Promise<void> {
  await new Promise<void>((resolve) => setImmediate(resolve));
  await new Promise<void>((resolve) => setImmediate(resolve));
}

afterEach(() => {
  posts.length = 0;
  respondPost = null;
  respondList = null;
  refuse = false;
  where = '';
  closes = 0;
  vi.unstubAllGlobals();
});

describe('B145: створення документа — рівно один POST', () => {
  it('подвійний клік, клік під час перечитування переліку і клік по майстру, що закрився, — один документ', async () => {
    mockServer();
    show();
    const apply = await toReview();

    // 1. Два кліки поспіль, до першого перемальовування.
    fireEvent.click(apply);
    fireEvent.click(apply);
    await waitFor(() => {
      expect(posts.length).toBeGreaterThan(0);
    });
    await settle();
    const sync = posts.length;

    // 2. Сервер створив документ; перелік документів перечитується, майстер ще ВІДКРИТИЙ.
    respondPost?.();
    await waitFor(() => {
      expect(respondList).not.toBeNull();
    });
    expect(closes).toBe(0);
    expect(apply.isConnected).toBe(true);
    // Кнопка зайнята аж до закриття: активна «Create» тут обіцяла б другий документ.
    expect(apply.hasAttribute('data-loading')).toBe(true);
    fireEvent.click(apply);
    await settle();
    const refreshing = posts.length;

    // 3. Перелік перечитано: майстер закрито, документ відкрито.
    respondList?.();
    await waitFor(() => {
      expect(closes).toBe(1);
      expect(where).toBe('/documents/77');
    });
    expect(apply.isConnected).toBe(true);
    fireEvent.click(apply);
    await settle();
    const closed = posts.length;

    expect({ sync, refreshing, closed }).toEqual({ sync: 1, refreshing: 1, closed: 1 });
  });

  it('дзеркало: після відмови сервера «Create» знову діє — захист не «залипає»', async () => {
    refuse = true;
    mockServer();
    show();
    const apply = await toReview();

    fireEvent.click(apply);
    await waitFor(() => {
      expect(posts).toHaveLength(1);
    });
    respondPost?.();
    await screen.findByTestId('wizard-step-error');
    await waitFor(() => {
      expect(apply.hasAttribute('data-loading')).toBe(false);
    });

    // Людина прочитала відмову й повторила.
    fireEvent.click(apply);
    await settle();
    expect(posts.length).toBe(2);
    expect(closes).toBe(0);
    respondPost?.();
  });
});
