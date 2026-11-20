import { act, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithMantine } from '@/test/render';
import { ScriptEditor } from '../ScriptEditor';

/*
 * Підроблений Monaco: перевіряє життєвий цикл і синхронізацію значення, а не малювання.
 */

interface FakeEditor {
  doc: string;
  setValue: ReturnType<typeof vi.fn>;
  dispose: ReturnType<typeof vi.fn>;
  fire: (text: string) => void;
}

const state = vi.hoisted(() => ({ editors: [] as unknown[] }));

vi.mock('@/features/expressions/monaco', () => ({
  prepare: () => undefined,
  LightTheme: 'light',
  DarkTheme: 'dark',
}));

vi.mock('monaco-editor/editor/editor.api', () => {
  const model = { uri: { toString: () => 'inmemory://model/1' }, getOffsetAt: () => 0 };
  return {
    KeyMod: { CtrlCmd: 1 },
    KeyCode: { KeyS: 2 },
    MarkerSeverity: { Error: 8 },
    languages: {
      register: () => undefined,
      setLanguageConfiguration: () => undefined,
      setMonarchTokensProvider: () => undefined,
      registerCompletionItemProvider: () => undefined,
    },
    editor: {
      setTheme: () => undefined,
      setModelMarkers: () => undefined,
      create: (_host: unknown, options: { value: string }) => {
        const contentListeners: (() => void)[] = [];
        const created = {
          doc: options.value,
          getValue: () => created.doc,
          setValue: vi.fn((text: string) => {
            created.doc = text;
            for (const listener of contentListeners) listener();
          }),
          getModel: () => model,
          getPosition: () => ({ lineNumber: 1, column: 1 }),
          onDidDispose: () => undefined,
          onDidChangeModelContent: (listener: () => void) => contentListeners.push(listener),
          onDidChangeCursorPosition: () => undefined,
          addCommand: () => undefined,
          updateOptions: () => undefined,
          dispose: vi.fn(),
          fire: (text: string) => {
            created.doc = text;
            for (const listener of contentListeners) listener();
          },
        };
        state.editors.push(created);
        return created;
      },
    },
  };
});

const editors = (): FakeEditor[] => state.editors as FakeEditor[];

function props(value: string, onChange: (text: string) => void) {
  return { value, onChange, diagnostics: [], completion: () => ({ outline: null, tables: [], functions: [], headerFields: [] }), ariaLabel: 'script' } as const;
}

describe('F13: ScriptEditor — синхронізація значення і прибирання', () => {
  beforeEach(() => {
    state.editors = [];
  });

  it('відлуння власного набору (value відстає від друку) не стирає набране', async () => {
    const onChange = vi.fn();
    const view = renderWithMantine(<ScriptEditor {...props('a', onChange)} />);
    await waitFor(() => {
      expect(editors()).toHaveLength(1);
    });
    const editor = editors()[0] as FakeEditor;

    // два символи надруковано, поки батько ще рендерить перший
    act(() => {
      editor.fire('ab');
      editor.fire('abc');
    });
    view.rerender(<ScriptEditor {...props('ab', onChange)} />);
    expect(editor.doc).toBe('abc');
    expect(editor.setValue).not.toHaveBeenCalled();

    view.rerender(<ScriptEditor {...props('abc', onChange)} />);
    expect(editor.doc).toBe('abc');
  });

  it('значення ззовні (перезавантаження) потрапляє в редактор', async () => {
    const onChange = vi.fn();
    const view = renderWithMantine(<ScriptEditor {...props('a', onChange)} />);
    await waitFor(() => {
      expect(editors()).toHaveLength(1);
    });
    const editor = editors()[0] as FakeEditor;

    act(() => {
      editor.fire('ab');
    });
    view.rerender(<ScriptEditor {...props('ab', onChange)} />);
    view.rerender(<ScriptEditor {...props('з сервера', onChange)} />);
    expect(editor.doc).toBe('з сервера');
  });

  it('unmount знищує редактор; створення, що не встигло, не лишає редактора', async () => {
    const view = renderWithMantine(<ScriptEditor {...props('a', vi.fn())} />);
    await waitFor(() => {
      expect(editors()).toHaveLength(1);
    });
    view.unmount();
    expect((editors()[0] as FakeEditor).dispose).toHaveBeenCalledTimes(1);

    const early = renderWithMantine(<ScriptEditor {...props('b', vi.fn())} />);
    early.unmount();
    await new Promise((done) => setTimeout(done, 30));
    expect(editors()).toHaveLength(1);
    expect(screen.queryByTestId('script-editor')).toBeNull();
  });
});
