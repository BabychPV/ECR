import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  canSaveScript,
  EmptyScriptEditorState,
  isScriptChangedError,
  isScriptDirty,
  replaceTextRange,
  saveScriptBody,
  scriptEditorReducer,
  ScriptChangedMessageKey,
  type ScriptEditorState,
} from '../scriptEditorState';

const loaded = (text: string, hash = 'h1'): ScriptEditorState =>
  scriptEditorReducer(EmptyScriptEditorState, { type: 'loaded', text, hash });

function problem(errorCode: string, messageKey?: string): EcrApiError {
  return new EcrApiError({
    title: 'Conflict',
    status: 409,
    errorCode,
    correlationId: 'c',
    ...(messageKey === undefined ? {} : { extensions2: { messageKey } }),
  });
}

describe('стан тексту редактора', () => {
  it('після завантаження чистий; правка робить його брудним, повернення тексту — знову чистим', () => {
    let state = loaded('a');
    expect(isScriptDirty(state)).toBe(false);

    state = scriptEditorReducer(state, { type: 'edited', text: 'ab' });
    expect(isScriptDirty(state)).toBe(true);

    state = scriptEditorReducer(state, { type: 'edited', text: 'a' });
    expect(isScriptDirty(state)).toBe(false);
  });

  it('збереження оновлює хеш; текст, набраний під час запиту, лишається незбереженим', () => {
    let state = loaded('a');
    state = scriptEditorReducer(state, { type: 'edited', text: 'ab' });
    state = scriptEditorReducer(state, { type: 'edited', text: 'abc' });

    state = scriptEditorReducer(state, { type: 'saved', submitted: 'ab', hash: 'h2' });

    expect(state).toMatchObject({ text: 'abc', savedText: 'ab', hash: 'h2', conflict: false });
    expect(isScriptDirty(state)).toBe(true);
  });

  it('конфлікт блокує «Зберегти», а «перезавантажити» (loaded) його знімає й скидає текст', () => {
    let state = scriptEditorReducer(loaded('a'), { type: 'edited', text: 'mine' });
    state = scriptEditorReducer(state, { type: 'conflict' });

    expect(state.conflict).toBe(true);
    expect(canSaveScript(state, false)).toBe(false);

    state = scriptEditorReducer(state, { type: 'loaded', text: 'theirs', hash: 'h9' });
    expect(state).toEqual({ text: 'theirs', savedText: 'theirs', hash: 'h9', conflict: false });
  });

  it('«Зберегти» доступне лише з правками, не в режимі читання і з непорожнім текстом', () => {
    const clean = loaded('a');
    const dirty = scriptEditorReducer(clean, { type: 'edited', text: 'b' });
    const blank = scriptEditorReducer(clean, { type: 'edited', text: '   ' });

    expect(canSaveScript(clean, false)).toBe(false);
    expect(canSaveScript(dirty, false)).toBe(true);
    expect(canSaveScript(dirty, true)).toBe(false);
    expect(canSaveScript(blank, false)).toBe(false);
  });

  it('тіло збереження несе expectedHash; порожній хеш (тексту ще не було) — не шле', () => {
    expect(saveScriptBody(loaded('a', 'abc'))).toEqual({ text: 'a', expectedHash: 'abc' });
    expect(saveScriptBody(loaded('a', ''))).toEqual({ text: 'a' });
  });

  it('scriptChanged розпізнається за кодом і ключем, а не за будь-яким 409', () => {
    expect(isScriptChangedError(problem('ECR-CALC-0409', ScriptChangedMessageKey))).toBe(true);
    expect(isScriptChangedError(problem('ECR-CALC-0409', 'err.ECR-CALC-0409.scriptNotEditable'))).toBe(false);
    expect(isScriptChangedError(problem('ECR-CALC-0409'))).toBe(false);
    expect(isScriptChangedError(problem('ECR-CELL-0409', ScriptChangedMessageKey))).toBe(false);
    expect(isScriptChangedError(new Error('x'))).toBe(false);
  });

  it('replaceTextRange замінює літерал і не чіпає решту тексту', () => {
    const text = 'const A = { a: 1 };\nvar x = 1;';
    expect(replaceTextRange(text, 10, 18, '{ a: 2 }')).toBe('const A = { a: 2 };\nvar x = 1;');
  });
});
