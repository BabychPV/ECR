import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * UI-31: доступність майстра «New document» в обох темах (`ФВ-14.16`, `D-127`).
 *
 * ⚠ Скануються стани, де з'являється нове: крок Sheets із банером помилки (жива область
 * `role="alert"`, фокус на першому чекбоксі) і крок Review з підсумком.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/document-template')) {
        return json({
          templateVersionId: 5, templateCode: 'GEN-UPSTREAM', version: '2.3.0', groupRules: [],
          sheets: [
            { id: 9, code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, sheetGroup: null, isMandatory: false },
            { id: 12, code: 'WST', nameL10n: { values: { en: 'Waste' } }, sheetGroup: null, isMandatory: false },
          ],
        });
      }

      if (url.includes('/projects/1/periods')) {
        return json({ projectId: 1, currentPeriodMode: 'Auto', periodKind: 'Monthly', periods: [{ id: 3, periodKey: 202610, state: 'Open', isCurrent: true }] });
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [{ id: 1, code: 'P07131100', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      if (url.includes('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      return json(null);
    }),
  );
}

const next = (): HTMLElement => screen.getByTestId('wizard-next');

async function toSheetsStep(): Promise<void> {
  fireEvent.click(await screen.findByLabelText(/documents\.project/, { selector: 'input' }));
  fireEvent.click(await screen.findByRole('option', { name: 'P07131100' }));
  await waitFor(() => expect((next() as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(next());
  await screen.findByLabelText('⟦documents.period⟧', { selector: 'input' });
  fireEvent.click(next());
  await screen.findByLabelText('Air emissions (AIR)');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateDocumentModal (майстер) — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: крок Sheets із банером помилки й крок Review — без блокуючих порушень`, async () => {
      mockServer();
      render(
        <Shell colorScheme={colorScheme}>
          <CreateDocumentModal opened onClose={() => {}} />
        </Shell>,
      );

      await toSheetsStep();
      for (const box of within(screen.getByTestId('wizard-step-body')).getAllByRole('checkbox')) fireEvent.click(box);
      fireEvent.click(next());
      await screen.findByTestId('wizard-step-error');

      const onError = await findViolations(screen.getByRole('dialog'));
      expect(onError, describeViolations(onError)).toHaveLength(0);

      fireEvent.click(screen.getByLabelText('Air emissions (AIR)'));
      fireEvent.click(next());
      await screen.findByTestId('wizard-summary');

      const onReview = await findViolations(screen.getByRole('dialog'));
      expect(onReview, describeViolations(onReview)).toHaveLength(0);
    });
  }
});
