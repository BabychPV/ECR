import { useEffect, useState, type JSX } from 'react';
import { Badge, Button, Group, Tabs, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type {
  RegistryDefinitionDto,
  RegistryDefinitionVersionResponse,
  RegistryHistoryEntryDto,
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
  emptyRule,
  isComplete,
  toDraft,
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
 * ⛔ Правила — єдина область, яку тут ПРАВЛЯТЬ. Поля, зв'язки і мапінг
 * показуються: код і тип наявного поля перетлумачують уже збережені значення
 * (`D2-202`), зв'язки обчислені з даних (`Q-027`), а мапінг заводять на екрані
 * джерела, де поруч є перелік тегів. Форма, яка дає натиснути там, де сервер
 * відмовить, гірша за відсутність кнопки.
 */
export function RegistryConstructorPage(): JSX.Element {
  const { code = '' } = useParams();
  const session = useSession();
  const queryClient = useQueryClient();

  const [rules, setRules] = useState<RuleDraft[]>([]);
  const [reason, setReason] = useState('');

  const definition = useQuery({
    queryKey: ['registry-definition', code],
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
    queryKey: ['registry-history', code],
    queryFn: () =>
      apiFetch<RegistryHistoryEntryDto[]>(
        `/api/v1/registries/${encodeURIComponent(code)}/history`,
      ),
  });

  // ⚠ Чернетка правил синхронізується з відповіддю сервера, а не будується в
  // рендері: інакше кожен натиск клавіші відкочував би поле до значення з
  // кешу запиту.
  useEffect(() => {
    if (definition.data !== undefined) {
      setRules(definition.data.rules.map((rule) => toDraft(rule, language())));
    }
  }, [definition.data]);

  const mayEdit = can(session.data, 'Registry.EditDefinition');
  const ready = rules.every(isComplete) && reason.trim().length > 0;

  const save = useMutation({
    mutationFn: () =>
      apiFetch<RegistryDefinitionVersionResponse>(
        `/api/v1/registries/${encodeURIComponent(code)}/definition`,
        {
          method: 'PUT',
          body: JSON.stringify(
            buildSaveRequest(definition.data!, rules, reason, language()),
          ),
        },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['registry-definition', code] });
      await queryClient.invalidateQueries({ queryKey: ['registry-history', code] });
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
                <RegistryFields definition={loaded} />
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
                <RegistryHistory entries={history.data ?? []} />
              </Tabs.Panel>
            </Tabs>
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

