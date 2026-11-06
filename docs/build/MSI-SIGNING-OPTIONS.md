# Підпис `Ecr.msi`: три варіанти

Стан на 2026-10-01. Рішення людини: «поки самопідписаний, або запропонуй інше» — тут три варіанти
й рекомендація.

✎ **2026-11-06, рішення людини (квіз, Q-6): «A+B зараз, потім C».** Видача: MSI підписується
самопідписаним сертифікатом (варіант A), а SHA-256 підписаного файла йде в release notes (варіант B);
корпоративний ЦС (C) — наступним кроком, коли замовник дасть шаблон Code Signing. Порядок видачі —
розділ [«Видача: A+B»](#видача-ab) нижче.

## Що є в репозиторії

- `tools/build-msi.ps1` збирає MSI (WiX 5, `installer/Ecr.Installer`) і друкує `SHA-256` у консоль.
- `tools/build-installer.ps1` збирає `Ecr-Setup-<версія>.exe` і теж друкує `SHA-256`.
- `tools/sign-msi.ps1 -MsiPath <файл> -SubjectName <ім'я з сертифіката>` — окремий крок:
  `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n <ім'я>`. Потрібні `signtool`
  у `PATH`, сертифікат із закритим ключем у сховищі й доступ до сервера міток часу в інтернеті.
  У `sign-msi.ps1` ім'я підписанта — параметр, жодного сертифіката в репозиторії немає.
- CI (`.github/workflows/ci.yml`, артефакт `ecr-msi`) збирає MSI **без підпису**; поруч лежить
  `Ecr.msi.sha256` (еталон для `verify-msi.ps1`). Підпис — крок видачі на машині реліз-менеджера
  (нижче), не CI: ключ не виходить із цієї машини. `Ecr-Setup-*.exe` не підписується жодним скриптом.
- ✎ 06.11: `tools/new-codesign-cert.ps1` — створення самопідписаного сертифіката підпису й експорт `.cer`;
  `tools/sign-msi.ps1 -Thumbprint` — `signtool sign /sha1 <відбиток> /fd SHA256` (без `signtool` у `PATH` —
  `Set-AuthenticodeSignature -HashAlgorithm SHA256`) і перезапис `<msi>.sha256` хешем підписаного файла.
- Інших засобів підпису (Azure/хмарні сервіси, HSM) у репозиторії немає — тут не пропонуються.

## Варіант A: самопідписаний сертифікат + довіра через GPO

Кроки (на машині реліз-менеджера, PowerShell від імені користувача, що підписує):

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ECR Test Signing' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
    -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
Export-Certificate -Cert $cert -FilePath .\EcrTestSigning.cer      # лише відкрита частина
.\tools\sign-msi.ps1 -MsiPath .\Ecr.msi -SubjectName 'ECR Test Signing'
Get-AuthenticodeSignature .\Ecr.msi | Format-List Status, SignerCertificate
```

Розповсюдження довіри (адміністратор домену замовника): `.cer` імпортується груповою політикою в
`Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies` —
і в **Trusted Publishers**, і в **Trusted Root Certification Authorities** (самопідписаний сертифікат
є власним коренем). Локально для перевірки: `Import-Certificate -FilePath .\EcrTestSigning.cer
-CertStoreLocation Cert:\LocalMachine\TrustedPublisher` (і `...\Root`).

- **Що побачить користувач:** на машині з імпортованим `.cer` — підписант відомий, «Unknown publisher»
  немає. На машині без імпорту — «Unknown publisher» / попередження SmartScreen або UAC. Чи знімає
  підпис самопідписаним сертифікатом попередження SmartScreen для файлу з позначкою «з Інтернету» —
  **не перевірено** в середовищі замовника; не обіцяти.
- **Ризики:** кореневий довірений сертифікат у всьому домені — широка довіра; його закритий ключ має
  жити лише на машині підпису (`NonExportable`, обмежений доступ). Через 2 роки сертифікат спливає —
  потрібні перевипуск і повторний GPO. Самопідпис не підтверджує особу видавця для сторонніх.
- **Ціна:** грошей немає; час адміністратора домену (GPO) і реліз-менеджера.

## Варіант B: без підпису, SHA-256 хеш і перевірка

Реліз-менеджер публікує поруч з артефактом хеш (з журналу `build-msi.ps1`/`build-installer.ps1` або
`Get-FileHash`) і записує його в `docs/build/TESTER-HANDOVER.md` (поле «Хеш коміту і `Version` збірки»).
Тестувальник перевіряє:

```powershell
(Get-FileHash .\Ecr.msi -Algorithm SHA256).Hash.ToLower()   # має збігтися з опублікованим
```

- **Що побачить користувач:** «Unknown publisher» / попередження SmartScreen при запуску (файл не
  підписано); `msiexec` працює, якщо політика ПЗ не вимагає підпису.
- **Ризики:** хеш захищає від пошкодження й підміни лише тоді, коли самого хеша отримано **іншим
  каналом**, ніж файл (інакше підмінити можна обидва). Особу видавця не доводить. Політика
  замовника «лише підписане ПЗ» цей варіант блокує.
- **Ціна:** нуль; працює негайно, жодних змін у скриптах (✎ CI уже кладе `Ecr.msi.sha256` в артефакт).

## Варіант C: підпис корпоративним ЦС замовника (AD CS)

Кроки: замовник публікує шаблон **Code Signing** у своєму AD CS і дає право `Enroll` обліковому
запису реліз-менеджера; видача — `certmgr.msc` → «Request New Certificate» (або `certreq`);
підпис — той самий `tools/sign-msi.ps1` (`signtool` з міткою часу `/tr … /td SHA256`); перевірка —
`Get-AuthenticodeSignature`. Корінь корпоративного ЦС на машинах домену вже довірений; у
**Trusted Publishers** достатньо імпортувати сам сертифікат підписанта (GPO).

- **Що побачить користувач:** на машинах домену — відомий підписант без попередження «Unknown
  publisher»; поза доменом — попередження (корінь не довірений).
- **Ризики:** залежність від замовника (шаблон, права, строки видачі й відкликання); закритий ключ
  потребує захисту (токен/окрема машина — політика замовника); мітка часу з `timestamp.digicert.com`
  потребує виходу в інтернет з машини підпису (інакше задати внутрішній сервер міток часу).
- **Ціна:** внутрішній ЦС — без плати за сертифікат, час адміністраторів замовника. Комерційний
  сертифікат (поза корпоративним ЦС) — за прайсом ЦС; у цьому документі **не оцінювалось**.

## Рекомендація

| Контур | Вибір | Чому |
|---|---|---|
| Тест (зараз) | **B** — хеш, негайно | нуль залежностей, закриває «чи той файл»; підпис для тесту не блокує |
| Тест, якщо замовник вимагає установку без попереджень | **A** | працює без ЦС замовника; довіра через GPO |
| Прод | **C** | довіра з корпоративного ЦС, мітка часу, керований життєвий цикл сертифіката |

Відкриті питання до людини: чи є в замовника AD CS із шаблоном Code Signing; чи діє політика «лише
підписане ПЗ»; чи дозволений вихід машини підпису до серверів міток часу.

## Видача: A+B (рішення 06.11) {#видача-ab}

**Один раз** (і на кожен перевипуск) — сертифікат на машині реліз-менеджера:

```powershell
.\tools\new-codesign-cert.ps1 -CerPath .\EcrCodeSigning.cer -WhatIf   # план
.\tools\new-codesign-cert.ps1 -CerPath .\EcrCodeSigning.cer           # відбиток — у вивід
```

Ключ і файли:

- закритий ключ типово **неекспортований** і живе лише в `Cert:\CurrentUser\My` реліз-менеджера: не
  кладеться в репозиторій, CI чи спільні теки; втрата машини = новий сертифікат і новий `.cer`;
- резервна копія ключа — лише свідомо: `-BackupPfxPath <файл> -PfxPassword (Read-Host -AsSecureString)`
  (ключ тоді експортований); PFX зберігати офлайн, окремо від пароля;
- розповсюджується лише `.cer` (відкрита частина) — його можна класти поруч із пакетом видачі;
- строк — 2 роки (`-Years`); за місяць до кінця — перевипуск і повторне розповсюдження `.cer`;
- щоб `verify-msi.ps1 -TrustedThumbprint` на цій самій машині дав `PASS`, а не `NotTrusted`, один раз
  довірити власний `.cer` користувачу: `Import-Certificate -FilePath .\EcrCodeSigning.cer
  -CertStoreLocation Cert:\CurrentUser\Root` (Windows попросить підтвердження).

**На кожну видачу** (`Ecr.msi` — артефакт `ecr-msi` або `tools/build-msi.ps1`):

```powershell
.\tools\sign-msi.ps1   -MsiPath .\Ecr.msi -Thumbprint <відбиток>      # signtool /fd SHA256; переписує Ecr.msi.sha256
.\tools\verify-msi.ps1 -MsiPath .\Ecr.msi -IntegrityOnly -RequireSignature -TrustedThumbprint <відбиток>
Get-Content .\Ecr.msi.sha256                                            # цей хеш — у release notes
```

У release notes (`docs/release-notes/rc-<дата>.md`) — рядок `SHA256 = <хеш підписаного файла>` і
відбиток сертифіката підпису. ⚠ Хеш із CI-артефакту після підпису вже НЕ збігається: публікується хеш
із `sign-msi.ps1`. Хеш передається іншим каналом, ніж сам файл (варіант B).

**Довіра на машинах замовника** (адміністратор домену; один раз на сертифікат):

- GPO: `Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`
  → імпорт `.cer` у **Trusted Publishers** і **Trusted Root Certification Authorities**;
- або вручну (від адміністратора) на окремій машині:

```bat
certutil -addstore TrustedPublisher EcrCodeSigning.cer
certutil -addstore Root EcrCodeSigning.cer
```

Перевірка на машині встановлення — та сама `verify-msi.ps1 -IntegrityOnly -RequireSignature
-TrustedThumbprint <відбиток>` (або `Get-AuthenticodeSignature .\Ecr.msi` → `Status = Valid` після
імпорту `.cer`).
