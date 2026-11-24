import type { MethodologyDraftVersionDto } from '@/api/types';

/**
 * Версії методології на екрані після виведення формульного рушія з інтерфейсу.
 *
 * ✎ 24.11, рішення людини: «має бути тільки наш новий редактор». Робочі версії — лише версії-скрипти: їх показує
 * таблиця версій, їх відкриває редактор скрипта, лише вони бувають джерелом копії. Версії формульного рушія лишаються
 * лише в базі (на них посилаються минулі прогони) — в інтерфейсі їх немає.
 *
 * ⛔ Нова версія — завжди `Script`, і клієнт шле рушій явно: пропущений рушій сервер колись читав як `Formulas`.
 */
export const NewVersionEngine = 'Script' as const;

type VersionFacts = Pick<MethodologyDraftVersionDto, 'id' | 'engine' | 'status' | 'effectiveFrom'>;

/** Версія-скрипт: «Відкрити» веде в редактор скрипта. */
export function opensScriptEditor(version: Pick<MethodologyDraftVersionDto, 'engine'> | undefined): boolean {
  return version?.engine === 'Script';
}

/** Робочі версії — лише скрипти, у тому самому порядку, що й у переліку сервера. */
export function scriptVersions<T extends VersionFacts>(versions: readonly T[]): T[] {
  return versions.filter((version) => opensScriptEditor(version));
}

/** Версії формульного рушія (поле `engine` відсутнє у версій до MS — це теж формули). */
export function formulaVersions<T extends VersionFacts>(versions: readonly T[]): T[] {
  return versions.filter((version) => !opensScriptEditor(version));
}

/** Сьогоднішня дата `YYYY-MM-DD` за місцевим часом — у тій самій формі, що `effectiveFrom` контракту. */
export function todayIso(now: Date = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');

  return `${String(now.getFullYear())}-${month}-${day}`;
}

/**
 * Поточна версія-скрипт: та, якою рахують сьогодні (опублікована з найпізнішою `effectiveFrom` ≤ сьогодні); якщо такої
 * немає — найновіша чернетка-скрипт; інакше — найновіша версія-скрипт. Формульна версія поточною не буває ніколи.
 */
export function currentScriptVersion<T extends VersionFacts>(versions: readonly T[], today: string): T | undefined {
  const scripts = scriptVersions(versions);
  const newest = (list: readonly T[]): T | undefined =>
    list.reduce<T | undefined>((best, item) => (best === undefined || item.id > best.id ? item : best), undefined);

  const effective = scripts
    .filter((item) => item.status === 'Published' && item.effectiveFrom !== null && item.effectiveFrom <= today)
    .reduce<T | undefined>(
      (best, item) =>
        best === undefined || (item.effectiveFrom ?? '') > (best.effectiveFrom ?? '') ? item : best,
      undefined,
    );

  return effective ?? newest(scripts.filter((item) => item.status === 'Draft')) ?? newest(scripts);
}

/**
 * Версія, режими якої показує форма: найновіша чернетка-скрипт (її режими можна правити), а без неї — поточна версія
 * (на читання). Формульна — ніколи.
 */
export function modesVersion<T extends VersionFacts>(versions: readonly T[], current: T | undefined): T | undefined {
  const draft = scriptVersions(versions)
    .filter((item) => item.status === 'Draft')
    .reduce<T | undefined>((best, item) => (best === undefined || item.id > best.id ? item : best), undefined);

  return draft ?? current;
}