import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Group, Select, Switch, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetchResponse } from '@/api/client';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { ReplaceGrantsRequest, ResourceGrantDto, RoleView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { t } from '@/shared/i18n';
import { EmptyPath, ResourcePicker, type PickPath } from '@/pages/admin/grants/ResourcePicker';
import {
  GrantLevels,
  ResourceKinds,
  grantLevelLabel,
  resourceKindLabel,
} from '@/pages/admin/grants/grantLabels';

/**
 * Ресурсні гранти ролі: до ЧОГО саме вона відкриває доступ.
 *
 * ⛔ Панель з'явилася після `A7-22`. Права відповідають на питання «що людина
 * вміє», гранти — «до чого»; без другої відповіді перша не відкриває нічого.
 * Доти гранти не міг створити ніхто: таблиця `sec.ResourceGrant` існувала від
 * Етапу 3, правило «IsDeny виграє завжди» було реалізоване в читанні, а
 * записати грант не було чим. Користувач із усіма 38 правами бачив ПОРОЖНІЙ
 * перелік проєктів.
 *
 * ⚠ Набір зберігається ЦІЛКОМ, а не по одному запису. Гранти — це відповідь на
 * питання «що покриває ця роль», і вона має бути видима одним поглядом;
 * часткові правки лишають стан, у якому джерело доступу не відновлюється
 * (ФВ-6.6).
 *
 * ⛔ Аудит U6: чернетка раніше перезаписувалась КОЖНОЮ відповіддю сервера
 * (зміна ролі, фоновий перезапит) — незбережені гранти зникали мовчки. Тепер
 * чернетка має ознаку «змінено», зареєстрована в `UnsavedGuard`, перемикання
 * ролі з незбереженим питає підтвердження, а перезапит не чіпає змінену
 * чернетку.
 *
 * ⛔ Версія набору: `GET` віддає її в `ETag`, `PUT` несе її в `If-Match`.
 * Застаріла — `409 ECR-SEC-0409`: набір перечитується, чернетка лишається, а
 * над таблицею — пояснення й «Скинути» (див. `seed.etag`).
 */

interface DraftRow {
  /** Стабільний ключ рядка для React: індекс зсувається при видаленні. */
  readonly key: number;
  readonly grant: ResourceGrantDto;
  readonly path: PickPath;
}

interface Seed {
  readonly roleId: number;
  readonly grants: readonly ResourceGrantDto[];
  /**
   * Версія набору, з якої почалася чернетка (`ETag` відповіді `GET`/`PUT`), —
   * саме її несе `If-Match`. ⛔ Не версія ОСТАННЬОЇ відповіді сервера: фоновий
   * перезапит змінену чернетку не чіпає, і якби `If-Match` брав свіжу версію,
   * збереження мовчки затерло б чужу правку, якої людина не бачила.
   */
  readonly etag: string | null;
}

/**
 * Гранти ролі разом із версією набору з заголовка `ETag`. ⚠ Не форма тіла
 * сервера (та — `ResourceGrantDto[]` із `schema.d.ts`), а клієнтська пара
 * «тіло + заголовок».
 */
interface LoadedGrants {
  readonly grants: ResourceGrantDto[];
  readonly etag: string | null;
}

async function fetchGrants(roleId: number): Promise<LoadedGrants> {
  const response = await apiFetchResponse(`/api/v1/roles/${roleId}/grants`);

  return { grants: (await response.json()) as ResourceGrantDto[], etag: response.headers.get('ETag') };
}

/**
 * Чи це відмова через застарілу версію набору (`ECR-SEC-0409` з актуальною
 * версією в `details.version`), а не інший конфлікт безпеки того самого коду
 * (дублікат ресурсу в наборі).
 */
function isStaleGrants(error: unknown): boolean {
  return (
    error instanceof EcrApiError &&
    error.problem.status === 409 &&
    error.problem.errorCode === 'ECR-SEC-0409' &&
    typeof error.problem.extensions2?.['version'] === 'string'
  );
}

/**
 * ⚠ Роль показується кодом: `GET /api/v1/roles` (`RoleView`) не віддає
 * локалізованої назви, хоча вона є в базі (`CreateRoleModal` її пише). Коли
 * контракт її отримає — міняється лише цей рядок.
 */
function roleLabel(role: RoleView | undefined): string {
  return role?.code ?? '';
}

/** Канонічний вигляд набору: порядок і `resourceName` не є зміною. */
function canonical(grants: readonly ResourceGrantDto[]): string {
  return grants
    .map((g) => `${g.resourceKind}:${String(g.resourceId)}:${g.level}:${String(g.isDeny)}`)
    .sort()
    .join('|');
}

function sameGrants(a: readonly ResourceGrantDto[], b: readonly ResourceGrantDto[]): boolean {
  return canonical(a) === canonical(b);
}

export function GrantsPanel({ roles }: { roles: RoleView[] }): JSX.Element {
  const [roleId, setRoleId] = useState<number | null>(null);
  const [rows, setRows] = useState<DraftRow[]>([]);
  const [seed, setSeed] = useState<Seed | null>(null);
  // `undefined` — питання немає; `null` — людина хоче зняти вибір ролі.
  const [pendingRole, setPendingRole] = useState<number | null | undefined>(undefined);
  // ⛔ Збереження відмовлене `409`: набір змінив хтось інший. Чернетка лишається,
  // точка відліку — вже свіжий набір сервера; людина вирішує сама.
  const [conflict, setConflict] = useState(false);
  const nextKey = useRef(0);
  const queryClient = useQueryClient();

  const grants = useQuery({
    queryKey: ['grants', roleId],
    queryFn: () => fetchGrants(roleId ?? 0),
    enabled: roleId !== null,
  });

  const draft = rows.map((row) => row.grant);
  const seeded = seed !== null && seed.roleId === roleId;
  const dirty = seeded && !sameGrants(draft, seed.grants);
  const incomplete = rows.some((row) => row.grant.resourceId <= 0);

  // ⚠ Знімок для ефекту й для `UnsavedGuard`: обидва читають поточний стан у
  // момент виклику, а не той, що був при оголошенні.
  const latest = useRef({ draft, dirty, seed });
  useEffect(() => {
    latest.current = { draft, dirty, seed };
  });

  useEffect(
    () =>
      registerUnsavedSource('grants', {
        hasUnsaved: () => latest.current.dirty,
      }),
    [],
  );

  /*
   * ⛔ Відповідь сервера наповнює чернетку лише тоді, коли в ній нема чого
   * втрачати: інша роль, ще не наповнена, не змінена — або вже дорівнює
   * відповіді. Змінену чернетку фоновий перезапит (фокус вікна, інвалідація)
   * НЕ перезаписує: доти це була мовчазна втрата незбережених грантів (U6).
   */
  useEffect(() => {
    if (roleId === null || grants.data === undefined) return;

    const current = latest.current;
    const sameRole = current.seed?.roleId === roleId;
    if (sameRole && current.dirty && !sameGrants(current.draft, grants.data.grants)) return;

    setRows(grants.data.grants.map((grant) => ({ key: nextKey.current++, grant, path: EmptyPath })));
    setSeed({ roleId, grants: grants.data.grants, etag: grants.data.etag });
    setConflict(false);
  }, [grants.data, roleId]);

  const save = useMutation({
    /*
     * ⛔ `If-Match` — версія, з якої почалася ЦЯ чернетка (`seed.etag`).
     * Без неї два адміністратори однієї ролі затирали набори один одного
     * мовчки: заміна цілком не лишає від чужої правки нічого.
     */
    mutationFn: async (next: { roleId: number; grants: ResourceGrantDto[]; etag: string | null }) => {
      const response = await apiFetchResponse(`/api/v1/roles/${next.roleId}/grants`, {
        method: 'PUT',
        body: JSON.stringify({ grants: next.grants } satisfies ReplaceGrantsRequest),
        ...(next.etag === null ? {} : { headers: { 'If-Match': next.etag } }),
      });

      return response.headers.get('ETag');
    },
    onSuccess: async (etag, next) => {
      // ⚠ Збережене стає новою точкою відліку ДО перечитання: інакше відповідь,
      // що відрізняється лише порядком чи назвою, лишила б чернетку «зміненою».
      setSeed((prev) =>
        prev?.roleId === next.roleId ? { roleId: next.roleId, grants: next.grants, etag } : prev,
      );
      setConflict(false);
      await queryClient.invalidateQueries({ queryKey: ['grants', next.roleId] });
      showDone(t('grants.saved'));
    },
    onError: async (error, next) => {
      // ⛔ `X-08`: тут стояв `error.message` — сирий `detail` сервера
      // (українською без `messageKey`). Той самий розбір, що й скрізь.
      showApiError(error);
      if (!isStaleGrants(error)) return;

      // ⚠ Перечитати набір, але НЕ чіпати чернетку: вона — робота людини.
      // Свіжий набір стає точкою відліку (і його версія — наступним
      // `If-Match`), тож наступне «Зберегти» — усвідомлена заміна чужої
      // правки, а «Скинути» показує, що там тепер.
      const fresh = await queryClient.fetchQuery({
        queryKey: ['grants', next.roleId],
        queryFn: () => fetchGrants(next.roleId),
        staleTime: 0,
      });
      setSeed((prev) =>
        prev?.roleId === next.roleId ? { roleId: next.roleId, grants: fresh.grants, etag: fresh.etag } : prev,
      );
      setConflict(!sameGrants(latest.current.draft, fresh.grants));
    },
  });

  function switchRole(next: number | null): void {
    setRoleId(next);
    setRows([]);
    setSeed(null);
    setConflict(false);
  }

  function discardDraft(): void {
    if (seed === null) return;
    setRows(seed.grants.map((grant) => ({ key: nextKey.current++, grant, path: EmptyPath })));
    setConflict(false);
  }

  function requestRole(next: number | null): void {
    if (next === roleId) return;
    if (dirty) setPendingRole(next);
    else switchRole(next);
  }

  function replace(key: number, change: (row: DraftRow) => DraftRow): void {
    setRows((prev) => prev.map((row) => (row.key === key ? change(row) : row)));
  }

  return (
    <>
      <Group mb="sm" gap="xs" align="flex-end">
        <Select
          size="xs"
          miw={200}
          label={t('security.role')}
          placeholder={t('grants.pickRole')}
          data={roles.map((r) => ({ value: String(r.id), label: roleLabel(r) }))}
          value={roleId === null ? null : String(roleId)}
          onChange={(value) => requestRole(value === null ? null : Number(value))}
        />

        {roleId !== null && (
          <>
            <Button
              size="xs"
              variant="default"
              onClick={() =>
                setRows((prev) => [
                  ...prev,
                  {
                    key: nextKey.current++,
                    grant: { resourceKind: 'Project', resourceId: 0, level: 'Read', isDeny: false },
                    path: EmptyPath,
                  },
                ])
              }
            >
              {t('grants.add')}
            </Button>

            {/* ⛔ Заблоковано, поки чернетка не наповнена ВІДПОВІДДЮ САМЕ на
                цю роль (інакше збереження замінило б справжні гранти чужими
                чи пусткою), поки в ній нема змін і поки хоч один рядок без
                обраного ресурсу (`resourceId` 0 — грант «ні на що»). */}
            <Button
              size="xs"
              loading={save.isPending}
              disabled={grants.isPending || Boolean(grants.error) || !seeded || !dirty || incomplete}
              onClick={() => save.mutate({ roleId, grants: draft, etag: seed?.etag ?? null })}
            >
              {t('common.save')}
            </Button>

            {dirty && (
              <Text size="xs" c="dimmed" fs="italic" data-testid="grants-unsaved">
                {incomplete ? t('grants.pickResourceFirst') : t('grants.unsaved')}
              </Text>
            )}
          </>
        )}
      </Group>

      {roleId !== null && conflict && (
        <Group mb="xs" gap="xs" data-testid="grants-conflict">
          <Text size="xs" c="statusError" role="alert">
            {t('grants.conflict')}
          </Text>
          <Button size="compact-xs" variant="default" onClick={discardDraft}>
            {t('grants.discardVerb')}
          </Button>
        </Group>
      )}

      {/* ⚠ D-207 п.2: вибір проєкту показує ВСІ проєкти (код і назву), а не
          лише доступні — колишня підказка `grants.projectsScopeHint` про
          «лише доступні» стала неправдою. */}
      {roleId !== null && (
        <Text size="xs" c="dimmed" mb="xs">
          {t('grants.projectsCatalogHint')}
        </Text>
      )}

      {/*
       * ⚠ Перелік редагується як ЧЕРНЕТКА (`rows`), а завантажене значення
       * лише наповнює її. Тому обгортка дивиться на запит, а таблиця малює
       * чернетку: інакше щойно доданий рядок зникав би, доки не збережено.
       *
       * ⛔ Порожній стан НЕ ховає кнопку «додати»: роль без грантів — це
       * звичайний початок роботи, а не збій (`A7-22`).
       */}
      <AsyncBoundary<ResourceGrantDto[]>
        isPending={roleId !== null && grants.isPending}
        error={grants.error}
        data={roleId === null ? undefined : grants.data?.grants}
        isEmpty={() => rows.length === 0}
        emptyTitle={roleId === null ? t('grants.pickRole') : t('grants.empty')}
        emptyHint={roleId === null ? t('grants.pickRoleHint') : t('grants.emptyHint')}
        skeleton="table"
        onRetry={() => void grants.refetch()}
      >
        {() => (
        <Table striped withTableBorder className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('grants.kind')}</Table.Th>
              <Table.Th>{t('grants.target')}</Table.Th>
              <Table.Th>{t('grants.resourceName')}</Table.Th>
              <Table.Th>{t('grants.level')}</Table.Th>
              <Table.Th>{t('grants.deny')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row, index) => {
              const { grant } = row;

              return (
              <Table.Tr key={row.key}>
                <Table.Td>
                  {/*
                   * ⚠ Тут `aria-label`, а не `label`: підпис уже стоїть у
                   * заголовку колонки, але читалка НЕ пов'язує `<th>` з полем
                   * усередині `<td>`.
                   */}
                  <Select
                    size="xs"
                    miw={110}
                    aria-label={`${t('grants.kind')} ${index + 1}`}
                    data={ResourceKinds.map((kind) => ({ value: kind, label: resourceKindLabel(kind) }))}
                    value={grant.resourceKind}
                    onChange={(value) =>
                      value !== null &&
                      replace(row.key, (r) => ({
                        ...r,
                        grant: { ...withoutResourceName(r.grant), resourceKind: value as never, resourceId: 0 },
                      }))
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <ResourcePicker
                    index={index}
                    kind={grant.resourceKind}
                    resourceId={grant.resourceId}
                    path={row.path}
                    onChange={({ resourceId, code, path }) =>
                      replace(row.key, (r) => {
                        const base = { ...withoutResourceName(r.grant), resourceId };

                        return { ...r, path, grant: code === null ? base : { ...base, resourceName: code } };
                      })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  {/*
                   * ⚠ Назва — від сервера (Q-299) або від щойно обраного в
                   * пікері ресурсу. `undefined` — ресурс ще не обрано (не
                   * помилка); `null` — сервер шукав і не знайшов (ресурс
                   * видалено чи id хибний), і це показується явно.
                   */}
                  {grant.resourceName === null ? (
                    <Text size="xs" c="statusError" fs="italic">
                      {t('grants.resourceNameUnknown')}
                    </Text>
                  ) : grant.resourceName === undefined ? (
                    <Text size="xs" c="dimmed">
                      —
                    </Text>
                  ) : (
                    <Text size="xs">{grant.resourceName}</Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Select
                    size="xs"
                    miw={110}
                    aria-label={`${t('grants.level')} ${index + 1}`}
                    data={GrantLevels.map((level) => ({ value: level, label: grantLevelLabel(level) }))}
                    value={grant.level}
                    onChange={(value) =>
                      value !== null &&
                      replace(row.key, (r) => ({ ...r, grant: { ...r.grant, level: value as never } }))
                    }
                  />
                </Table.Td>
                <Table.Td>
                  {/* ⚠ Заборона перекриває будь-який дозвіл будь-якого рівня
                      (ФВ-6.6) — тому вона окремим перемикачем, а не «рівнем
                      None»: рівень і заборона — різні речі. */}
                  <Switch
                    size="xs"
                    aria-label={`${t('grants.deny')} ${index + 1}`}
                    checked={grant.isDeny}
                    onChange={(event) => {
                      const isDeny = event.currentTarget.checked;
                      replace(row.key, (r) => ({ ...r, grant: { ...r.grant, isDeny } }));
                    }}
                  />
                </Table.Td>
                <Table.Td>
                  <Button
                    size="compact-xs"
                    color="statusError"
                    variant="subtle"
                    onClick={() => setRows((prev) => prev.filter((r) => r.key !== row.key))}
                  >
                    {t('grants.remove')}
                  </Button>
                </Table.Td>
              </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
        )}
      </AsyncBoundary>

      <ConfirmModal
        opened={pendingRole !== undefined}
        title={t('grants.discardTitle', { role: roleLabel(roles.find((r) => r.id === roleId)) })}
        text={t('grants.discardText')}
        verb={t('grants.discardVerb')}
        onClose={() => setPendingRole(undefined)}
        onConfirm={() => {
          if (pendingRole !== undefined) switchRole(pendingRole);
          setPendingRole(undefined);
        }}
      />
    </>
  );
}

/**
 * Прибирає `resourceName` з гранта перед правкою kind/id чернетки.
 *
 * ⚠ Не `{ ...grant, resourceName: undefined }`: поле в типі — `string | null`
 * (не `| undefined`), і `exactOptionalPropertyTypes` відмовляє таке
 * присвоєння на рівні типів. Це не косметика — стара розв'язана назва не
 * має лишатись видимою під ЗМІНЕНИМ kind/id: вона більше не описує цей рядок.
 */
function withoutResourceName(grant: ResourceGrantDto): Omit<ResourceGrantDto, 'resourceName'> {
  const { resourceName: _ignored, ...rest } = grant;
  return rest;
}
