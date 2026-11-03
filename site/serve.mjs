import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const siteDirectory = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(siteDirectory, 'dist');
const port = Number(process.env.PORT || 4173);
const types = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.xml', 'application/xml; charset=utf-8'],
  ['.yaml', 'application/yaml; charset=utf-8'],
  ['.yml', 'application/yaml; charset=utf-8']
]);

createServer(async (request, response) => {
  const requestedPath = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
  let filePath = path.resolve(root, `.${requestedPath}`);
  if (!filePath.startsWith(root)) return send(response, 403, 'Forbidden');

  try {
    const info = await stat(filePath);
    if (info.isDirectory()) filePath = path.join(filePath, 'index.html');
    await stat(filePath);
  } catch {
    filePath = path.join(root, '404.html');
    response.statusCode = 404;
  }

  response.setHeader('Content-Type', types.get(path.extname(filePath)) || 'application/octet-stream');
  response.setHeader('Cache-Control', 'no-store');
  createReadStream(filePath).pipe(response);
}).listen(port, '127.0.0.1', () => {
  console.log(`BOS Core docs: http://127.0.0.1:${port}`);
});

function send(response, status, body) {
  response.statusCode = status;
  response.end(body);
}
