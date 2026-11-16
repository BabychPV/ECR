import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { MethodologyCategoryRulePanel } from '@/features/methodologies/CategoryRulePanel';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог видалення правила категорії, що вже закрився, не шле другий DELETE.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжня панель): після відповіді сервера правило перечитано, діалог
 * закрито (`confirmingDelete = false`), але власний `Modal` ще ~200 мс у DOM і «Delete» активна. Другий
 * клік подвійного кліку слав другий `DELETE …/category-rule` тієї ж версії — видалення, якого ніхто не
 * підтверджував (а якщо правило за цей час задали знову — видалення вже нового).
 *
 * Захист — у самій панелі: кнопка діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційний доказ: прибрати `!confirmingDelete ||` у `CategoryRulePanel.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ 'DELETE /api/v1/methodologies/1/versions/10/category-rule' ]`).
 */

const Url = '/api/v1/methodologies/1/versions/10/category-rule';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: видалення правила категорії — діалог, що закрився', () => {
  it('один DELETE; клік по «Delete» закритого діалогу нічого не шле', async () => {
    let expression: string | null = "'Diesel'";
    const server = holdWrites({
      read: (url) => (url.endsWith(Url) ? { expression, updatedAt: expression === null ? null : '2026-10-08T01:00:00Z' } : {}),
      write: () => {
        expression = null;

        return { status: 204 };
      },
    });
    renderKeepMounted(<MethodologyCategoryRulePanel methodologyId={1} versionId={10} editable />);

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.categoryRuleDelete⟧' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: '⟦common.delete⟧' });
    fireEvent.click(confirm);
    await waitFor(() => {
      expect(server.sent).toEqual([`DELETE ${Url}`]);
    });

    // Сервер видалив, правило перечитано, діалог закрито, зайнятість знято.
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(confirm).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(confirm.isConnected).toBe(true);
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([`DELETE ${Url}`]);
  });
});
