import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, NativeSelect, NumberInput, Stack, Table, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { EffectiveAccessContribution, EffectiveAccessView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';

type Kind = 'Registry' | 'Project' | 'Sheet' | 'Table' | 'Column';

/** Аркуш, таблиця й колонка — id версії шаблону, спільної для проєктів: без проєкту рівень невизначений. */
function needsProject(kind: Kind): boolean {
  return kind === 'Sheet' || kind === 'Table' || kind === 'Column';
}

interface Asked {
  resource: string;
  projectId: number | null;
}

/** Підпис області внеску (`EffectiveAccessContribution.scope`); ключі — літерали, щоб їх бачив сторож каталогу. */
function scopeLabel(scope: string): string {
  switch (scope) {
    case 'Unscoped':
      return t('effectiveAccess.scopeUnscoped');
    case 'InScope':
      return t('effectiveAccess.scopeInScope');
    case 'Narrowed':
      return t('effectiveAccess.scopeNarrowed');
    case 'Expired':
      return t('effectiveAccess.scopeExpired');
    default:
      return t('effectiveAccess.scopeOutOfScope');
  }
}

/**
 * Розріз «ресурс → підсумковий рівень → який грант якої ролі його дав» (`ФВ-6.16`, `D-220`).
 *
 * ⚠ Панель нічого не вирішує: рівень приходить із сервера тим самим профілем доступу, що й усі
 * рішення, а таблиця лише пояснює його. Внесок, що НЕ порахувався (чужий проєкт, область, строк),
 * показується теж, зі словом «ні» у колонці «Враховано» — саме він відповідає на «чому в мене
 * немає доступу», коли роль начебто є.
 *
 * ⚠ Запит іде лише після «Пояснити»: порожня форма нічого не питає в сервера.
 *
 * ⚠ Аркуш, таблиця, колонка (`ФВ-6.16`): потрібен проєкт, відповідь несе застереження про стан документа,
 * а внески — «успадковано від …» (`inheritedFrom`).
 */
export function EffectiveAccessPanel({ userId }: { userId: number }): JSX.Element {
  const [kind, setKind] = useState<Kind>('Registry');
  const [id, setId] = useState<number | ''>('');
  const [projectId, setProjectId] = useState<number | ''>('');
  const [asked, setAsked] = useState<Asked | null>(null);

  const view = useQuery({
    queryKey: ['effective-access', userId, asked?.resource ?? null, asked?.projectId ?? null],
    queryFn: () =>
      apiFetch<EffectiveAccessView>(
        `/api/v1/security/users/${userId}/effective-access?resource=${encodeURIComponent(asked?.resource ?? '')}` +
          (asked?.projectId == null ? '' : `&projectId=${String(asked.projectId)}`),
      ),
    enabled: asked !== null,
  });
  const projectMissing = needsProject(kind) && (projectId === '' || projectId < 1);

  return (
    <Stack gap="sm" mt="md" data-testid="effective-access-panel">
      <Title order={5}>{t('effectiveAccess.title')}</Title>
      <Text size="sm" c="dimmed">
        {t('effectiveAccess.hintAll')}
      </Text>

      <Group align="flex-end" gap="sm" wrap="wrap">
        <NativeSelect
          label={t('effectiveAccess.kind')}
          value={kind}
          onChange={(event) => setKind(event.currentTarget.value as Kind)}
          data={[
            { value: 'Registry', label: t('effectiveAccess.kindRegistry') },
            { value: 'Project', label: t('effectiveAccess.kindProject') },
            { value: 'Sheet', label: t('effectiveAccess.kindSheet') },
            { value: 'Table', label: t('effectiveAccess.kindTable') },
            { value: 'Column', label: t('effectiveAccess.kindColumn') },
          ]}
        />
        <NumberInput
          label={t('effectiveAccess.resourceId')}
          value={id}
          onChange={(value) => setId(typeof value === 'number' ? value : '')}
          min={1}
          allowDecimal={false}
          allowNegative={false}
          hideControls
        />
        {needsProject(kind) && (
          <NumberInput
            label={t('effectiveAccess.projectId')}
            value={projectId}
            onChange={(value) => setProjectId(typeof value === 'number' ? value : '')}
            min={1}
            allowDecimal={false}
            allowNegative={false}
            hideControls
          />
        )}
        <Button
          disabled={id === '' || id < 1 || projectMissing}
          onClick={() =>
            setAsked({
              resource: `${kind}:${String(id)}`,
              projectId: needsProject(kind) && projectId !== '' ? projectId : null,
            })
          }
        >
          {t('effectiveAccess.explain')}
        </Button>
      </Group>

      {asked !== null && (
        <AsyncBoundary<EffectiveAccessView>
          isPending={view.isPending}
          error={view.error}
          data={view.data}
          skeleton="form"
          onRetry={() => void view.refetch()}
        >
          {(data) => <EffectiveAccessResult view={data} />}
        </AsyncBoundary>
      )}
    </Stack>
  );
}

function EffectiveAccessResult({ view }: { view: EffectiveAccessView }): JSX.Element {
  return (
    <Stack gap="xs">
      <Group gap="xs">
        <Text fw={600}>{t('effectiveAccess.level', { level: view.level })}</Text>
        <Badge variant="light" color={view.isDenied ? 'statusError' : 'gray'}>
          {view.resource}
        </Badge>
      </Group>

      {/* ⛔ Для аркуша, таблиці й колонки сервер не знає стану документа й звужень за періодом: без цього
          застереження поруч із рівнем розріз брехав би про те, що людина зможе зробити з коміркою зараз. */}
      {view.caveat != null && (
        <Alert color="statusWarning" data-testid="effective-access-caveat">
          {t('effectiveAccess.caveat')}
        </Alert>
      )}

      {/* ⛔ Слово, а не лише колір: заборона виграє над усім, і це читається тими, хто не бачить кольору. */}
      {view.isDenied && (
        <Alert color="statusError" title={t('effectiveAccess.deny')}>
          {t('effectiveAccess.denied')}
        </Alert>
      )}
      {!view.isDenied && view.denyReason === 'NoGrant' && (
        <Text size="sm" c="statusWarning">
          {t('effectiveAccess.noGrant')}
        </Text>
      )}

      {/* ⚠ Для чужого запису групи з квитка невідомі (`P-02`): без цього рядка порожній перелік брехав би. */}
      {!view.groupsFromTicket && (
        <Text size="sm" c="dimmed">
          {t('effectiveAccess.groupsUnknown')}
        </Text>
      )}

      {view.contributions.length === 0 ? (
        <Text size="sm">{t('effectiveAccess.noContributions')}</Text>
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table striped aria-label={t('effectiveAccess.title')}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('effectiveAccess.colSource')}</Table.Th>
                <Table.Th>{t('effectiveAccess.colRole')}</Table.Th>
                <Table.Th>{t('effectiveAccess.colVia')}</Table.Th>
                <Table.Th>{t('effectiveAccess.colLevel')}</Table.Th>
                <Table.Th>{t('effectiveAccess.colScope')}</Table.Th>
                <Table.Th>{t('effectiveAccess.colCounted')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {view.contributions.map((c, index) => (
                <ContributionRow key={`${c.roleCode}|${c.source}|${c.permissionCode ?? ''}|${c.principalSid ?? ''}|${String(index)}`} c={c} />
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}

function ContributionRow({ c }: { c: EffectiveAccessContribution }): JSX.Element {
  return (
    <Table.Tr>
      <Table.Td>
        {c.source === 'Permission'
          ? t('effectiveAccess.sourcePermission', { permission: c.permissionCode ?? '' })
          : t('effectiveAccess.sourceGrant')}
      </Table.Td>
      <Table.Td>
        {c.roleCode}
        {c.inheritedFrom != null && (
          <Text size="xs" c="dimmed">
            {t('effectiveAccess.inheritedFrom', { resource: c.inheritedFrom })}
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        {c.principalSid === null
          ? t('effectiveAccess.viaPersonal')
          : t('effectiveAccess.viaGroup', { sid: c.principalSid })}
      </Table.Td>
      <Table.Td>{c.isDeny ? t('effectiveAccess.deny') : c.level}</Table.Td>
      <Table.Td>{scopeLabel(c.scope)}</Table.Td>
      <Table.Td>{c.counted ? t('effectiveAccess.counted') : t('effectiveAccess.notCounted')}</Table.Td>
    </Table.Tr>
  );
}
