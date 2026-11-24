import { Alert, Box, useComputedColorScheme } from '@mantine/core';
import {
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type JSX,
  type Ref,
} from 'react';
import { t } from '@/shared/i18n';
import type { MonacoModule } from '../expressions/monaco';
import {
  scriptCompletions,
  ScriptTriggerCharacters,
  type ScriptCompletionContext,
  type ScriptCompletionKind,
} from './scriptCompletion';
import { registerScriptLanguage, ScriptLanguageId } from './scriptLanguage';
import { scriptMarkers } from './scriptMarkers';
import type { ScriptDiagnosticDto } from './types';

/**
 * Редактор скрипта методології на Monaco.
 *
 * ⛔ Monaco вантажиться лише динамічно (`await import`), як у `ExpressionEditor`:
 * чанк важить сотні КБ gzip і не повинен потрапляти в чанк маршруту (`D-132`).
 * Уся логіка підказок, розмітки й токенізації — у чистих модулях поруч.
 */

/** Дані для підказок, що не залежать від курсора. */
export type ScriptCompletionBase = Omit<ScriptCompletionContext, 'text' | 'offset'>;

export interface ScriptEditorHandle {
  /** Переходить до позиції (1-базові рядок і колонка) і фокусує редактор. */
  readonly reveal: (line: number, column: number) => void;
  /** Замінює діапазон символів тексту (для «Редагувати як таблицю»). */
  readonly replaceRange: (start: number, end: number, text: string) => void;
  /** Зміщення курсора в тексті. */
  readonly cursorOffset: () => number;
  /** Фокус у редактор (курсор і виділення лишаються). */
  readonly focus: () => void;
  /** Перерозкладка під нову рамку (вхід/вихід «на весь екран»). */
  readonly layout: () => void;
}

export interface ScriptEditorProps {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly diagnostics: readonly ScriptDiagnosticDto[];
  /** Читається у мить запиту підказок, тож може змінюватися без перестворення редактора. */
  readonly completion: () => ScriptCompletionBase;
  /** ⛔ Обов'язковий: Monaco малює поле без підпису (ФВ-14.16). */
  readonly ariaLabel: string;
  readonly height?: number | string;
  readonly onSave?: () => void;
  /** Курсор перемістився: нове зміщення в тексті (для кнопки «Редагувати як таблицю»). */
  readonly onCursorChange?: (offset: number) => void;
  readonly readOnly?: boolean;
  readonly ref?: Ref<ScriptEditorHandle>;
}

type Editor = ReturnType<MonacoModule['editor']['create']>;

/** Джерело підказок кожної живої моделі — за її `uri`. */
const sources = new Map<string, () => ScriptCompletionBase>();
const providerRegistered = new WeakSet<object>();

const MarkerOwner = 'ecr-script';

function kindOf(monaco: MonacoModule, kind: ScriptCompletionKind): number {
  const k = monaco.languages.CompletionItemKind;
  switch (kind) {
    case 'keyword':
      return k.Keyword;
    case 'snippet':
      return k.Snippet;
    case 'field':
      return k.Field;
    case 'constant':
      return k.Constant;
    case 'variable':
      return k.Variable;
    case 'function':
      return k.Function;
    case 'property':
      return k.Property;
    case 'module':
      return k.Module;
  }
}

function registerProvider(monaco: MonacoModule): void {
  if (providerRegistered.has(monaco)) return;
  providerRegistered.add(monaco);

  monaco.languages.registerCompletionItemProvider(ScriptLanguageId, {
    triggerCharacters: [...ScriptTriggerCharacters],
    provideCompletionItems(model, position) {
      const base = sources.get(model.uri.toString())?.();
      if (base === undefined) return { suggestions: [] };

      const items = scriptCompletions({
        ...base,
        text: model.getValue(),
        offset: model.getOffsetAt(position),
      });
      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn,
      };

      return {
        suggestions: items.map((item) => ({
          label: item.label,
          kind: kindOf(monaco, item.kind),
          insertText: item.insertText,
          ...(item.isSnippet
            ? { insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet }
            : {}),
          ...(item.detail === undefined ? {} : { detail: item.detail }),
          ...(item.documentation === undefined ? {} : { documentation: item.documentation }),
          ...(item.sortText === undefined ? {} : { sortText: item.sortText }),
          range,
        })),
      };
    },
  });
}

export function ScriptEditor(props: ScriptEditorProps): JSX.Element {
  const { value, diagnostics, ariaLabel, height = 480, readOnly = false } = props;
  const scheme = useComputedColorScheme('light');

  const host = useRef<HTMLDivElement | null>(null);
  const editor = useRef<Editor | null>(null);
  const api = useRef<MonacoModule | null>(null);
  const themes = useRef<{ light: string; dark: string }>({ light: '', dark: '' });
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);

  // Тексти, які редактор уже віддав батькові, але батько ще не повернув їх у `value` (друк швидший за рендер).
  const emitted = useRef<string[]>([]);
  const valueRef = useRef(value);
  valueRef.current = value;
  const onChangeRef = useRef(props.onChange);
  onChangeRef.current = props.onChange;
  const onSaveRef = useRef(props.onSave);
  onSaveRef.current = props.onSave;
  const onCursorRef = useRef(props.onCursorChange);
  onCursorRef.current = props.onCursorChange;
  const completionRef = useRef(props.completion);
  completionRef.current = props.completion;
  const schemeRef = useRef(scheme);
  schemeRef.current = scheme;

  useImperativeHandle(
    props.ref,
    () => ({
      reveal(line, column) {
        const current = editor.current;
        if (current === null) return;
        current.revealLineInCenterIfOutsideViewport(line);
        current.setPosition({ lineNumber: line, column });
        current.focus();
      },
      replaceRange(start, end, text) {
        const current = editor.current;
        const model = current?.getModel();
        if (current === null || model === null || model === undefined) return;
        const from = model.getPositionAt(start);
        const to = model.getPositionAt(end);
        current.executeEdits('const-table', [
          {
            range: {
              startLineNumber: from.lineNumber,
              startColumn: from.column,
              endLineNumber: to.lineNumber,
              endColumn: to.column,
            },
            text,
          },
        ]);
      },
      cursorOffset() {
        const current = editor.current;
        const model = current?.getModel();
        const position = current?.getPosition();
        if (model === null || model === undefined || position === null || position === undefined) {
          return 0;
        }
        return model.getOffsetAt(position);
      },
      focus() {
        editor.current?.focus();
      },
      layout() {
        editor.current?.layout();
      },
    }),
    [],
  );

  // ── Створення редактора ───────────────────────────────────────────────────
  useEffect(() => {
    let disposed = false;

    void (async () => {
      try {
        const expressions = await import('../expressions/monaco');
        const monaco = await import('monaco-editor/editor/editor.api');
        if (disposed || host.current === null) return;

        // ⚠ Теми визначає лише `prepare` наявного модуля; виклик заодно реєструє
        // мову виразів, яка тут не використовується, але нічого не коштує.
        expressions.prepare('Methodology', () => undefined);

        api.current = monaco;
        themes.current = { light: expressions.LightTheme, dark: expressions.DarkTheme };
        registerScriptLanguage(monaco);
        registerProvider(monaco);

        const created = monaco.editor.create(host.current, {
          value: valueRef.current,
          language: ScriptLanguageId,
          theme: schemeRef.current === 'dark' ? themes.current.dark : themes.current.light,
          ariaLabel,
          readOnly,
          minimap: { enabled: false },
          lineNumbers: 'on',
          scrollBeyondLastLine: false,
          automaticLayout: true,
          fixedOverflowWidgets: true,
          tabSize: 4,
          insertSpaces: true,
          wordWrap: 'off',
          quickSuggestions: { other: true, comments: false, strings: false },
          suggestOnTriggerCharacters: true,
          wordBasedSuggestions: 'off',
        });
        editor.current = created;

        const model = created.getModel();
        if (model !== null) {
          const key = model.uri.toString();
          sources.set(key, () => completionRef.current());
          created.onDidDispose(() => sources.delete(key));
        }

        created.onDidChangeModelContent(() => {
          const text = created.getValue();
          emitted.current.push(text);
          onChangeRef.current(text);
        });
        created.onDidChangeCursorPosition((event) => {
          const current = created.getModel();
          if (current !== null) onCursorRef.current?.(current.getOffsetAt(event.position));
        });
        created.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
          onSaveRef.current?.();
        });

        setReady(true);
      } catch {
        if (!disposed) setFailed(true);
      }
    })();

    return () => {
      disposed = true;
      editor.current?.dispose();
      editor.current = null;
      api.current = null;
      setReady(false);
    };
    // Редактор створюється один раз; все змінне читається з ref.
  }, []);

  // ── Зовнішня зміна тексту ─────────────────────────────────────────────────
  useEffect(() => {
    const current = editor.current;
    if (current === null) return;
    if (current.getValue() === value) {
      emitted.current = [];
      return;
    }

    // ⛔ `value` — відлуння того, що редактор сам щойно віддав (а друк уже пішов уперед): `setValue` стер би набране.
    const own = emitted.current.lastIndexOf(value);
    if (own !== -1) {
      emitted.current = emitted.current.slice(own + 1);
      return;
    }

    emitted.current = [];
    current.setValue(value);
  }, [value, ready]);

  // ── Діагностики ───────────────────────────────────────────────────────────
  useEffect(() => {
    const monaco = api.current;
    const model = editor.current?.getModel();
    if (monaco === null || model === null || model === undefined) return;

    monaco.editor.setModelMarkers(
      model,
      MarkerOwner,
      scriptMarkers(diagnostics).map((marker) => ({
        ...marker,
        severity: monaco.MarkerSeverity.Error,
      })),
    );
  }, [diagnostics, ready]);

  // ── Тема й режим читання ──────────────────────────────────────────────────
  useEffect(() => {
    const monaco = api.current;
    if (monaco === null) return;
    monaco.editor.setTheme(scheme === 'dark' ? themes.current.dark : themes.current.light);
  }, [scheme, ready]);

  useEffect(() => {
    editor.current?.updateOptions({ readOnly });
  }, [readOnly, ready]);

  if (failed) {
    return (
      <Alert color="statusError" role="alert">
        {t('methodologyScript.editorLoadFailed')}
      </Alert>
    );
  }

  return <Box ref={host} h={height} style={{ minHeight: 160 }} data-testid="script-editor" />;
}
