/* kit-ext.js — розширення набору для EA-0 БЕЗ змін kit.js/index.html (KIT.md:3).
   Секції «Assigned to me» / «Awaiting my validation» вставляються в шторку «My tasks»: E.Drawer обгортається, і для drawer-tasks
   тіло шторки спостерігається — після кожного перемальовування (paintTasks чистить тіло) секції ставляться нагору.
   Секції дає screen-edit-assignments.js через E.taskSections (масив функцій, що повертають вузол або null). */
(function (E) {
  'use strict';
  E.taskSections = E.taskSections || [];
  var orig = E.Drawer, repaint = null;
  E.Drawer = function (o) {
    var ctl = orig.apply(this, arguments);
    if (o && o.id === 'drawer-tasks' && ctl && ctl.body) {
      var body = ctl.body, busy = false;
      repaint = function () {
        if (busy || !document.contains(body)) return; busy = true;
        var old = body.querySelector('#doc-ea-tasks'); if (old) old.remove();
        var nodes = E.taskSections.map(function (f) { return f(); }).filter(Boolean);
        nodes.reverse().forEach(function (n) { body.insertBefore(n, body.firstChild); });
        var empty = body.querySelector(':scope > .empty'); if (empty && nodes.length) empty.remove();
        busy = false;
      };
      new MutationObserver(function () { if (!body.querySelector('#doc-ea-tasks')) repaint(); }).observe(body, { childList: true });
      setTimeout(repaint, 0);
    }
    return ctl;
  };
  E.ea_repaintTasks = function () { if (repaint) repaint(); };
})(window.ECR);
