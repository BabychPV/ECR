import { act } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadCatalog } from '@/shared/i18n';
import { localizedScriptMessage, scriptMarkers } from '../scriptMarkers';
import type { ScriptDiagnosticDto } from '../types';

const diagnostic = (patch: Partial<ScriptDiagnosticDto> = {}): ScriptDiagnosticDto => ({
  code: 'script.unknownName',
  messageKey: null,
  params: null,
  line: 3,
  column: 5,
  length: 4,
  message: 'Unknown name "abc".',
  ...patch,
});

describe('маркери скрипта', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('координати 1-базові і переносяться без зсуву', () => {
    const [marker] = scriptMarkers([diagnostic()]);
    expect(marker).toMatchObject({
      startLineNumber: 3,
      startColumn: 5,
      endLineNumber: 3,
      endColumn: 9,
      code: 'script.unknownName',
    });
  });

  it('нульова довжина дає підкреслення в один символ', () => {
    const [marker] = scriptMarkers([diagnostic({ length: 0 })]);
    expect(marker?.endColumn).toBe(6);
  });

  it('некоректні рядок/колонка підтягуються до одиниці', () => {
    const [marker] = scriptMarkers([diagnostic({ line: 0, column: 0, length: 1 })]);
    expect(marker?.startLineNumber).toBe(1);
    expect(marker?.startColumn).toBe(1);
  });

  it('без messageKey — запасний message сервера', () => {
    expect(localizedScriptMessage(diagnostic())).toBe('Unknown name "abc".');
  });

  it('ключа немає в каталозі — запасний message, не ⟦ключ⟧', () => {
    const text = localizedScriptMessage(diagnostic({ messageKey: 'script.noSuchKeyInCatalog' }));
    expect(text).toBe('Unknown name "abc".');
  });

  it('ключ є в каталозі — локалізований текст із параметрами', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              languageCode: 'en',
              revision: 1,
              strings: { 'script.unknownName': 'Name "{name}" is not declared.' },
            }),
            {
              status: 200,
              headers: { 'Content-Type': 'application/json', ETag: '"private-en-1"' },
            },
          ),
        ),
      ),
    );
    await act(async () => {
      await loadCatalog('en', 'private');
    });

    const [marker] = scriptMarkers([
      diagnostic({ messageKey: 'script.unknownName', params: { name: 'abc' } }),
    ]);
    expect(marker?.message).toBe('Name "abc" is not declared.');
  });
});
