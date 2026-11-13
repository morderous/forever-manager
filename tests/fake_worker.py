"""A stand-in for the friends' Worker (forever-friends\\worker\\worker.js), on 127.0.0.1, over a
folder that stands for <bucket>/<prefix>. The same routes and rules: the key in X-Forever-Key
(the fake one of make_secrets.ps1 -Fake), ?k= on /latest/, 404 for anything else, Range
(a-b, a-, -n; 416 past the end), HEAD. Each request is logged to <folder>/../worker-log.jsonl
(path, method, range, whether the key came, whether an Authorization header came).

    python -I fake_worker.py <folder> <port> [link key]
"""
import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

FAKE_KEY = "FAKE_TEST_FRIENDS_KEY_0123456789_abcdefghijklmnopqrs"
PATH_OK = re.compile(r"^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$")
ROOT = os.path.abspath(sys.argv[1])
LINK_KEY = sys.argv[3] if len(sys.argv) > 3 else "FAKE_LINK_KEY"
LOG = os.path.join(os.path.dirname(ROOT), "worker-log.jsonl")


class Worker(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_HEAD(self):
        self.handle_it(True)

    def do_GET(self):
        self.handle_it(False)

    def nothing(self, code):
        self.send_response(code)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def handle_it(self, head):
        path, _, query = self.path.partition("?")
        keyed = self.headers.get("X-Forever-Key") == FAKE_KEY
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": path, "method": self.command, "range": self.headers.get("Range"),
                                "key": keyed, "agent": self.headers.get("User-Agent"), "authorization": self.headers.get("Authorization") is not None}) + "\n")
        if path.startswith("/latest/"):
            link = dict(p.split("=", 1) for p in query.split("&") if "=" in p).get("k") == LINK_KEY
            if not keyed and not link:
                return self.nothing(404)
            m = re.match(r"^/latest/([a-z0-9-]{1,40})\.zip$", path)
            man = os.path.join(ROOT, "manifest.json")
            if not m or not os.path.isfile(man):
                return self.nothing(404)
            entry = json.load(open(man, encoding="utf-8")).get("latest", {}).get(m.group(1))
            if not entry or not PATH_OK.match(entry["path"]) or ".." in entry["path"]:
                return self.nothing(404)
            return self.serve(entry["path"], head, {"Content-Disposition": 'attachment; filename="%s"' % entry["name"]})
        if not keyed:
            return self.nothing(404)
        if path in ("/manifest.json", "/pack.json"):
            return self.serve(path[1:], head, {"Cache-Control": "no-store"})
        if path.startswith("/f/"):
            rest = path[3:]
            if not PATH_OK.match(rest) or ".." in rest or rest in ("manifest.json", "pack.json"):
                return self.nothing(404)
            return self.serve(rest, head, {})
        return self.nothing(404)

    def serve(self, rel, head, extra):
        full = os.path.join(ROOT, *rel.split("/"))
        if not os.path.isfile(full):
            return self.nothing(404)
        size = os.path.getsize(full)
        start, end, partial = 0, size - 1, False
        asked = self.headers.get("Range")
        if asked:
            m = re.match(r"^bytes=(\d*)-(\d*)$", asked.strip())
            if not m or (m.group(1) == "" and m.group(2) == ""):
                return self.out(size)
            if m.group(1) == "":
                last = int(m.group(2))
                if last == 0:
                    return self.out(size)
                start = max(0, size - last)
            else:
                start = int(m.group(1))
                if m.group(2):
                    end = min(int(m.group(2)), size - 1)
                if start >= size or start > end:
                    return self.out(size)
            partial = True
        self.send_response(206 if partial else 200)
        for k, v in extra.items():
            self.send_header(k, v)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if partial:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()
        if head:
            return
        with open(full, "rb") as f:
            f.seek(start)
            left = end - start + 1
            while left > 0:
                chunk = f.read(min(1 << 20, left))
                if not chunk:
                    break
                self.wfile.write(chunk)
                left -= len(chunk)

    def out(self, size):
        self.send_response(416)
        self.send_header("Content-Range", "bytes */%d" % size)
        self.send_header("Content-Length", "0")
        self.end_headers()


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", int(sys.argv[2])), Worker).serve_forever()
