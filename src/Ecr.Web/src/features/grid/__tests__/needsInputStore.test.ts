import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearCellNavigation, currentCellNavigation } from '../cellNavigation';
import { publishFocus, resetFocus } from '../focusStore';
import type { NeedsInputCell } from '../needsInput';
import {
  allNeedsInput,
  clearNeedsInput,
  goToFirstNeedsInput,
  installNeedsInputKeys,
  needsInputSummary,
  publishNeedsInput,
  resetNeedsInput,
  stepNeedsInput,
} from '../needsInputStore';
import { needsInputStepKey } from '../shortcutKey';
import { showDone } from '@/shared/ui/notify';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * ✎ DOCUX L2 (AC L2-3): F8 / Shift+F8 і ‹ › переводять до наступної / попередньої порожньої обов'язкової комірки АРКУША:
 * після останньої в таблиці — перша наступної таблиці; після останньої на аркуші — «більше немає».
 */
function cell(rowKey: string, columnCode: string, rowIndex: number, columnIndex: number, level: 'block' | 'warn' = 'block'): NeedsInputCell {
  return { rowKey, columnCode, level, source: 'template', methodologies: [], rowIndex, columnIndex };
}

function publishTwoTables(): void {
  publishNeedsInput({ tableInstanceId: 11, tableDefId: 1, periodKey: 202609, order: 1, labelOffset: 1, cells: [cell('a1', 'C1', 0, 0), cell('a3', 'C2', 2, 1, 'warn')] });
  publishNeedsInput({ tableInstanceId: 22, tableDefId: 2, periodKey: 202609, order: 2, labelOffset: 0, cells: [cell('b2', 'X', 1, 0)] });
}

const target = (): string | null => {
  const request = currentCellNavigation();
  return request === null ? null : `${String(request.tableInstanceId)}/${String(request.rowKey)}/${String(request.columnCode)}`;
};

function f8(shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'F8', code: 'F8', shiftKey, cancelable: true, bubbles: true });
  window.dispatchEvent(event);
  return event;
}

afterEach(() => {
  resetNeedsInput();
  resetFocus();
  clearCellNavigation();
  vi.mocked(showDone).mockClear();
});

describe('needsInputStore: обхід порожніх обов’язкових комірок аркуша', () => {
  it('без курсора — перша комірка першої таблиці; зведення лічить усі таблиці', () => {
    publishTwoTables();

    expect(needsInputSummary()).toEqual({ count: 3, blocking: 2, unmountedTables: 0 });
    expect(stepNeedsInput(1)).toBe(true);
    expect(target()).toBe('11/a1/C1');
  });

  it('від курсора: наступна в таблиці, далі — перша наступної таблиці, після останньої — false', () => {
    publishTwoTables();
    // Курсор на a1:C1 таблиці 11 (відрендерена колонка = 0 + колонка підпису).
    publishFocus(11, 202609, { rowIndex: 0, columnIndex: 1 });

    expect(stepNeedsInput(1)).toBe(true);
    expect(target()).toBe('11/a3/C2');

    publishFocus(11, 202609, { rowIndex: 2, columnIndex: 2 });
    expect(stepNeedsInput(1)).toBe(true);
    expect(target()).toBe('22/b2/X');

    publishFocus(22, 202609, { rowIndex: 1, columnIndex: 0 });
    clearCellNavigation();
    expect(stepNeedsInput(1)).toBe(false);
    expect(target()).toBeNull();
  });

  it('Shift: попередня — через межу таблиць назад', () => {
    publishTwoTables();
    publishFocus(22, 202609, { rowIndex: 1, columnIndex: 0 });

    expect(stepNeedsInput(-1)).toBe(true);
    expect(target()).toBe('11/a3/C2');
  });

  it('‹ › з тулбару таблиці без курсора в ній — від її початку', () => {
    publishTwoTables();
    publishFocus(11, 202609, { rowIndex: 0, columnIndex: 1 });

    expect(stepNeedsInput(1, { tableInstanceId: 22, periodKey: 202609 })).toBe(true);
    expect(target()).toBe('22/b2/X');
  });

  it('F8 на сторінці — той самий обхід; після останньої — тост «більше немає»; Shift+F8 — назад', () => {
    const uninstall = installNeedsInputKeys();
    try {
      publishTwoTables();
      expect(f8().defaultPrevented).toBe(true);
      expect(target()).toBe('11/a1/C1');

      publishFocus(22, 202609, { rowIndex: 1, columnIndex: 0 });
      f8();
      expect(vi.mocked(showDone)).toHaveBeenCalledWith(expect.stringContaining('grid.needsInput.none'));

      f8(true);
      expect(target()).toBe('11/a3/C2');
    } finally {
      uninstall();
    }
  });

  it('порожніх немає — F8 нічого не перехоплює; розмонтована сітка знімає свої комірки', () => {
    const uninstall = installNeedsInputKeys();
    try {
      expect(f8().defaultPrevented).toBe(false);
      publishTwoTables();
      clearNeedsInput(11, 202609);
      expect(allNeedsInput().map((entry) => entry.cell.rowKey)).toEqual(['b2']);
      expect(goToFirstNeedsInput()).toBe(true);
      expect(target()).toBe('22/b2/X');
    } finally {
      uninstall();
    }
  });
});

describe('needsInputStepKey', () => {
  const base = { code: 'F8', key: 'F8', ctrlKey: false, altKey: false, shiftKey: false, metaKey: false };

  it('F8 — наступна, Shift+F8 — попередня; з Ctrl/Alt/Meta чи інша клавіша — ні', () => {
    expect(needsInputStepKey(base)).toBe(1);
    expect(needsInputStepKey({ ...base, shiftKey: true })).toBe(-1);
    expect(needsInputStepKey({ ...base, code: '', key: 'F8' })).toBe(1);
    expect(needsInputStepKey({ ...base, ctrlKey: true })).toBeNull();
    expect(needsInputStepKey({ ...base, altKey: true })).toBeNull();
    expect(needsInputStepKey({ ...base, metaKey: true })).toBeNull();
    expect(needsInputStepKey({ ...base, code: 'F9', key: 'F9' })).toBeNull();
  });
});
