import { Suspense, lazy, useState, type JSX } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { useRegistryDefinition } from '@/features/registries/rc812/data';
import { RegistryTreeEditor } from '@/features/registries/tree/RegistryTreeEditor';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import type { TreeLevel, TreeNode, TreeNodeId } from '@/shared/ui/TreeEditor';
import { useUrlState } from '@/shared/ui/useUrlState';
import { useMayWritePermits } from '../list/permitListRights';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { NaeRegistryCode } from './nodeAttributes';
import { WhereUsed } from './WhereUsed';
import './naeStructure.css';

/*
 * ⚠ Форма атрибутів і діалог нового вузла - окремими чанками (`D-132`): Mantine `Modal`, поля й локалізований ввід
 * не потрібні, доки людина не вибрала вузол чи не натиснула «Add child». Чанк маршруту стоїть під бюджетом.
 */
const NodeAttributesForm = lazy(async () => ({
  default: (await import('./NodeAttributesForm')).NodeAttributesForm,
}));
const AfImportPanel = lazy(async () => ({
  default: (await import('./AfImportPanel')).AfImportPanel,
}));
const CreateNodeModal = lazy(async () => ({
  default: (await import('./CreateNodeModal')).CreateNodeModal,
}));

interface Draft {
  readonly parent: TreeNode | null;
  readonly level: TreeLevel;
}

/**
 * Структура НВ (`/admin/permits/nae`, NAE plan N2): дерево регіон › район › майданчик › обладнання › НВ ліворуч, панель
 * вузла праворуч - відомості, атрибути (форма) і «Де використовується».
 *
 * ⛔ Право: читання - `Registry.View` (маршрут); додати, перенести, вивести з обігу й зберегти атрибути - лише з
 * `Registry.EditData` і поза симуляцією (`useMayWritePermits`: те саме право, що й у дозволів). Без нього лишається
 * дерево, пошук і панель, а кнопок немає (приховані, не вимкнені). Грант `Write` на довідник клієнт не бачить:
 * сервер відмовить `403`, і відмова покажеться сповіщенням.
 *
 * ⚠ Вибраний вузол живе в адресі (`?node=<id>`): оновлення сторінки й посилання повертають на той самий вузол.
 */
export function NaeStructurePage(): JSX.Element {
  const canEdit = useMayWritePermits();
  const definition = useRegistryDefinition(NaeRegistryCode);
  const [nodeParam, setNodeParam] = useUrlState('node');
  const [draft, setDraft] = useState<Draft | null>(null);
  const [importOpen, setImportOpen] = useState(false);
  const session = useSession();
  // ⛔ Завантаження з AF створює довідник і вузли: сервер вимагає `Registry.EditDefinition` ТА `Registry.EditData`.
  const canImport = canEdit && can(session.data, 'Registry.EditDefinition');
  // Довідника ще немає (його створює перше завантаження з AF): визначення відповідає 404.
  const notProvisioned = definition.error instanceof EcrApiError && definition.error.problem.status === 404;

  const selected: TreeNodeId | null = nodeParam === null || nodeParam === '' ? null : Number(nodeParam);
  const registry = definition.data;
  // ⚠ Код запису NAE_TREE технічний: бізнес-код - поле NaeCode. Поки визначення не приїхало, код вважаємо технічним.
  const codeAutomatic = registry === undefined || registry.codeMode === 'Auto';

  return (
    <Stack gap="md" data-nae-page="">
      <PageHeader title={t('permits.nae.title')} description={t('permits.nae.description')} />
      <PermitsSubnav current="nae" />
      <Text size="sm" c="dimmed" data-nae-source="">
        {t('permits.nae.sourceNote')}
      </Text>
      {!canEdit && (
        <Text size="sm" c="dimmed" data-nae-readonly="">
          {t('permits.nae.readOnly')}
        </Text>
      )}

      {canImport && (
        <Group gap="xs" data-nae-import-bar="">
          <Button variant="default" size="xs" onClick={() => setImportOpen((open) => !open)} aria-expanded={importOpen}>
            {t('permits.nae.import.open')}
          </Button>
        </Group>
      )}
      {canImport && importOpen && (
        <Suspense fallback={null}>
          <AfImportPanel registryCode={NaeRegistryCode} onClose={() => setImportOpen(false)} />
        </Suspense>
      )}

      {notProvisioned ? (
        <Text size="sm" role="status" data-nae-not-provisioned="">
          {canImport ? t('permits.nae.import.hintNotProvisioned') : t('permits.nae.import.hintAskAdmin')}
        </Text>
      ) : (
        definition.error !== null &&
        definition.error !== undefined && <ErrorAlert error={definition.error} onRetry={() => void definition.refetch()} />
      )}

      {!notProvisioned && <RegistryTreeEditor
        registryCode={NaeRegistryCode}
        label={t('permits.nae.treeLabel')}
        canEdit={canEdit}
        hideEntryCode={codeAutomatic}
        selectedId={selected}
        onSelect={(id) => setNodeParam(id === null ? null : String(id))}
        onAddChild={
          canEdit && registry !== undefined ? (parent, level) => setDraft({ parent, level }) : undefined
        }
        renderPanelExtra={(node) => (
          <div className="nae-panel-extra">
            {registry !== undefined && (
              <Suspense fallback={null}>
                <NodeAttributesForm
                  key={String(node.id)}
                  registryCode={NaeRegistryCode}
                  registryId={registry.id}
                  codeAutomatic={codeAutomatic}
                  fields={registry.fields}
                  target={{ mode: 'edit', node }}
                  readOnly={!canEdit || node.retired === true}
                  onSaved={() => undefined}
                />
              </Suspense>
            )}
            <WhereUsed key={`used-${String(node.id)}`} registryCode={NaeRegistryCode} entryId={Number(node.id)} />
          </div>
        )}
      />}

      {draft !== null && registry !== undefined && (
        <Suspense fallback={null}>
          <CreateNodeModal
            registryCode={NaeRegistryCode}
            registryId={registry.id}
            codeAutomatic={codeAutomatic}
            fields={registry.fields}
            draft={draft}
            onClose={() => setDraft(null)}
            onCreated={(id) => {
              setDraft(null);
              setNodeParam(String(id));
            }}
          />
        </Suspense>
      )}
    </Stack>
  );
}
