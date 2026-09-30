import { useState, type JSX } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PeriodAccessRuleManager } from '@/features/templates/PeriodAccessRuleEditor';
import type { UpdatePeriodAccessRuleDraft } from '@/features/templates/periodAccessRule';

/**
 * Аудит 2026-09-16, §10.8: нечисловий ввід ID правила ставав `NaN`, і саме
 * `NaN` розблоковував кнопки.
 *
 * ⛔ `onRuleIdChange(value.length === 0 ? null : Number(value))` — `Number('x')`
 * дає `NaN`, а перевірка доступності кнопок питає `ruleId === null`. `NaN ===
 * null` — `false`, тож «Зберегти» і «Видалити» ставали активними з
 * ідентифікатором, якого не існує: `PUT /period-access-rules/NaN` — адреса, на
 * яку сервер відповідає 400/404, тобто кнопка обіцяє дію, яку неможливо
 * виконати.
 */
const draft: UpdatePeriodAccessRuleDraft = {
  onOutOfWindow: 'Warn',
  sheetDefId: null,
  tableDefId: null,
  roleId: null,
  rowKind: null,
};

const IdLabel = '⟦periodRules.manageId⟧';
const SaveLabel = '⟦periodRules.save⟧';
const DeleteLabel = '⟦periodRules.delete⟧';

function Harness(): JSX.Element {
  const [ruleId, setRuleId] = useState<number | null>(null);

  return (
    <MantineProvider>
      <PeriodAccessRuleManager
        ruleId={ruleId}
        draft={draft}
        structure={undefined}
        roles={null}
        disabled={false}
        saving={false}
        deleting={false}
        onRuleIdChange={setRuleId}
        onChange={() => {}}
        onSave={() => {}}
        onDelete={() => {}}
      />
      <output aria-label="rule-id">{ruleId === null ? 'null' : String(ruleId)}</output>
    </MantineProvider>
  );
}

function type(value: string): void {
  fireEvent.change(screen.getByLabelText(IdLabel), { target: { value } });
}

function idState(): string {
  return screen.getByLabelText('rule-id').textContent ?? '';
}

describe('PeriodAccessRuleManager: нечисловий ID не розблоковує кнопки (§10.8)', () => {
  it('ввід «abc» лишає ID відсутнім, а не NaN', () => {
    render(<Harness />);

    type('abc');

    // ⛔ Мутаційний доказ (RED до фіксу): у стані опинявся `NaN`, і поле
    // показувало буквально «NaN».
    expect(idState()).toBe('null');
    expect(screen.getByRole('button', { name: SaveLabel })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: DeleteLabel })).toHaveProperty('disabled', true);
  });

  it('дробовий і недодатний ID теж відхиляються: ID правила — цілий', () => {
    render(<Harness />);

    type('12.5');
    expect(idState()).toBe('null');

    type('0');
    expect(idState()).toBe('null');

    type('-3');
    expect(idState()).toBe('null');
  });

  it('справжній ID приймається і розблоковує кнопки', () => {
    render(<Harness />);

    type('42');

    expect(idState()).toBe('42');
    expect(screen.getByRole('button', { name: SaveLabel })).toHaveProperty('disabled', false);
    expect(screen.getByRole('button', { name: DeleteLabel })).toHaveProperty('disabled', false);
  });

  it('очищення поля повертає відсутній ID', () => {
    render(<Harness />);

    type('42');
    type('');

    expect(idState()).toBe('null');
    expect(screen.getByRole('button', { name: SaveLabel })).toHaveProperty('disabled', true);
  });
});
