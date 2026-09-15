import { useState, type JSX } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RelationForm } from '../RelationForm';
import { emptyDraft, type RelationDraft } from '../relation';

/**
 * Аудит-пас 8, lane7, п.12: форма зв'язку таблиць показувала «Column mapping»
 * повністю активним для КОЖНОГО `Kind`, включно з `Check`, чий власний
 * лейбл (`tables.kindCheck` — «a cross-table rule, no values move») прямо
 * каже, що переносити нічого. Фікс навмисно вузький — лише `Check`: для
 * решти п'яти видів (Mirror/Rollup/Reference/Cascade/Copy) жодне джерело
 * домену не документує, яке поле для них саме не застосовне (`Kind` там
 * сьогодні суто описовий).
 *
 * ⛔ Каталог рядків не завантажений (як і в `editor.test.tsx` поруч) —
 * підписи приходять ключами в `⟦…⟧`; перевіряється структура (disabled,
 * зміна опису), а не переклад.
 */
function Harness(): JSX.Element {
  const [draft, setDraft] = useState<RelationDraft>(emptyDraft());

  return (
    <RelationForm
      draft={draft}
      tables={[]}
      disabled={false}
      saving={false}
      onChange={setDraft}
      onSubmit={() => {}}
    />
  );
}

function show(): void {
  render(
    <MantineProvider>
      <Harness />
    </MantineProvider>,
  );
}

describe('RelationForm: «Column mapping» для Kind=Check (аудит-пас 8, lane7, п.12)', () => {
  it('Kind=Rollup (початковий) — «Column mapping» активне', () => {
    show();

    const mapJson = screen.getByLabelText('⟦tables.mapJson⟧') as HTMLTextAreaElement;
    expect(mapJson.hasAttribute('disabled')).toBe(false);
    expect(screen.getByText('⟦tables.mapJsonHint⟧')).toBeDefined();
  });

  it('перемикання на Kind=Check вимикає «Column mapping» і пояснює причину', () => {
    show();

    fireEvent.change(screen.getByLabelText('⟦tables.relationKind⟧'), {
      target: { value: 'Check' },
    });

    const mapJson = screen.getByLabelText('⟦tables.mapJson⟧') as HTMLTextAreaElement;
    // ⛔ Мутаційний доказ: без `disabled || mapJsonNotApplicable` у
    // `RelationForm.tsx` поле лишається клікабельним і на `Check`.
    expect(mapJson.hasAttribute('disabled')).toBe(true);
    expect(screen.getByText('⟦tables.mapJsonNotApplicableForCheck⟧')).toBeDefined();
    expect(screen.queryByText('⟦tables.mapJsonHint⟧')).toBeNull();
  });

  it('перемикання назад із Check на інший Kind знову вмикає поле', () => {
    show();

    const kindSelect = screen.getByLabelText('⟦tables.relationKind⟧');
    fireEvent.change(kindSelect, { target: { value: 'Check' } });
    fireEvent.change(kindSelect, { target: { value: 'Mirror' } });

    const mapJson = screen.getByLabelText('⟦tables.mapJson⟧') as HTMLTextAreaElement;
    expect(mapJson.hasAttribute('disabled')).toBe(false);
    expect(screen.getByText('⟦tables.mapJsonHint⟧')).toBeDefined();
  });
});
