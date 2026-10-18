import { lazy, Suspense, useMemo, type JSX } from 'react';
import { Badge, Button, Group, Skeleton, Stack, Tabs } from '@mantine/core';
import { useSearchParams } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { useMayEditPermitTypes } from '../permitRights';
import { EditorParams, useEditorActions } from './editorActions';
import { useTypeEditorApi } from './editorContext';
import { typeWithDraft } from './selection';
import { StructureTab } from './StructureTab';

// Тіла «Checks» і «Versions» не потрібні на першому показі (за замовчуванням — «Structure»):
// окремі чанки, що вантажаться, лише коли вкладка активна (бюджет маршруту, D-132).
const TypeChecksTab = lazy(() => import('./checks').then((module) => ({ default: module.TypeChecksTab })));
const TypeVersionsTab = lazy(() => import('./versions').then((module) => ({ default: module.TypeVersionsTab })));

export const EditorTabIds = ['structure', 'rules', 'versions'] as const;
export type EditorTabId = (typeof EditorTabIds)[number];

/** Вкладка з адреси; невідома — «Structure». */
export function readTab(params: URLSearchParams): EditorTabId {
  const value = params.get(EditorParams.tab);
  return (EditorTabIds as readonly string[]).includes(value ?? '') ? (value as EditorTabId) : 'structure';
}

/**
 * Вкладки конструктора (мокап: `E.Tabs` — Structure | Checks (N) | Versions, `?tab=`). «Structure»
 * справжня; тіла «Checks» і «Versions» — слоти PM-08c (`checks/`, `versions/`). Над вкладками —
 * відмови збереження й підказка для опублікованого типу.
 */
export function TypeEditorTabs(): JSX.Element {
  const api = useTypeEditorApi();
  const actions = useEditorActions();
  const [params, setParams] = useSearchParams();
  const { draft, editable, save, saveError } = api;
  // Вкладки показують тип разом із правками, яких сервер ще не бачив; право — ПОТОЧНОГО користувача.
  const shown = useMemo(() => typeWithDraft(api.type, draft), [api.type, draft]);
  const mayEdit = useMayEditPermitTypes();
  const tab = readTab(params);
  const rulesCount = draft.rules.length + draft.tables.reduce((sum, table) => sum + table.rules.length, 0);

  const select = (value: string | null): void => {
    const next = new URLSearchParams(params);
    if (value === null || value === 'structure') next.delete(EditorParams.tab);
    else next.set(EditorParams.tab, value);
    setParams(next, { replace: true });
  };

  return (
    <Stack gap="md">
      {(save.kind === 'conflict' || save.kind === 'failed') && saveError !== null && (
        <Stack gap="xs">
          <ErrorAlert error={saveError} onRetry={save.kind === 'failed' ? api.retry : undefined} />
          {save.kind === 'conflict' && (
            <Group>
              <Button variant="default" onClick={() => void api.reload()}>
                {t('permits.editor.save.reload')}
              </Button>
            </Group>
          )}
        </Stack>
      )}
      {!editable && (
        <Banner
          tone="info"
          title={t('permits.editor.published.title')}
          text={t('permits.editor.published.text')}
          actions={mayEdit ? [{ label: t('permits.editor.actions.newVersion'), onClick: actions.openNewVersion }] : undefined}
        />
      )}
      <Tabs value={tab} onChange={select} keepMounted={false}>
        <Tabs.List aria-label={t('permits.editor.tabs.aria')} mb="md">
          <Tabs.Tab value="structure">{t('permits.editor.tabs.structure')}</Tabs.Tab>
          <Tabs.Tab
            value="rules"
            rightSection={
              <Badge size="xs" variant="default" circle={false}>
                {rulesCount}
              </Badge>
            }
          >
            {t('permits.editor.tabs.checks')}
          </Tabs.Tab>
          <Tabs.Tab value="versions">{t('permits.editor.tabs.versions')}</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="structure">
          <StructureTab />
        </Tabs.Panel>
        <Tabs.Panel value="rules">
          <Suspense fallback={<Skeleton height={160} aria-label={t('common.loading')} />}>
            <TypeChecksTab type={shown} onAddCheck={actions.openAddRule} canEdit={editable && mayEdit} />
          </Suspense>
        </Tabs.Panel>
        <Tabs.Panel value="versions">
          <Suspense fallback={<Skeleton height={160} aria-label={t('common.loading')} />}>
            <TypeVersionsTab type={shown} />
          </Suspense>
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
