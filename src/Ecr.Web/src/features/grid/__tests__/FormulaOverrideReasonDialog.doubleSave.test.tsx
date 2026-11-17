import { useState, type JSX } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { renderKeepMounted } from '@/test/closedDialogProbe';
import FormulaOverrideReasonDialog from '../FormulaOverrideReasonDialog';

/**
 * B149: причина ручного значення (D-461) — «Save» віддає причину рівно один раз (зонд: ЗАКРИТО, база 826f654e4).
 *
 * Сам діалог запиту не шле: він віддає причину сітці (`onSave`), а сітка (`DocumentGrid`) знімає його з дерева тим
 * самим кліком (`setOverrideRequest(null)` → `{overrideRequest !== null && …}`). Стан React у дискретній події
 * застосовується синхронно, тож другий клік подвійного кліку приходить уже на від'єднану кнопку.
 *
 * Тест тримає це: якби власник лишав діалог у дереві, другий клік віддав би причину вдруге — червоний.
 */

describe('B149: FormulaOverrideReasonDialog — причина віддається один раз', () => {
  it('подвійний клік «Save»: onSave один раз, діалог знято з дерева', () => {
    const onSave = vi.fn();

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? (
        <FormulaOverrideReasonDialog
          count={1}
          initialReason=""
          onSave={(reason) => {
            setOpen(false);
            onSave(reason);
          }}
          onCancel={() => setOpen(false)}
        />
      ) : null;
    }

    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Meter replaced, value from the act' } });
    const save = document.querySelector<HTMLButtonElement>('[data-formula-override-save]');
    if (save === null) throw new Error('кнопки «Save» немає');

    fireEvent.click(save);
    fireEvent.click(save);

    expect(save.isConnected).toBe(false);
    expect(onSave).toHaveBeenCalledTimes(1);
    expect(onSave).toHaveBeenCalledWith('Meter replaced, value from the act');
  });
});
