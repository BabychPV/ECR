import { describe, expect, it } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { GridKeyHint } from '../GridKeyHint';
import { isRecalculateKey, isTypingOrDialogTarget } from '../shortcutKey';

/** `UI-41`: видима підказка клавіш і довідка «Keyboard shortcuts». */
function show(readOnly: boolean): void {
  render(
    <MantineProvider>
      <GridKeyHint readOnly={readOnly} />
    </MantineProvider>,
  );
}

describe('GridKeyHint', () => {
  it('редагований аркуш: підказка про Enter/F2/Ctrl+V/F9, у довідці — F9 і Ctrl+V', async () => {
    show(false);
    expect(screen.getByText(/grid\.keys\.hint⟧/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /grid\.keys\.help/ }));
    const keys = await waitFor(() => {
      const found = [...document.querySelectorAll('.ecr-keyhint-list kbd')].map((node) => node.textContent);
      expect(found.length).toBeGreaterThan(0);

      return found;
    });
    expect(keys).toContain('F9');
    expect(keys).toContain('Ctrl+V');
    expect(keys).toContain('Enter · F2');
  });

  it('аркуш лише для читання: інша підказка, у довідці немає клавіш редагування', async () => {
    show(true);
    expect(screen.getByText(/grid\.keys\.hintReadOnly/)).toBeTruthy();

    await userEvent.click(screen.getByRole('button', { name: /grid\.keys\.help/ }));
    const keys = await waitFor(() => {
      const found = [...document.querySelectorAll('.ecr-keyhint-list kbd')].map((node) => node.textContent);
      expect(found.length).toBeGreaterThan(0);

      return found;
    });
    expect(keys).toContain('Ctrl+C');
    expect(keys).not.toContain('F9');
    expect(keys).not.toContain('Ctrl+V');
    expect(keys).not.toContain('Enter · F2');
  });
});

describe('isRecalculateKey', () => {
  const base = { code: 'F9', key: 'F9', ctrlKey: false, altKey: false, shiftKey: false, metaKey: false };

  it('лише голий F9; за фізичною клавішею, запас — key без code', () => {
    expect(isRecalculateKey(base)).toBe(true);
    expect(isRecalculateKey({ ...base, code: '' })).toBe(true);
    expect(isRecalculateKey({ ...base, ctrlKey: true })).toBe(false);
    expect(isRecalculateKey({ ...base, altKey: true })).toBe(false);
    expect(isRecalculateKey({ ...base, shiftKey: true })).toBe(false);
    expect(isRecalculateKey({ ...base, metaKey: true })).toBe(false);
    expect(isRecalculateKey({ ...base, code: 'F8', key: 'F8' })).toBe(false);
  });
});

describe('isTypingOrDialogTarget', () => {
  it('поле, textarea, contenteditable, діалог — так; звичайний вузол і null — ні', () => {
    document.body.innerHTML =
      '<input id="i"/><textarea id="t"></textarea><div contenteditable="true" id="c"></div>' +
      '<div role="dialog"><button id="d">x</button></div><div id="p">plain</div>';
    const byId = (id: string) => document.getElementById(id);

    expect(isTypingOrDialogTarget(byId('i'))).toBe(true);
    expect(isTypingOrDialogTarget(byId('t'))).toBe(true);
    expect(isTypingOrDialogTarget(byId('c'))).toBe(true);
    expect(isTypingOrDialogTarget(byId('d'))).toBe(true);
    expect(isTypingOrDialogTarget(byId('p'))).toBe(false);
    expect(isTypingOrDialogTarget(null)).toBe(false);
  });
});
