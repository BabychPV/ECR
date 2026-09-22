import { useEffect, useMemo, useState, type JSX } from 'react';
import { Badge, Group, Skeleton, Tabs, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  RegistryDefDto,
  RegistryDefinitionDto,
  RegistryHistoryEntryDto,
  SaveRegistryDefinitionDto,
  UserPage,
} from '@/api/types';
import {
  RegistryFields,
  RegistryHistory,
  RegistryMappings,
  RegistryRelations,
  RegistryRules,
} from '@/features/registries/RegistryConstructor';
import { RegistryDraftPanel } from '@/features/registries/RegistryDraftPanel';
import { RegistryUsagePanel } from '@/features/registries/RegistryUsage';
import {
  buildSaveRequest,
  emptyField,
  emptyRule,
  isComplete,
  isFieldComplete,
  toDraft,
  type FieldDraft,
  type RuleDraft,
} from '@/features/registries/definition';
import {
  draftNewFields,
  draftRules,
  getRegistryDraft,
  registryDraftKey,
} from '@/features/registries/registryDraft';
import { language, t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Конструктор довідника (`ФВ-8.12`): поля, зв'язки, правила, мапінг, історія.
 *
 * ⛔ Окремий маршрут, а не вкладка в переліку довідників. Перелік відповідає на
 * питання «які значення можна обрати», конструктор — на «як цей довідник
 * улаштований»; це різні питання і різні права (`Registry.EditData` проти
 * `Registry.EditDefinition`). На друге треба вміти дати посилання
 * (`ФВ-14.29`).
 *
 * ⛔ Правила — область, де ПРАВЛЯТЬ наявні записи. Поля — область, куди лише
 * ДОДАЮТЬ нові: код і тип НАЯВНОГО поля перетлумачують уже збережені значення
 * (`D2-202`), і форма їх не чіпає, — але додати нове поле можна, бо сервер
 * розрізняє «нове» від «правки наявного» за `id === null`, той самий принцип,
 * що й у правил. Зв'язки обчислені з даних (`Q-027`), а мапінг заводять на
 * екрані джерела, де поруч є перелік тегів, — там і далі лише показ. Форма,
 * яка дає натиснути там, де сервер відмовить, гірша за відсутність кнопки.
 */
export function RegistryConstructorPage(): JSX.Element {
  const { code = '' } = useParams();
  const session = useSession();

  const [rules, setRules] = useState<RuleDraft[]>([]);
  const [newFields, setNewFields] = useState<FieldDraft[]>([]);
  const [reason, setReason] = useState('');

  const definition = useQuery({
    queryKey: queryKeys.registries.definition(code),
    queryFn: () =>
      apiFetch<RegistryDefinitionDto>(
        `/api/v1/registries/${encodeURIComponent(code)}/definition`,
      ),

    // ⛔ Фонового перечитування тут бути не може. Правила — форма, і відповідь
    // сервера засіває чернетку: перечитування при поверненні фокуса стерло б
    // недописане правило рівно в той момент, коли людина перемкнулася в
    // сусіднє вікно подивитися шлях тега. Опис довідника міняють одиниці разів
    // на рік, тож ціна «застарілих» даних тут нульова, а ціна втраченої
    // правки — уся робота за сеанс.
    refetchOnWindowFocus: false,
    staleTime: Number.POSITIVE_INFINITY,
  });

  /**
   * Чернетка опису (`BE-24` крок 2).
   *
   * ⛔ Той самий ключ, що й у `RegistryDraftPanel`: обидва в межах одного
   * `QueryClient` діляться кешем, тож запит іде ОДИН, а панель і форма ніколи
   * не показують чернетки різного віку. Панель зберігає й публікує; сторінка
   * лише засіває нею форму.
   */
  const draft = useQuery({
    queryKey: registryDraftKey(code),
    queryFn: () => getRegistryDraft(code),
    refetchOnWindowFocus: false,
    staleTime: Number.POSITIVE_INFINITY,
  });

  const history = useQuery({
    queryKey: queryKeys.registries.history(code),
    queryFn: () =>
      apiFetch<RegistryHistoryEntryDto[]>(
        `/api/v1/registries/${encodeURIComponent(code)}/history`,
      ),
  });

  /**
   * Перелік користувачів — щоб історія показувала ІМ'Я автора, а не голий
   * `changedByUserId` (сиблінг-виправлення до пропущеного в `AuditPage.tsx`).
   *
   * ⛔ Той самий ключ і ендпоінт, що й `SecurityPage.tsx`
   * (`/api/v1/users?limit=200`, право `Security.ManageUsers`): той самий
   * ключ `['users']` дає їм ділити кеш, коли обидва змонтовані в межах того
   * самого `QueryClient`.
   *
   * ⛔ НЕ під головним `<AsyncBoundary>` нижче — і не в СВОЄМУ. Головна межа
   * стосується `definition`: без опису довідника сторінки взагалі немає.
   * Право читати перелік користувачів — ІНШЕ право (`Security.ManageUsers`
   * проти `Registry.EditDefinition`/`Registry.View`), і `403` на ньому не
   * повинен ховати решту сторінки під власним екраном «немає права» —
   * глядач без цього права однаково має бачити ІСТОРІЮ, лише без імен
   * авторів. `RegistryHistory` сама показує голий ідентифікатор і бейдж
   * «нерозв'язано» в рядку, де ім'я не знайшлося (немає права, або
   * користувача видалено і його немає в першій сторінці переліку).
   */
  const users = useQuery({
    queryKey: ['users'],
    queryFn: () => apiFetch<UserPage>('/api/v1/users?limit=200'),
  });

  const userNames = useMemo(() => {
    const map = new Map<number, string>();
    for (const user of users.data?.items ?? []) {
      map.set(user.id, user.displayName);
    }
    return map;
  }, [users.data]);

  /**
   * Перелік довідників — щоб поле типу `Lookup` обирало ціль зі списку, а не
   * вимагало вгадати ідентифікатор напам'ять. Той самий ключ і запит, що й
   * `RegistriesPage.tsx` (`queryKeys.registries.list()`): обидва в межах
   * одного `QueryClient` діляться кешем.
   */
  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });

  const registryOptions = useMemo(
    () =>
      (registries.data ?? []).map((registry) => ({
        value: String(registry.id),
        label: `${localized(registry.nameL10n)} (${registry.code})`,
      })),
    [registries.data],
  );

  /*
   * ⚠ Чернетка правил і нових полів синхронізується з відповіддю сервера, а
   * не будується в рендері: інакше кожен натиск клавіші відкочував би поле до
   * значення з кешу запиту.
   *
   * ⛔ Джерело — ЧЕРНЕТКА, якщо вона є, і лише інакше опублікований опис. Це і
   * є крок 2 `BE-24`: людина, яка повернулася на екран, має побачити те, що
   * зберегла вона (або сусід), а не версію, яка діє. Показати опубліковану
   * поверх наявної чернетки означало б тихо запропонувати перезаписати чужу
   * незакінчену роботу — і при цьому назвати це «поточним станом».
   *
   * ⚠ Чекаємо ОБИДВІ відповіді: доки чернетка ще їде, засівати форму
   * опублікованим описом не можна — інакше вміст чернетки на мить з'являвся б
   * і зникав, а введене за цю мить губилося б.
   */
  useEffect(() => {
    if (definition.data === undefined || draft.isPending) return;

    const saved = draft.data?.draft ?? null;

    if (saved === null) {
      // Нові поля скидаються тут само: після публікації вони вже стали
      // НАЯВНИМИ полями у свіжому `definition.data`, і лишити їх чернеткою
      // означало б надіслати їх іще раз під `id === null`, тобто як дублікат.
      setRules(definition.data.rules.map((rule) => toDraft(rule, language())));
      setNewFields([]);
      setReason('');
      return;
    }

    setRules(draftRules(saved, language()));
    setNewFields(draftNewFields(saved, language()));
    setReason(saved.reason);
  }, [definition.data, draft.data, draft.isPending]);

  const mayEdit = can(session.data, 'Registry.EditDefinition');
  const ready =
    rules.every(isComplete) && newFields.every(isFieldComplete) && reason.trim().length > 0;

  /**
   * Повний стан форми для збереження чернетки; `null` — ще не готовий.
   *
   * ⚠ Та сама форма, що й у прямого `PUT …/definition` (`buildSaveRequest`):
   * чернетка зберігає РІВНО те, що буде застосовано при публікації, і друга
   * збірка того самого тіла розійшлася б із першою мовчки.
   */
  const request = useMemo<SaveRegistryDefinitionDto | null>(
    () =>
      ready && definition.data !== undefined
        ? buildSaveRequest(definition.data, rules, newFields, reason, language())
        : null,
    [ready, definition.data, rules, newFields, reason],
  );

  return (
    <>
      <PageHeader
        title={t('registries.constructor')}
        actions={
          <Group gap="xs" align="end">
            {definition.data !== undefined && (
              <Badge variant="light">
                {t('registries.definitionVersion', {
                  version: definition.data.definitionVersion,
                })}
              </Badge>
            )}
          </Group>
        }
      />

      <AsyncBoundary<RegistryDefinitionDto>
        isPending={definition.isPending}
        error={definition.error}
        data={definition.data}
        onRetry={() => void definition.refetch()}
      >
        {(loaded) => (
          <>
            <Group gap="xs" mb="sm">
              <Text fw={600}>{localized(loaded.nameL10n) || loaded.code}</Text>
              <Text size="xs" c="dimmed">
                {loaded.code}
              </Text>
              <Badge variant="light">{loaded.sourceKind}</Badge>
              {loaded.isTemporal && <Badge variant="light">{t('registries.temporal')}</Badge>}
            </Group>

            {/* ⛔ Чернетка і публікація стоять ПЕРЕД вкладками, а не в шапці
                сторінки: вони стосуються всього, що нижче, а шапка ділиться
                між усіма адміністративними екранами і місця під смугу стану
                там немає. */}
            <RegistryDraftPanel
              code={loaded.code}
              request={request}
              reason={reason}
              onReasonChange={setReason}
            />

            <Tabs defaultValue="fields" keepMounted={false}>
              <Tabs.List>
                <Tabs.Tab value="fields">{t('registries.tabFields')}</Tabs.Tab>
                <Tabs.Tab value="relations">{t('registries.tabRelations')}</Tabs.Tab>
                <Tabs.Tab value="rules">{t('registries.tabRules')}</Tabs.Tab>
                <Tabs.Tab value="mapping">{t('registries.tabMapping')}</Tabs.Tab>
                <Tabs.Tab value="history">{t('registries.tabHistory')}</Tabs.Tab>
                {/* ⚠ Лише з правом `Registry.EditDefinition` — те саме право, що
                    на сервері (`GetRegistryUsageHandler`): вкладка без права
                    вела б у відому відмову. */}
                {mayEdit && <Tabs.Tab value="usage">{t('registries.tabUsage')}</Tabs.Tab>}
              </Tabs.List>

              <Tabs.Panel value="fields" pt="sm">
                {/*
                  ⛔ Директива D15 §0, правило L10: відмова `GET /api/v1/registries`
                  давала `registryOptions === []`, тобто перелік цілей для поля
                  `Lookup` складався з самого «—». Автор поля бачив рівно те саме,
                  що й при довіднику без жодного сусіда, а зберегти не міг
                  (`isFieldComplete` вимагає цілі) — і підказка внизу казала
                  «поле неповне», тобто називала НЕ ту причину.
                */}
                {registries.error !== null && (
                  <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />
                )}

                <RegistryFields
                  definition={loaded}
                  canEdit={mayEdit}
                  newFields={newFields}
                  registryOptions={registryOptions}
                  onAddField={() => setNewFields((all) => [...all, emptyField()])}
                  onChangeField={(index, field) =>
                    setNewFields((all) => all.map((item, i) => (i === index ? field : item)))
                  }
                  onRemoveField={(index) =>
                    setNewFields((all) => all.filter((_, i) => i !== index))
                  }
                />
              </Tabs.Panel>

              <Tabs.Panel value="relations" pt="sm">
                <RegistryRelations definition={loaded} />
              </Tabs.Panel>

              <Tabs.Panel value="rules" pt="sm">
                <RegistryRules
                  rules={rules}
                  canEdit={mayEdit}
                  onChange={(index, rule) =>
                    setRules((all) => all.map((item, i) => (i === index ? rule : item)))
                  }
                  onAdd={() => setRules((all) => [...all, emptyRule('Expression')])}
                />
              </Tabs.Panel>

              <Tabs.Panel value="mapping" pt="sm">
                <RegistryMappings definition={loaded} />
              </Tabs.Panel>

              <Tabs.Panel value="history" pt="sm">
                {/*
                  ⛔ Найдорожче місце цієї сторінки: `history.data ?? []` при
                  відмові давало `entries.length === 0`, а `RegistryHistory`
                  на нулі записів каже «змін не було». Тобто відмова читалася
                  як ТВЕРДЖЕННЯ про журнал змін — саме там, куди приходять із
                  питанням «хто і навіщо це змінив». Порядок той самий, що в
                  `AsyncBoundary`: `error` → `isPending` → дані.

                  ⚠ `AsyncBoundary` тут не годиться: вона малює власний
                  `<Title order={4}>`, а сторінка вже має заголовок і `<Title
                  order={2}>` у самій вкладці — вставка розірвала б порядок
                  заголовків (`heading-order`, гейти `a11y`).
                */}
                {history.error !== null && (
                  <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />
                )}

                {history.error === null && history.isPending && (
                  <Skeleton height={120} radius="sm" data-registry-history="pending" />
                )}

                {history.error === null && !history.isPending && (
                  <RegistryHistory
                    entries={history.data}
                    userNames={userNames}
                    usersResolved={!users.isPending}
                  />
                )}
              </Tabs.Panel>

              {mayEdit && (
                <Tabs.Panel value="usage" pt="sm">
                  <RegistryUsagePanel code={loaded.code} />
                </Tabs.Panel>
              )}
            </Tabs>
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

