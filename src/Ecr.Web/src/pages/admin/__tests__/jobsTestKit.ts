import { expect } from 'vitest';
import { waitFor } from '@testing-library/react';

/**
 * Спільні локатори тестів `/admin/jobs` (UI-28).
 *
 * ⚠ Рядок переліку впізнається за значком стану (`data-status-state`), а не за
 * текстом: підпис стану береться з каталогу, а не з коду сервера.
 */
export function rowOfState(state: string): HTMLElement {
  const row = document.querySelector(`[data-status-state="${state}"]`)?.closest('tr') ?? null;
  expect(row, `рядок задачі у стані «${state}»`).not.toBeNull();

  return row as HTMLElement;
}

/** Чекає, доки в переліку з'явиться рядок у цьому стані. */
export async function findRowOfState(state: string): Promise<HTMLElement> {
  await waitFor(() => expect(document.querySelector(`tr [data-status-state="${state}"]`)).not.toBeNull());

  return rowOfState(state);
}

/**
 * Шторка задачі з рядка у цьому стані.
 *
 * ✎ UI-28: дії («Cancel job», «Restart», файл результату) переїхали з рядка в
 * підвал шторки (макет `/admin/jobs`): кнопка в рядку — лише назва задачі, і
 * вона відкриває шторку (`?panel=<jobId>`).
 */
export async function openDrawerOf(state: string): Promise<HTMLElement> {
  const row = await findRowOfState(state);
  const open = row.querySelector('[data-job-open]') as HTMLElement;
  open.click();

  return waitFor(() => {
    const node = document.querySelector(`[data-panel="${open.getAttribute('data-job-open') ?? ''}"]`);
    expect(node).not.toBeNull();

    return node as HTMLElement;
  });
}
