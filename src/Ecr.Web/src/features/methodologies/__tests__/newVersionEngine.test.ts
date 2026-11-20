import { describe, expect, it } from 'vitest';
import {
  DefaultNewVersionEngine,
  engineForNewVersion,
  opensScriptEditor,
  showEngineChoice,
} from '../newVersionEngine';

const versions = [{ id: 1, engine: 'Formulas' as const }, { id: 2, engine: 'Script' as const }, { id: 3 }];

describe('рушій нової версії методології', () => {
  it('порожня версія — скрипт за замовчуванням, вибір показується й діє', () => {
    expect(DefaultNewVersionEngine).toBe('Script');
    expect(showEngineChoice(null)).toBe(true);
    expect(engineForNewVersion({ copyFromVersionId: null, versions, choice: 'Script' })).toBe('Script');
    expect(engineForNewVersion({ copyFromVersionId: null, versions, choice: 'Formulas' })).toBe('Formulas');
  });

  it('копія бере рушій джерела, вибір прихований і ігнорується', () => {
    expect(showEngineChoice(2)).toBe(false);
    expect(engineForNewVersion({ copyFromVersionId: 2, versions, choice: 'Formulas' })).toBe('Script');
    expect(engineForNewVersion({ copyFromVersionId: 1, versions, choice: 'Script' })).toBe('Formulas');
  });

  it('джерело без поля engine (до MS) — формули; невідоме джерело — теж', () => {
    expect(engineForNewVersion({ copyFromVersionId: 3, versions, choice: 'Script' })).toBe('Formulas');
    expect(engineForNewVersion({ copyFromVersionId: 99, versions, choice: 'Script' })).toBe('Formulas');
  });

  it('«Відкрити» веде в редактор скрипта лише для версій-скриптів', () => {
    expect(opensScriptEditor({ engine: 'Script' })).toBe(true);
    expect(opensScriptEditor({ engine: 'Formulas' })).toBe(false);
    expect(opensScriptEditor({})).toBe(false);
    expect(opensScriptEditor(undefined)).toBe(false);
  });
});
