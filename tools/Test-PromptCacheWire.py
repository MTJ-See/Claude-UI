"""Offline wire check against an installed Claude CLI; Python standard library only.

Uses synthetic input/credentials and a loopback Messages API. No real model calls.
"""
import argparse
import copy
import json
import os
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def without_cache(value):
    if isinstance(value, dict):
        return {k: without_cache(v) for k, v in value.items() if k != "cache_control"}
    if isinstance(value, list):
        return [without_cache(v) for v in value]
    return value


def cache_marks(value):
    if isinstance(value, dict):
        return ([value["cache_control"]] if "cache_control" in value else []) + [
            mark for v in value.values() for mark in cache_marks(v)
        ]
    if isinstance(value, list):
        return [mark for v in value for mark in cache_marks(v)]
    return []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, help="Absolute path to native claude.exe")
    args = parser.parse_args()
    captured = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if self.path.split("?")[0] != "/v1/messages":
                self.send_response(404)
                self.end_headers()
                return
            captured.append(copy.deepcopy(body))
            message = dict(id="msg_fixture", type="message", role="assistant", model=body["model"],
                           content=[], stop_reason=None, stop_sequence=None,
                           usage=dict(input_tokens=20, output_tokens=0))
            events = [
                ("message_start", dict(type="message_start", message=message)),
                ("content_block_start", dict(type="content_block_start", index=0,
                                             content_block=dict(type="text", text=""))),
                ("content_block_delta", dict(type="content_block_delta", index=0,
                                             delta=dict(type="text_delta", text="OK"))),
                ("content_block_stop", dict(type="content_block_stop", index=0)),
                ("message_delta", dict(type="message_delta", delta=dict(stop_reason="end_turn", stop_sequence=None),
                                       usage=dict(output_tokens=1))),
                ("message_stop", dict(type="message_stop")),
            ]
            wire = "".join(f"event: {kind}\ndata: {json.dumps(data)}\n\n" for kind, data in events).encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(wire)))
            self.send_header("request-id", "req_cache_fixture")
            self.end_headers()
            self.wfile.write(wire)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="ClaudeUI-cache-wire-") as work:
            baseline = None
            for policy in ("off", "5m", "1h"):
                env = {k: v for k, v in os.environ.items() if not k.upper().startswith(
                    ("ANTHROPIC_", "CLAUDE_", "DISABLE_PROMPT_CACHING", "ENABLE_PROMPT_CACHING", "FORCE_PROMPT_CACHING"))}
                env.update(ANTHROPIC_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                           ANTHROPIC_API_KEY="synthetic-not-a-real-key", CLAUDE_CONFIG_DIR=work,
                           CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST="1", CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC="1",
                           CLAUDE_CODE_MAX_RETRIES="0", CLAUDE_CODE_MAX_OUTPUT_TOKENS="64",
                           DISABLE_PROMPT_CACHING="1" if policy == "off" else "0",
                           FORCE_PROMPT_CACHING_5M="1" if policy == "5m" else "0",
                           ENABLE_PROMPT_CACHING_1H="1" if policy == "1h" else "0",
                           CLAUDE_CODE_PROMPT_CACHE_TTL=policy, CLAUDE_CODE_SUBAGENT_PROMPT_CACHE_TTL=policy)
                command = [args.cli, "--print", "--output-format", "stream-json", "--verbose",
                           "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--tools", "",
                           "--setting-sources", "", "--max-turns", "1", "--model", "claude-sonnet-4-5",
                           "--system-prompt", "Reply with OK. Preserve all input."]
                start = len(captured)
                completed = subprocess.run(command, input="完整保留 {\"x\":1}  空白\nEND", encoding="utf-8",
                                           capture_output=True, env=env, cwd=work, timeout=30)
                assert completed.returncode == 0, f"CLI failed for {policy}: {completed.stderr[-500:]}"
                requests = captured[start:]
                assert len(requests) == 1, f"Expected one request for {policy}, got {len(requests)}"
                request = requests[0]
                semantic = without_cache({k: request.get(k) for k in (
                    "model", "system", "messages", "tools", "tool_choice", "thinking", "output_config", "max_tokens")})
                if baseline is None:
                    baseline = semantic
                assert semantic == baseline, f"Model input changed under {policy}"
                marks = cache_marks(request)
                if policy == "off":
                    assert not marks, "Disabled caching still sent cache markers"
                else:
                    assert marks, f"No cache markers for {policy}"
                    assert all(mark.get("ttl", "5m") == policy for mark in marks), f"Wrong TTL for {policy}"
                print(f"PASS actual CLI wire: {policy}, {len(marks)} cache markers, identical model input")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    main()
