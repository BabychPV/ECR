/* =====================================================================
   screens-work.js — вхід, зміна пароля, перелік документів, «мої групи», службові сторінки.
   Маршрути (title · group · icon · order):
     /login              Sign in            — · chrome:false · шанує ?return=<шлях>
     /change-password    Change password    — · chrome:false (оболонка повертається, якщо зміна добровільна)
     /                   Documents          Work · file · 1
     /my-groups          My groups          Work · users · 2
     /404  /403  /error  службові           — (без group)
   ?dialog= :  «/»            create-document · create-document-template · create-document-exists · create-document-sheets ·
                              create-document-review · result-created ·
                              create-denied · new-version
               «/my-groups»   sign-out-unsaved
               «/error»       reload-unsaved
   ?panel=  :  «/»            <ключ документа> (Quick look)
   Власні параметри:
     /login            case=local|capslock|wrong|locked|expired|not-registered|i18n|signed-out · lang=en|ru|kk · return=<шлях> · unsaved=<n>
                       (state=error ≡ case=wrong, state=loading ≡ «Signing in…»)
     /change-password  reason=temporary|expired (примусова, без оболонки) · account=local · case=invalid · return=<шлях>
     /                 period=2026-MM · view=board · q= · project= · status= · mine=1 · stat=approved|issues|late · reset=1 · case=no-access
     /my-groups        open=<домен прав, напр. Document> — яку секцію «What you can do» розгорнути
     /403              need=<код права>
     /error            unsaved=<n> · case=new-version
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, D = E.data, F = E.fmt, icon = E.icon;

  E.css('work', [
    /* сторінки поза оболонкою */
    '.work-auth{flex:1;min-height:0;overflow:auto;display:grid;place-items:center;padding:var(--s5) var(--s4);background:var(--ground)}',
    '.work-auth-col{width:100%;max-width:400px;display:grid;gap:var(--s4)}',
    '.work-brand{display:flex;align-items:center;gap:var(--s3)}',
    '.work-mark{width:40px;height:40px;border-radius:var(--r2);background:var(--accent);color:var(--on-accent);display:grid;place-items:center;font:600 var(--fs-h2)/1 var(--sans);flex:none}',
    '.work-brand h1{font-size:var(--fs-h3);font-weight:600;line-height:1.2}',
    '.work-brand p{color:var(--muted)}',
    '.work-card{display:grid;gap:var(--s3);padding:var(--s5)}',
    '.work-card h2{font-size:var(--fs-md);font-weight:600}',
    '.work-wide{width:100%;justify-content:center}',
    '.work-center{text-align:center}',
    '.work-form{display:grid;gap:var(--s3)}',
    '.work-form-sep{border-top:1px solid var(--border);padding-top:var(--s3)}',
    '.work-pw{position:relative;display:flex;min-width:0}',
    '.work-pw .input{padding-right:calc(var(--ctl) + var(--s1))}',
    '.work-pw .icon-btn{position:absolute;right:0;top:0;width:var(--ctl);height:var(--ctl)}',
    '.work-foot{display:flex;align-items:center;justify-content:space-between;gap:var(--s2);flex-wrap:wrap;color:var(--muted);font-size:var(--fs-xs)}',
    '.work-foot .select{width:auto}',
    '.work-reqs{display:grid;gap:var(--s1);margin:0;padding:0;list-style:none}',
    '.work-reqs li{display:flex;align-items:center;gap:var(--s2);color:var(--muted);font-size:var(--fs-xs)}',
    '.work-reqs li.ok{color:var(--text)}',
    '.work-reqs li svg{flex:none}',
    '.work-pwpanel{display:grid;gap:var(--s3);padding:var(--s5);max-width:480px}',
    /* перелік документів */
    '.work-fill{flex:1;min-height:0;min-width:0;display:flex;flex-direction:column;gap:var(--s3)}',
    '.work-late{display:inline-flex;align-items:center;gap:var(--s1);color:var(--warning)}',
    '.work-board{flex:1;min-height:0;overflow:auto;display:grid;grid-template-columns:repeat(4,minmax(220px,1fr));gap:var(--s3);align-items:start}',
    '.work-board-full{grid-column:1/-1}',
    '.work-col{display:grid;gap:var(--s2);align-content:start;min-width:0}',
    '.work-col-h{display:flex;align-items:center;gap:var(--s2);height:var(--row);color:var(--muted);font-weight:500;border-bottom:1px solid var(--border)}',
    '.work-col-empty{color:var(--faint);font-size:var(--fs-xs);padding:var(--s2) 0}',
    '.work-bcard{display:grid;gap:var(--s2);padding:var(--s3);min-width:0;cursor:pointer}',
    '.work-bcard:hover{border-color:var(--border-strong);background:var(--hover)}',
    '.work-bcard .work-bmeta{display:flex;align-items:center;justify-content:space-between;gap:var(--s2);color:var(--muted);font-size:var(--fs-xs);min-width:0}',
    /* швидкий перегляд */
    '.work-drawer{min-height:0}',
    '.work-list.work-perms li{grid-template-columns:minmax(0,1fr) auto auto;padding:var(--s1) 0}',
    '.work-tbl{flex:none}',
    '.work-list{display:grid;margin:0;padding:0;list-style:none}',
    '.work-list li{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:0 var(--s3);align-items:center;padding:var(--s2) 0;border-bottom:1px solid var(--border)}',
    '.work-list li:last-child{border-bottom:0}',
    '.work-list small{display:block;color:var(--muted);font-size:var(--fs-xs)}',
    '.work-list a{font-weight:500}',
    /* майстер створення */
    '.work-opts{display:grid;gap:var(--s2)}',
    '.work-opt{display:flex;align-items:flex-start;gap:var(--s2);padding:var(--s2) var(--s3);border:1px solid var(--border);border-radius:var(--r2);cursor:pointer}',
    '.work-opt:hover{border-color:var(--border-strong)}',
    '.work-opt:has(input:checked){border-color:var(--accent);background:var(--accent-soft)}',
    '.work-opt:has(input:focus-visible){outline:2px solid var(--focus);outline-offset:1px}',
    '.work-opt input{margin:2px 0 0;accent-color:var(--accent);flex:none}',
    '.work-opt small{display:block;color:var(--muted);font-size:var(--fs-xs)}',
    '.work-opt small.work-opt-note{display:flex;align-items:center;gap:var(--s1)}',
    /* службові сторінки */
    '.work-state{max-width:560px}',
    '.work-state-h{font-size:var(--fs-md);font-weight:600}',
    '.work-state-wide{width:100%;text-align:left}',
    '.work-sections{display:grid;gap:var(--s5);max-width:960px;width:100%}',
    '@media (max-width:640px){.work-board{grid-template-columns:1fr}.work-card{padding:var(--s4)}.work-pwpanel{padding:var(--s4)}}'
  ].join('\n'));

  /* ================= спільні помічники ================= */
  function project(id) { return D.projects.filter(function (p) { return p.id === id; })[0]; }
  function roleName(id) { var r = D.roles.filter(function (x) { return x.id === id; })[0]; return r ? r.name : id; }
  function permission(code) { var out = null; D.permissions.forEach(function (g) { g.items.forEach(function (p) { if (p.code === code) out = { code: p.code, label: p.label, domain: g.domain, dangerous: p.dangerous }; }); }); return out; }
  function productVersion() { return 'ECR Web ' + D.health.application[0][1].split(' · ').slice(0, 2).join(' · '); }
  function nextVersion() { var v = D.health.application[0][1].split(' · ')[0].split('.'); v[2] = String(+v[2] + 1); return v.join('.'); }
  function admins() { return D.users.filter(function (u) { return u.state === 'Active' && u.roles.some(function (r) { return D.granted(r, 'Security.ManageGrants'); }); }); }
  function adminNames() { var a = admins().map(function (u) { return u.name; }); return a.length ? a.join(', ') : 'a system administrator'; }
  function copy(text, done) { try { navigator.clipboard.writeText(text); } catch (e) { } E.Toast(done || 'Copied'); }
  /* Коротка таблиця лише для читання (без сортування й «Showing 1 of 1») — на класах набору div.table-wrap > table.data */
  function simpleTable(id, caption, heads, rows) {
    return h('div', { class: 'table-wrap work-tbl', id: id }, h('table', { class: 'data' }, h('caption', { class: 'sr' }, caption),
      h('thead', h('tr', heads.map(function (c) { return h('th', { scope: 'col', class: c.num ? 'num' : null }, c.label); }))),
      h('tbody', rows.map(function (cells) { return h('tr', { class: 'tall' }, cells.map(function (c, i) { return h('td', { class: heads[i].num ? 'num' : null }, c == null ? h('span', { class: 'faint' }, '—') : c); })); }))));
  }
  function monoText(s) { return h('span', { class: 'mono' }, s); }   /* KeyValue {mono:true} робить моноширинною і підказку — тому моно лише на значенні */
  function openPalette() { var b = document.getElementById('btn-cmdk'); if (b) b.click(); }

  /* Поле пароля з показом і підказкою Caps Lock. У наборі немає — зібрано з E.Field + E.Button. */
  function passwordField(o) {
    var inp = h('input', { class: 'input', id: o.id, name: o.id, type: 'password', autocomplete: o.autocomplete || 'off', required: true });
    var show = E.Button({ iconOnly: true, icon: 'eye', label: o.showLabel || 'Show password', id: o.id + '-show', pressed: false, onClick: function () {
      var on = inp.type === 'password'; inp.type = on ? 'text' : 'password'; show.textContent = ''; show.appendChild(icon(on ? 'eyeOff' : 'eye'));
      var l = on ? (o.hideLabel || 'Hide password') : (o.showLabel || 'Show password'); show.setAttribute('aria-label', l); show.title = l; show.setAttribute('aria-pressed', String(on)); inp.focus(); } });
    var control = h('div', { class: 'work-pw' }, inp, show); control.input = inp;
    var caps = h('div', { class: 'hint warn', id: o.id + '-caps', role: 'status', hidden: true }, icon('alert', 14), o.capsLabel || 'Caps Lock is on');
    var f = E.Field({ id: o.id, label: o.label, hint: o.hint, required: o.required, control: control });
    f.insertBefore(caps, control.nextSibling);
    function capsCheck(e) { if (e.getModifierState) caps.hidden = !e.getModifierState('CapsLock'); }
    inp.addEventListener('keydown', capsCheck); inp.addEventListener('keyup', capsCheck); inp.addEventListener('blur', function () { caps.hidden = true; });
    if (o.onInput) inp.addEventListener('input', function () { o.onInput(inp.value); });
    f.showCaps = function (v) { caps.hidden = !v; };
    return f;
  }
  function brand(tagline) {
    return h('div', { class: 'work-brand' }, h('span', { class: 'work-mark', 'aria-hidden': 'true' }, 'E'),
      h('div', h('h1', { tabindex: '-1', 'data-page-title': '1', id: 'work-brand-h1' }, 'ECR Web'), h('p', tagline)));
  }
  /* Службова сторінка на класах набору .state-fill > .state (EmptyState не вміщує код права, перелік людей і банер). */
  function statePage(o) {
    return h('div', { class: 'page' }, h('div', { class: 'state-fill' }, h('div', { class: 'state work-state' + (o.err ? ' err' : '') },
      h('div', { class: 'ico' }, icon(o.icon, 20)), h('h1', { class: 'work-state-h', tabindex: '-1', 'data-page-title': '1', id: o.id + '-h1' }, o.title), o.body)));
  }

  /* ================= модель доступу (похідна від ECR.data) ================= */
  function groupRole(g) { return /approver/i.test(g.name) ? 'Approver' : /auditor/i.test(g.name) ? 'Auditor' : /data entry/i.test(g.name) ? 'DataEntry' : null; }
  function adName(g) { return 'CORP\\ECR-' + g.name.replace(/\s*—\s*/g, '-').replace(/\s+/g, '-'); }
  function access(u) {
    var groups = D.groups.filter(function (g) { return g.members.indexOf(u.id) >= 0; });
    var roles = u.roles.map(function (rid) { var r = D.roles.filter(function (x) { return x.id === rid; })[0], g = groups.filter(function (x) { return groupRole(x) === rid; });
      var n = 0; D.permissions.forEach(function (dm) { dm.items.forEach(function (p) { if (D.granted(rid, p.code)) n++; }); }); return { id: rid, role: r, groups: g, count: n }; });
    var LEVEL = { DataEntry: 'Enter data', Approver: 'Approve sheets', Auditor: 'Read only' };
    var projects = D.projects.map(function (p) {
      var full = u.roles.filter(function (r) { return (D.grants[r] || []).indexOf('*') >= 0; })[0];
      if (full) return { id: p.id, name: p.name, level: 'Full access', rank: 0, source: 'Role ' + roleName(full), edit: true };
      var g = groups.filter(function (x) { return x.projects.indexOf('*') >= 0 || x.projects.indexOf(p.id) >= 0; })[0];
      if (g) { var lv = LEVEL[groupRole(g)] || 'Read only'; return { id: p.id, name: p.name, level: lv, rank: lv === 'Enter data' ? 1 : lv === 'Approve sheets' ? 2 : 3, source: g.name, sourceCode: adName(g), edit: lv === 'Enter data' }; }
      var own = D.documents.filter(function (d) { return d.project === p.id && d.owner === u.name; })[0];
      if (own && u.roles.some(function (r) { return D.granted(r, 'Document.Edit'); })) return { id: p.id, name: p.name, level: 'Enter data', rank: 1, source: 'Assigned as owner of ' + own.id, edit: true };
      var view = u.roles.filter(function (r) { return D.granted(r, 'Document.View'); })[0];
      return view ? { id: p.id, name: p.name, level: 'View only', rank: 4, source: 'Role ' + roleName(view), edit: false } : { id: p.id, name: p.name, level: 'No access', rank: 5, source: null, edit: false };
    }).sort(function (a, b) { return a.rank - b.rank; });
    var domains = D.permissions.map(function (dm) { var items = dm.items.map(function (p) { var from = u.roles.filter(function (r) { return D.granted(r, p.code); }); return from.length ? { code: p.code, label: p.label, from: from } : null; }).filter(Boolean); return { domain: dm.domain, total: dm.items.length, items: items }; });
    return { groups: groups, roles: roles, projects: projects, domains: domains };
  }

  /* ================= /login ================= */
  var LANGS = [{ value: 'en', label: 'English' }, { value: 'ru', label: 'Русский' }, { value: 'kk', label: 'Қазақша' }];
  var TXT = {
    en: { tagline: 'Structured environmental reporting for industrial sites.', windows: 'Sign in with Windows', windowsHint: 'Uses the Windows account you are signed in to on this computer.', local: 'Use a local account', user: 'User name', password: 'Password', signIn: 'Sign in', signingIn: 'Signing in…',
      show: 'Show password', hide: 'Hide password', caps: 'Caps Lock is on', errUser: 'Enter your user name', errPass: 'Enter your password',
      wrong: 'User name or password is incorrect. Attempts left before a 15-minute lock: {n}.', locked: 'This account is locked after 5 failed attempts. Try again in {n} minutes, or ask an administrator to unlock it.', retryIn: 'Try again in {n} min',
      expiredT: 'Session expired', expired: 'Sign in to restore {c}. They are kept in this browser.', expired0: 'Sign in to continue where you left off.',
      notRegT: 'Your Windows account is not registered in ECR Web', notReg: 'Windows recognised you as {login}, but this account has no ECR role yet. Ask a system administrator ({admins}) for access, then try again.', tryAgain: 'Try again',
      outT: 'You are signed out', out: 'On a shared computer, close this browser tab as well.', language: 'Interface language', help: 'Need access? Ask your system administrator.' },
    ru: { tagline: 'Структурированная экологическая отчётность промышленного предприятия.', windows: 'Войти через Windows', windowsHint: 'Используется учётная запись Windows, под которой вы работаете на этом компьютере.', local: 'Войти с локальной учётной записью', user: 'Имя пользователя', password: 'Пароль', signIn: 'Войти', signingIn: 'Выполняется вход…',
      show: 'Показать пароль', hide: 'Скрыть пароль', caps: 'Включён Caps Lock', errUser: 'Введите имя пользователя', errPass: 'Введите пароль',
      wrong: 'Неверное имя пользователя или пароль. Попыток до блокировки на 15 минут: {n}.', locked: 'Учётная запись заблокирована после 5 неудачных попыток. Повторите через {n} мин или попросите администратора снять блокировку.', retryIn: 'Повторите через {n} мин',
      expiredT: 'Сеанс истёк', expired: 'Войдите, чтобы восстановить несохранённые изменения ({n}). Они хранятся в этом браузере.', expired0: 'Войдите, чтобы продолжить с того же места.',
      notRegT: 'Ваша учётная запись Windows не зарегистрирована в ECR Web', notReg: 'Windows определил вас как {login}, но у этой учётной записи пока нет ролей ECR. Попросите доступ у системного администратора ({admins}) и повторите вход.', tryAgain: 'Повторить',
      outT: 'Вы вышли из системы', out: 'На общем компьютере закройте также эту вкладку браузера.', language: 'Язык интерфейса', help: 'Нужен доступ? Обратитесь к системному администратору.' },
    kk: { tagline: 'Өнеркәсіптік кәсіпорынның құрылымдалған экологиялық есептілігі.', windows: 'Windows арқылы кіру', windowsHint: 'Осы компьютерде жұмыс істеп отырған Windows тіркелгіңіз пайдаланылады.', local: 'Жергілікті тіркелгімен кіру', user: 'Пайдаланушы аты', password: 'Құпиясөз', signIn: 'Кіру', signingIn: 'Кіру орындалуда…',
      show: 'Құпиясөзді көрсету', hide: 'Құпиясөзді жасыру', caps: 'Caps Lock қосулы', errUser: 'Пайдаланушы атын енгізіңіз', errPass: 'Құпиясөзді енгізіңіз',
      wrong: 'Пайдаланушы аты немесе құпиясөз қате. 15 минутқа бұғатталғанға дейін қалған әрекет саны: {n}.', locked: 'Тіркелгі 5 сәтсіз әрекеттен кейін бұғатталды. {n} минуттан кейін қайталаңыз немесе әкімшіден бұғатты алуды сұраңыз.', retryIn: '{n} мин кейін қайталаңыз',
      expiredT: 'Сеанс аяқталды', expired: 'Сақталмаған өзгерістерді ({n}) қалпына келтіру үшін кіріңіз. Олар осы браузерде сақталған.', expired0: 'Сол жерден жалғастыру үшін кіріңіз.',
      notRegT: 'Windows тіркелгіңіз ECR Web жүйесінде тіркелмеген', notReg: 'Windows сізді {login} ретінде таныды, бірақ бұл тіркелгіде ECR рөлдері әлі жоқ. Жүйе әкімшісінен ({admins}) қолжетімділік сұрап, қайта кіріңіз.', tryAgain: 'Қайталау',
      outT: 'Сіз жүйеден шықтыңыз', out: 'Ортақ компьютерде осы браузер қойындысын да жабыңыз.', language: 'Интерфейс тілі', help: 'Қолжетімділік керек пе? Жүйе әкімшісіне хабарласыңыз.' }
  };
  var auth = { lang: E.store.get('work-lang', 'en'), failures: 0 };
  if (!TXT[auth.lang]) auth.lang = 'en';

  E.screen('/login', { title: 'Sign in', chrome: false, icon: 'key', palette: true, render: function (el, ctx) {
    var q = ctx.query, kase = q['case'] || (ctx.state === 'error' ? 'wrong' : ctx.state === 'loading' ? 'signing' : '');
    if (q.lang && TXT[q.lang]) auth.lang = q.lang;
    var lang = kase === 'i18n' ? 'en' : auth.lang, ret = q['return'] || '', unsaved = kase === 'expired' ? (q.unsaved != null ? +q.unsaved : 3) : 0, me = D.users[1], timer = null;
    function t(k, vars) { var s = TXT[lang][k] || TXT.en[k] || k; Object.keys(vars || {}).forEach(function (v) { s = s.replace('{' + v + '}', vars[v]); }); return s; }
    var localOpen = ['local', 'wrong', 'locked', 'capslock'].indexOf(kase) >= 0, busy = kase === 'signing';
    ctx.onLeave(function () { clearTimeout(timer); });

    var card = h('div', { class: 'panel work-card' });
    if (kase === 'expired') card.appendChild(E.Banner({ tone: 'info', icon: 'clock', id: 'work-login-expired', title: t('expiredT'), text: unsaved ? t('expired', { n: unsaved, c: F.plural(unsaved, { one: 'unsaved change', other: 'unsaved changes' }) }) : t('expired0') }));
    if (kase === 'signed-out') card.appendChild(E.Banner({ icon: 'logout', id: 'work-login-out', title: t('outT'), text: t('out') }));
    if (kase === 'not-registered') card.appendChild(E.Banner({ tone: 'danger', id: 'work-login-notreg', title: t('notRegT'), text: h('span', t('notReg', { login: me.login, admins: adminNames() }), ' ', h('span', { class: 'chip' }, 'ECR-AUTH-0403')) }));
    if (kase === 'i18n') card.appendChild(E.Banner({ tone: 'warning', id: 'work-login-i18n', title: 'Interface texts could not be loaded', text: h('span', 'The screen is shown in English. Signing in works as usual; other languages come back after a retry. ', h('span', { class: 'chip' }, 'ECR-I18N-0503')),
      actions: [{ label: 'Retry', icon: 'refresh', id: 'work-login-i18n-retry', onClick: function () { E.go('/login', { force: true, params: { 'return': ret || null }, toast: 'Interface texts loaded' }); } }] }));

    var winBtn = E.Button({ label: busy ? t('signingIn') : kase === 'not-registered' ? t('tryAgain') : t('windows'), icon: busy ? null : 'windows', variant: localOpen ? null : 'primary', size: 'lg', id: 'work-login-windows', class: 'work-wide', disabled: busy, onClick: function () { setBusy(); timer = setTimeout(function () { if (kase === 'not-registered') ctx.refresh(); else finish(false, me.name); }, 700); } });
    if (busy) winBtn.insertBefore(h('span', { class: 'spin' }), winBtn.firstChild);
    card.appendChild(winBtn);
    card.appendChild(h('p', { class: 'muted t-xs work-center' }, t('windowsHint')));

    var user = E.Input({ id: 'work-login-user', label: t('user'), autocomplete: 'username', disabled: busy });
    var pass = passwordField({ id: 'work-login-pass', label: t('password'), autocomplete: 'current-password', showLabel: t('show'), hideLabel: t('hide'), capsLabel: t('caps') });
    var submit = E.Button({ label: t('signIn'), id: 'work-login-submit', submit: true, variant: localOpen ? 'primary' : null, class: 'work-wide', disabled: busy });
    var form = h('form', { class: 'work-form work-form-sep', id: 'work-login-form', novalidate: 'novalidate', hidden: !localOpen, 'aria-label': t('local') }, user, pass, submit);
    var toggle = h('button', { class: 'link-btn', type: 'button', id: 'work-login-local-toggle', 'aria-expanded': String(localOpen), 'aria-controls': 'work-login-form' }, t('local'));
    toggle.addEventListener('click', function () { localOpen = !localOpen; form.hidden = !localOpen; toggle.setAttribute('aria-expanded', String(localOpen)); winBtn.classList.toggle('btn-primary', !localOpen); submit.classList.toggle('btn-primary', localOpen); if (localOpen) user.input.focus(); });
    card.appendChild(h('div', { class: 'work-center' }, toggle));
    card.appendChild(form);

    function setBusy() { winBtn.disabled = true; submit.disabled = true; winBtn.textContent = ''; winBtn.appendChild(h('span', { class: 'spin' })); winBtn.appendChild(document.createTextNode(t('signingIn'))); }
    function finish(oneTime, who) {
      auth.failures = 0;
      if (oneTime) return E.go('/change-password', { force: true, params: { reason: 'temporary', 'return': ret || null } });
      E.go(ret || '/', { force: true, toast: unsaved ? 'Signed in — ' + F.plural(unsaved, { one: 'unsaved change', other: 'unsaved changes' }) + ' restored' : 'Signed in as ' + who });
    }
    function lock(min) { user.setError(t('locked', { n: min }) + ' (ECR-AUTH-0423)'); submit.disabled = true; submit.textContent = t('retryIn', { n: min }); }
    form.addEventListener('submit', function (e) {
      e.preventDefault(); user.setError(null); pass.setError(null);
      var u = user.value.trim(), p = pass.value;
      if (!u) { user.setError(t('errUser')); user.input.focus(); return; }
      if (!p) { pass.setError(t('errPass')); pass.input.focus(); return; }
      if (/^temp/i.test(p)) return finish(true);
      if (/dzhaksybekov/i.test(u)) return lock(14);
      if (p === 'wrong') { auth.failures++; var left = 5 - auth.failures; pass.value = ''; if (left <= 0) return lock(15); pass.setError(t('wrong', { n: left }) + ' (ECR-AUTH-0401)'); pass.input.focus(); return; }
      var known = D.users.filter(function (x) { return x.login.toLowerCase() === u.toLowerCase(); })[0]; setBusy(); timer = setTimeout(function () { finish(false, known ? known.name : u); }, 500);
    });
    if (kase === 'wrong') { user.value = 'svc-import'; pass.setError(t('wrong', { n: 3 }) + ' (ECR-AUTH-0401)'); }
    if (kase === 'locked') { user.value = 'r.dzhaksybekov'; lock(14); }
    if (kase === 'capslock') { user.value = 'svc-import'; pass.showCaps(true); }

    var langSel = E.Select({ id: 'work-login-lang', bare: true, ariaLabel: t('language'), options: LANGS, value: lang, disabled: kase === 'i18n', onChange: function (v) { auth.lang = v; E.store.set('work-lang', v); var p = Object.assign({}, q); delete p.lang; E.go('/login', { force: true, params: p }); } });
    if (kase === 'i18n') langSel.title = 'Other languages are unavailable until the interface texts load';
    el.appendChild(h('div', { class: 'work-auth', lang: lang }, h('div', { class: 'work-auth-col' }, brand(t('tagline')), card,
      h('div', { class: 'work-foot' }, h('span', { class: 'row gap-2' }, icon('globe', 14), langSel), h('span', { class: 'mono', id: 'work-login-version' }, productVersion())),
      h('p', { class: 'muted t-xs work-center' }, t('help')))));
    if (kase === 'capslock') setTimeout(function () { pass.input.focus(); pass.showCaps(true); }, 0);

    function fill(u, p) { return function () { if (!localOpen) toggle.click(); user.value = u; pass.value = p; submit.focus(); }; }
    ctx.proto({ id: 'login-fill', label: 'Local sign-in: fill the form', buttons: [{ label: 'Valid', onClick: fill('svc-import', 'demo') }, { label: 'Wrong password', onClick: fill('svc-import', 'wrong') }, { label: 'Locked account', onClick: fill('r.dzhaksybekov', 'demo') }, { label: 'One-time password', onClick: fill('r.dzhaksybekov', 'Temp-4821') }] });
    ctx.proto({ id: 'login-case', label: 'Sign-in case', buttons: ['', 'local', 'capslock', 'wrong', 'locked', 'expired', 'not-registered', 'i18n', 'signed-out'].map(function (c) { return { label: c || 'default', onClick: function () { E.go('/login', { force: true, params: { 'case': c || null, 'return': c === 'expired' ? '/documents/DOC-000001' : null } }); } }; }) });
  } });

  /* ================= /change-password ================= */
  E.screen('/change-password', { title: 'Change password', chrome: false, icon: 'key', palette: true, render: function (el, ctx) {
    var q = ctx.query, forced = q.reason === 'temporary' || q.reason === 'expired', app = document.getElementById('app');
    if (app) app.classList.toggle('bare', forced);           /* добровільна зміна — в оболонці; примусова — без неї */
    var local = forced || q.account === 'local', account = local ? D.user('R. Dzhaksybekov') : ctx.me, ret = q['return'] || '';
    var backTo = ctx.from || { label: 'Documents', href: '#/' }, dirty = false;

    /* Windows-обліковка: пароля в ECR немає — чесне пояснення замість форми */
    if (!forced && (ctx.state === 'empty' || (account.auth === 'Windows' && ctx.state === 'data'))) {
      var pg = h('div', { class: 'page' }); el.appendChild(pg);
      pg.appendChild(E.PageHeader({ ctx: ctx, id: 'work-pw', title: 'Change password', back: { label: 'Documents', href: '#/' } }));
      pg.appendChild(E.EmptyState({ icon: 'windows', title: 'Your password is managed by Windows', text: 'You sign in as ' + account.login + ' with your Windows account, so ECR Web does not keep a password for you. Change it in Windows (Ctrl + Alt + Del → Change a password); the next sign-in here uses the new one.',
        actions: [{ label: 'Back to ' + backTo.label, variant: 'primary', id: 'work-pw-back', onClick: function () { E.back('#/'); } }, { label: 'See my access', href: '#/my-groups' }] }));
      ctx.proto({ id: 'pw-variant', label: 'Variant', buttons: [{ label: 'Local account form', onClick: function () { E.go('/change-password', { params: { account: 'local' } }); } }, { label: 'Forced (one-time password)', onClick: function () { E.go('/change-password', { params: { reason: 'temporary' } }); } }] });
      return;
    }

    var namePart = account.login.split('\\').pop().split('.').pop().toLowerCase();
    var cur = passwordField({ id: 'work-pw-current', label: forced ? 'One-time password' : 'Current password', required: true, autocomplete: 'current-password', hint: forced ? 'The password the administrator gave you.' : null, onInput: changed });
    var nw = passwordField({ id: 'work-pw-new', label: 'New password', required: true, autocomplete: 'new-password', onInput: changed });
    var rep = passwordField({ id: 'work-pw-repeat', label: 'Repeat new password', required: true, autocomplete: 'new-password', onInput: changed });
    var RULES = [['len', 'At least 12 characters', function (v) { return v.length >= 12; }], ['case', 'Upper- and lower-case letters', function (v) { return /[a-zа-яё]/.test(v) && /[A-ZА-ЯЁ]/.test(v); }], ['digit', 'A digit', function (v) { return /\d/.test(v); }],
      ['symbol', 'A symbol, such as ! ? # %', function (v) { return /[^\w\s]|_/.test(v); }], ['name', 'Does not contain your user name', function (v) { return v.length > 0 && v.toLowerCase().indexOf(namePart) < 0; }], ['diff', 'Differs from the ' + (forced ? 'one-time' : 'current') + ' password', function (v) { return v.length > 0 && v !== cur.value; }]];
    var reqs = h('ul', { class: 'work-reqs', id: 'work-pw-reqs', 'aria-label': 'Password requirements' });
    function unmet() { return RULES.filter(function (r) { return !r[2](nw.value); }).length; }
    function paintReqs() { reqs.textContent = ''; RULES.forEach(function (r) { var ok = r[2](nw.value); reqs.appendChild(h('li', { class: ok ? 'ok' : null, id: 'work-pw-req-' + r[0] }, icon(ok ? 'check' : 'circle', 14), r[1], h('span', { class: 'sr' }, ok ? ' — met' : ' — not met yet'))); }); }
    function changed() { dirty = !!(cur.value || nw.value || rep.value); paintReqs(); }
    paintReqs();

    var submit = E.Button({ label: forced ? 'Save password and continue' : 'Change password', variant: 'primary', submit: true, id: 'work-pw-submit', icon: 'check' });
    var other = forced ? E.Button({ label: 'Sign in as a different user', variant: 'ghost', id: 'work-pw-other', onClick: function () { E.go('/login', { force: true, params: { 'case': 'local' } }); } }) : E.Button({ label: 'Cancel', id: 'work-pw-cancel', onClick: function () { E.back('#/'); } });
    var slot = h('div', { hidden: true });
    var form = h('form', { class: 'work-form', id: 'work-pw-form', novalidate: 'novalidate' }, slot, cur, nw, h('div', { class: 'stack gap-1' }, h('span', { class: 'muted t-xs' }, 'The new password must have:'), reqs), rep, forced ? h('div', { class: 'stack gap-2' }, submit, h('div', { class: 'work-center' }, other)) : h('div', { class: 'row wrap gap-2' }, submit, other));
    if (forced) submit.classList.add('work-wide');
    function validate() {
      var bad = null; [cur, nw, rep].forEach(function (f) { f.setError(null); });
      if (!cur.value) { cur.setError(forced ? 'Enter the one-time password you were given' : 'Enter your current password'); bad = bad || cur; }
      else if (cur.value === 'wrong') { cur.setError('This password is incorrect (ECR-AUTH-0401)'); bad = bad || cur; }
      var n = unmet(); if (!nw.value) { nw.setError('Enter a new password'); bad = bad || nw; } else if (n) { nw.setError('The password misses ' + F.plural(n, { one: 'requirement', other: 'requirements' }) + ' from the list below'); bad = bad || nw; }
      if (!rep.value) { rep.setError('Repeat the new password'); bad = bad || rep; } else if (rep.value !== nw.value) { rep.setError('The two passwords do not match'); bad = bad || rep; }
      if (bad) bad.input.focus(); return !bad;
    }
    form.addEventListener('submit', function (e) {
      e.preventDefault(); if (!validate()) return; submit.disabled = true; dirty = false;
      setTimeout(function () { E.go(forced ? (ret || '/') : backTo.href, { force: true, toast: forced ? 'Password changed — signed in as ' + account.name : 'Password changed — other sessions of this account were signed out' }); }, 400);
    });
    if (ctx.state === 'error') { slot.hidden = false; slot.appendChild(E.Banner({ tone: 'danger', id: 'work-pw-failed', title: 'The password was not changed', text: h('span', 'The server did not answer in time, so your old password still works. ', h('span', { class: 'chip' }, 'ECR-AUTH-0503')), actions: [{ label: 'Retry', icon: 'refresh', onClick: function () { var p = Object.assign({}, q); delete p.state; E.go('/change-password', { force: true, params: p, toast: 'Connection restored — try again' }); } }] })); }
    if (q['case'] === 'invalid') { cur.value = 'Temp-4821'; nw.value = 'dzhaksybekov2026'; rep.value = 'dzhaksybekov2027'; changed(); setTimeout(validate, 0); }

    var content = ctx.state === 'loading' ? E.Skeleton({ kind: 'form', rows: 4 }) : form;
    if (forced) {
      el.appendChild(h('div', { class: 'work-auth' }, h('div', { class: 'work-auth-col' }, brand('Structured environmental reporting for industrial sites.'),
        h('div', { class: 'panel work-card' }, h('h2', { id: 'work-pw-h2' }, 'Choose your own password'),
          E.Banner({ tone: 'info', id: 'work-pw-why', text: q.reason === 'expired' ? 'Your password is older than 90 days. Choose a new one to continue — it takes a minute.' : 'You signed in with a one-time password from an administrator. It works only once, so choose your own password to continue.' }),
          E.KeyValue([{ label: 'Account', value: monoText(account.login), hint: account.fullName + ' · local ECR account' }]), content),
        h('div', { class: 'work-foot' }, h('span', ret ? 'After saving you continue to ' + ret : 'After saving you continue to Documents'), h('span', { class: 'mono' }, productVersion())))));
    } else {
      var page = h('div', { class: 'page' }); el.appendChild(page);
      page.appendChild(E.PageHeader({ ctx: ctx, id: 'work-pw', title: 'Change password', subtitle: 'For the local ECR account ' + account.login + '. After the change, other sessions of this account are signed out.', back: { label: 'Documents', href: '#/' } }));
      page.appendChild(h('div', { class: 'panel work-pwpanel' }, content));
      ctx.hasUnsaved(function () { return dirty; }, { text: 'The new password is not saved yet. If you leave now, your current password stays as it is.', onDiscard: function () { dirty = false; } });
    }
    ctx.proto({ id: 'pw-variant', label: 'Variant', buttons: [{ label: 'Forced · one-time', onClick: function () { E.go('/change-password', { force: true, params: { reason: 'temporary' } }); } }, { label: 'Forced · expired', onClick: function () { E.go('/change-password', { force: true, params: { reason: 'expired' } }); } }, { label: 'Voluntary · local', onClick: function () { E.go('/change-password', { force: true, params: { account: 'local' } }); } }, { label: 'Voluntary · Windows', onClick: function () { E.go('/change-password', { force: true }); } }, { label: 'Show field errors', onClick: function () { E.go('/change-password', { force: true, params: { reason: forced ? q.reason : null, account: forced ? null : 'local', 'case': 'invalid' } }); } }] });
  } });

  /* ================= / Documents ================= */
  var derived = {};
  function docsFor(pid) {
    var p = D.period(pid); if (!p || p.state === 'NotOpened') return [];
    if (pid === D.currentPeriod || p.state === 'Open' || p.state === 'Grace') return D.documents.filter(function (d) { return d.period === pid; });
    if (!derived[pid]) { var mm = +pid.slice(5), close = '2026-' + ('0' + Math.min(12, mm + 1)).slice(-2) + '-0';
      derived[pid] = D.documents.slice(0, 12).map(function (d, i) { return Object.assign({}, d, { period: pid, late: false, updated: close + (1 + i % 4) + 'T' + ('0' + (9 + i % 8)).slice(-2) + ':' + ('0' + (i * 7 % 60)).slice(-2), sheets: d.sheets.map(function (s) { return { name: s.name, state: 'Approved', tables: s.tables, filled: s.tables, issues: 0 }; }) }); }); }
    return derived[pid];
  }
  function findDoc(id, pid) { return docsFor(pid).filter(function (d) { return d.id === id; })[0] || D.doc(id); }
  function docIssues(d) { return d.sheets.reduce(function (n, s) { return n + s.issues; }, 0); }
  function docApproved(d) { return d.sheets.filter(function (s) { return s.state === 'Approved'; }).length; }
  function docHref(d, extra) { return E.href('/documents/' + d.id, Object.assign({ period: d.period }, extra)); }
  function segBar(d) { return E.SegmentBar({ segments: d.sheets.map(function (s) { return { label: s.name, state: s.state, note: s.issues ? F.plural(s.issues, { one: 'error', other: 'errors' }) : null }; }) }); }

  /* ---- майстер створення ---- */
  function templatesFor(pid) {
    var own = 'GEN' + pid.slice(1), t = D.templates.filter(function (x) { return x.id === own; })[0], p = project(pid);
    var list = [{ id: own, name: t ? t.name : 'General environmental report — ' + p.name, version: t ? t.version : '1.0.0', sheets: 5, tables: t ? t.tables : 222, own: true, known: !!t }];
    var up = D.templates.filter(function (x) { return x.id === 'GEN-UPSTREAM' && x.state === 'Published'; })[0];
    if (up) list.push({ id: up.id, name: up.name, version: up.version, sheets: up.sheets, tables: up.tables, known: true });
    return list;
  }
  function templateSheets(tp) { return D.sheetNames.slice(0, tp.sheets).map(function (n, i) { return { name: n, tables: D.sheetTables[i] }; }); }
  function duplicate(data) { return D.documents.filter(function (d) { return d.project === data.project && d.template === data.template && d.period === data.period; })[0]; }
  function createDoc(data) {
    var id = 'DOC-' + ('00000' + (D.documents.length + 1)).slice(-6), p = project(data.project), tp = templatesFor(data.project).filter(function (x) { return x.id === data.template; })[0];
    var d = { id: id, project: p.id, facility: p.name, period: data.period, owner: E.me().name, approver: 'A. Iskakov', updated: D.today.slice(0, 16), late: false, template: data.template, version: data.version,
      sheets: templateSheets(tp).filter(function (s) { return data.sheets[s.name] !== false; }).map(function (s) { return { name: s.name, state: 'Draft', tables: s.tables, filled: 0, issues: 0 }; }) };
    D.documents.push(d); return d;
  }
  function creatable() { return access(E.me()).projects.filter(function (p) { return p.edit; }); }
  function openCreate(preset, advance, denied) {
    if (denied || !E.can('Document.Create')) { var pm = permission('Document.Create');
      return E.ResultDialog({ id: 'work-create-denied', tone: 'warning', title: 'You can’t create documents', text: 'Creating a document needs the permission “' + pm.label + '”, and none of your roles (' + E.me().roles.map(roleName).join(', ') + ') includes it. Ask ' + adminNames() + ' to add it.', body: h('div', { class: 'sumline' }, h('span', { class: 'chip' }, pm.code), h('span', { class: 'chip' }, 'ECR-SEC-0403')), actions: [{ label: 'See my access', href: '#/my-groups', primary: false }, { label: 'Close', primary: true }] }); }
    var projects = creatable(), data = Object.assign({ project: projects.length ? projects[0].id : null, template: null, version: null, period: D.currentPeriod, sheets: {} }, preset || {});
    function tpl() { return templatesFor(data.project).filter(function (x) { return x.id === data.template; })[0]; }
    function existingFor(tid, per) { return duplicate({ project: data.project, template: tid, period: per || data.period }); }
    var api = E.Wizard({ id: 'work-create', title: 'New document', data: data, applyLabel: 'Create document', reviewText: 'Nothing is created until you confirm. The document starts empty, in the state Draft, with you as its owner.',
      steps: [
        { id: 'project', label: 'Project', hint: 'Which site is this report for?', render: function (b) {
          if (!projects.length) { b.appendChild(E.Banner({ tone: 'warning', title: 'There is no project you can enter data for', text: 'Your roles allow creating documents, but no project group includes you yet.', actions: [{ label: 'See my access', href: '#/my-groups' }] })); return; }
          b.appendChild(E.Select({ id: 'work-cd-project', label: 'Project', required: true, options: projects.map(function (p) { return { value: p.id, label: p.name + ' · ' + p.id }; }), value: data.project, hint: 'Only projects where you can enter data are listed.', onChange: function (v) { data.project = v; data.template = null; data.sheets = {}; } }));
          b.appendChild(h('p', { class: 'muted t-xs' }, 'Missing a project? ', h('a', { href: '#/my-groups' }, 'See where your access comes from'), '.')); },
          canNext: function () { return !!data.project; } },
        { id: 'template', label: 'Template', hint: 'The template decides which sheets and tables the document has.', render: function (b, _d, wz) {
          var list = templatesFor(data.project); if (!data.template || !list.some(function (x) { return x.id === data.template; })) { data.template = (list.filter(function (x) { return !existingFor(x.id, D.currentPeriod); })[0] || list[0]).id; data.version = null; }
          var verSlot = h('div');
          function paintVersion() { var tp = tpl(), vs = tp.known ? D.templateVersions(tp.id).filter(function (v) { return v.state === 'Published'; }).map(function (v) { return v.version; }) : [tp.version]; if (!vs.length) vs = [tp.version]; if (vs.indexOf(data.version) < 0) data.version = vs[0];
            verSlot.textContent = ''; verSlot.appendChild(E.Select({ id: 'work-cd-version', label: 'Version', options: vs.map(function (v, i) { return { value: v, label: 'v' + v + (i === 0 ? ' · latest published' : '') }; }), value: data.version, hint: 'Only published versions can be used. Drafts and archived versions are not offered.', onChange: function (v) { data.version = v; } })); }
          b.appendChild(h('div', { class: 'work-opts', role: 'radiogroup', 'aria-label': 'Template' }, list.map(function (tp) { var ex = existingFor(tp.id, D.currentPeriod);
            var r = h('input', { type: 'radio', name: 'work-cd-tpl', id: 'work-cd-tpl-' + tp.id, value: tp.id, checked: tp.id === data.template }); r.addEventListener('change', function () { data.template = tp.id; data.version = null; data.sheets = {}; paintVersion(); wz.refreshButtons(); });
            return h('label', { class: 'work-opt', for: 'work-cd-tpl-' + tp.id }, r, h('span', { class: 'grow' }, h('span', { class: 't-b' }, tp.name), h('small', tp.id + ' · ' + tp.sheets + ' sheets · ' + tp.tables + ' tables' + (tp.own ? ' · default for this project' : '')),
              ex ? h('small', { class: 'work-opt-note' }, icon('info', 12), ex.id + ' already uses it for ' + F.period(ex.period)) : null)); })));
          b.appendChild(verSlot); paintVersion();
          b.appendChild(h('p', { class: 'muted t-xs' }, 'Annual and draft templates are not offered for a monthly period.')); } },
        { id: 'period', label: 'Period', hint: 'A document belongs to exactly one reporting period.', render: function (b, _d, wz) {
          var open = D.periods.filter(function (p) { return p.state === 'Open' || p.state === 'Grace'; }), warn = h('div');
          if (!open.some(function (p) { return p.id === data.period; })) data.period = open.length ? open[0].id : null;
          function check() { warn.textContent = ''; var ex = data.period && duplicate(data); if (ex) warn.appendChild(E.Banner({ tone: 'warning', id: 'work-cd-exists', title: 'This document already exists', text: ex.id + ' covers ' + ex.facility + ' · ' + F.period(ex.period) + ' with the same template. One project can have one document per template and period.',
            actions: [{ label: 'Open ' + ex.id, icon: 'arrowR', id: 'work-cd-open-existing', onClick: function () { wz.close(); E.go('/documents/' + ex.id, { params: { period: ex.period } }); } }] })); wz.refreshButtons(); }
          if (!open.length) { b.appendChild(E.Banner({ tone: 'warning', title: 'No period is open', text: 'Documents can be created only in an open period. Ask a period administrator to open the next one.' })); return; }
          b.appendChild(E.Select({ id: 'work-cd-period', label: 'Reporting period', required: true, options: open.map(function (p) { return { value: p.id, label: F.period(p.id) + ' · ' + p.note }; }), value: data.period, hint: 'Closed periods are read-only and periods that are not opened yet cannot be used, so they are not listed.', onChange: function (v) { data.period = v; check(); } }));
          b.appendChild(warn); setTimeout(check, 0); },
          canNext: function () { return !!data.period && !duplicate(data); } },
        { id: 'sheets', label: 'Sheets', hint: 'Leave out sheets that do not apply to this site. All are included by default.', render: function (b) {
          templateSheets(tpl()).forEach(function (s, i) { b.appendChild(E.Checkbox({ id: 'work-cd-sheet-' + i, label: s.name, hint: s.tables + ' tables', checked: data.sheets[s.name] !== false, onChange: function (v) { data.sheets[s.name] = v; } })); }); },
          validate: function () { return templateSheets(tpl()).some(function (s) { return data.sheets[s.name] !== false; }) ? null : 'Include at least one sheet — a document without sheets cannot be filled in.'; } }],
      summary: function () { var tp = tpl(), all = templateSheets(tp), inc = all.filter(function (s) { return data.sheets[s.name] !== false; }), p = project(data.project);
        return E.KeyValue([{ label: 'Project', value: p.name, hint: p.id }, { label: 'Template', value: tp.name, hint: tp.id + ' · v' + data.version }, { label: 'Period', value: F.period(data.period), hint: D.period(data.period).note },
          { label: 'Sheets', value: inc.length + ' of ' + all.length, hint: inc.map(function (s) { return s.name; }).join(' · ') }, { label: 'Owner', value: E.me().name, hint: 'You enter the data and submit the sheets' }, { label: 'Approver', value: 'A. Iskakov' },
          { label: 'Document key', value: monoText('DOC-' + ('00000' + (D.documents.length + 1)).slice(-6)), hint: 'Assigned when the document is created' }]); },
      onApply: function (_d, wz) { var btn = document.getElementById('work-create-next'); if (btn) { btn.disabled = true; btn.textContent = 'Creating…'; }
        setTimeout(function () { if (duplicate(data)) { if (btn) { btn.disabled = false; btn.textContent = 'Create document'; } return wz.fail('Someone has just created this document. Go back to the period step to open it.'); } var d = createDoc(data); wz.close(); E.go('/documents/' + d.id, { force: true, params: { period: d.period }, toast: 'Document created' }); }, 600); } });
    for (var i = 0; i < (advance || 0); i++) api.next();
    return api;
  }
  var DEMO_NEW = { project: 'P07131100', template: 'GEN-UPSTREAM', version: '2.3.0' }, DEMO_DUP = { project: 'P07131100', template: 'GEN07131100', version: '1.0.0' };

  E.screen('/', { title: 'Documents', group: 'Work', icon: 'file', order: 1, render: function (el, ctx) {
    var q = ctx.query, saved = ctx.saved, me = ctx.me;
    if (q.reset) { saved.filters = {}; saved.stat = null; saved.view = null; saved.period = null; }
    saved.filters = saved.filters || {};
    ['q', 'project', 'status', 'mine'].forEach(function (k) { if (q[k] != null) saved.filters[k] = q[k]; });
    if (q.stat) saved.stat = q.stat; if (q.period && D.period(q.period)) saved.period = q.period; if (q.view) saved.view = q.view === 'board' ? 'board' : 'table';
    ctx.setQuery({ q: null, project: null, status: null, mine: null, stat: null, reset: null });   /* одноразові: далі їх тримає ctx.saved */
    var period = saved.period || D.currentPeriod, view = saved.view || 'table', noAccess = q['case'] === 'no-access', canCreate = E.can('Document.Create') && !noAccess;
    var rows, strip, bar, table, board, statActive;

    var page = h('div', { class: 'page page-fill' }); el.appendChild(page);
    var head = h('div'), bannerSlot = h('div', { hidden: true }), body = h('div', { class: 'work-fill' });
    var picker = E.PeriodPicker({ id: 'docs-period', value: period, onChange: function (id) { period = saved.period = id; ctx.setQuery({ period: id === D.currentPeriod ? null : id }); paint(); } });
    var note = h('span', { class: 'muted', id: 'docs-period-note' });
    page.appendChild(head); page.appendChild(bannerSlot); page.appendChild(h('div', { class: 'row wrap gap-3 no-shrink', hidden: q['case'] === 'no-access' }, picker, note)); page.appendChild(body);

    var STATS = [{ id: 'all', label: 'documents', filter: false, value: function (r) { return r.length; } },
      { id: 'approved', label: 'sheets approved', hint: 'Show documents that have approved sheets', value: function (r) { return r.reduce(function (n, d) { return n + docApproved(d); }, 0); }, of: function (r) { return r.reduce(function (n, d) { return n + d.sheets.length; }, 0); }, match: function (d) { return docApproved(d) > 0; } },
      { id: 'issues', label: 'open validation errors', tone: 'danger', hint: 'Show documents with validation errors', value: function (r) { return r.reduce(function (n, d) { return n + docIssues(d); }, 0); }, match: function (d) { return docIssues(d) > 0; } },
      { id: 'late', label: 'late edits', tone: 'warning', hint: 'Show documents edited after their sheets were submitted', value: function (r) { return r.filter(function (d) { return d.late; }).length; }, match: function (d) { return d.late; } }];
    function statItems() { return STATS.map(function (s) { return { id: s.id, label: s.label, tone: s.tone, hint: s.hint, filter: s.filter, value: s.value(rows), of: s.of ? s.of(rows) : null }; }); }

    function paintHead() {
      head.textContent = '';
      /* D1: у невідкритому / закритому / архівному періоді документ створити не можна — головна дія вимкнена з причиною */
      var ps = (D.period(period) || {}).state, why = ps === 'Open' || !ps ? null : ps === 'NotOpened' ? 'The period is not opened yet' : ps === 'Archived' ? 'The period is archived — it is read-only' : 'The period is closed — it is read-only';
      head.appendChild(E.PageHeader({ ctx: ctx, id: 'docs', title: 'Documents', back: false, count: ctx.state === 'data' && rows.length ? rows.length : null,
        subtitle: 'Reports of your projects for one period. Open a document to work in it, or take a quick look at its sheets without leaving the list.',
        primary: canCreate ? { label: 'New document', icon: 'plus', id: 'docs-new', disabled: !!why, title: why, onClick: function () { ctx.openDialog('create-document'); } } : null,
        more: noAccess ? [] : [{ label: 'Export this list to Excel', icon: 'download', onClick: exportList }] }));
    }
    function exportList() { var n = rows.length; E.tasks.start({ title: 'Excel export · Documents, ' + F.period(period), icon: 'download', detail: F.plural(n, { one: 'document', other: 'documents' }), duration: 4000, doneDetail: 'documents-' + period + '.xlsx · 18 KB', result: { label: 'Download', onClick: function () { E.Toast('documents-' + period + '.xlsx downloaded'); } } });
      E.Toast('Export started — it continues in My tasks', { action: { label: 'Open My tasks', onClick: function () { E.tasks.open(); } } }); }
    function openDoc(d) { E.go('/documents/' + d.id, { params: { period: d.period } }); }
    function quickBtn(d) { return E.Button({ iconOnly: true, icon: 'eye', label: 'Quick look at ' + d.id, id: 'docs-ql-' + d.id, onClick: function (e) { e.stopPropagation(); ctx.openPanel(d.id); } }); }

    function filtered() {
      var v = bar.values(), text = (v.q || '').toLowerCase(), st = STATS.filter(function (s) { return s.id === statActive; })[0];
      return rows.filter(function (d) {
        if (text && (d.id + ' ' + d.facility + ' ' + d.project + ' ' + d.owner + ' ' + d.template).toLowerCase().indexOf(text) < 0) return false;
        if (st && st.match && !st.match(d)) return false; if (v.project && d.project !== v.project) return false; if (v.status && D.docState(d) !== v.status) return false;
        if (v.mine && d.owner !== me.name && !(d.approver === me.name && E.can('Document.Approve'))) return false; return true; });
    }
    function clearAll() { statActive = saved.stat = null; if (strip) strip.setActive(null); bar.reset(); }
    function apply() { var list = filtered(); if (view === 'table') table.setRows(list, { filtered: list.length !== rows.length }); else paintBoard(list); }

    var COLS = [{ id: 'Draft', label: 'Draft', states: ['Draft'] }, { id: 'Submitted', label: 'Waiting for approval', states: ['Submitted'] }, { id: 'Rework', label: 'Returned or rejected', states: ['Returned', 'Rejected'], tone: 'warn' }, { id: 'Approved', label: 'Approved', states: ['Approved'] }];
    function paintBoard(list) {
      board.textContent = '';
      if (!list.length) { board.appendChild(h('div', { class: 'work-board-full' }, rows.length ? E.EmptyState({ icon: 'search', title: 'Nothing matches these filters', text: 'Documents exist in ' + F.period(period) + ', but none fits the current search and filters.', actions: [{ label: 'Clear filters', id: 'docs-board-clear', onClick: clearAll }] }) : E.EmptyState(emptyPeriod()))); return; }
      COLS.forEach(function (c) { var items = list.filter(function (d) { return c.states.indexOf(D.docState(d)) >= 0; });
        board.appendChild(h('section', { class: 'work-col', 'aria-label': c.label }, h('div', { class: 'work-col-h' }, h('h2', { class: 't-sm t-b' }, c.label), h('span', { class: 'count' + (c.tone && items.length ? ' ' + c.tone : '') }, items.length)),
          items.length ? items.map(function (d) { var n = docIssues(d), st = D.docState(d);
            var card = h('div', { class: 'panel work-bcard', 'data-key': d.id }, h('div', { class: 'row between' }, h('a', { class: 'key', href: docHref(d), id: 'docs-card-' + d.id }, d.id), quickBtn(d)), h('div', { class: 'truncate', title: d.facility }, d.facility), segBar(d),
              h('div', { class: 'work-bmeta' }, h('span', { class: 'truncate' }, d.owner + ' · ' + F.date(d.updated)), h('span', { class: 'row gap-1 no-shrink' }, c.id === 'Rework' ? E.StatusBadge('sheet', st, { quiet: true }) : null, d.late ? h('span', { class: 'work-late', title: 'Edited after submission' }, icon('alert', 12), 'late') : null, n ? h('span', { class: 'count bad', title: F.plural(n, { one: 'open validation error', other: 'open validation errors' }) }, n) : null)));
            card.addEventListener('click', function (e) { if (e.target.closest('a,button')) return; openDoc(d); }); return card; }) : h('div', { class: 'work-col-empty' }, 'Nothing here'))); });
    }
    function emptyPeriod() { return { icon: 'file', title: 'No documents for ' + F.period(period) + ' yet', text: canCreate ? 'The period is open, but nobody has created a document in it. Create the first one — it takes four short steps.' : 'The period is open, but no document has been created in it. Data entry staff create documents; you will see them here as soon as they do.',
      actions: canCreate ? [{ label: 'New document', icon: 'plus', id: 'docs-empty-new', onClick: function () { ctx.openDialog('create-document'); } }] : [{ label: 'Go to ' + F.period(D.currentPeriod), onClick: function () { picker.set(D.currentPeriod); period = saved.period = D.currentPeriod; ctx.setQuery({ period: null }); paint(); } }] }; }

    function paint() {
      var p = D.period(period); rows = ctx.state === 'empty' || noAccess ? [] : docsFor(period); statActive = saved.stat || null;
      note.textContent = p.state === 'Open' ? 'Open · ' + p.note : p.state === 'Closed' ? 'Closed · read-only' + (p.closedBy ? ' · closed by ' + p.closedBy : '') : p.state === 'Archived' ? 'Archived · read-only' : 'Not opened · ' + p.note;
      paintHead(); body.textContent = ''; strip = bar = table = board = null;
      if (noAccess) { body.appendChild(E.EmptyState({ icon: 'lock', title: 'You don’t have access to any project yet', text: 'Documents are shown per project, and your account ' + me.login + ' is not in any ECR project group. Ask a system administrator (' + adminNames() + ') to add you, then sign in again so Windows passes the new group.',
        actions: [{ label: 'See where my access comes from', variant: 'primary', id: 'docs-noaccess-groups', href: '#/my-groups?state=empty' }, { label: 'Copy request details', icon: 'copy', id: 'docs-noaccess-copy', onClick: function () { copy('ECR Web access request · ' + me.fullName + ' · ' + me.login + ' · needs a project group', 'Request details copied — paste them into your message'); } }] })); return; }
      if (p.state === 'NotOpened' && ctx.state === 'data') { var pa = D.users.filter(function (u) { return u.roles.indexOf('PeriodAdministrator') >= 0; })[0];
        body.appendChild(E.EmptyState({ icon: 'calendar', title: F.period(period) + ' is not opened yet', text: 'Documents can be created once a period administrator' + (pa ? ' (' + pa.name + ')' : '') + ' opens this period — it ' + p.note + '. Until then the work is in ' + F.period(D.currentPeriod) + '.',
          actions: [{ label: 'Go to ' + F.period(D.currentPeriod), icon: 'arrowL', variant: canCreate ? null : 'primary', id: 'docs-goto-current', onClick: function () { picker.set(D.currentPeriod); period = saved.period = D.currentPeriod; ctx.setQuery({ period: null }); paint(); } }, E.can('Period.Open') ? { label: 'Open periods', href: '#/admin/periods' } : null].filter(Boolean) })); return; }
      var data = ctx.state === 'data';
      if (data && rows.length) { strip = E.StatStrip({ id: 'docs-stat', label: 'Campaign summary', items: statItems(), active: statActive, onSelect: function (s) { statActive = saved.stat = s; apply(); } }); body.appendChild(strip); }
      bar = E.FilterBar({ id: 'docs-flt', memory: saved, search: { placeholder: 'Document, project or owner' }, onChange: apply,
        filters: [{ id: 'project', label: 'Projects', options: D.projects.map(function (x) { return { value: x.id, label: x.name }; }) }, { id: 'status', label: 'States', options: ['Draft', 'Submitted', 'Returned', 'Rejected', 'Approved'] }, { id: 'mine', label: 'Owners', all: 'Everyone’s documents', options: [{ value: '1', label: 'Only mine' }] }],
        right: E.Segmented({ id: 'docs-view', label: 'View', value: view, options: [{ value: 'table', label: 'Table', icon: 'rows4' }, { value: 'board', label: 'Board', icon: 'columns' }], onChange: function (v) { view = saved.view = v; ctx.setQuery({ view: v === 'board' ? 'board' : null }); paint(); } }) });
      if (!data || !rows.length) bar.hidden = true; body.appendChild(bar);
      if (view === 'board' && data) { board = h('div', { class: 'work-board', id: 'docs-board' }); body.appendChild(board); }
      else if (view === 'board') { body.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'table', rows: 8, cols: 4 }, error: { code: 'ECR-DOC-0503', onRetry: retry }, empty: emptyPeriod() })); return; }
      else { table = E.DataTable({ id: 'docs-tbl', rows: [], rowKey: 'id', state: ctx.state, memory: saved, tall: true, pageSize: 25, caption: 'Documents of ' + F.period(period), onRowClick: openDoc, rowLabel: function (d) { return 'Open ' + d.id + ' · ' + d.facility; }, onClearFilters: clearAll,
          empty: emptyPeriod(), error: { title: 'The documents could not be loaded', code: 'ECR-DOC-0503', onRetry: retry },
          columns: [{ key: 'id', label: 'Document', render: function (d) { return E.twoLine(d.id, d.facility, { mono: true }); } },
            { key: 'period', label: 'Period', hideSm: true, sortable: false, render: function (d) { return F.period(d.period); } },
            { key: 'sheets', label: 'Sheets', sortValue: docApproved, render: segBar },
            { key: 'issues', label: 'Issues', num: true, sortValue: docIssues, title: 'Open validation errors', render: function (d) { var n = docIssues(d); return n ? h('span', { class: 'count bad', title: F.plural(n, { one: 'open validation error', other: 'open validation errors' }) }, n) : null; } },
            { key: 'updated', label: 'Updated', hideSm: true, render: function (d) { return h('span', { class: 'two-line' }, h('span', d.owner), h('small', F.dateTime(d.updated), d.late ? ' · ' : null, d.late ? h('span', { class: 'work-late', title: 'Edited after submission' }, 'late edit') : null)); } },
            { key: 'state', label: 'State', sortValue: function (d) { return D.docState(d); }, render: function (d) { return E.StatusBadge('sheet', D.docState(d), { quiet: true }); } },
            { key: 'ql', label: '', sortable: false, width: 44, render: quickBtn }] });
        body.appendChild(table); }
      if (data) apply();
    }
    function retry() { var p = Object.assign({}, ctx.query); delete p.state; E.go('/', { params: p, toast: 'Documents loaded' }); }
    paint();

    /* Quick look — шторка під ?panel=<ключ> */
    ctx.panel('*', function (id) {
      var d = findDoc(id, period); if (!d) return; var st = D.docState(d), n = docIssues(d), log = D.audit.filter(function (a) { return a.object.indexOf(d.id) === 0; }).slice(0, 4);
      if (table) table.select(d.id);
      var drawer = E.Drawer({ id: 'docs-ql', title: d.id, subtitle: d.facility + ' · ' + F.period(d.period), badge: h('span', E.StatusBadge('sheet', st)), onClose: function () { if (table) table.select(null); },
        body: function (b) {
          if (n) b.appendChild(E.Banner({ tone: 'danger', id: 'docs-ql-issues', text: F.plural(n, { one: 'open validation error blocks', other: 'open validation errors block' }) + ' submission.', actions: [{ label: 'Show them', href: docHref(d, { panel: 'issues' }) }] }));
          else if (d.late) b.appendChild(E.Banner({ tone: 'warning', id: 'docs-ql-late', text: 'Edited after its sheets were submitted — the approver sees the change marked as late.' }));
          b.appendChild(E.Section({ title: 'Sheets', hint: docApproved(d) + ' of ' + d.sheets.length + ' approved', content: h('ul', { class: 'work-list' }, d.sheets.map(function (s, i) {
            return h('li', h('div', h('a', { href: docHref(d, { sheet: i }), id: 'docs-ql-sheet-' + i }, s.name), h('small', s.filled + ' of ' + s.tables + ' tables filled' + (s.issues ? ' · ' + F.plural(s.issues, { one: 'error', other: 'errors' }) : ''))), E.StatusBadge('sheet', s.state, { quiet: true })); })) }));
          b.appendChild(E.Section({ title: 'Responsible', content: E.KeyValue([{ label: 'Owner', value: d.owner, hint: 'Enters data and submits' }, { label: 'Approver', value: d.approver }, { label: 'Template', value: monoText(d.template), hint: 'v' + d.version }, { label: 'Updated', value: F.dateTime(d.updated) }]) }));
          b.appendChild(E.Section({ title: 'Latest changes', content: log.length ? h('ul', { class: 'work-list' }, log.map(function (a) { return h('li', h('div', h('span', a.label), h('small', a.object.replace(d.id + ' · ', ''))), h('small', a.user + ' · ' + F.dateTime(a.at))); })) : h('p', { class: 'muted' }, 'No changes recorded in the last seven days.') }));
        },
        footer: [{ label: 'Close', id: 'docs-ql-close' }, { label: 'Open document', icon: 'arrowR', variant: 'primary', id: 'docs-ql-open', onClick: function () { openDoc(d); } }] });
      drawer.el.classList.add('work-drawer');   /* без цього високий вміст виштовхує футер шторки за край вікна */
    });

    ctx.dialog('create-document', function (preset) { openCreate(preset); });
    ctx.dialog('create-document-template', function () { openCreate(DEMO_DUP, 1); });
    ctx.dialog('create-document-exists', function () { openCreate(DEMO_DUP, 2); });
    ctx.dialog('create-document-sheets', function () { openCreate(DEMO_NEW, 3); });
    ctx.dialog('create-document-review', function () { openCreate(DEMO_NEW, 4); });
    ctx.dialog('create-denied', function () { openCreate(null, 0, true); });
    ctx.dialog('result-created', function () { setTimeout(function () { var data = Object.assign({ period: D.currentPeriod, sheets: {} }, DEMO_NEW), d = duplicate(data) || createDoc(data); E.go('/documents/' + d.id, { force: true, params: { period: d.period }, toast: 'Document created' }); }, 0); });
    ctx.dialog('new-version', function () { bannerSlot.hidden = false; bannerSlot.textContent = '';
      bannerSlot.appendChild(E.Banner({ tone: 'info', icon: 'refresh', id: 'docs-new-version', title: 'New version installed — ECR Web ' + nextVersion(), text: 'Reload when it suits you. Nothing on this screen is unsaved, and your filters stay as they are.', dismissable: true, onDismiss: function () { bannerSlot.hidden = true; ctx.setQuery({ dialog: null }); },
        actions: [{ label: 'Reload', icon: 'refresh', id: 'docs-new-version-reload', onClick: function () { ctx.setQuery({ dialog: null }); ctx.refresh(); E.Toast('Reloaded — you are on version ' + nextVersion(), 'success'); } }] })); });
    if (canCreate) ctx.action('New document', 'plus', function () { ctx.openDialog('create-document'); });
    ctx.action('Documents: switch to ' + (view === 'board' ? 'table' : 'board') + ' view', 'columns', function () { saved.view = view === 'board' ? 'table' : 'board'; ctx.setQuery({ view: saved.view === 'board' ? 'board' : null }); ctx.refresh(); });
    ctx.proto({ id: 'docs-case', label: 'Empty-state variants', buttons: [{ label: 'Period not opened', onClick: function () { E.go('/', { params: { period: '2026-10' } }); } }, { label: 'Filters match nothing', onClick: function () { E.go('/', { params: { q: 'zzz' } }); } }, { label: 'No project access', onClick: function () { E.go('/', { params: { 'case': 'no-access' } }); } }, { label: 'Reset', onClick: function () { E.go('/', { params: { reset: 1 } }); } }] });
  } });

  /* ================= /my-groups ================= */
  E.screen('/my-groups', { title: 'My groups', group: 'Work', icon: 'users', order: 2, render: function (el, ctx) {
    var me = ctx.me, a = access(me), page = h('div', { class: 'page' }); el.appendChild(page);
    function signOut() { E.go('/login', { params: { 'case': 'signed-out' } }); }
    page.appendChild(E.PageHeader({ ctx: ctx, id: 'mg', title: 'My groups', back: false, subtitle: 'Where your access comes from: directory groups give you roles, roles give you permissions and projects. Nothing here can be edited — an administrator changes it for you.',
      secondary: [{ label: 'Change password', icon: 'lock', id: 'mg-change-password', href: '#/change-password' }, { label: 'Sign out', icon: 'logout', id: 'mg-sign-out', onClick: signOut }] }));
    page.appendChild(E.StateSwitch(ctx.state, { loading: { kind: 'form', rows: 7 }, error: { title: 'Your access could not be loaded', code: 'ECR-SEC-0503' },
      empty: h('div', { class: 'state-fill' }, h('div', { class: 'state work-state' }, h('div', { class: 'ico' }, icon('users', 20)), h('h3', 'Your sign-in ticket has no ECR groups'),
        h('p', 'You signed in as ' + me.login + ', but Windows did not pass any group that ECR Web knows, so you have no roles and see no projects.'),
        h('ul', { class: 'conseq work-state-wide' }, h('li', { class: 'note' }, icon('info'), h('span', 'Added to a group today? Windows puts groups into the ticket only when you sign in to Windows — sign out of Windows (or restart) and come back.')), h('li', { class: 'note' }, icon('info'), h('span', 'Not in any ECR group yet? Ask a system administrator (' + adminNames() + ') to add you to a project group.'))),
        h('div', { class: 'st-row' }, E.Button({ label: 'Copy details for the administrator', icon: 'copy', variant: 'primary', id: 'mg-empty-copy', onClick: function () { copy('ECR Web · no groups in ticket · ' + me.fullName + ' · ' + me.login + ' · ' + F.dateTime(D.today) + ' · ECR-SEC-0404', 'Details copied — paste them into your message'); } }), E.Button({ label: 'Sign out', icon: 'logout', id: 'mg-empty-signout', onClick: signOut })),
        h('div', { class: 'st-row' }, h('span', { class: 'chip' }, 'ECR-SEC-0404')))),
      data: function () {
        var granted = a.domains.filter(function (d) { return d.items.length; }), none = a.domains.filter(function (d) { return !d.items.length; });
        return h('div', { class: 'work-sections' },
          E.Section({ id: 'mg-me', title: 'You', content: E.KeyValue([{ label: 'Name', value: me.fullName }, { label: 'Account', value: monoText(me.login), hint: me.auth === 'Windows' ? 'Windows account — groups come from the directory at sign-in' : 'Local ECR account — roles are assigned directly' }, { label: 'Last sign-in', value: me.lastSeen ? F.dateTime(me.lastSeen) : null }], { wide: true }) }),
          E.Section({ id: 'mg-roles', title: 'Roles', hint: 'and the directory group each one came from', content: simpleTable('mg-roles-tbl', 'Your roles', [{ label: 'Role' }, { label: 'Comes from' }, { label: 'Permissions', num: true }], a.roles.map(function (r) {
            return [E.twoLine(r.role.name, r.role.description), r.groups.length ? E.twoLine(r.groups.map(function (g) { return g.name; }).join(', '), r.groups.map(adName).join(', ')) : E.twoLine('Assigned to you directly', 'Not through a directory group'), String(r.count)]; })) }),
          E.Section({ id: 'mg-perms', title: 'What you can do', hint: 'by area — open one to see each permission and the role that gives it', content: h('div', { class: 'stack gap-2' }, granted.map(function (dm) {
            var src = {}; dm.items.forEach(function (p) { p.from.forEach(function (r) { src[r] = 1; }); });
            return E.Collapsible({ id: 'mg-dom-' + dm.domain, title: dm.domain, summary: dm.items.length + ' of ' + dm.total + ' · from ' + Object.keys(src).map(roleName).join(', '), open: ctx.query.open === dm.domain,
              content: h('ul', { class: 'work-list work-perms' }, dm.items.map(function (p) { return h('li', h('span', p.label), h('small', { class: 'mono hide-sm' }, p.code), h('small', 'from ' + p.from.map(roleName).join(', '))); })) }); }),
            none.length ? h('p', { class: 'muted t-xs' }, 'Nothing granted in: ' + none.map(function (d) { return d.domain; }).join(', ') + '. If your work needs it, ask ' + adminNames() + '.') : null) }),
          E.Section({ id: 'mg-projects', title: 'Projects', hint: a.projects.filter(function (p) { return p.edit; }).length + ' of ' + a.projects.length + ' where you can enter data', content: simpleTable('mg-projects-tbl', 'Your projects', [{ label: 'Project' }, { label: 'Access' }, { label: 'Comes from' }], a.projects.map(function (p) {
            return [E.twoLine(p.name, p.id), p.level, p.source ? E.twoLine(p.source, p.sourceCode || null) : null]; })) }));
      } }));
    /* те саме, що UnsavedGuard, але словами про вихід: набірний діалог каже «Leave…», а тут дія — «Sign out» */
    ctx.dialog('sign-out-unsaved', function () { E.Dialog({ id: 'mg-signout', title: 'Sign out with 3 unsaved changes?', alert: true, narrow: true,
      body: h('p', 'DOC-000001 has 3 changes that were not saved because the connection dropped. If you sign out now without saving, they will be lost.'),
      footer: [{ label: 'Discard and sign out', variant: 'ghost', align: 'left', id: 'mg-signout-discard', onClick: function () { E.go('/login', { force: true, params: { 'case': 'signed-out' } }); } }, { label: 'Stay signed in', autofocus: true, id: 'mg-signout-stay' },
        { label: 'Save and sign out', variant: 'primary', id: 'mg-signout-save', onClick: function () { E.go('/login', { force: true, params: { 'case': 'signed-out' }, toast: '3 changes saved' }); } }] }); });
    ctx.action('Sign out', 'logout', signOut);
  } });

  /* ================= /404 · /403 · /error ================= */
  E.screen('/404', { title: 'Page not found', icon: 'route', render: function (el, ctx) {
    var path = ctx.path === '/404' ? null : ctx.path, last = path ? path.split('/').filter(Boolean).pop().toLowerCase().slice(0, 5) : '';
    var near = last ? E.screens().filter(function (s) { return !s.keys.length && s.chrome !== false && s.path.toLowerCase().indexOf(last) >= 0 && s.path !== '/404'; }).slice(0, 3) : [];
    el.appendChild(statePage({ id: 'nf', icon: 'route', title: 'This page does not exist', body: [
      h('p', path ? 'Nothing in ECR Web lives at this address. The link may be old, or the address was typed with a mistake. Your data is not affected.' : 'Nothing in ECR Web lives at the address you followed. Your data is not affected.'),
      path ? h('div', { class: 'st-row' }, h('span', { class: 'chip', title: 'The address you opened' }, path)) : null,
      near.length ? h('p', 'Did you mean ', near.map(function (s, i) { return [i ? ', ' : '', h('a', { href: '#' + s.path }, s.title)]; }), '?') : null,
      h('div', { class: 'st-row' }, ctx.from ? E.Button({ label: 'Back to ' + ctx.from.label, icon: 'arrowL', id: 'nf-back', onClick: function () { E.back('#/'); } }) : null,
        E.Button({ label: 'Search screens and documents', icon: 'search', kbd: 'Ctrl K', id: 'nf-search', onClick: openPalette }), E.Button({ label: 'Go to Documents', variant: 'primary', id: 'nf-home', href: '#/' }))] }));
  } });

  E.screen('/403', { title: 'Access denied', icon: 'ban', render: function (el, ctx) {
    var pm = permission(ctx.query.need || '') || permission('Security.ViewRoles'), me = ctx.me;
    var roles = D.roles.filter(function (r) { return D.granted(r.id, pm.code); }).map(function (r) { return r.name; });
    el.appendChild(statePage({ id: 'fb', icon: 'ban', title: 'You don’t have access to this page', body: [
      h('p', 'It needs the permission ', h('b', { class: 't-b' }, '“' + pm.label + '”'), ', and none of your roles (' + me.roles.map(roleName).join(', ') + ') includes it. Nothing was changed.'),
      h('ul', { class: 'conseq work-state-wide' }, h('li', { class: 'note' }, icon('info'), h('span', 'Roles that include it: ' + roles.join(', ') + '.')), h('li', { class: 'note' }, icon('info'), h('span', 'Who can give you such a role: ' + adminNames() + '.'))),
      h('div', { class: 'st-row' }, h('span', { class: 'chip', title: 'Permission code — include it in your request' }, pm.code), h('span', { class: 'chip', title: 'Stable error code' }, 'ECR-SEC-0403'),
        E.Button({ iconOnly: true, icon: 'copy', label: 'Copy request details', id: 'fb-copy', onClick: function () { copy('ECR Web access request · ' + me.fullName + ' · ' + me.login + ' · needs ' + pm.code + ' (' + pm.label + ')', 'Request details copied — paste them into your message'); } })),
      h('div', { class: 'st-row' }, E.Button({ label: 'See my access', icon: 'users', id: 'fb-groups', href: '#/my-groups' }), E.Button({ label: ctx.from ? 'Back to ' + ctx.from.label : 'Back to Documents', icon: 'arrowL', variant: 'primary', id: 'fb-back', onClick: function () { E.back('#/'); } }))] }));
  } });

  E.screen('/error', { title: 'Something went wrong', icon: 'alertCircle', render: function (el, ctx) {
    var q = ctx.query, newVersion = q['case'] === 'new-version', unsaved = q.unsaved != null ? Math.max(0, +q.unsaved || 0) : (newVersion ? 0 : 3), corr = 'c41f7a2e-9b03-4d6e-8a15-5e0b7d3f2a90';
    var from = ctx.from || { label: 'DOC-000001', href: '#/documents/DOC-000001' }, slot = h('div', { class: 'work-state-wide' });
    function changes(n) { return F.plural(n, { one: 'unsaved change', other: 'unsaved changes' }); }
    function reload() { E.go(from.href, { force: true, toast: newVersion ? 'Reloaded — you are on version ' + nextVersion() : 'Screen reloaded' }); }
    function paintUnsaved() { slot.textContent = ''; if (!unsaved) return;
      slot.appendChild(E.Banner({ tone: 'warning', id: 'err-unsaved', title: changes(unsaved) + ' — still in this browser', text: 'They were typed before the screen stopped. Save them now, before you reload.', actions: [{ label: 'Save now', icon: 'save', variant: 'primary', id: 'err-save', onClick: function () { var n = unsaved; unsaved = 0; slot.textContent = ''; reloadBtn.classList.add('btn-primary'); slot.appendChild(E.ResultBanner({ id: 'err-saved', title: changes(n).replace('unsaved ', '') + ' saved at ' + F.time(D.today), text: 'It is safe to reload now.', actions: [{ label: 'Reload', icon: 'refresh', onClick: reload }] })); } }] })); }
    /* одна головна дія: доки є незбережене — «Save now», після збереження — «Reload» */
    var reloadBtn = E.Button({ label: 'Reload', icon: 'refresh', variant: unsaved ? null : 'primary', id: 'err-reload', onClick: function () { if (unsaved) ctx.openDialog('reload-unsaved'); else reload(); } });
    paintUnsaved();
    el.appendChild(statePage({ id: 'err', icon: newVersion ? 'refresh' : 'alertCircle', err: !newVersion, title: newVersion ? 'ECR Web was updated while you were working' : 'This screen stopped working', body: [
      h('p', newVersion ? 'Version ' + nextVersion() + ' was installed and this tab still runs the previous one, so the screen “' + from.label + '” could not open. Reload to continue — saved data is not affected.' : 'Something failed while drawing “' + from.label + '”. Everything already saved is safe, and nothing was sent to the server by this error.'),
      slot,
      newVersion ? null : h('div', { class: 'st-row' }, h('span', { class: 'chip', title: 'Stable error code' }, 'ECR-UI-0500'), h('span', { class: 'chip', title: 'Correlation ID — include it when contacting support' }, corr), E.Button({ iconOnly: true, icon: 'copy', label: 'Copy correlation ID', id: 'err-copy', onClick: function () { copy(corr, 'Correlation ID copied'); } })),
      h('div', { class: 'st-row' }, E.Button({ label: 'Go to Documents', id: 'err-home', onClick: function () { if (unsaved) ctx.openDialog('reload-unsaved', 'home'); else E.go('/', { force: true }); } }), reloadBtn),
      newVersion ? null : h('div', { class: 'work-state-wide' }, E.Collapsible({ id: 'err-details', title: 'Technical details', summary: 'for support', content: E.CodeText('TypeError: Cannot read properties of undefined (reading \'sheets\')\n  at renderDoc (screen-document)\n  at render (router)\nscreen: ' + from.href + '\nversion: ' + D.health.application[0][1] + '\ncorrelation: ' + corr, { block: true }) }))] }));
    ctx.dialog('reload-unsaved', function (where) { var n = unsaved || 3; E.ConfirmDialog({ id: 'err-confirm', title: (where === 'home' ? 'Leave' : 'Reload') + ' without saving ' + changes(n) + '?', consequences: [changes(n) + ' typed on “' + from.label + '” will be lost.', { text: 'Choose Cancel and then “Save now” to keep them.', note: true }], verb: where === 'home' ? 'Leave without saving' : 'Reload without saving', icon: 'refresh', onConfirm: function () { unsaved = 0; if (where === 'home') E.go('/', { force: true }); else reload(); } }); });
    ctx.proto({ id: 'err-variant', label: 'Variant', buttons: [{ label: 'Render error · 3 unsaved', onClick: function () { E.go('/error', { force: true }); } }, { label: 'Render error · nothing unsaved', onClick: function () { E.go('/error', { force: true, params: { unsaved: 0 } }); } }, { label: 'New version installed', onClick: function () { E.go('/error', { force: true, params: { 'case': 'new-version' } }); } }] });
  } });

  /* ================= сценарії ================= */
  var RET = '%2Fdocuments%2FDOC-000001';
  E.flow('work-login', { group: 'Sign-in & access', title: 'Sign in — every branch', actor: 'Any user', note: 'Local sign-in demo: password “wrong” → error, user r.dzhaksybekov → locked, password starting with “Temp” → one-time password.', steps: [
    { label: 'Sign in (Windows is the main action)', href: '#/login' }, { label: 'Signing in…', href: '#/login?state=loading' }, { label: 'Windows account not registered', href: '#/login?case=not-registered' },
    { label: 'Local account form', href: '#/login?case=local' }, { label: 'Caps Lock hint', href: '#/login?case=capslock' }, { label: 'Wrong password · attempts left', href: '#/login?state=error' }, { label: 'Same error in Russian', href: '#/login?case=wrong&lang=ru' },
    { label: 'Account locked · minutes left', href: '#/login?case=locked' }, { label: 'Session expired · restore 3 unsaved changes', href: '#/login?case=expired&return=' + RET }, { label: 'Interface texts not loaded', href: '#/login?case=i18n&lang=en' }, { label: 'Signed in → Documents', href: '#/?reset=1' }] });
  E.flow('work-first-login', { group: 'Sign-in & access', title: 'First sign-in with a one-time password', actor: 'Local account (R. Dzhaksybekov)', steps: [
    { label: 'Local account form', href: '#/login?case=local&lang=en' }, { label: 'Forced password change · why', href: '#/change-password?reason=temporary' }, { label: 'Field errors and live checklist', href: '#/change-password?reason=temporary&case=invalid' },
    { label: 'Server did not answer', href: '#/change-password?reason=temporary&state=error' }, { label: 'Password expired (same screen)', href: '#/change-password?reason=expired' }, { label: 'Saved → Documents (toast)', href: '#/?reset=1' }] });
  E.flow('work-account', { group: 'Sign-in & access', title: 'User menu: my access, password, sign out', actor: 'Any user', steps: [
    { label: 'My groups', href: '#/my-groups' }, { label: 'Change password · Windows account', href: '#/change-password' }, { label: 'Change password · local account', href: '#/change-password?account=local' }, { label: 'Field errors', href: '#/change-password?account=local&case=invalid' },
    { label: 'Sign out with unsaved changes', href: '#/my-groups?dialog=sign-out-unsaved' }, { label: 'Signed out', href: '#/login?case=signed-out' }] });
  E.flow('work-create-document', { group: 'Reporting — documents', title: 'Create a document', actor: 'Data entry', steps: [
    { label: 'Documents of the period', href: '#/?reset=1' }, { label: 'New document · project', href: '#/?dialog=create-document' }, { label: 'Template and version (early “already exists” note)', href: '#/?dialog=create-document-template' }, { label: 'Period step · this document already exists', href: '#/?dialog=create-document-exists' },
    { label: 'Sheets to include', href: '#/?dialog=create-document-sheets' }, { label: 'Review before creating', href: '#/?dialog=create-document-review' }, { label: 'Created → the document opens (toast)', href: '#/?dialog=result-created' }, { label: 'No permission to create (Approver)', href: '#/?dialog=create-denied&as=Approver' }] });
  E.flow('work-find-document', { group: 'Reporting — documents', title: 'Find a document and come back to the same filters', actor: 'Data entry · Approver', steps: [
    { label: 'Documents', href: '#/?reset=1' }, { label: 'Click a StatStrip figure: validation errors', href: '#/?reset=1&stat=issues' }, { label: 'Filter: drafts, only mine', href: '#/?reset=1&status=Draft&mine=1' }, { label: 'Quick look', href: '#/?panel=DOC-000001' },
    { label: 'Open the document', href: '#/documents/DOC-000001?period=2026-09' }, { label: 'Back — filters are still there', href: '#/' }, { label: 'Board view', href: '#/?reset=1&view=board' }, { label: 'Filters match nothing', href: '#/?reset=1&q=zzz' },
    { label: 'Closed period (read-only)', href: '#/?reset=1&period=2026-08' }, { label: 'Period not opened yet', href: '#/?reset=1&period=2026-10' }] });
  E.flow('work-no-access', { group: 'Sign-in & access', title: 'No access: what the user sees and where to go', actor: 'User without groups', steps: [
    { label: 'Windows account not registered', href: '#/login?case=not-registered' }, { label: 'Documents · no project access', href: '#/?case=no-access' }, { label: 'My groups · ticket has no groups', href: '#/my-groups?state=empty' },
    { label: 'My groups · where access comes from', href: '#/my-groups' }, { label: 'Page needs a permission (403)', href: '#/403?need=Security.ViewRoles' }] });
  E.flow('work-errors', { group: 'Sign-in & access', title: 'Service pages: 404, 403, render error, new version', actor: 'Any user', steps: [
    { label: 'Unknown address (404)', href: '#/admin/template' }, { label: 'Access denied (403)', href: '#/403?need=Template.Publish' }, { label: 'Screen stopped · 3 unsaved changes', href: '#/error' }, { label: 'Reload without saving?', href: '#/error?dialog=reload-unsaved' },
    { label: 'Screen stopped · nothing unsaved', href: '#/error?unsaved=0' }, { label: 'Updated while working', href: '#/error?case=new-version' }, { label: 'Banner: new version installed', href: '#/?reset=1&dialog=new-version' }] });
})(window.ECR);
