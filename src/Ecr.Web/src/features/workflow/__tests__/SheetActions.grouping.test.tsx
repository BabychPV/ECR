import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Group } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * Аудит Етапу 3, лана "Documents core" (`lane3-workflow-buttons-not-grouped`,
 * знахідка людини зі скріншотом сторінки документа): панель дій у шапці
 * (`DocumentPage.tsx`) — ОДИН `Group`, що несе Period/Validate/Import/Export
 * і вкладає `<SheetActions>`. До цього фіксу `SheetActions` сам огортав
 * свої кнопки в ОКРЕМИЙ `<Group>` — вкладена Group у флекс-контейнері
 * поводиться як ОДИН елемент переносу рядка, тож рядок ламався нерівномірно
 * залежно від того, скільки кнопок показано (права/стан аркуша різні для
 * кожного користувача).
 *
 * ⚠ Тест мостить `SheetActions` у ТОЙ САМИЙ спосіб, що й `DocumentPage.tsx`
 * — усередині одного зовнішнього `Group` поруч із «іншою» кнопкою — і
 * перевіряє СТРУКТУРУ DOM: рівно один `Group` на всю панель (не два
 * вкладених), і `Divider` — прямий сусід кнопок робочого процесу всередині
 * ТОГО САМОГО `Group`, а не власного контейнера.
 */

/**
 * ⚠ Грант на проєкт іде В ПРОФІЛЬ разом із правами (F9): робочий процес сервер
 * закриває РІВНЕМ ГРАНТА (`EditRules.CanSubmit` — поріг `GrantLevel.Submit`),
 * а не іменованим правом, тож без гранта кнопки «Submit» більше не буває — і
 * цей файл, який перевіряє РОЗМІТКУ панелі, мусить спершу зробити так, щоб
 * кнопка взагалі з'явилася. Поріг як такий доводить
 * `SheetActions.workflowGrant.test.tsx`, не цей файл.
 */
function currentUser(permissions: string[]) {
  return {
    denies: [],
    grants: { 'Project:7': 'Manage' },
    isSimulation: false,
    language: 'en',
    mustChangePassword: false,
    permissions,
    simulatedForUserId: null,
    userId: 9,
    userName: 'tester',
  };
}

function mockFetch(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(currentUser(permissions)), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(permissions: string[], state: string): void {
  mockFetch(permissions);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  // ⚠ Зведення документа — у кеші під тим самим ключем, що його тримає
  // `DocumentPage`: `SheetActions` бере звідти `projectId` для рішення про
  // грант і не робить власного запиту.
  client.setQueryData(['document', 1, 202601], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: {},
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        {/* ⚠ Той самий каркас, що `DocumentPage.tsx`: ОДИН зовнішній `Group`
            несе "іншу" (не-workflow) кнопку і вкладає `<SheetActions>`. */}
        <Group gap="xs" data-testid="actions-bar">
          <button type="button">Validate</button>
          <SheetActions documentId={1} sheetDefId={2} periodKey={202601} state={state} />
        </Group>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: кнопки — прямі елементи батьківського Group, не вкладений контейнер', () => {
  it('є бодай одна дія робочого процесу (Draft → Submit) — рівно ОДИН Group на всю панель', async () => {
    show(['Calculation.Recalculate'], 'Draft');

    await screen.findByRole('button', { name: /submit/i });

    // ⛔ Мутаційний доказ: до фіксу тут було ДВА `.mantine-Group-root`
    // (зовнішній із тесту + власний Group SheetActions) — RED. Рівно один
    // означає, що кнопки робочого процесу — прямі flex-діти ЗОВНІШНЬОГО
    // Group, а не вкладеного.
    const groups = document.querySelectorAll('.mantine-Group-root');
    expect(groups).toHaveLength(1);
  });

  it('є бодай одна дія робочого процесу — Divider стоїть ПОРУЧ із кнопками в тому самому Group', async () => {
    show(['Calculation.Recalculate'], 'Draft');

    const submitButton = await screen.findByRole('button', { name: /submit/i });
    const bar = screen.getByTestId('actions-bar');
    const divider = bar.querySelector('.mantine-Divider-root');

    expect(divider).not.toBeNull();

    // ⚠ Роздільник і кнопка — ОБИДВА прямі діти панелі, не діти одне
    // одного: доводить, що немає прошарку-обгортки між ними.
    expect(divider?.parentElement).toBe(bar);
    expect(submitButton.parentElement).toBe(bar);
  });

  it('жодної дії робочого процесу не дозволено (Auditor, Approved) — Divider не рендериться взагалі', async () => {
    show([], 'Approved');

    await waitFor(() => expect(document.querySelectorAll('.mantine-Divider-root')).toHaveLength(0));

    // ⚠ Немає й «зайвого хвоста»: рівно один Group (зовнішній), як і в
    // попередньому випадку.
    expect(document.querySelectorAll('.mantine-Group-root')).toHaveLength(1);
  });
});
