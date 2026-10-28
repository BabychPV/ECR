import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider, Tabs } from '@mantine/core';
import { testTheme } from '@/test/render';
import { neighbourSheet, SheetStrip } from '../SheetStrip';

const Sheets = [
  { code: 'S1', name: 'First', state: 'Draft' },
  { code: 'S2', name: 'Second', state: 'Submitted' },
  { code: 'S3', name: 'Third', state: 'Draft' },
];

function show(activeCode: string, onSelect = vi.fn<(code: string) => void>()): typeof onSelect {
  render(
    <MantineProvider theme={testTheme}>
      <Tabs value={activeCode}>
        <SheetStrip sheets={Sheets} activeCode={activeCode} onSelect={onSelect} />
      </Tabs>
    </MantineProvider>,
  );
  return onSelect;
}

describe('SheetStrip', () => {
  it('neighbourSheet: краї дають null', () => {
    expect(neighbourSheet(Sheets, 'S1', -1)).toBeNull();
    expect(neighbourSheet(Sheets, 'S1', 1)).toBe('S2');
    expect(neighbourSheet(Sheets, 'S3', 1)).toBeNull();
    expect(neighbourSheet(Sheets, undefined, 1)).toBeNull();
  });

  it('на першому аркуші «‹» вимкнена, «›» веде на сусідній', () => {
    const onSelect = show('S1');

    expect((screen.getByTestId('sheet-prev') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('sheet-next'));
    expect(onSelect).toHaveBeenCalledWith('S2');
  });

  it('на останньому аркуші «›» вимкнена', () => {
    show('S3');

    expect((screen.getByTestId('sheet-next') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('sheet-prev') as HTMLButtonElement).disabled).toBe(false);
  });

  it('вкладки лишаються tablist з усіма аркушами', () => {
    show('S2');

    const tabs = within(screen.getByRole('tablist')).getAllByRole('tab');
    expect(tabs).toHaveLength(3);
    expect(tabs[1]?.getAttribute('aria-selected')).toBe('true');
  });

  it('«…» відкриває список усіх аркушів; вибір переходить', async () => {
    const onSelect = show('S1');

    fireEvent.click(screen.getByTestId('sheet-all'));
    const item = await screen.findByRole('menuitem', { name: /Third/ });
    fireEvent.click(item);

    expect(onSelect).toHaveBeenCalledWith('S3');
  });
  it('макет A: група «‹ › …» ліворуч, далі вкладки, праворуч слот прокрутки таблиці', () => {
    render(
      <MantineProvider theme={testTheme}>
        <Tabs value="S1">
          <SheetStrip sheets={Sheets} activeCode="S1" onSelect={vi.fn()}>
            <div data-testid="scroll-slot" />
          </SheetStrip>
        </Tabs>
      </MantineProvider>,
    );

    const group = document.querySelector('.doc-sheet-nav-group') as HTMLElement;
    const tablist = screen.getByRole('tablist');
    const slot = screen.getByTestId('scroll-slot');
    expect(group.contains(screen.getByTestId('sheet-prev'))).toBe(true);
    expect(group.contains(screen.getByTestId('sheet-next'))).toBe(true);
    expect(group.contains(screen.getByTestId('sheet-all'))).toBe(true);
    expect(group.compareDocumentPosition(tablist) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(tablist.compareDocumentPosition(slot) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(slot.closest('.doc-sheet-scroll')).not.toBeNull();
  });
});
