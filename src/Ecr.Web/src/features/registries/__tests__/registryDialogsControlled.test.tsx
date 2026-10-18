import { createRef, type ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import RegistryExportMenu from '@/features/registries/export/RegistryExportMenu';
import { RegistryImportPanel, type RegistryImportHandle } from '@/features/registries/RegistryImportPanel';
import { SourceKindSwitch } from '@/features/registries/SourceKindSwitch';
import { testTheme } from '@/test/render';

/**
 * Керований режим діалогів довідника (REG-UI 3/3): `opened`/`onClose` замість власної кнопки.
 * Поведінка за замовчуванням (кнопка + діалог разом) покрита наявними тестами
 * `RegistryExportMenu.test`, `RegistryImportPanel*.test`, `RegistriesList.test`.
 *
 * ⚠ Каталог рядків не завантажений — підписи приходять ключами в `⟦…⟧`.
 */

const Registries: RegistryDefDto[] = [
  {
    id: 1,
    code: 'UNITS',
    nameL10n: { values: { en: 'Units' } },
    fields: [],
    isTemporal: false,
    isHierarchical: false,
    sourceKind: 'Local',
  },
];

function wrap(node: ReactElement): ReactElement {
  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient()}>{node}</QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('SourceKindSwitch: керований режим', () => {
  it('без opened — кнопка й діалог разом, як було', async () => {
    render(wrap(<SourceKindSwitch registries={Registries} />));

    fireEvent.click(screen.getByRole('button', { name: /registries\.sourceSwitch/ }));
    expect(await screen.findByRole('dialog')).toBeDefined();
  });

  it('з opened — власної кнопки немає; діалог відкритий; «Cancel» повідомляє onClose', () => {
    const onClose = vi.fn();
    render(wrap(<SourceKindSwitch registries={Registries} opened onClose={onClose} />));

    expect(screen.getByRole('dialog')).toBeDefined();
    // Єдина кнопка з таким іменем — підтвердження всередині діалогу, не кнопка-відкривач.
    expect(screen.getAllByRole('button', { name: /registries\.sourceSwitch/ }).length).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: /common\.cancel/ }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('opened = false — діалогу немає', () => {
    render(wrap(<SourceKindSwitch registries={Registries} opened={false} onClose={vi.fn()} />));

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('RegistryExportMenu: керований режим', () => {
  it('з opened — діалог з CSV/XLSX без кнопки-відкривача', () => {
    render(wrap(<RegistryExportMenu registryCode="UNITS" asOf={null} opened onClose={vi.fn()} />));

    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('registries.export.csv');
    expect(dialog.textContent).toContain('registries.export.xlsx');
  });

  it('закриття діалогу повідомляє onClose', () => {
    const onClose = vi.fn();
    render(wrap(<RegistryExportMenu registryCode="UNITS" asOf={null} opened onClose={onClose} />));

    fireEvent.click(document.querySelector('.mantine-Modal-close')!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('без opened — кнопка-відкривач, як було', () => {
    render(wrap(<RegistryExportMenu registryCode="UNITS" asOf={null} />));

    expect(screen.getByRole('button', { name: /registries\.export\.button/ })).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('RegistryImportPanel: керований режим', () => {
  it('hideButton — кнопки немає; pick() відкриває вибір файлу', () => {
    const handle = createRef<RegistryImportHandle>();
    const click = vi.spyOn(HTMLInputElement.prototype, 'click');
    render(wrap(<RegistryImportPanel registryCode="UNITS" hideButton handleRef={handle} />));

    expect(screen.queryByRole('button')).toBeNull();

    handle.current?.pick();
    expect(click).toHaveBeenCalledTimes(1);
  });

  it('вимкнений імпорт: pick() нічого не відкриває', () => {
    const handle = createRef<RegistryImportHandle>();
    const click = vi.spyOn(HTMLInputElement.prototype, 'click');
    render(wrap(<RegistryImportPanel registryCode="UNITS" disabled hideButton handleRef={handle} />));

    handle.current?.pick();
    expect(click).not.toHaveBeenCalled();
  });

  it('без hideButton — кнопка як була', () => {
    render(wrap(<RegistryImportPanel registryCode="UNITS" />));

    expect(screen.getByRole('button', { name: /registry\.import\.pick/ })).toBeDefined();
  });
});
