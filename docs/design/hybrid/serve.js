// Статичний сервер для макета: node docs/design/hybrid/serve.js [порт]
// ⚠ index.html — фрагмент без <!doctype>/<head>/<body> (так його публікує
// оболонка артефактів), тому тут він загортається в каркас із charset:
// відкритий напряму з диска, він показав би кирилицю й «−» кракозябрами.
const http = require('http'), fs = require('fs'), path = require('path');
const root = __dirname;
const port = Number(process.argv[2]) || 5197;
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.md': 'text/plain; charset=utf-8', '.txt': 'text/plain; charset=utf-8' };
http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/' || p === '') p = '/index.html';
  const file = path.join(root, p);
  if (!file.startsWith(root)) { res.writeHead(403); return res.end(); }
  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404); return res.end('not found'); }
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    if (p === '/index.html') {
      res.end('<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body>' + buf.toString('utf8') + '</body></html>');
    } else res.end(buf);
  });
}).listen(port, '127.0.0.1', () => console.log('hybrid on http://127.0.0.1:' + port + '/#/flows'));
