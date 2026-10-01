import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Храповик миттєвого спінера (`ФВ-14.26`).
 *
 * ⛔ Предмет. `loading={mutation.isPending}` вмикає спінер із першого кадру:
 * дія на 80 мс блимає ним, хоча `ФВ-14.26` каже «`< 100 мс` — нічого».
 * Правильна форма — `loading={usePendingLoading(x.isPending)}`
 * (`features/common/usePendingLoading.ts`, пороги — `useDurationIndicator`).
 *
 * ⚠ Перелік файлів із числом, а не заборона: місць було ~100. Переведено
 * адміністративні екрани поза графом бюджетних сторінок (методології, реєстри,
 * одиниці, канали сповіщень, задачі, знімки, імпорт) і повтор збереження /
 * новий рядок сітки. Перевірка В ОБИДВА БОКИ: переведене місце вимагає
 * зменшити число, інакше перелік перестає бути заміром.
 *
 * ⚠ `SheetActions.tsx`, `DocumentPage.tsx`, `PeriodsPage.tsx` — у переліку
 * СВІДОМО, хоча це щоденні шляхи: вони в графі сторінок із бюджетом `D-132`
 * (≈235/234 КБ із 250), і переведення додавало ~0.2 КБ gzip кожній; те саме
 * для `PeriodPolicyManager`, `RecalculationApprovals` (статичні в `PeriodsPage`)
 * і `VersionMigrationDialog` (статичний у `DocumentPage`). Правило
 * лейна — бюджет не збільшувати. Переводити разом із розвантаженням графа.
 *
 * ⚠ `shared/ui/ConfirmModal.tsx` і `ReasonModal.tsx` — у переліку свідомо:
 * `shared/ui` цей лейн не чіпає (правило зон), а їхній `isPending` приходить
 * від споживача.
 */

/** Замір: файл (від `src/`) → скільки `loading={…isPending…}` дозволено. */
const Ledger: Readonly<Record<string, number>> = {
  'features/documents/BusinessKeyChangeAction.tsx': 1,
  'features/documents/CreateDocumentModal.tsx': 1,
  'features/documents/DocumentHeaderPanel.tsx': 1,
  'features/documents/VersionMigrationDialog.tsx': 2,
  'features/expressions/TestCaseRunner.tsx': 1,
  'features/integration/AddSourceEntityModal.tsx': 1,
  'features/integration/DataSourceFormModal.tsx': 1,
  'features/integration/TestDataSourceModal.tsx': 1,
  'features/jobs/JobFacts.tsx': 1,
  'features/mapping/CreateMappingModal.tsx': 1,
  'features/mapping/PauseResumeAction.tsx': 1,
  'features/mapping/PiAfProbeAction.tsx': 1,
  'features/mapping/UnitChangeAction.tsx': 1,
  'features/notifications/RulesMatrixPanel.tsx': 1,
  'features/projects/ApprovalRouteEditor.tsx': 1,
  'features/projects/CreateProjectModal.tsx': 1,
  'features/projects/PeriodPolicyManager.tsx': 2,
  'features/projects/RecalculationApprovals.tsx': 2,
  'features/registries/CreateRegistryModal.tsx': 1,
  'features/registries/RegistryExternalKeysPanel.tsx': 1,
  'features/registries/SourceKindSwitch.tsx': 1,
  'features/registries/impact/RegistryImpactPage.tsx': 1,
  'features/security/CreateRoleModal.tsx': 1,
  'features/security/CreateUserModal.tsx': 1,
  'features/security/GroupAssignmentsPanel.tsx': 3,
  'features/security/RoleActions.tsx': 2,
  'features/security/SimulationPanel.tsx': 1,
  'features/security/UserAccessEditor.tsx': 1,
  'features/security/UserAdminActions.tsx': 1,
  'features/sources/RegistrySyncPolicyModal.tsx': 1,
  'features/sources/RowWindowMapModal.tsx': 1,
  'features/sources/RowWindowMapsPanel.tsx': 1,
  'features/sources/SourceEventMapModal.tsx': 1,
  'features/sources/SourceEventMapsPanel.tsx': 1,
  'features/sources/SourceEventProbePanel.tsx': 1,
  'features/sources/SourceEventsTable.tsx': 1,
  'features/templates/ConditionalFormatPanel.tsx': 1,
  'features/templates/NewTemplateVersionModal.tsx': 1,
  'features/templates/PresentationEditor.tsx': 1,
  'features/units/UnitEditModal.tsx': 1,
  'features/workflow/SheetActions.tsx': 3,
  'features/workflow/WorkflowHistory.tsx': 1,
  'pages/DocumentPage.tsx': 1,
  'pages/admin/GrantsPanel.tsx': 1,
  'pages/admin/PeriodsPage.tsx': 7,
  'pages/admin/SourcesPage.tsx': 1,
  'pages/admin/TableRelationsPage.tsx': 1,
  'pages/admin/TemplateCardPage.tsx': 1,
  'pages/admin/TemplateVersionPage.tsx': 3,
  'pages/admin/TemplatesPage.tsx': 1,
  'shared/ui/ConfirmModal.tsx': 1,
  'shared/ui/ReasonModal.tsx': 1,
};

const Root = path.resolve(process.cwd(), 'src');

const Site = /loading=\{[^}]*\bisPending\b[^}]*\}/g;

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);

    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.(ts|tsx)$/.test(name) && !/\.(test|spec)\.tsx?$/.test(name) ? [full] : [];
  });
}

/** Код без коментарів: згадка в поясненні — не місце. */
function codeOf(file: string): string {
  return readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

describe('ФВ-14.26: миттєвий спінер не стає частішим', () => {
  it('замір збігається з переліком в обидва боки', () => {
    const actual = new Map<string, number>();

    for (const file of sources(Root)) {
      const count = codeOf(file).match(Site)?.length ?? 0;

      if (count > 0) actual.set(path.relative(Root, file).split(path.sep).join('/'), count);
    }

    const failures: string[] = [];

    for (const [file, count] of [...actual].sort()) {
      const allowed = Ledger[file] ?? 0;

      if (count > allowed) {
        failures.push(
          `${file}: loading={…isPending} — ${count}, дозволено ${allowed}. ` +
            'Візьми `const xLoading = usePendingLoading(x.isPending)` (features/common/usePendingLoading.ts).',
        );
      } else if (count < allowed) {
        failures.push(`${file}: лишилось ${count}, а перелік обіцяє ${allowed} — зменш число в Ledger.`);
      }
    }

    for (const [file, allowed] of Object.entries(Ledger)) {
      if (!actual.has(file)) failures.push(`${file}: у переліку ${allowed}, а місць немає — прибери рядок.`);
    }

    expect(failures.join('\n')).toBe('');
  });

  it('сито бачить обидві форми і не бачить правильну', () => {
    const sample = [
      '<Button loading={submit.isPending}>',
      '<Button loading={recalculate.isPending || recalcRunning}>',
      '<Button loading={submitLoading}>',
    ].join('\n');

    expect(sample.match(Site)).toHaveLength(2);
  });
});
