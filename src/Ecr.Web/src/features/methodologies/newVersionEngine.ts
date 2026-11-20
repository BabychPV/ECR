import type { MethodologyDraftVersionDto } from '@/api/types';

/**
 * Вибір рушія в діалозі «Нова версія» (методології як скрипт).
 *
 * ⛔ Клієнт ЗАВЖДИ шле `engine` явно: пропущений рушій сервер читає як `Formulas`
 * (так поводяться версії до MS), тож «забути поле» мовчки створило б стару версію.
 */
export type VersionEngine = 'Formulas' | 'Script';

/** Нова порожня версія — скрипт. */
export const DefaultNewVersionEngine: VersionEngine = 'Script';

/** Вибір рушія показується лише для порожньої чернетки: клон успадковує рушій джерела. */
export function showEngineChoice(copyFromVersionId: number | null): boolean {
  return copyFromVersionId === null;
}

/** Рушій, який піде в запит створення версії. */
export function engineForNewVersion(args: {
  readonly copyFromVersionId: number | null;
  readonly versions: readonly Pick<MethodologyDraftVersionDto, 'id' | 'engine'>[];
  readonly choice: VersionEngine;
}): VersionEngine {
  if (args.copyFromVersionId === null) return args.choice;

  const source = args.versions.find((version) => version.id === args.copyFromVersionId);
  // Версія без поля `engine` — з часів до MS.
  return source?.engine ?? 'Formulas';
}

/** «Відкрити» веде на сторінку скрипта, а не обирає версію на цьому екрані. */
export function opensScriptEditor(version: Pick<MethodologyDraftVersionDto, 'engine'> | undefined): boolean {
  return version?.engine === 'Script';
}
