import { t } from '@/shared/i18n';
import { localizedScriptMessage } from './scriptMarkers';
import type { ScriptDiagnosticDto } from './types';

/**
 * Рядок стану під редактором скрипта.
 *
 * ✎ 24.11 (UI-прохід, знімок людини): нова чернетка з заготовки показує одну проблему — «немає жодного `rows from`»
 * (заготовка навмисно закоментована: вона нічого не рахує). Голе «1 problem» з червоним лічильником читалося як
 * зламана заготовка; тепер ОДНА проблема називається прямо в рядку стану — це й підказка, з чого почати.
 */
export function scriptStatusText(args: {
  readonly pausedSeconds: number | null;
  readonly checking: boolean;
  readonly diagnostics: readonly ScriptDiagnosticDto[];
}): string {
  if (args.pausedSeconds !== null) return t('methodologyScript.checksPaused', { seconds: args.pausedSeconds });
  if (args.checking) return t('methodologyScript.checking');

  const [first] = args.diagnostics;
  if (first === undefined) return t('methodologyScript.problems.none');
  if (args.diagnostics.length === 1) {
    return t('methodologyScript.problemSingle', { message: localizedScriptMessage(first) });
  }

  return t('methodologyScript.problemCount', { count: args.diagnostics.length });
}
