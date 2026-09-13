import { useEffect, useMemo, useState, type JSX } from 'react';
import { Badge, Button, Group, Tabs, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  RegistryDefDto,
  RegistryDefinitionDto,
  RegistryDefinitionVersionResponse,
  RegistryHistoryEntryDto,
  UserPage,
} from '@/api/types';
import {
  RegistryFields,
  RegistryHistory,
  RegistryMappings,
  RegistryRelations,
  RegistryRules,
} from '@/features/registries/RegistryConstructor';
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
import { language, t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';

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
  const queryClient = useQueryClient();

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

  // ⚠ Чернетка правил і нових полів синхронізується з відповіддю сервера, а
  // не будується в рендері: інакше кожен натиск клавіші відкочував би поле до
  // значення з кешу запиту. Нові поля скидаються тут само: після успішного
  // збереження вони вже стали НАЯВНИМИ полями у свіжому `definition.data`, і
  // лишити їх чернеткою означало б надіслати їх іще раз при наступному
  // збереженні — під новим `id === null`, тобто як дублікат.
  useEffect(() => {
    if (definition.data !== undefined) {
      setRules(definition.data.rules.map((rule) => toDraft(rule, language())));
      setNewFields([]);
    }
  }, [definition.data]);

  const mayEdit = can(session.data, 'Registry.EditDefinition');
  const ready =
    rules.every(isComplete) && newFields.every(isFieldComplete) && reason.trim().length > 0;

  const save = useMutation({
    mutationFn: () =>
      apiFetch<RegistryDefinitionVersionResponse>(
        `/api/v1/registries/${encodeURIComponent(code)}/definition`,
        {
          method: 'PUT',
          body: JSON.stringify(
            buildSaveRequest(definition.data!, rules, newFields, reason, language()),
          ),
        },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.definition(code) });
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.history(code) });
      setReason('');
      showDone(t('registries.definitionSaved', { version: result.definitionVersion }));
    },

    // ⚠ Сервер відхиляє п'ятий вид правила, зміну виду наявного і брак
    // ключового поля окремими повідомленнями (`ECR-REG-0422`). Показуємо їх, а
    // не «не вдалося зберегти»: кожне з них називає, що саме виправити.
    onError: showApiError,
  });

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

            {mayEdit && (
              <>
                {/* ⛔ Причина обов'язкова: опис довідника змінює те, як
                    читаються ВЖЕ збережені записи, і питання «чому тут
                    з'явилося це поле» ставлять через рік. */}
                <TextInput
                  size="xs"
                  miw={260}
                  label={t('registries.reason')}
                  description={t('registries.reasonHint')}
                  value={reason}
                  onChange={(event) => setReason(event.currentTarget.value)}
                />

                <Button
                  size="xs"
                  disabled={!ready || definition.data === undefined || save.isPending}
                  onClick={() => save.mutate()}
                >
                  {t('registries.saveDefinition')}
                </Button>
              </>
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

            <Tabs defaultValue="fields" keepMounted={false}>
              <Tabs.List>
                <Tabs.Tab value="fields">{t('registries.tabFields')}</Tabs.Tab>
                <Tabs.Tab value="relations">{t('registries.tabRelations')}</Tabs.Tab>
                <Tabs.Tab value="rules">{t('registries.tabRules')}</Tabs.Tab>
                <Tabs.Tab value="mapping">{t('registries.tabMapping')}</Tabs.Tab>
                <Tabs.Tab value="history">{t('registries.tabHistory')}</Tabs.Tab>
              </Tabs.List>

              <Tabs.Panel value="fields" pt="sm">
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
                <RegistryHistory
                  entries={history.data ?? []}
                  userNames={userNames}
                  usersResolved={!users.isPending}
                />
              </Tabs.Panel>
            </Tabs>
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

