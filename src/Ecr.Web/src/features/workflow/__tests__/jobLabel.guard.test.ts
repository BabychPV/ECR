import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { jobKindLabel } from '../jobLabel';

/**
 * Сторож повноти CL-25: кожен тип задачі, який сервер ставить у чергу чи планує
 * (`ScheduleAsync<T>`, `Enqueue…Async<T>`), має ключ у `KindKeys` (`jobLabel.ts`).
 * Без нього `/admin/jobs` показує запасну англійську назву замість ru/kz.
 *
 * Ключ каталогу в тестах не завантажується, тож `t()` повертає `⟦ключ⟧`; запасна
 * назва (`readableTypeName`) такого маркера не має.
 */
const SrcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', '..');

function csFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'node_modules') continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) csFiles(full, out);
    else if (entry.name.endsWith('.cs')) out.push(full);
  }

  return out;
}

function serverJobTypes(): Map<string, string> {
  const found = new Map<string, string>();
  const pattern = /\b(?:ScheduleAsync|Enqueue\w*Async)\s*<\s*([\w.]+)\s*>/g;
  const dirs = readdirSync(SrcRoot, { withFileTypes: true }).filter(
    (d) => d.isDirectory() && d.name.startsWith('Ecr.') && d.name !== 'Ecr.Web',
  );
  for (const d of dirs) {
    for (const file of csFiles(join(SrcRoot, d.name))) {
      const text = readFileSync(file, 'utf8');
      for (const m of text.matchAll(pattern)) {
        const name = m[1]!.slice(m[1]!.lastIndexOf('.') + 1);
        // Узагальнений параметр самого планувальника (`TJob`) — не тип задачі.
        if (name === 'TJob') continue;
        if (!found.has(name)) found.set(name, file);
      }
    }
  }

  return found;
}

describe('KindKeys повні відносно серверних постановок задач', () => {
  const types = serverJobTypes();

  it('сторож бачить відомі типи (інакше regex зламано)', () => {
    expect(types.has('OrphanScanJob')).toBe(true);
    expect(types.has('IRecalculationJob')).toBe(true);
    expect(types.size).toBeGreaterThan(15);
  });

  it('кожен тип задачі має ключ у KindKeys', () => {
    const missing = [...types.keys()].filter((n) => !jobKindLabel(n).startsWith('⟦jobs.kind.'));
    expect(missing).toEqual([]);
  });
});
