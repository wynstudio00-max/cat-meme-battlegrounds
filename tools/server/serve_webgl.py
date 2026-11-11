"""Serve the WebGL build to phones on the same Wi-Fi (2026-10-28).

    python Tools/Server/serve_webgl.py            # http on 8080 + https on 8443
    python Tools/Server/serve_webgl.py 8090       # http on 8090 + https on 8090+363

Phones' Chrome often switches to https by itself ("site can't provide a secure connection" when only http exists), so an https
port runs too, with a self-signed certificate made once with openssl (Git for Windows has one) in Tools/Server/.cert/. The phone
shows a warning the first time: Advanced -> Proceed. Sends the Brotli files (*.br) with Content-Encoding: br and the right MIME types.
"""
import http.server
import os
import shutil
import socket
import ssl
import subprocess
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, '..', '..', 'CatBattleground', 'Builds', 'WebGL')
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
SPORT = 8443 if PORT == 8080 else PORT + 363
CERT_DIR = os.path.join(HERE, '.cert')

TYPES = {'.js': 'application/javascript', '.wasm': 'application/wasm', '.data': 'application/octet-stream',
         '.json': 'application/json', '.html': 'text/html', '.css': 'text/css', '.png': 'image/png', '.ico': 'image/x-icon'}


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def guess_type(self, path):
        p = path[:-3] if path.endswith('.br') or path.endswith('.gz') else path
        return TYPES.get(os.path.splitext(p)[1].lower(), super().guess_type(path))

    def end_headers(self):
        path = getattr(self, 'path', '').split('?')[0]   # no path on a bad request (e.g. https sent to the http port)
        if path.endswith('.br'):
            self.send_header('Content-Encoding', 'br')
        elif path.endswith('.gz'):
            self.send_header('Content-Encoding', 'gzip')
        self.send_header('Cache-Control', 'no-cache')
        super().end_headers()


def lan_addresses():
    found = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if not ip.startswith('127.') and not ip.startswith('169.'):
                found.add(ip)
    except OSError:
        pass
    return sorted(found)


def find_openssl():
    for p in (shutil.which('openssl'), r'C:\Program Files\Git\usr\bin\openssl.exe', r'C:\Program Files\Git\mingw64\bin\openssl.exe'):
        if p and os.path.isfile(p):
            return p
    return None


def make_cert(ips):
    """Self-signed certificate for localhost + the PC's LAN addresses (remade when the addresses change)."""
    os.makedirs(CERT_DIR, exist_ok=True)
    cert, key, stamp = (os.path.join(CERT_DIR, n) for n in ('cert.pem', 'key.pem', 'ips.txt'))
    names = ','.join(['DNS:localhost', 'IP:127.0.0.1'] + ['IP:' + ip for ip in ips])
    if os.path.isfile(cert) and os.path.isfile(key) and os.path.isfile(stamp) and open(stamp).read() == names:
        return cert, key
    openssl = find_openssl()
    if not openssl:
        return None
    subprocess.run([openssl, 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '825', '-keyout', key, '-out', cert,
                    '-subj', '/CN=CatBattleground local test', '-addext', 'subjectAltName=' + names],
                   check=True, capture_output=True)
    with open(stamp, 'w') as f:
        f.write(names)
    return cert, key


if __name__ == '__main__':
    if not os.path.isfile(os.path.join(ROOT, 'index.html')):
        sys.exit('No build at ' + os.path.abspath(ROOT) + ' (Unity: CatBattleground > Build > WebGL Client)')
    ips = lan_addresses()
    print('Serving', os.path.abspath(ROOT))

    https = None
    try:
        pair = make_cert(ips)
        if pair:
            https = http.server.ThreadingHTTPServer(('0.0.0.0', SPORT), Handler)
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ctx.load_cert_chain(*pair)
            https.socket = ctx.wrap_socket(https.socket, server_side=True)
            threading.Thread(target=https.serve_forever, daemon=True).start()
        else:
            print('  (no openssl found: https off)')
    except Exception as e:
        print('  (https off:', e, ')')

    for ip in ips:
        print(f'  phone:  http://{ip}:{PORT}/' + (f'    or  https://{ip}:{SPORT}/  (warning -> Advanced -> Proceed)' if https else ''))
    print(f'  this PC: http://localhost:{PORT}/?touch=1   (touch buttons, mouse acts as a finger)')
    http.server.ThreadingHTTPServer(('0.0.0.0', PORT), Handler).serve_forever()
