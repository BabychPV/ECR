// Статичний сервер мокапу пермітів: node docs/design/permits/serve.js [порт]
// Корінь — docs/design, щоб index.html міг брати kit.js та екрани з ../hybrid/.
// index.html — фрагмент без <head> (як у hybrid), тому тут його загортаємо в каркас із charset.
const http = require('http'), fs = require('fs'), path = require('path');
const root = path.resolve(__dirname, '..');
const port = Number(process.argv[2]) || 5198;
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.md': 'text/plain; charset=utf-8' };
http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/' || p === '') p = '/permits/index.html';
  const file = path.join(root, p);
  if (!file.startsWith(root)) { res.writeHead(403); return res.end(); }
  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404); return res.end('not found'); }
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    if (p === '/permits/index.html') res.end('<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body>' + buf.toString('utf8') + '</body></html>');
    else res.end(buf);
  });
}).listen(port, '127.0.0.1', () => console.log('permits mockup on http://127.0.0.1:' + port + '/permits/index.html#/admin/permits'));