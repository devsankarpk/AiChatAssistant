"""Test double for the Python AI service: same contract as POST /generate, with knobs for latency and failures
and counters that prove how many calls were really made. Test use only, never part of the repo."""
import json, os, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY = os.environ.get("INTERNAL_API_KEY", "")
lock = threading.Lock()
state = {"delay_ms": 0, "fail_next": 0, "fail_always": False, "calls": 0, "in_flight": 0, "max_concurrent": 0, "prompts": [], "histories": []}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/__stats":
            with lock:
                return self._send(200, state)
        return self._send(200, {"status": "ok"})

    def do_POST(self):
        if self.headers.get("transfer-encoding", "").lower() == "chunked":  # .NET's HttpClient may send the body chunked
            raw = b""
            while True:
                size = int(self.rfile.readline().strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()
                    break
                raw += self.rfile.read(size)
                self.rfile.readline()
        else:
            raw = self.rfile.read(int(self.headers.get("content-length", "0") or 0))
        body = json.loads(raw or b"{}")
        if self.path == "/__control":
            with lock:
                if body.get("reset"):
                    state.update({"calls": 0, "max_concurrent": 0, "prompts": [], "histories": []})
                for k in ("delay_ms", "fail_next", "fail_always"):
                    if k in body:
                        state[k] = body[k]
            return self._send(200, state)
        if self.path != "/generate":
            return self._send(404, {"detail": "not found"})
        if self.headers.get("X-Internal-Key") != KEY:
            return self._send(401, {"detail": "bad key"})
        with lock:
            state["calls"] += 1
            state["in_flight"] += 1
            state["max_concurrent"] = max(state["max_concurrent"], state["in_flight"])
            state["prompts"].append(body.get("prompt"))
            state["histories"].append(len(body.get("history", [])))
            fail = state["fail_always"] or state["fail_next"] > 0
            if state["fail_next"] > 0:
                state["fail_next"] -= 1
            delay = state["delay_ms"]
        try:
            time.sleep(delay / 1000)
            if fail:
                return self._send(502, {"detail": "stub: upstream failure"})
            return self._send(200, {"reply": f"stub reply to: {body.get('prompt')}", "tokens_used": 7})
        finally:
            with lock:
                state["in_flight"] -= 1


ThreadingHTTPServer(("0.0.0.0", 8000), H).serve_forever()
