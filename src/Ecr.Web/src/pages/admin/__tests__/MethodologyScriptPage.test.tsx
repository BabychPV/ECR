import { useImperativeHandle, type Ref } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, configure, fireEvent, screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { MethodologyScriptPage } from '@/pages/admin/MethodologyScriptPage';
import { scriptKeys } from '@/features/methodologyScript/scriptQueryKeys';
import { ScriptChangedMessageKey } from '@/features/methodologyScript/scriptEditorState';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * F13c: сторінка редактора скрипта.
 *  • «Перезавантажити» повертає серверний текст і за ІДЕНТИЧНИХ даних, і не лишає прапорця, що дозволив би
 *    наступному фоновому оновленню затерти набране;
 *  • режим «лише читання» (опублікована версія; користувач без права правки) — редактор не редагується, кнопок
 *    «Зберегти» й «Редагувати як таблицю» немає;
 *  • «Застосувати» в діалозі таблиці вставляє літерал із коментарями й `1.0` БЕЗ серверного `format-const`.
 */

interface EditorProps {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly readOnly?: boolean;
  readonly ariaLabel: string;
  readonly ref?: Ref<unknown>;
}

let cursorAt = 0;

vi.mock('@/features/methodologyScript/ScriptEditor', () => ({
  ScriptEditor: function MockEditor(props: EditorProps) {
    useImperativeHandle(props.ref, () => ({
      reveal: () => undefined,
      cursorOffset: () => cursorAt,
      replaceRange: (start: number, end: number, text: string) => {
        props.onChange(props.value.slice(0, start) + text + props.value.slice(end));
      },
    }));

    return (
      <textarea
        aria-label={props.ariaLabel}
        value={props.value}
        readOnly={props.readOnly === true}
        onChange={(event) => {
          props.onChange(event.currentTarget.value);
        }}
      />
    );
  },
}));
vi.mock('@/features/methodologyScript/ScriptPreviewPanel', () => ({ ScriptPreviewPanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptTestsPanel', () => ({ ScriptTestsPanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptOutlinePanel', () => ({ ScriptOutlinePanel: () => null }));
vi.mock('@/features/methodologyScript/ScriptCompareModal', () => ({ ScriptCompareModal: () => null }));
vi.mock('@/features/methodologies/MethodologyContentPanels', () => ({
  MethodologyRequiredInputsPanel: () => null,
}));

const ServerText = 'var server = 1;';
const Hash = 'h1';

interface Setup {
  readonly permissions?: string[];
  readonly status?: string;
  readonly isEditable?: boolean;
  readonly isDraft?: boolean;
  readonly text?: string;
  readonly level?: string;
}

function server(setup: Setup = {}): {
  readonly sent: string[];
  readonly release: () => void;
  readonly scriptGets: () => number;
} {
  let scriptGets = 0;
  const version = {
    id: 12,
    versionNumber: '2.0',
    engine: 'Script',
    status: setup.status ?? 'Draft',
    isEditable: setup.isEditable ?? true,
    effectiveFrom: null,
    numericMode: 'Strict',
    calendarMode: 'Actual',
    traceLevel: 'Off',
    createdByUserId: 1,
    level: setup.level ?? 'Configuration',
  };
  const outline = {
    tables: [], inputs: [], outputs: [], substances: [], imports: [], constants: [], functions: [], variables: [],
  };

  const held = holdWrites({
    read: (url) => {
      if (url.endsWith('/api/v1/me')) {
        return {
          userId: 1,
          userName: 'tester',
          language: 'en',
          permissions: setup.permissions ?? ['Calculation.EditFormula', 'Calculation.View'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        };
      }
      if (url.endsWith('/api/v1/methodologies/1/versions')) return [version];
      if (url.endsWith('/api/v1/methodologies')) {
        return [{ id: 1, code: 'M1', nameL10n: { values: { en: 'Method' } } }];
      }
      if (url.endsWith('/versions/12/script')) {
        scriptGets++;
        return {
          diagnostics: [], engine: 'Script', hash: Hash, isDraft: setup.isDraft ?? true, outline,
          text: setup.text ?? ServerText,
        };
      }
      if (url.endsWith('/versions/12/script/validate')) return { diagnostics: [], outline, kind: null };

      return [];
    },
    write: (method, url) => {
      if (method === 'PUT' && url.endsWith('/versions/12/script')) {
        return {
          status: 409,
          body: { title: 'x', status: 409, errorCode: 'ECR-CALC-0409', messageKey: ScriptChangedMessageKey },
        };
      }

      return {};
    },
  });

  return { sent: held.sent, release: held.release, scriptGets: () => scriptGets };
}

function show(): ReturnType<typeof renderKeepMounted> {
  return renderKeepMounted(
    <Routes>
      <Route path="/admin/methodologies/:id/versions/:vid/script" element={<MethodologyScriptPage />} />
    </Routes>,
    '/admin/methodologies/1/versions/12/script',
  );
}

const editor = (): HTMLTextAreaElement => screen.getByLabelText('⟦methodologyScript.editorLabel⟧');

async function loaded(text = ServerText): Promise<HTMLTextAreaElement> {
  await waitFor(() => {
    expect(editor().value).toBe(text);
  });

  return editor();
}

afterEach(() => {
  vi.unstubAllGlobals();
  cursorAt = 0;
});

// ⚠ Сторінка тягне ліниві модулі й кілька запитів: під навантаженням (повний набір, Windows) типова секунда `waitFor`/`findBy`
// замала — тест падав лише в повному прогоні, в ізоляції проходив. Умови очікування ті самі, довший лише строк.
configure({ asyncUtilTimeout: 10_000 });

describe('MethodologyScriptPage', { timeout: 30_000 }, () => {
  it('«Перезавантажити» за ідентичних даних повертає серверний текст і не лишає прапорця перезапису', async () => {
    const api = server();
    const view = show();
    // Ідентична відповідь у проді зберігає посилання на кеш (structural sharing) — ефект за `script.data` тоді не
    // спрацьовує. Тест робить це детермінованим, бо збіг об'єктів у jsdom не гарантований.
    view.client.setQueryDefaults(scriptKeys.script(12), {
      structuralSharing: (previous, next) =>
        JSON.stringify(previous) === JSON.stringify(next) ? previous : next,
    });
    const area = await loaded();

    // Правка → «Зберегти» → сервер: хеш не збігся → конфлікт.
    fireEvent.change(area, { target: { value: 'var mine = 2;' } });
    fireEvent.click(await screen.findByText('⟦methodologies.save⟧'));
    await waitFor(() => {
      expect(api.sent).toHaveLength(1);
    });
    api.release();
    const reload = await screen.findByText('⟦methodologyScript.conflict.reload⟧', undefined, { timeout: 5000 });
    expect(screen.getByTestId('script-conflict')).toBeTruthy();

    const before = api.scriptGets();
    fireEvent.click(reload);
    // Відповідь ідентична кешу (той самий `hash`/`text`) — кеш не змінюється, але текст мусить стати серверним.
    await waitFor(() => {
      expect(api.scriptGets()).toBeGreaterThan(before);
      expect(editor().value).toBe(ServerText);
    });
    await waitFor(() => {
      expect(screen.queryByTestId('script-conflict')).toBeNull();
    });

    // Далі людина набирає; фонове оновлення кешу (інша вкладка зберегла) не повинно затерти набране.
    fireEvent.change(editor(), { target: { value: 'var typing = 3;' } });
    act(() => {
      view.client.setQueryData(scriptKeys.script(12), {
        diagnostics: [], engine: 'Script', hash: 'h2', isDraft: true, text: 'var other = 4;',
        outline: { tables: [], inputs: [], outputs: [], substances: [], imports: [], constants: [], functions: [], variables: [] },
      });
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(editor().value).toBe('var typing = 3;');
  });

  it('лише читання: опублікована версія — редактор не редагується, «Зберегти» й «Редагувати як таблицю» немає', async () => {
    server({ status: 'Published', isEditable: false, isDraft: false });
    show();
    const area = await loaded();

    expect(area.readOnly).toBe(true);
    expect(screen.queryByText('⟦methodologies.save⟧')).toBeNull();
    expect(screen.queryByText('⟦methodologyScript.editConstTable⟧')).toBeNull();
    expect(screen.getByText('⟦methodologies.readOnly⟧')).toBeTruthy();
    // Вкладки справа працюють і для читання.
    expect(screen.getByText('⟦methodologyScript.tab.preview⟧')).toBeTruthy();
  });

  /**
   * Сервер на перегляд версії рівня Module відповідає 422 `scriptPreviewModuleLevel`: вкладку вимкнено з поясненням,
   * відкрита за замовчуванням — «Входи й виходи». Для звичайної версії вкладка доступна й відкрита першою.
   * Мутація: прибрати `disabled`/ефект перемикання на `io` у `MethodologyScriptPage` (або умову `version?.level === 'Module'`) — червоний.
   */
  it('версія рівня Module: вкладка перегляду вимкнена з поясненням, відкрито «Входи й виходи»', async () => {
    server({ level: 'Module' });
    show();
    await loaded();

    const tab = await screen.findByTestId('script-preview-tab-disabled');
    expect(tab.hasAttribute('disabled') || tab.getAttribute('aria-disabled') === 'true' || tab.getAttribute('data-disabled') === 'true').toBe(true);
    expect(screen.getByTestId('script-preview-module-level').textContent).toContain('⟦methodologyScript.preview.moduleLevel⟧');
    await waitFor(() => {
      expect(screen.getByRole('tab', { name: '⟦methodologyScript.tab.io⟧' }).getAttribute('aria-selected')).toBe('true');
    });

    fireEvent.click(tab);
    expect(screen.getByRole('tab', { name: '⟦methodologyScript.tab.io⟧' }).getAttribute('aria-selected')).toBe('true');
  });

  it('версія рівня Configuration: вкладка перегляду доступна й відкрита першою, пояснення немає', async () => {
    server();
    show();
    await loaded();

    expect(screen.queryByTestId('script-preview-tab-disabled')).toBeNull();
    expect(screen.queryByTestId('script-preview-module-level')).toBeNull();
    expect(screen.getByRole('tab', { name: '⟦methodologyScript.tab.preview⟧' }).getAttribute('aria-selected')).toBe('true');
  });

  it('лише читання: чернетка, але користувач без права редагування формул', async () => {
    server({ permissions: ['Calculation.View'] });
    show();
    const area = await loaded();

    expect(area.readOnly).toBe(true);
    expect(screen.queryByText('⟦methodologies.save⟧')).toBeNull();
    expect(screen.queryByText('⟦methodologyScript.editConstTable⟧')).toBeNull();
  });

  it('чернетка з правом правки: редактор активний, «Зберегти» й «Редагувати як таблицю» є', async () => {
    server();
    show();
    const area = await loaded();

    expect(area.readOnly).toBe(false);
    expect(screen.getByText('⟦methodologies.save⟧')).toBeTruthy();
    expect(screen.getByText('⟦methodologyScript.editConstTable⟧')).toBeTruthy();
  });

  it('«Застосувати» в таблиці коефіцієнтів вставляє коментарі й `1.0` у скрипт без серверного format-const', async () => {
    const literal = ['{', '    // зима', '    A: { x: 1.0, y: 2 }, // холодно', '}'].join('\n');
    const text = `const EF = ${literal};`;
    const api = server({ text });
    show();
    await loaded(text);

    cursorAt = text.indexOf('x:');
    fireEvent.click(screen.getByText('⟦methodologyScript.editConstTable⟧'));
    // Сітка відкрилась; змінюємо `y` — запис `A` друкується каноном, але коментарі лишаються, а `1.0` — дослівно.
    const cell = await waitFor(() => {
      const found = screen.getAllByRole('textbox').find((input) => (input as HTMLInputElement).value === '2');
      expect(found).toBeDefined();

      return found as HTMLElement;
    });
    fireEvent.change(cell, { target: { value: '5' } });
    fireEvent.click(screen.getByText('⟦methodologyScript.constTable.apply⟧'));

    await waitFor(() => {
      expect(editor().value).toBe(
        `const EF = ${['{', '    // зима', '    A: { x: 1.0, y: 5 }, // холодно', '}'].join('\n')};`,
      );
    });
    expect(api.sent.some((request) => request.includes('format-const'))).toBe(false);
  });
});
