/**
 * Завантаження Monaco для редактора скрипта — ОДНЕ на сесію вкладки (✎ P-1124-1, замір 25.11).
 *
 * ⛔ Раніше `ScriptEditor` вантажив два чанки ПОСЛІДОВНО (`expressions/monaco`, потім `editor.api`) і лише після
 * монтування — водоспад на першому відкритті. Тепер обидва йдуть паралельно, а сторінка версій методології
 * (звідки людина йде в редактор) прогріває їх у вільний час браузера (`requestIdleCallback`).
 */
type Loaded = readonly [typeof import('../expressions/monaco'), typeof import('monaco-editor/editor/editor.api')];

let pending: Promise<Loaded> | null = null;

/** Чанки Monaco і тем; повторний виклик повертає той самий запит. Відмова скидає кеш — наступний виклик повторить. */
export function loadScriptEditorModules(): Promise<Loaded> {
  pending ??= Promise.all([import('../expressions/monaco'), import('monaco-editor/editor/editor.api')]).catch(
    (error: unknown) => {
      pending = null;
      throw error;
    },
  );

  return pending;
}

/**
 * Прогрів у вільний час браузера. Без `requestIdleCallback` (jsdom, старі рушії) — нічого: прогрів не повинен
 * конкурувати з першим кадром сторінки, а редактор однаково завантажить сам.
 */
export function preloadScriptEditorWhenIdle(): () => void {
  if (typeof window === 'undefined' || typeof window.requestIdleCallback !== 'function') return () => undefined;

  const handle = window.requestIdleCallback(() => {
    void loadScriptEditorModules().catch(() => undefined);
  });

  return () => window.cancelIdleCallback(handle);
}
