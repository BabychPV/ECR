import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { ParticipantModal } from '@/features/editAssignments/ParticipantDialogs';
import { testTheme } from '@/test/render';

/**
 * P2 10.11 (живий знімок людини, ru): у діалозі «Добавить участника документа» рівні доступу стояли в
 * `SegmentedControl` — чотири рівні сегменти на ширину діалогу, і четвертий підпис «Заполнение, подача и согласование»
 * обрізався до «Заполнение» (виглядав дублем Write). Тепер — вертикальний перелік радіо (KIT.md §6.3).
 *
 * jsdom не міряє ширину, тож «влазить у діалог» доводить e2e `participantLevelsFit.spec.ts` (ru/kz, rect). Тут —
 * структура й клавіатура: група названа підписом, кожен рівень — окремий рядок-радіо з ПОВНИМ підписом, стрілки
 * всередині групи перемикають рівень, Tab виходить із групи одним кроком.
 */

const Levels = ['Read', 'Write', 'Submit', 'Approve'] as const;

function show(): void {
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ParticipantModal opened documentId={42} sheets={[]} isPending={false} error={null} onSubmit={() => undefined} onClose={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const checked = (el: HTMLElement): boolean => (el as HTMLInputElement).checked;

beforeEach(() => {
  cleanup();
  // user-event ходить стрілками по радіо через `CSS.escape`, якого jsdom не має.
  if (typeof globalThis.CSS?.escape !== 'function') {
    vi.stubGlobal('CSS', { ...(globalThis.CSS ?? {}), escape: (value: string) => value.replace(/["\\]/g, '\\$&') });
  }
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ParticipantModal — рівні доступу (P2 10.11)', () => {
  it('чотири рівні — окремі радіо з повним підписом у групі, названій «Рівень доступу»; не сегменти', async () => {
    show();
    const modal = await screen.findByTestId('participant-modal');
    const group = within(modal).getByRole('radiogroup', { name: '⟦editAssign.participant.level⟧' });

    const radios = within(group).getAllByRole('radio');
    expect(radios.map((r) => r.getAttribute('value'))).toEqual([...Levels]);
    for (const level of Levels) {
      const radio = within(group).getByRole('radio', { name: `⟦editAssign.level.${level}⟧` });
      // Підпис — видимий текст label цього радіо цілком, а не обрізаний сегмент.
      const label = modal.querySelector(`label[for="${radio.id}"]`);
      expect(label?.textContent).toBe(`⟦editAssign.level.${level}⟧`);
    }
    // Причина дефекту — рівні сегменти на ширину діалогу; сегментів тут бути не повинно.
    expect(modal.querySelector('.mantine-SegmentedControl-root')).toBeNull();
    expect(checked(within(group).getByRole('radio', { name: '⟦editAssign.level.Read⟧' }))).toBe(true);
  });

  it('клавіатура: стрілки перемикають рівень у групі, Tab виходить із групи одним кроком', async () => {
    const user = userEvent.setup();
    show();
    const modal = await screen.findByTestId('participant-modal');
    const group = within(modal).getByRole('radiogroup', { name: '⟦editAssign.participant.level⟧' });
    const radio = (level: (typeof Levels)[number]): HTMLElement => within(group).getByRole('radio', { name: `⟦editAssign.level.${level}⟧` });

    radio('Read').focus();
    await user.keyboard('{ArrowDown}');
    expect(checked(radio('Write'))).toBe(true);
    expect(document.activeElement).toBe(radio('Write'));
    await user.keyboard('{ArrowDown}{ArrowDown}');
    expect(checked(radio('Approve'))).toBe(true);
    await user.keyboard('{ArrowUp}');
    expect(checked(radio('Submit'))).toBe(true);

    // Одна зупинка Tab на групу: наступний Tab — уже поза радіо (поле «Аркуш»).
    await user.tab();
    expect(document.activeElement?.getAttribute('type')).not.toBe('radio');
    expect(group.contains(document.activeElement)).toBe(false);
  });
});
