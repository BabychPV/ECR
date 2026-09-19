import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryHistoryEntryDto } from '@/api/types';
import { formatDateTime } from '@/shared/format';
import { RegistryHistory } from '@/features/registries/RegistryConstructor';

/**
 * `UI-07`: момент зміни опису довідника — читабельний на екрані, точний у
 * розмітці.
 *
 * ⛔ Навіщо окремо від `shared/ui/__tests__/Timestamp.test.tsx`. Той доводить
 * поведінку КОМПОНЕНТА і лишиться зеленим, якщо жодна сторінка його не
 * викличе — рівно в цьому стані модуль `shared/format` і прожив увесь час
 * (написаний, протестований, без жодного споживача). Тут перевіряється інше
 * твердження: ця колонка цього екрана показує момент через набір.
 *
 * ⛔ Дві половини, і поодинці кожна порожня:
 *   • сама лише рівність із `formatDateTime(...)` лишилася б зеленою на
 *     форматувальнику, що повертає вхід — тобто на стані ДО цієї зміни;
 *   • саме лише «текст не дорівнює входу» лишилося б зеленим на чому завгодно,
 *     що зіпсувало значення.
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а НЕ пишеться літералом:
 * літерал був би перевіркою версії ICU у Node і червонів би від оновлення
 * середовища, нічого не зламавши.
 *
 * ⚠ Компонент, а не сторінка — з тієї самої причини, що й у сусідньому
 * `constructor.test.tsx`: рендер сторінки з `Tabs` і запитами в jsdom іде
 * хвилинами, а предмет тут — одна клітинка.
 */

/** Момент зміни в UTC (`RegistryHistoryEntryDto`, `Format: date-time`). */
const ChangedAt = '2026-05-01T10:00:00Z';

const History: RegistryHistoryEntryDto[] = [
  {
    changedAt: ChangedAt,
    entityType: 'cfg.RegistryDef',
    operation: 'SaveDefinition',
    oldJson: '{}',
    newJson: '{}',
    changeReason: 'ліміт перенесено з Configuration!J3',
    changedByUserId: 9,
  },
];

describe('RegistryHistory: момент зміни опису довідника', () => {
  it('читабельний на екрані, точний у розмітці — і година лишається на місці', () => {
    render(
      <MantineProvider>
        <RegistryHistory entries={History} userNames={new Map()} usersResolved={false} />
      </MantineProvider>,
    );

    const row = screen.getByText('SaveDefinition').closest('tr');
    expect(row).not.toBeNull();

    const node = within(row!).getByText(formatDateTime(ChangedAt));

    // ⛔ Перша половина: видимий текст НЕ дорівнює сирому входу. Без неї набір
    // лишився б зеленим і на `{entry.changedAt}` — тобто рівно на стані, який
    // ця зміна усуває.
    expect(node.textContent).not.toBe(ChangedAt);

    /*
     * ⛔ Друга половина: точне значення нікуди не діло́ся. Журнал змін — доказ,
     * і доказ мусить лишатися однозначним та придатним до копіювання; тому
     * воно живе в `dateTime`, а не в тому, що бачить око.
     */
    expect(node.tagName).toBe('TIME');
    expect(node.getAttribute('datetime')).toBe(ChangedAt);
    expect(node.getAttribute('title')).toBe(ChangedAt);
  });

  it('година показана, секунди — ні: `changedAt` момент, але не телеметрія', () => {
    render(
      <MantineProvider>
        <RegistryHistory entries={History} userNames={new Map()} usersResolved={false} />
      </MantineProvider>,
    );

    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node, 'момент зміни не намальовано елементом <time>').not.toBeNull();

    /*
     * ⛔ БЕЗ `dateOnly`. Питання, на яке дивляться в цій колонці, — «яка з
     * двох правок опису була пізніша»; календарний день на нього не
     * відповідає, бо правки лягають пачками в один день. Поставте `dateOnly`
     * у `RegistryConstructor.tsx` — година зникне і цей `expect` почервоніє.
     */
    expect(node?.textContent).toMatch(/\d{1,2}:\d{2}/);

    /*
     * ⛔ І БЕЗ `precise`. Секунда тут нічого не вирішує: дві правки в одну
     * хвилину відрізняються операцією і причиною, а не секундою, і зайва
     * точність на екрані читається як важливість (`TimestampProps.precise`).
     * Точне значення й так ціле — воно в `dateTime`, перевірене вище.
     *
     * ⚠ Порівняння з `formatDateTime` БЕЗ опцій, а не регексп на три числа:
     * так твердження не залежить від того, якими роздільниками ICU складає
     * час у поточній версії Node.
     */
    expect(node?.textContent).toBe(formatDateTime(ChangedAt));
    expect(node?.textContent).not.toBe(
      formatDateTime(ChangedAt, { dateStyle: 'medium', timeStyle: 'medium' }),
    );
  });
});
