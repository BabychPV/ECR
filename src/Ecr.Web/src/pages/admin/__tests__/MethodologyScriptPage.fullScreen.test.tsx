import { useImperativeHandle, type Ref } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { MethodologyScriptPage } from '@/pages/admin/MethodologyScriptPage';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * ✎ 24.11, вимога людини: «код на весь екран» у редакторі скрипта методології.
 *  • кнопка (`aria-pressed`) і `Ctrl+Shift+F` розгортають панель коду на все вікно, бічні вкладки ховаються;
 *  • той самий екземпляр редактора: незбережений текст живе, після входу — фокус у редактор і перерозкладка;
 *  • `Esc` і та сама кнопка — вихід, фокус повертається на кнопку; `Esc` у діалозі режиму не чіпає;
 *  • на весь екран видно лічильник проблем, «Save» працює.
 */

interface EditorProps {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly readOnly?: boolean;
  readonly ariaLabel: string;
  readonly height?: number | string;
  readonly ref?: Ref<unknown>;
}

const focus = vi.fn();
const layout = vi.fn();
let mounts = 0;

vi.mock('@/features/methodologyScript/ScriptEditor', async () => {
  const { useEffect } = await import('react');

  return {
    ScriptEditor: function MockEditor(props: EditorProps) {
      useImperativeHandle(props.ref, () => ({
        reveal: () => undefined,
        cursorOffset: () => 0,
        replaceRange: () => undefined,
        focus,
        layout,
      }));
      useEffect(() => {
        mounts++;
      }, []);

      return (
        <textarea
          aria-label={props.ariaLabel}
          data-height={String(props.height)}
          value={props.value}
          readOnly={props.readOnly === true}
          onChange={(event) => {
            props.onChange(event.currentTarget.value);
          }}
        />
      );
    },
  };
});
vi.mock('@/features/methodologyScript/ScriptPreviewPanel', () => ({ ScriptPreviewPanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptTestsPanel', () => ({ ScriptTestsPanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptOutlinePanel', () => ({ ScriptOutlinePanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptCompareModal', () => ({ ScriptCompareModal: () => null }));
vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyRequiredInputsPanel: () => null,
}));

const Outline = { tables: [], inputs: [], outputs: [], substances: [], imports: [], constants: [], functions: [], variables: [] };
const MissingRows = {
  code: 'ECR-SCRIPT-0001',
  message: 'The script has no rows from clause.',
  messageKey: null,
  params: null,
  line: 1,
  column: 1,
  length: 1,
  severity: 'Error',
};

function server(): ReturnType<typeof holdWrites> {
  return holdWrites({
    read: (url) => {
      if (url.endsWith('/api/v1/me')) {
        return {
          userId: 1,
          userName: 'tester',
          language: 'en',
          permissions: ['Calculation.EditFormula', 'Calculation.View'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        };
      }
      if (url.endsWith('/api/v1/methodologies/1/versions')) {
        return [
          {
            id: 12,
            versionNumber: '2.0',
            engine: 'Script',
            status: 'Draft',
            isEditable: true,
            effectiveFrom: null,
            numericMode: 'Strict',
            calendarMode: 'Actual',
            traceLevel: 'Off',
            createdByUserId: 1,
            level: 'Configuration',
          },
        ];
      }
      if (url.endsWith('/api/v1/methodologies')) return [{ id: 1, code: 'M1', nameL10n: { values: { en: 'Method' } } }];
      if (url.endsWith('/versions/12/script')) {
        return { diagnostics: [MissingRows], engine: 'Script', hash: 'h1', isDraft: true, outline: Outline, text: '// x' };
      }
      if (url.endsWith('/versions/12/script/validate')) return { diagnostics: [MissingRows], outline: Outline, kind: null };

      return [];
    },
    write: (_method, url) =>
      url.endsWith('/script/validate')
        ? { body: { diagnostics: [MissingRows], outline: Outline, kind: null } }
        : { body: { diagnostics: [], engine: 'Script', hash: 'h2', isDraft: true, outline: Outline, text: '' } },
  });
}

function show(): void {
  renderKeepMounted(
    <Routes>
      <Route path="/admin/methodologies/:id/versions/:vid/script" element={<MethodologyScriptPage />} />
    </Routes>,
    '/admin/methodologies/1/versions/12/script',
  );
}

const editorBox = (): HTMLTextAreaElement => screen.getByLabelText('⟦methodologyScript.editorLabel⟧');
const pane = (): HTMLElement => screen.getByTestId('script-editor-pane');
const toggle = (): HTMLElement => screen.getByTestId('script-fullscreen-toggle');

beforeEach(() => {
  focus.mockClear();
  layout.mockClear();
  mounts = 0;
});

afterEach(() => {
  vi.unstubAllGlobals();
  document.body.style.overflow = '';
});

describe('MethodologyScriptPage: код на весь екран', { timeout: 30_000 }, () => {
  it('кнопка розгортає той самий редактор, незбережене живе; Esc — вихід, фокус на кнопці', async () => {
    server();
    show();

    await waitFor(() => {
      expect(editorBox().value).toBe('// x');
    });
    const before = editorBox();
    fireEvent.change(before, { target: { value: '// unsaved edit' } });

    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    expect(toggle().getAttribute('aria-label')).toBe('⟦methodologyScript.fullScreen.label⟧');
    fireEvent.click(toggle());

    expect(pane().getAttribute('data-fullscreen')).toBe('true');
    expect(pane().style.position).toBe('fixed');
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
    // Бічні вкладки сховані, але змонтовані; лічильник проблем — у смузі.
    expect(screen.getByRole('tablist', { hidden: true }).closest('[style*="display: none"]')).not.toBeNull();
    expect(screen.getByTestId('script-fullscreen-problems')).toBeTruthy();
    // Той самий екземпляр редактора (без перемонтування) і той самий незбережений текст.
    expect(editorBox()).toBe(before);
    expect(editorBox().value).toBe('// unsaved edit');
    expect(mounts).toBe(1);
    expect(editorBox().getAttribute('data-height')).toBe('calc(100vh - 110px)');
    await waitFor(() => {
      expect(focus).toHaveBeenCalled();
    });
    expect(layout).toHaveBeenCalled();
    expect(document.body.style.overflow).toBe('hidden');

    fireEvent.keyDown(window, { key: 'Escape' });

    expect(pane().getAttribute('data-fullscreen')).toBe('false');
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    await waitFor(() => {
      expect(document.activeElement).toBe(toggle());
    });
    expect(editorBox().value).toBe('// unsaved edit');
    expect(mounts).toBe(1);
    expect(document.body.style.overflow).toBe('');
  });

  it('Ctrl+Shift+F вмикає й вимикає; Esc у діалозі режиму не чіпає', async () => {
    server();
    show();
    await waitFor(() => {
      expect(editorBox().value).toBe('// x');
    });

    fireEvent.keyDown(window, { key: 'F', code: 'KeyF', ctrlKey: true, shiftKey: true });
    expect(pane().getAttribute('data-fullscreen')).toBe('true');

    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    const inside = document.createElement('button');
    dialog.append(inside);
    document.body.append(dialog);
    fireEvent.keyDown(inside, { key: 'Escape' });
    expect(pane().getAttribute('data-fullscreen')).toBe('true');
    dialog.remove();

    fireEvent.keyDown(window, { key: 'F', code: 'KeyF', ctrlKey: true, shiftKey: true });
    expect(pane().getAttribute('data-fullscreen')).toBe('false');
  });

  it('на весь екран «Save» зберігає набране', async () => {
    const held = server();
    show();
    await waitFor(() => {
      expect(editorBox().value).toBe('// x');
    });
    fireEvent.change(editorBox(), { target: { value: '// saved in full screen' } });
    fireEvent.click(toggle());

    const save = screen.getAllByRole('button', { name: '⟦methodologies.save⟧' });
    fireEvent.click(save[save.length - 1] as HTMLElement);

    await waitFor(() => {
      expect(held.sent).toContain('PUT /api/v1/methodologies/1/versions/12/script');
    });
    const put = held.sent.indexOf('PUT /api/v1/methodologies/1/versions/12/script');
    expect(held.bodies[put]).toMatchObject({ text: '// saved in full screen' });
  });

  it('одна проблема називається в рядку стану, а не лише лічильником', async () => {
    const held = server();
    show();

    // Перевірка тексту — `POST …/validate`: запис, який тест тримає, доки не відпустить.
    await waitFor(() => {
      held.release();
      expect(screen.getByTestId('script-status').textContent).toContain('methodologyScript.problemSingle');
    }, { timeout: 10_000 });
  });
});
