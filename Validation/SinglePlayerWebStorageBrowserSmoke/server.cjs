const http = require('http');
const fs = require('fs');
const path = require('path');
http.createServer((request, response) => {
  if (request.url === '/save.js') {
    response.setHeader('Content-Type', 'text/javascript; charset=utf-8');
    response.end(fs.readFileSync(path.join(__dirname, '../../Assets/Plugins/WebGL/SinglePlayerSave.jslib')));
  } else {
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end(fs.readFileSync(path.join(__dirname, 'index.html')));
  }
}).listen(8127, '127.0.0.1', () => console.log('Save validation server: http://127.0.0.1:8127'));
