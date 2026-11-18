import { Alert, Box, useComputedColorScheme } from '@mantine/core';
import { useEffect, useRef, useState, type JSX } from 'react';
import { t } from '@/shared/i18n';
import type { MonacoModule } from '../expressions/monaco';
import { registerScriptLanguage, ScriptLanguageId } from './scriptLanguage';

/**
 * Порівняння двох текстів скрипта (Monaco diff, лише читання).
 *
 * ⛔ Monaco — лише динамічним `import()`, як у `ScriptEditor`: чанк не повинен
 * потрапляти в чанк маршруту (`D-132`).
 */

export interface ScriptDiffEditorProps {
  /** Текст «було». */
  readonly original: string;
  /** Текст «стало». */
  readonly modified: string;
  /** ⛔ Обов'язковий: Monaco малює поле без підпису (ФВ-14.16). */
  readonly ariaLabel: string;
  readonly height?: number | string;
}

type DiffEditor = ReturnType<MonacoModule['editor']['createDiffEditor']>;
type TextModel = ReturnType<MonacoModule['editor']['createModel']>;

export function ScriptDiffEditor(props: ScriptDiffEditorProps): JSX.Element {
  const { original, modified, ariaLabel, height = 420 } = props;
  const scheme = useComputedColorScheme('light');

  const host = useRef<HTMLDivElement | null>(null);
  const editor = useRef<DiffEditor | null>(null);
  const models = useRef<{ original: TextModel; modified: TextModel } | null>(null);
  const api = useRef<MonacoModule | null>(null);
  const themes = useRef<{ light: string; dark: string }>({ light: '', dark: '' });
  const textRef = useRef({ original, modified });
  textRef.current = { original, modified };
  const schemeRef = useRef(scheme);
  schemeRef.current = scheme;
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let disposed = false;

    void (async () => {
      try {
        const expressions = await import('../expressions/monaco');
        const monaco = await import('monaco-editor/editor/editor.api');
        if (disposed || host.current === null) return;

        expressions.prepare('Methodology', () => undefined);
        api.current = monaco;
        themes.current = { light: expressions.LightTheme, dark: expressions.DarkTheme };
        registerScriptLanguage(monaco);

        const created = monaco.editor.createDiffEditor(host.current, {
          theme: schemeRef.current === 'dark' ? themes.current.dark : themes.current.light,
          readOnly: true,
          originalEditable: false,
          renderSideBySide: true,
          automaticLayout: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          ariaLabel,
        });
        const pair = {
          original: monaco.editor.createModel(textRef.current.original, ScriptLanguageId),
          modified: monaco.editor.createModel(textRef.current.modified, ScriptLanguageId),
        };
        created.setModel(pair);
        editor.current = created;
        models.current = pair;
        setReady(true);
      } catch {
        if (!disposed) setFailed(true);
      }
    })();

    return () => {
      disposed = true;
      editor.current?.dispose();
      models.current?.original.dispose();
      models.current?.modified.dispose();
      editor.current = null;
      models.current = null;
      api.current = null;
      setReady(false);
    };
    // Редактор створюється один раз; тексти й тема оновлюються нижче.
  }, []);

  useEffect(() => {
    const pair = models.current;
    if (pair === null) return;
    if (pair.original.getValue() !== original) pair.original.setValue(original);
    if (pair.modified.getValue() !== modified) pair.modified.setValue(modified);
  }, [original, modified, ready]);

  useEffect(() => {
    api.current?.editor.setTheme(scheme === 'dark' ? themes.current.dark : themes.current.light);
  }, [scheme, ready]);

  if (failed) {
    return (
      <Alert color="statusError" role="alert">
        {t('methodologyScript.editorLoadFailed')}
      </Alert>
    );
  }

  return <Box ref={host} h={height} style={{ minHeight: 160 }} data-testid="script-diff-editor" />;
}
