import { describe, expect, it } from 'vitest';
import {
  NewVersionEngine,
  currentScriptVersion,
  modesVersion,
  opensScriptEditor,
  scriptVersions,
  todayIso,
} from '../newVersionEngine';

/**
 * ✎ 24.11 «має бути тільки наш новий редактор»: робочі версії — лише скрипти; формульних в інтерфейсі немає.
 *
 * Набір як на live (ECW_C05_01): V1 формули з 2024-01-01, V2 скрипт з 2024-01-02, чернетка формул «test».
 */
type V = {
  id: number;
  engine?: 'Formulas' | 'Script';
  status: 'Draft' | 'Published' | 'Deprecated';
  effectiveFrom: string | null;
};

const v1: V = { id: 2, engine: 'Formulas', status: 'Published', effectiveFrom: '2024-01-01' };
const test: V = { id: 58, engine: 'Formulas', status: 'Draft', effectiveFrom: null };
const v2: V = { id: 70, engine: 'Script', status: 'Published', effectiveFrom: '2024-01-02' };

describe('newVersionEngine', () => {
  it('нова версія — лише скрипт', () => {
    expect(NewVersionEngine).toBe('Script');
    expect(opensScriptEditor(v2)).toBe(true);
    expect(opensScriptEditor(v1)).toBe(false);
    // Версія без поля `engine` — з часів до MS, тобто формули.
    expect(opensScriptEditor({})).toBe(false);
  });

  it('робочі версії — лише скрипти', () => {
    expect(scriptVersions([v1, test, v2]).map((item) => item.id)).toEqual([70]);
  });

  it('поточна — чинний скрипт, а не перша в переліку формульна V1', () => {
    expect(currentScriptVersion([v1, test, v2], '2026-11-24')?.id).toBe(70);
  });

  it('скрипт із датою в майбутньому не поточний, доки є чинний; чернетка-скрипт — лише без чинного', () => {
    const v3: V = { id: 80, engine: 'Script', status: 'Published', effectiveFrom: '2027-01-01' };
    const draft: V = { id: 90, engine: 'Script', status: 'Draft', effectiveFrom: null };
    expect(currentScriptVersion([v1, v2, v3, draft], '2026-11-24')?.id).toBe(70);
    expect(currentScriptVersion([v1, draft], '2026-11-24')?.id).toBe(90);
    expect(currentScriptVersion([v1, v3], '2026-11-24')?.id).toBe(80);
  });

  it('методологія без скрипта (вода) поточної версії не має', () => {
    expect(currentScriptVersion([v1], '2026-11-24')).toBeUndefined();
  });

  it('режими — найновішої чернетки-скрипта, без неї — поточної; формульна чернетка не береться', () => {
    const d1: V = { id: 90, engine: 'Script', status: 'Draft', effectiveFrom: null };
    const d2: V = { id: 91, engine: 'Script', status: 'Draft', effectiveFrom: null };
    expect(modesVersion([v1, test, v2, d1, d2], v2)?.id).toBe(91);
    expect(modesVersion([v1, test, v2], v2)?.id).toBe(70);
    expect(modesVersion([v1, test], undefined)).toBeUndefined();
  });
  it('todayIso — у формі effectiveFrom', () => {
    expect(todayIso(new Date(2026, 10, 4))).toBe('2026-11-04');
  });
});