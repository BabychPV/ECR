import { fireEvent, screen } from '@testing-library/react';

/**
 * ✎ UAT-p3 №1: «Submit: <місяць>» спершу відкриває підтвердження (як Approve / Return for edits); POST іде лише
 * після «Submit» у діалозі. Тести, що перевіряють саме подання, підтверджують його цим кроком.
 *
 * @param timeout Скільки чекати діалог (повільне середовище).
 */
export async function confirmSubmit(timeout = 15_000): Promise<void> {
  const confirm = await screen.findByRole('button', { name: /^(⟦document\.submit⟧|Submit)$/ }, { timeout });
  fireEvent.click(confirm);
}
