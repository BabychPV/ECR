import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { apiFetch } from '@/api/client';
import type { SimulationResultDto } from '@/api/types';
import { renderWithQuery } from '@/test/render';
import { TestCaseRunner } from '../TestCaseRunner';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));

/**
 * B21: вердикт тесту без розбіжностей ще не означає «зелений». Сервер (`GoldenSet.Judge`) вважає тест без
 * жодного очікування (`expectedJson = {}`) червоним — він нічого не перевіряє. Рядок таблиці мусить показати
 * колір ВЕРДИКТУ: доти він дивився на довжину переліку розбіжностей і малював такий тест зеленим поруч із
 * червоним загальним вердиктом.
 *
 * Доказ: повернути в `TestCaseRunner.tsx` безумовний зелений бейдж для `mismatches.length === 0` — червоніє
 * перший тест.
 */
const result = (testCases: SimulationResultDto['testCases'], isGreen: boolean): SimulationResultDto => ({
  outputs: {},
  diffWithPublished: {},
  trace: [],
  testCases,
  isGreen,
});

let reply: SimulationResultDto;

beforeEach(() => {
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation(() => Promise.resolve(reply));
});

async function runAndGetRow(code: string): Promise<HTMLElement> {
  renderWithQuery(<TestCaseRunner methodologyId={11} methodologyVersionId={80} periodKey={202609} />);
  await userEvent.click(screen.getByRole('button', { name: 'expressions.runTests' }));
  const cell = await screen.findByRole('cell', { name: code });

  return cell.closest('tr') as HTMLElement;
}

describe('TestCaseRunner — тест без очікувань', () => {
  it('червоний вердикт без розбіжностей показано червоним, а не зеленим', async () => {
    reply = result([{ code: 'PARITY_1', isGreen: false, mismatches: [] }], false);

    const row = await runAndGetRow('PARITY_1');

    expect(within(row).getByText('expressions.testsRed')).toBeTruthy();
    expect(within(row).queryByText('expressions.testsGreen')).toBeNull();
  });

  it('зелений вердикт і далі зелений', async () => {
    reply = result([{ code: 'GOLD_P1', isGreen: true, mismatches: [] }], true);

    const row = await runAndGetRow('GOLD_P1');

    expect(within(row).getByText('expressions.testsGreen')).toBeTruthy();
    expect(within(row).queryByText('expressions.testsRed')).toBeNull();
  });
});
