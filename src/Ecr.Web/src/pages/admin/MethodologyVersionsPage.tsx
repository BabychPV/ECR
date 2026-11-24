import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
import { Alert, Button, Group, Select, Skeleton, Stack, Table, Text, TextInput } from '@mantine/core';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type { CalculationLevel, MethodologyDraftVersionDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { createMethodologyVersion, methodologyVersions } from '@/features/methodologies/api';
import { mayEditContent } from '@/features/methodologies/draft';
import { useDeleteVersionAction } from '@/features/methodologies/DeleteVersionAction';
import { VersionDiffModal } from '@/features/methodologies/VersionDiffModal';
import { usePublishVersion } from '@/features/methodologies/PublishVersionFlow';
import {
  currentScriptVersion,
  modesVersion,
  NewVersionEngine,
  scriptVersions,
  todayIso,
} from '@/features/methodologies/newVersionEngine';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import { usePendingLoading } from '@/features/common/usePendingLoading';

/*
 * ⛔ Панелі методології — за `import()` (бюджет маршруту `D-132`): режими, прив'язки, журнал публікацій і прапор
 * трасування вантажаться паралельно із запитом версій. Усі з `MethodologyContentPanels` резолвляться в ОДИН чанк.
 */
const MethodologyModesForm = lazy(async () => ({
  default: (await import('@/features/methodologies/MethodologyContentPanels')).MethodologyModesForm,
}));

const MethodologyBindingsPanel = lazy(async () => ({
  default: (await import('@/features/methodologies/MethodologyContentPanels')).MethodologyBindingsPanel,
}));

/** Журнал публікацій (F-16) — власний чанк. */
const MethodologyPublicationsPanel = lazy(async () => ({
  default: (await import('@/features/methodologies/MethodologyPublicationsPanel')).MethodologyPublicationsPanel,
}));

/** Прапор «трасувати проміжні кроки» (`D-565`) — власний чанк. */
const MethodologyTraceStepsPanel = lazy(async () => ({
  default: (await import('@/features/methodologies/TraceStepsPanel')).MethodologyTraceStepsPanel,
}));

/** Адреса редактора скрипта версії. */
function scriptHref(methodologyId: number, versionId: number): string {
  return `/admin/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script`;
}

/**
 * Методологія: версії-скрипти й методологічні налаштування (`METHODOLOGY-SCRIPT-PLAN.md`).
 *
 * ✎ 24.11, рішення людини: «загалом мене цікавить реальний перехід а не сідити на 2 стульях має бути тільки наш новий
 * редакор». Екран більше не конфігуратор формул: робочі версії — лише скрипти, їх правлять у редакторі скрипта
 * (`/versions/:vid/script` — там же тести, обов'язкові колонки, входи й виходи). Тут лишилося те, що належить
 * МЕТОДОЛОГІЇ, а не тексту скрипта: перелік версій із публікацією й видаленням чернетки, режими поточної версії
 * (скрипт їх читає — `NumericPolicy`, `CalendarContext`), прив'язки виходів до колонок, трасування й журнал публікацій.
 *
 * ⚠ Версії формульного рушія лишаються в базі (на них посилаються минулі прогони), але в інтерфейсі їх немає зовсім
 * (людина 24.11: жодного перегляду на читання). Методологія, у якої поки лише формульні версії (до вікна перенесення
 * P-1124-2), показує пояснення «переноситься» — без редактора.
 */
export function MethodologyVersionsPage(): JSX.Element {
  const params = useParams();
  const navigate = useNavigate();
  const methodologyId = Number(params['id']);
  const known = Number.isFinite(methodologyId);

  const queryClient = useQueryClient();
  const session = useSession();
  const mayEdit = can(session.data, 'Calculation.EditFormula');

  // ⛔ Право небезпечне (`sec.Permission.IsDangerous`): публікація змінює числа, які вже подані регуляторові.
  const mayPublish = can(session.data, 'Calculation.Publish');

  const [creating, setCreating] = useState(false);
  const [newVersion, setNewVersion] = useState('');
  const [copyFrom, setCopyFrom] = useState<string | null>(null);

  // ⛔ Рівень драбини виразності (`ФВ-9.2`) — вибір лише для ПОРОЖНЬОЇ чернетки: клон бере його з джерела.
  const [level, setLevel] = useState<CalculationLevel>('Configuration');

  // ⛔ Публікація — спільний потік зі сторінкою скрипта (`PublishVersionFlow`): причина й дата обов'язкові.
  const publishFlow = usePublishVersion(methodologyId);

  const versions = useQuery({
    queryKey: queryKeys.methodologies.versionsOf(methodologyId),
    queryFn: () => methodologyVersions(methodologyId),
    enabled: known,
  });

  const all = useMemo(() => versions.data ?? [], [versions.data]);
  const scripts = useMemo(() => scriptVersions(all), [all]);
  const current = useMemo(() => currentScriptVersion(all, todayIso()), [all]);
  // ⚠ Режими править чернетка: є чернетка-скрипт — форма її (редагована), інакше — поточної (на читання).
  const modesTarget = useMemo(() => modesVersion(all, current), [all, current]);

  const versionDeletion = useDeleteVersionAction({ methodologyId, allowed: mayEdit });
  const [compareTarget, setCompareTarget] = useState<MethodologyDraftVersionDto | null>(null);

  const create = useMutation({
    mutationFn: () =>
      createMethodologyVersion(methodologyId, {
        versionNumber: newVersion,
        copyFromVersionId: copyFrom === null ? null : Number(copyFrom),

        // ⛔ Рушій — завжди явно і лише скрипт: пропущений сервер колись читав як `Formulas`.
        engine: NewVersionEngine,
        level,
      }),
    onSuccess: async (draft) => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      setCreating(false);
      setNewVersion('');
      setCopyFrom(null);
      showDone(t('methodologies.versionCreated'));

      // Версію-скрипт править лише редактор скрипта: ведемо туди одразу.
      void navigate(scriptHref(methodologyId, draft.id));
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const createLoading = usePendingLoading(create.isPending);

  return (
    <Stack gap="md">
      <PageHeader
        title={t('methodologies.versionsTitle')}
        actions={
          mayEdit && (
            <Button variant="default" onClick={() => setCreating(true)}>
              {t('methodologies.newVersion')}
            </Button>
          )
        }
      />

      {versionDeletion.refusal}

      <ErrorAlert error={versions.error} onRetry={() => void versions.refetch()} />

      {/* ⛔ Поточна версія-скрипт — головне на екрані: саме нею рахують сьогодні, і саме з неї людина йде в редактор.
          Формульна V1 поточною не буває ніколи, навіть коли вона перша в переліку сервера. */}
      {known && versions.data !== undefined && current !== undefined && (
        <Alert
          color="statusInfo"
          title={t('methodologies.currentScript.title')}
          data-testid="current-script-version"
        >
          <Stack gap="xs">
            <Group justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                <Text fw={600}>{current.versionNumber}</Text>
                <StatusBadge kind="version" state={current.status} />
                <Text size="sm" c="dimmed">
                  {t('methodologies.effectiveFrom')}: <Timestamp value={current.effectiveFrom} dateOnly />
                </Text>
              </Group>
              <Button component={Link} to={scriptHref(methodologyId, current.id)} size="xs">
                {t('methodologyScript.versions.openEditor')}
              </Button>
            </Group>
            <Text size="sm">{t('methodologies.currentScript.hint')}</Text>
          </Stack>
        </Alert>
      )}

      {/* ⛔ Лише формульні версії (до вікна перенесення P-1124-2): редактора для них немає — пояснення, а не порожнеча. */}
      {known && versions.data !== undefined && current === undefined && all.length > 0 && (
        <Alert color="statusWarning" title={t('methodologies.converting.title')} data-testid="methodology-converting">
          {t('methodologies.converting.hint')}
        </Alert>
      )}

      {known && versions.data !== undefined && all.length === 0 && (
        <Alert color="gray" title={t('methodologies.noVersions')} data-testid="no-versions">
          {t('methodologies.noVersionsHint')}
        </Alert>
      )}

      {versions.isPending && known && <Skeleton height={120} data-testid="versions-loading" />}

      {scripts.length > 0 && (
        <Table striped className="ecr-sticky-head" data-testid="script-versions">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('methodologies.version')}</Table.Th>
              <Table.Th>{t('methodologies.status')}</Table.Th>
              <Table.Th>{t('methodologies.modes')}</Table.Th>
              <Table.Th>{t('methodologies.effectiveFrom')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {scripts.map((version) => (
              <Table.Tr key={version.id}>
                <Table.Td>{version.versionNumber}</Table.Td>
                <Table.Td>
                  <StatusBadge kind="version" state={version.status} />
                </Table.Td>
                <Table.Td>
                  {/* ⚠ Режими поруч зі статусом: вони визначають числа (`ФВ-9.9`, `ФВ-16.11`), і клон їх переносить. */}
                  <Text size="sm" c="dimmed">
                    {version.numericMode} · {version.calendarMode} · {version.traceLevel}
                  </Text>
                </Table.Td>
                {/* ⚠ `dateOnly`: дата набуття чинності порівнюється з календарем періодів, а не з годинником. */}
                <Table.Td>
                  <Timestamp value={version.effectiveFrom} dateOnly />
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <Button
                      component={Link}
                      to={scriptHref(methodologyId, version.id)}
                      size="xs"
                      variant={current?.id === version.id ? 'filled' : 'subtle'}
                    >
                      {t('methodologies.openVersion')}
                    </Button>

                    {version.status !== 'Published' && mayPublish && (
                      <Button size="xs" variant="default" onClick={() => publishFlow.open(version.id)}>
                        {t('methodologies.publish')}
                      </Button>
                    )}

                    {/* ⚠ Лише коли є з чим порівнювати: з однією версією діалог відкривався б без базової. */}
                    {scripts.length > 1 && (
                      <Button size="xs" variant="subtle" onClick={() => setCompareTarget(version)}>
                        {t('methodologies.compare')}
                      </Button>
                    )}

                    {versionDeletion.triggerFor(version)}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {/* ⚠ Режими — поточної версії-скрипта: скрипт їх читає. Правити — лише чернетку (сервер тримає те саме). */}
      {modesTarget !== undefined && (
        <Suspense fallback={null}>
          <MethodologyModesForm
            methodologyId={methodologyId}
            version={modesTarget}
            editable={mayEditContent(modesTarget, mayEdit)}
          />
        </Suspense>
      )}

      {/* ⚠ Належать МЕТОДОЛОГІЇ, не версії: переживають усі версії й клонуванням не копіюються. */}
      {known && (
        <Suspense fallback={null}>
          <MethodologyBindingsPanel
            methodologyId={methodologyId}
            editable={can(session.data, 'Calculation.EditRule')}
          />
          <MethodologyTraceStepsPanel methodologyId={methodologyId} editable={mayEdit} />
          <MethodologyPublicationsPanel methodologyId={methodologyId} />
        </Suspense>
      )}

      <ResizableModal
        sizeKey="methodology.newVersion"
        opened={creating}
        onClose={() => setCreating(false)}
        title={t('methodologies.newVersionTitle')}
      >
        <Stack gap="sm">
          {/* ⛔ Клон — єдиний спосіб змінити опубліковану версію (`ФВ-9.1`). */}
          <Alert color="statusWarning">{t('methodologies.cloneHint')}</Alert>

          <TextInput
            label={t('methodologies.versionNumber')}
            description={t('methodologies.versionNumberHint')}
            value={newVersion}
            onChange={(event) => setNewVersion(event.currentTarget.value)}
            data-autofocus
          />

          {/* ⛔ Відмова `GET …/versions` не має читатися як «копіювати нема з чого» (`D15-00`, L10).
              ⚠ Джерело копії — лише версія-скрипт: клон бере рушій джерела, а формульний рушій виведено. */}
          {versions.error !== null ? (
            <ErrorAlert error={versions.error} onRetry={() => void versions.refetch()} />
          ) : (
            <Select
              label={t('methodologies.copyFrom')}
              description={t('methodologies.copyFromHint')}
              placeholder={t('methodologies.emptyDraft')}
              clearable
              disabled={versions.isPending}
              value={copyFrom}
              data={scripts.map((version) => ({
                value: String(version.id),
                label: `${version.versionNumber} · ${t(statusKey('version', version.status))}`,
              }))}
              onChange={setCopyFrom}
            />
          )}

          {/* ⛔ FP-3: коли версії-скрипти вже є, а джерело не обрано, — пряме попередження про порожню чернетку. */}
          {copyFrom === null && versions.error === null && !versions.isPending && scripts.length > 0 && (
            <Alert color="statusWarning" data-testid="empty-draft-warning">
              {t('methodologies.emptyDraftWarning')}
            </Alert>
          )}

          {/* ⛔ Лише для ПОРОЖНЬОЇ чернетки: клон бере рівень із джерела. */}
          {copyFrom === null && (
            <Select
              label={t('methodologies.level')}
              description={t('methodologies.levelHint')}
              allowDeselect={false}
              value={level}
              data={[
                { value: 'Configuration', label: 'Configuration' },
                { value: 'Module', label: 'Module' },
              ]}
              onChange={(value) => setLevel(value === 'Module' ? 'Module' : 'Configuration')}
            />
          )}

          <Button
            disabled={newVersion.trim().length === 0}
            loading={createLoading}
            onClick={() => {
              // ⛔ B149 (KIT §6.9): кнопка закритого `Modal` (~200 мс у DOM) заводила версію вже після відмови.
              if (!creating || create.isPending) return;
              create.mutate();
            }}
          >
            {t('methodologies.createVersion')}
          </Button>
        </Stack>
      </ResizableModal>

      {publishFlow.dialogs}

      {versionDeletion.dialog}

      <VersionDiffModal
        methodologyId={methodologyId}
        versions={scripts}
        target={compareTarget}
        onClose={() => setCompareTarget(null)}
      />
    </Stack>
  );
}
