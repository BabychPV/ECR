import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Combobox, Group, Modal, MultiSelect, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { AffectedRolesResponse, RoleView, UserView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Ролі й адреса наявного користувача.
 *
 * ⛔ Двох дефектів тут було два, і обидва мовчазні.
 *
 * `A7-61`: способу призначити роль наявному користувачеві не існувало
 * **взагалі**. Ролі видавалися лише при створенні, а форма створення
 * надсилала порожній перелік — обліковий запис виходив працездатним на
 * вигляд і безправним насправді, і виправити це було нічим.
 *
 * `A7-62`: `User.Email` не присвоювався ніде в системі. `NotificationJob`
 * завжди отримував порожній перелік адресатів, тобто сповіщення (`ФВ-12`)
 * не надходили нікому, а перемикач алертів був вічно неактивним і виглядав
 * як налаштування, яке просто вимкнули.
 */
export function UserAccessEditor({
  user,
  roles,
  onClose,
}: {
  user: UserView | null;
  roles: RoleView[];
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string[]>([]);
  const [email, setEmail] = useState('');

  // ⛔ UI-аудит, lane 1 (`lane1-roles-dropdown-traps-clicks.md`): `Escape`,
  // клік по власному шеврону мультиселекту й клік деінде в діалозі не
  // закривали список опцій ролей — лишався відкритим ПОВЕРХ Save/Cancel і
  // перехоплював кліки, призначені для них. Причина не одна: (1) стоковий
  // `MultiSelect.onClick` для `searchable` ЗАВЖДИ викликає `openDropdown()`
  // (ніколи `toggleDropdown()`), тож клік по власному тоглу не міг
  // закрити його; (2) `Modal`'s Escape — це window-level capture-listener
  // (`use-modal.cjs`), який спрацьовує РАНІШЕ за будь-який React-обробник
  // на вкладеному полі, тож покладатися на те, що дропдаун сам «встигне»
  // зупинити його — крихко. Рішення: керуємо станом дропдауна САМІ
  // (`dropdownOpened`/`onDropdownOpen`/`onDropdownClose`), вимикаємо
  // `Modal.closeOnEscape`, поки він відкритий, і власний capture-обробник
  // на `Escape`/клік перехоплює обидва випадки ДО того, як вони дійдуть до
  // Save/Cancel чи до `Modal`. Клік ВСЕРЕДИНІ самого дропдауна (реальні
  // опції, позначені `role="listbox"`/`role="option"` — стандартний ARIA-
  // патерн Mantine `Combobox`, не деталь реалізації, що могла б змінитися
  // непомітно) не займаємо: Mantine продовжує сама вибирати опцію.
  const [rolesOpened, setRolesOpened] = useState(false);
  const rolesFieldRef = useRef<HTMLDivElement>(null);

  // ⛔ UI-аудит, lane 1: обраний перелік МІГ бути непорожнім і водночас не
  // давати жодного права — роль без прав `AsyncBoundary`'s «ролей немає»
  // (нижче) не бачить узагалі, бо з погляду мультиселекту роль ПРИЗНАЧЕНА.
  // Адмін, що зняв єдину змістовну роль і додав замість неї порожню, не
  // отримував жодного натяку, чому обліковий запис і далі нічого не бачить.
  const grantsNothing =
    selected.length > 0 &&
    selected.every((code) => {
      const role = roles.find((r) => r.code === code);
      return role !== undefined && role.permissions.length === 0 && role.dangerousPermissions.length === 0;
    });

  const assigned = useQuery({
    queryKey: ['user-roles', user?.id],
    queryFn: () => apiFetch<string[]>(`/api/v1/users/${user?.id ?? 0}/roles`),
    enabled: user !== null,
  });

  // ⚠ Форма наповнюється тим, що ВЖЕ призначено. Порожній перелік на
  // відкритті виглядав би як «ролей немає», і збереження мовчки відібрало б
  // усі права.
  useEffect(() => {
    if (assigned.data !== undefined) {
      setSelected(assigned.data);
    }
  }, [assigned.data]);

  useEffect(() => {
    setEmail(user?.email ?? '');
  }, [user]);

  /**
   * Збереження ролей і адреси — ДВА незалежні записи, і другий може відмовити
   * після першого.
   *
   * ⛔ Аудит 2026-09-16 §10.8: часткова відмова не називалася й не оновлювала
   * кеш. `onSuccess` не виконується, коли `mutationFn` кинув, тож при відмові
   * `PUT …/email` (а вона реальна: адреса може вже належати іншому обліковому
   * запису) кеш `['users']`/`['user-roles', id]` лишався зі СТАРИМИ ролями,
   * хоча сервер уже застосував нові, — а адмін бачив лише текст про пошту.
   * Найдорожчий наслідок — повторна спроба: він «виправляв» уже збережене,
   * дивлячись на застарілий перелік, а `PUT …/roles` тут ПОВНА заміна.
   *
   * ⚠ Тому: (1) інвалідація живе в `onSettled` — вона про те, що на сервері
   * ЩОСЬ змінилося, а не про те, чи все вдалося; (2) повідомлення про часткову
   * відмову називає обидва факти — що збережено і що ні, — і робить це
   * наявними рядками каталогу (новий рядок живе в сіді БД, поза цим пакетом);
   * (3) діалог НЕ закривається, бо закрити його означало б сховати те, що
   * лишилося незбереженим.
   */
  const save = useMutation({
    mutationFn: async () => {
      const result = await apiFetch<AffectedRolesResponse>(
        `/api/v1/users/${user?.id ?? 0}/roles`,
        { method: 'PUT', body: JSON.stringify({ roleCodes: selected }) },
      );

      try {
        await apiFetch(`/api/v1/users/${user?.id ?? 0}/email`, {
          method: 'PUT',
          body: JSON.stringify({ email: email.trim().length === 0 ? null : email.trim() }),
        });
      } catch (error) {
        throw new PartialAccessSaveError(result.roles, error);
      }

      return result;
    },

    // ⚠ Кеш перечитується в ОБОХ випадках: ролі вже змінено на сервері навіть
    // тоді, коли другий запис відмовив.
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      await queryClient.invalidateQueries({ queryKey: ['user-roles', user?.id] });
    },
    onSuccess: (result) => {
      onClose();
      showDone(t('security.accessSaved', { count: result.roles }));
    },
    onError: (error) => {
      if (error instanceof PartialAccessSaveError) {
        // ⚠ `statusWarning`, не `statusError`: частина роботи ЗРОБЛЕНА, і
        // червоне «не вдалося» тут читалося б як «нічого не сталося».
        notifications.show({
          color: 'statusWarning',
          message: `${t('security.accessSaved', { count: error.savedRoles })} · ${messageOf(error.cause)}`,
        });

        return;
      }

      showApiError(error);
    },
  });

  return (
    <Modal
      opened={user !== null}
      onClose={onClose}
      title={`${t('security.access')} · ${user?.userName ?? ''}`}
      // ⛔ lane 1: доки список ролей відкритий, `Escape` має закрити ЛИШЕ
      // його — не весь діалог (`Modal`'s власний Escape інакше спрацював
      // би раніше за capture-обробник нижче, дивись коментар вище).
      closeOnEscape={!rolesOpened}
    >
      {/* ⛔ lane 1: capture-обробники на ОБГОРТЦІ — щоб побачити клік/Escape
          РАНІШЕ за Save/Cancel і за сам `MultiSelect`. Клік усередині поля
          ролей чи в самому дропдауні (реальні опції, `role="listbox"`)
          проходить далі без змін; будь-який інший клік, поки список
          відкритий, лише закриває його й НЕ доходить до Save/Cancel — це і
          є фікс «клік по Save під час відкритого списку не повинен обрати
          роль», без другого кліка список уже не заважає. */}
      <div
        onClickCapture={(event) => {
          if (!rolesOpened) {
            return;
          }
          const target = event.target instanceof Element ? event.target : null;
          const insideDropdown = target?.closest('[role="listbox"]') != null;
          const insideField = rolesFieldRef.current?.contains(target) ?? false;
          if (insideDropdown || insideField) {
            return;
          }
          event.preventDefault();
          event.stopPropagation();
          setRolesOpened(false);
        }}
        onKeyDownCapture={(event) => {
          if (rolesOpened && event.key === 'Escape') {
            event.stopPropagation();
            setRolesOpened(false);
          }
        }}
      >
      {/*
       * ⛔ Невдалий запит ролей і справді порожній перелік раніше виглядали
       * ОДНАКОВО: обидва малювали ту саму жовту пересторогу «ролей немає», і
       * причину збою побачити не можна було нізвідки (`Q-254`). `AsyncBoundary`
       * малює помилку (`ErrorAlert`, `role="alert"`) окремо від порожнього
       * стану — той самий клас дефекту, що й `A7-04`.
       *
       * ⛔ УВАГА (аудит UI, lane 1): тут НАВМИСНО немає `isEmpty` —
       * `AsyncBoundary` без нього ніколи не підмінює дітей порожнім станом.
       * Раніше `isEmpty={() => selected.length === 0}` перевіряв ЖИВИЙ стан
       * форми, а не відповідь сервера: щойно адмін знімав останню роль-чіп
       * у `MultiSelect`, `AsyncBoundary` миттю ховав САМ `MultiSelect` і
       * малював натомість нередаговуваний текст — без жодного контролю,
       * щоб додати роль назад. Єдиний вихід був «Cancel», що відкидав і цю
       * зміну, і будь-яку іншу зроблену в тому самому сеансі (наприклад,
       * правку email). Попередження нижче (`security.noRolesTitle`/
       * `security.noRolesWarning`) лишається — тим самим текстом — але
       * ПОРЯД із контролем, а не ЗАМІСТЬ нього (той самий рисунок, що вже
       * working «Add grant» у `GrantsPanel.tsx`: кнопка стоїть ПОЗА
       * `AsyncBoundary`, тож порожній перелік грантів так само не ховає
       * спосіб додати перший).
       */}
      <AsyncBoundary<string[]>
        isPending={user !== null && assigned.isPending}
        error={assigned.error}
        data={user === null ? undefined : assigned.data}
        skeleton="form"
        onRetry={() => void assigned.refetch()}
      >
        {() => (
          <>
            <div ref={rolesFieldRef}>
              <MultiSelect
                mt="md"
                label={t('security.roles')}
                description={t('security.rolesHint')}
                data={roles.map((r) => r.code)}
                value={selected}
                onChange={setSelected}
                searchable
                dropdownOpened={rolesOpened}
                onDropdownOpen={() => setRolesOpened(true)}
                onDropdownClose={() => setRolesOpened(false)}
                rightSectionPointerEvents="all"
                rightSection={
                  <Combobox.Chevron
                    size="sm"
                    style={{ cursor: 'pointer' }}
                    data-testid="roles-dropdown-toggle"
                    // ⛔ lane 1: стоковий `rightSection`-шеврон не реагує
                    // на клік узагалі (`rightSectionPointerEvents` за
                    // умовчанням "none", коли `clearable` вимкнено) — тож
                    // «клік по власному шеврону» раніше не робив НІЧОГО.
                    // `stopPropagation` тут не дає кліку дійти до
                    // обгортки `PillsInput`, чий власний `onClick` для
                    // `searchable` ЗАВЖДИ відкриває (ніколи не закриває).
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={(event) => {
                      event.stopPropagation();
                      setRolesOpened((opened) => !opened);
                    }}
                  />
                }
              />
            </div>

            {/*
             * ⛔ НЕ `<Alert>`: Mantine ставить йому `role="alert"` за
             * умовчанням, а це саме той стан, від якого `Q-254` навмисно
             * відрізняв «дійсно порожньо» (`AsyncBoundary.tsx`'s власний
             * коментар про `NoPermissionState`/`EmptyState` — обидва
             * СВІДОМО без `role="alert"`, щоб код помилки з кореляцією
             * (`ErrorAlert`) лишався єдиним, що читач екрана чує як
             * тривогу).
             */}
            {selected.length === 0 && (
              <Stack gap="xs" mt="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('security.noRolesTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('security.noRolesWarning')}
                </Text>
              </Stack>
            )}

            {/* ⛔ UI-аудит, lane 1: роль(і) призначені, але жодна не несе
                жодного права — та сама пастка, що й «ролей немає», лише
                непомітна для самого мультиселекту. */}
            {grantsNothing && (
              <Stack gap="xs" mt="xs">
                <Text size="sm" fw={600} c="statusWarning">
                  {t('security.rolesGrantNothingTitle')}
                </Text>
                <Text size="sm" c="dimmed">
                  {t('security.rolesGrantNothingWarning')}
                </Text>
              </Stack>
            )}
          </>
        )}
      </AsyncBoundary>

      <TextInput
        mt="sm"
        label={t('security.email')}
        description={t('security.emailHint')}
        value={email}
        onChange={(event) => setEmail(event.currentTarget.value)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        {/* ⛔ Заблоковано, поки `assigned` не підтвердив саме поточні ролі
            користувача: PUT тут — повна заміна (`roleCodes: []` знімає
            ВСІ ролі), і невдалий запит лишає `selected` порожнім — без
            цього збереження мовчки забрало б усі права після звичайного
            збою мережі. */}
        <Button
          loading={save.isPending}
          disabled={assigned.isPending || Boolean(assigned.error)}
          onClick={() => save.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
      </div>
    </Modal>
  );
}

/**
 * Ролі збережено, адресу — ні (аудит §10.8).
 *
 * ⛔ Окремий тип, а не прапорець у повідомленні: обробник відмови мусить
 * РОЗРІЗНЯТИ «нічого не сталося» (упав перший запис) і «половина застосована»
 * — від цього залежить і колір, і те, чи закривати діалог. Розрізняти це за
 * текстом означало б порівнювати рядки каталогу.
 */
class PartialAccessSaveError extends Error {
  /** Скільки ролей сервер справді застосував до того, як відмовив другий запис. */
  readonly savedRoles: number;

  /** Відмова ДРУГОГО запису — її текст і показуємо людині. */
  override readonly cause: unknown;

  constructor(savedRoles: number, cause: unknown) {
    super('partial access save');
    this.name = 'PartialAccessSaveError';
    this.savedRoles = savedRoles;
    this.cause = cause;
  }
}

/**
 * Текст відмови так, як його назвав сервер.
 *
 * ⚠ Той самий вибір, що в `showApiError` (`notify.ts`): `error.message` для
 * `EcrApiError` — це `detail ?? title`, тобто змістовна причина, а не «щось
 * пішло не так».
 */
function messageOf(error: unknown): string {
  return error instanceof EcrApiError ? error.message : String(error);
}
