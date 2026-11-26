import { afterEach, describe, expect, it, vi } from 'vitest';

/**
 * ✎ P-1124-1 (замір 25.11): чанки Monaco редактора скрипта — паралельно, один запит на вкладку, прогрів у вільний час.
 * Мутація: прибрати `??=` (новий запит щоразу) — перший тест червоний (імпорт двічі).
 */
const loads = { expressions: 0, api: 0 };

vi.mock('@/features/expressions/monaco', () => {
  loads.expressions += 1;
  return { prepare: () => undefined, LightTheme: 'l', DarkTheme: 'd' };
});
vi.mock('monaco-editor/editor/editor.api', () => {
  loads.api += 1;
  return { editor: {} };
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('preloadScriptEditor', () => {
  it('обидва чанки — одним спільним запитом; повтор не вантажить удруге', async () => {
    const { loadScriptEditorModules } = await import('../preloadScriptEditor');
    const first = loadScriptEditorModules();
    const second = loadScriptEditorModules();
    expect(second).toBe(first);
    const [expressions, monaco] = await first;
    expect(expressions.LightTheme).toBe('l');
    expect(monaco.editor).toEqual({});
    expect(loads).toEqual({ expressions: 1, api: 1 });
  });

  it('без requestIdleCallback прогрів нічого не робить; з ним — ставить і знімає завдання', async () => {
    const { preloadScriptEditorWhenIdle } = await import('../preloadScriptEditor');
    vi.stubGlobal('requestIdleCallback', undefined);
    expect(() => preloadScriptEditorWhenIdle()()).not.toThrow();

    const request = vi.fn(() => 7);
    const cancel = vi.fn();
    vi.stubGlobal('requestIdleCallback', request);
    vi.stubGlobal('cancelIdleCallback', cancel);
    const stop = preloadScriptEditorWhenIdle();
    expect(request).toHaveBeenCalledTimes(1);
    stop();
    expect(cancel).toHaveBeenCalledWith(7);
  });
});
