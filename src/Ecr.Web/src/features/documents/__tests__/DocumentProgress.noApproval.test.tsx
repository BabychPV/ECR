import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentProgress } from '../DocumentProgress';

/**
 * ✎ B143 (B75): версія шаблону «без подання й затвердження» (`DocumentSummary.workflowMode = 'None'`) — у рядку
 * прогресу замість стану аркуша (Draft/Submitted/…, який там нічого не означає) стоїть нейтральна позначка
 * «No approval» з поясненням; у звичайному режимі — стан аркуша, як було.
 *
 * ⛔ Мутація: прибрати гілку `workflowOff` у `DocumentProgress` — червоніє тест режиму `None`.
 */
function show(workflowOff: boolean | undefined): HTMLElement {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () => new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    ),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentProgress
          documentId={1}
          periodKey={202601}
          sheets={[{ code: 'S1', name: 'Emissions', state: 'Submitted' }]}
          activeCode="S1"
          issues={null}
          onShowIssues={() => undefined}
          {...(workflowOff === undefined ? {} : { workflowOff })}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.getByTestId('document-progress');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⚠ Каталогу немає: підписи — ключі в маркерах (`⟦workflow.noApproval⟧`).
describe('DocumentProgress: позначка «Без погодження» (B143, B75)', () => {
  it('None — позначка «No approval» з поясненням, стану аркуша немає', () => {
    const progress = show(true);

    const chip = within(progress).getByTestId('document-no-approval');
    expect(chip.textContent).toContain('⟦workflow.noApproval⟧');
    expect(chip.getAttribute('title')).toBe('⟦workflow.noApprovalHint⟧');
    // Нейтральна: без кольору проблеми чи «чекає дії».
    expect(chip.getAttribute('data-status-tone')).toBe('neutral');

    expect(within(progress).queryByText('⟦status.sheet.Submitted⟧')).toBeNull();
    expect(progress.querySelector('[data-status-kind="sheet"]')).toBeNull();
    // «Sheet:» підписував стан аркуша — без стану він нічого не підписує.
    expect(within(progress).queryByText('⟦document.sheetStateLabel⟧')).toBeNull();
  });

  it('контроль: без пропа — стан аркуша, як було; позначки немає', () => {
    const progress = show(undefined);

    expect(within(progress).getByText('⟦status.sheet.Submitted⟧')).toBeDefined();
    expect(within(progress).getByText('⟦document.sheetStateLabel⟧')).toBeDefined();
    expect(within(progress).queryByTestId('document-no-approval')).toBeNull();
  });

  it('контроль: workflowOff=false — стан аркуша, позначки немає', () => {
    const progress = show(false);

    expect(progress.querySelector('[data-status-kind="sheet"][data-status-state="Submitted"]')).not.toBeNull();
    expect(within(progress).queryByTestId('document-no-approval')).toBeNull();
  });
});
