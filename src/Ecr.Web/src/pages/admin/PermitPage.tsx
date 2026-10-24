import { lazy, Suspense, useEffect, type JSX } from 'react';
import { Badge, Button, Menu, Skeleton, Stack, Tabs } from '@mantine/core';
import { useParams, useSearchParams } from 'react-router-dom';
import { NotFoundPage } from '@/app/NotFoundPage';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { Permit } from '@/features/permits/api/types';
import { DialogParam, readLifecycleDialog } from '@/features/permits/permit/lifecycleDialogIds';
import { localName } from '@/features/permits/permit/names';
import { OverviewTab } from '@/features/permits/permit/OverviewTab';
import { useMayEditPermits } from '@/features/permits/permit/permitDataRights';
import { capabilities, PermitStatus, type PermitCapabilities } from '@/features/permits/permit/permitRules';
import { usePermit, usePermitTypeDefinition, useUnitLabels } from '@/features/permits/permit/permitQueries';
import { PanelParam } from '@/features/permits/permit/tableParams';
import { PermitEditorHead, PermitStateStepper } from '@/features/permits/shell/PermitEditorHead';
import { PermitsPaths } from '@/features/permits/shell/permitPaths';
import { PermitStateBadge } from '@/features/permits/ui/PermitStateBadge';

// Діалоги життєвого циклу — окремий чанк: на першому показі `?dialog=` порожній (бюджет маршруту, D-132).
const LifecycleDialogs = lazy(() =>
  import('@/features/permits/permit/LifecycleDialogs').then((module) => ({ default: module.LifecycleDialogs })),
);

/*
 * Вкладка таблиці — окремим чанком (бюджет маршруту, D-132): широка таблиця, модель рядка й розбір вставки
 * (`rc812/rowModel`) потрібні лише на вкладці таблиці, а відкривається дозвіл на Overview. Поки чанк їде, на місці
 * вкладки той самий скелет, що й поки читається визначення типу; шапка з діями — поза вкладкою і не зсувається.
 * Чанк підтягується одразу після показу дозволу з таблицями, тож перемикання вкладки його зазвичай не чекає.
 */
const loadTableTab = () => import('@/features/permits/permit/TableTab');
const TableTab = lazy(() => loadTableTab().then((module) => ({ default: module.TableTab })));

const OverviewTabId = 'overview';
const TabParam = 'tab';

function Actions({ caps, open }: { readonly caps: PermitCapabilities; readonly open: (id: string) => void }): JSX.Element | null {
  if (!caps.renew && !caps.activate && !caps.end && !caps.remove) return null;

  return (
    <>
      {caps.renew && (
        <Button variant="default" onClick={() => open('renew')}>
          {t('permits.page.actions.renew')}
        </Button>
      )}
      {caps.activate && <Button onClick={() => open('activate')}>{t('permits.page.actions.activate')}</Button>}
      {(caps.end || caps.remove) && (
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <Button variant="default" data-permit-more>
              {t('permits.page.actions.more')}
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {caps.end && <Menu.Item onClick={() => open('end')}>{t('permits.page.actions.end')}</Menu.Item>}
            {caps.remove && <Menu.Item onClick={() => open('delete')}>{t('permits.page.actions.delete')}</Menu.Item>}
          </Menu.Dropdown>
        </Menu>
      )}
    </>
  );
}

function allowedDialog(dialog: ReturnType<typeof readLifecycleDialog>, caps: PermitCapabilities): boolean {
  switch (dialog) {
    case 'activate':
      return caps.activate;
    case 'end':
      return caps.end;
    case 'renew':
      return caps.renew;
    case 'delete':
      return caps.remove;
    default:
      return false;
  }
}

function PermitScreen({ permit }: { readonly permit: Permit }): JSX.Element {
  const mayEdit = useMayEditPermits();
  const caps = capabilities(permit.status, mayEdit);
  const typeQuery = usePermitTypeDefinition(permit.typeCode);
  const units = useUnitLabels(true);
  const [params, setParams] = useSearchParams();

  const requested = params.get(TabParam) ?? OverviewTabId;
  const tab = permit.tables.some((table) => table.code === requested) ? requested : OverviewTabId;
  const dialog = readLifecycleDialog(params.get(DialogParam));

  const update = (change: (next: URLSearchParams) => void): void => {
    const next = new URLSearchParams(params);
    change(next);
    setParams(next, { replace: true });
  };

  const selectTab = (value: string | null): void =>
    update((next) => {
      // Шторка й діалог належать вкладці; на іншій вони не мають лишатись в адресі.
      next.delete(PanelParam);
      next.delete(DialogParam);
      if (value === null || value === OverviewTabId) next.delete(TabParam);
      else next.set(TabParam, value);
    });

  const open = (id: string): void => update((next) => next.set(DialogParam, id));
  const closeDialog = (): void => update((next) => next.delete(DialogParam));

  const hasTables = permit.tables.length > 0;
  useEffect(() => {
    if (hasTables) void loadTableTab();
  }, [hasTables]);

  const typeName = localName(permit.typeNameL10n) || permit.typeCode;
  const subtitle = permit.location === null || permit.location === '' ? typeName : `${typeName} · ${permit.location}`;

  return (
    <>
      <PermitEditorHead
        back={{ label: t('permits.back.permits'), href: PermitsPaths.permits }}
        title={permit.number}
        mono
        subtitle={subtitle}
        badge={<PermitStateBadge state={permit.status} />}
        progress={<PermitStateStepper state={permit.status} />}
        actions={<Actions caps={caps} open={open} />}
      />
      <Stack gap="md">
        {permit.status === PermitStatus.Expired && (
          <Banner tone="info" title={t('permits.page.banner.expiredTitle')} text={t('permits.page.banner.expiredText')} />
        )}
        <Tabs value={tab} onChange={selectTab} keepMounted={false}>
          <Tabs.List aria-label={t('permits.page.tabs.aria')} mb="md">
            <Tabs.Tab value={OverviewTabId}>{t('permits.page.tabs.overview')}</Tabs.Tab>
            {permit.tables.map((table) => (
              <Tabs.Tab
                key={table.code}
                value={table.code}
                rightSection={
                  <Badge size="xs" variant="default" circle={false}>
                    {table.rowCount}
                  </Badge>
                }
              >
                {localName(table.nameL10n) || table.code}
              </Tabs.Tab>
            ))}
          </Tabs.List>
          <Tabs.Panel value={OverviewTabId}>
            <OverviewTab permit={permit} caps={caps} onActivate={() => open('activate')} onRenew={() => open('renew')} />
          </Tabs.Panel>
          {permit.tables.map((table) => (
            <Tabs.Panel key={table.code} value={table.code}>
              {typeQuery.isError ? (
                <ErrorAlert error={typeQuery.error} onRetry={() => void typeQuery.refetch()} />
              ) : typeQuery.data === undefined ? (
                <Skeleton height={160} role="status" aria-label={t('common.loading')} />
              ) : (
                <Suspense fallback={<Skeleton height={160} role="status" aria-label={t('common.loading')} />}>
                  <TableTab permit={permit} table={table} type={typeQuery.data} units={units} caps={caps} />
                </Suspense>
              )}
            </Tabs.Panel>
          ))}
        </Tabs>
      </Stack>
      {dialog !== null && allowedDialog(dialog, caps) && (
        <Suspense fallback={<Skeleton height={80} role="status" aria-label={t('common.loading')} />}>
          <LifecycleDialogs permit={permit} dialog={dialog} onClose={closeDialog} />
        </Suspense>
      )}
    </>
  );
}

/**
 * Один дозвіл (`/admin/permits/:id`; мокап `PERMIT_SCREEN`): шапка зі станом і діями життєвого циклу, вкладки
 * Overview | по вкладці на кожну таблицю типу. Дані — `GET /permits/{id}` і визначення типу; рядки таблиць читає й
 * пише наявний `registries/{PRM_…}` (PM-04).
 */
export function PermitPage(): JSX.Element {
  const { id } = useParams();
  const permitId = Number(id);

  // ⛔ `/admin/permits/abc`: ідентифікатор - додатне ціле, інакше це «не знайдено», а не вічний скелет (запит із
  // `NaN` у шляху не йде).
  return Number.isInteger(permitId) && permitId > 0 ? <PermitById id={id ?? ''} permitId={permitId} /> : <NotFoundPage />;
}

function PermitById({ id, permitId }: { readonly id: string; readonly permitId: number }): JSX.Element {
  const query = usePermit(permitId);

  return (
    <>
      {query.data === undefined && (
        <PermitEditorHead back={{ label: t('permits.back.permits'), href: PermitsPaths.permits }} title={id} mono />
      )}
      <AsyncBoundary<Permit>
        isPending={query.isPending}
        error={query.error}
        data={query.data}
        skeleton="form"
        onRetry={() => void query.refetch()}
      >
        {(permit) => <PermitScreen permit={permit} />}
      </AsyncBoundary>
    </>
  );
}
