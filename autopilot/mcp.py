"""`autopilot mcp`: Autopilot as an MCP server (stdio) for the user's own Claude Code or Codex, which then works on the
game on the user's own plan (autopilot/agents/external.py).

    claude mcp add autopilot -- autopilot mcp
    codex mcp add autopilot -- autopilot mcp

It is a front for the agent service the editor's plugin started: it finds it by brain.json (bridge.brain_file: its
"control" address) and passes every call on to it. The game must be open in the editor with the Autopilot tab
connected. A call that takes longer than WAIT_SECONDS (an episode, the game starting: a client gives up on a slow
call, Codex after 60 s) answers "still going", and `wait` gives its result.

Nothing but the protocol goes to stdout.
"""

import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request

PROTOCOL = "2025-06-18"
WAIT_SECONDS = float(os.environ.get("AUTOPILOT_MCP_WAIT") or 40)  # (under Codex's 60 s for one call)

INSTRUCTIONS = ("Autopilot playtests the game open in the user's game editor (Unreal, Unity, Godot or Roblox Studio): "
                "call start_run with the user's request (explore it, test it, verify a claim, reach a goal), then "
                "follow the INSTRUCTIONS.md it names. A long call answers \"still going\": call wait for its result.")
ATTACHED_INSTRUCTIONS = ("The editor already started this run. Follow the briefing and INSTRUCTIONS.md from your "
                         "initial prompt. Use wait for a pending game call and report the outcome in this run.")

NOT_RUNNING = ("Autopilot's agent service isn't running: open the game's project in Unreal, Unity, Godot or Roblox "
               "Studio with the Autopilot plugin, and let the Autopilot tab connect (Roblox Studio's plugin can't "
               "start the agent service: run `autopilot serve` in a terminal first). Then call start_run again.")
TOO_OLD = ("The Autopilot agent service running now is older than this way of using it: update it (the Autopilot "
           "tab's Settings, or reinstall the plugin's agent service), then call start_run again.")

START_SPEC = (
    "start_run",
    "Start an Autopilot run on the game open in the user's editor: Autopilot's decision model plays it, in a harness "
    "you write. request: what the user asks for, in their words (\"Verify that the door opens with the key\", not "
    "\"Use Autopilot to ...\"). kind: verify for a claim to check (\"the door opens with "
    "the key\": CONFIRMED, DENIED or INCONCLUSIVE), goal for something to make happen in play (\"sell three crops\"), "
    "explore to look for bugs (\"the market\") or to test the game (\"go through the level and tell me everything "
    "that looks wrong\"). minutes: an exploration's minutes of play (default 10). "
    "time_limit_minutes: the run's time limit (default 30). level: the level to play, when you know its path or "
    "name (default: the one open in the editor; the briefing lists the project's levels, and restart_level moves to "
    "the one the request means). Answers with the briefing and the instructions to follow; "
    "the game is paused between calls.",
    {"type": "object", "properties": {
        "request": {"type": "string"}, "kind": {"type": "string", "enum": ["verify", "goal", "explore"]},
        "minutes": {"type": "number"}, "time_limit_minutes": {"type": "number"}, "level": {"type": "string"}},
     "required": ["request", "kind"]})
WAIT_SPEC = (
    "wait",
    "Wait for the call still going (an episode, the game starting) and give its result. If it is still going after "
    f"about {WAIT_SECONDS:.0f} s, it says so: call wait again.",
    {"type": "object", "properties": {}})
DESCRIBE_HERE = ("Fallback only (use your describer subagent and `frames`, as INSTRUCTIONS.md says): Autopilot's own "
                 "vision model describes an episode's frames; it works only where the user has a key or Aura for "
                 "one. ")
READ_ONLY = {"wait", "snapshot", "frames", "describe_frames", "read_file", "list_files", "search"}
FILE_TOOLS = {"read_file", "list_files", "search", "write_file", "edit_file"}  # (quick, and take no game time: never
# held up by a call still going)


def tools(attached=False):
    """The tools as MCP lists them."""
    from .agents.external import FRAMES_SPEC
    from .agents.files import FILE_TOOL_SPECS
    from .agents.session import TOOL_SPECS

    specs = [WAIT_SPEC] if attached else [START_SPEC, WAIT_SPEC]
    for name, description, schema in TOOL_SPECS:
        specs.append((name, DESCRIBE_HERE + description if name == "describe_frames" else description, schema))
    specs += [FRAMES_SPEC, *FILE_TOOL_SPECS]
    return [{"name": n, "description": d, "inputSchema": s,
             "annotations": {"readOnlyHint": n in READ_ONLY, "destructiveHint": False, "openWorldHint": False}}
            for n, d, s in specs]


def client_name(info):
    """The client, as a run records it: claude-code, codex, or the name it gave."""
    name = str((info or {}).get("name") or "").strip()
    low = name.lower()
    return "claude-code" if "claude" in low else "codex" if "codex" in low else (name or None)


def control_urls():
    """The running agent services' control addresses, the newest first (brain.json: the editor's; aura-brain.json: the
    Aura app's brain, which may run beside it with no editor connected), or ([], why)."""
    from . import aura, bridge

    found = []
    for name in (bridge.BRAIN_FILE, aura.BRAIN_FILE):
        try:
            with open(bridge.brain_file(name), encoding="utf-8") as f:
                info = json.load(f)
        except (OSError, ValueError):
            continue
        if isinstance(info, dict) and info.get("url"):
            found.append(info)
    if not found:
        return [], NOT_RUNNING
    with_control = [i for i in found if i.get("control")]
    if not with_control:
        return [], TOO_OLD
    return [i["control"] for i in sorted(with_control, key=lambda i: i.get("started") or 0, reverse=True)], None


def text(t, error=False):
    return {"content": [{"type": "text", "text": t}], "isError": bool(error)}


# A service's answer that passes the call on to the next one: its flag (set false), and the words that begin the same
# answer from an agent service older than the flag
PASSES_ON = {"connected": "Autopilot isn't connected to the editor", "going": "No run is going"}


def passes_on(answer, flag):
    """Whether the answer says this service has no editor connected (flag "connected") or no run going ("going")."""
    if answer.get(flag) is False:
        return True
    return bool(answer.get("isError")) and any(str(c.get("text", "")).startswith(PASSES_ON[flag])
                                               for c in answer.get("content") or [])


def plain(answer):
    """The answer as the client gets it: without the flags meant for this front."""
    for flag in (*PASSES_ON, "reached"):
        answer.pop(flag, None)
    return answer


class Server:
    def __init__(self, out=None, find=control_urls, connection=None):
        self.out = out or sys.stdout
        self.find = find
        self.send_lock = threading.Lock()
        self.lock = threading.Lock()
        self.client = None
        self.attached = connection is not None  # an editor-launched CLI and its helpers serve exactly one run
        self.control = connection["control"] if self.attached else None
        self.run = connection["run"] if self.attached else None
        self.pending = None  # (thread, box, tool, started) of a call still going

    def send(self, message):
        with self.send_lock:
            self.out.write(json.dumps(message) + "\n")
            self.out.flush()

    def post(self, url, body):
        req = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="POST",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=None) as r:  # (an episode may take minutes)
                return json.loads(r.read().decode("utf-8", "replace"))
        except (urllib.error.URLError, OSError, ValueError) as e:
            return {**text(f"Autopilot's agent service couldn't be reached ({e}). {NOT_RUNNING}", error=True),
                    "reached": False}

    def first_taker(self, path, body, flag):
        """The call made on each agent service running, the newest first, until one takes it: one with no editor
        connected, or no run going (flag), passes it on (the Aura app's brain runs beside the editor's service, and
        may have started after it). (its address, its answer), else (None, why none took it)."""
        urls, why = self.find()
        if not urls:
            return None, text(why, error=True)
        refused = unreached = None
        for url in urls:
            answer = self.post(f"{url}/{path}", body)
            if answer.get("reached") is False:
                unreached = unreached or answer
            elif passes_on(answer, flag):
                refused = refused or answer
            else:
                return url, answer
        return None, refused or unreached

    def forward(self, name, args):
        """The call, made on the agent service (blocking)."""
        if name == "start_run":
            if self.attached:
                return text("The editor has already started this run. Use its briefing to continue.", error=True)
            url, answer = self.first_taker("start", {**(args or {}), "client": self.client}, "connected")
            if url is not None and not answer.get("isError"):
                self.control = url
                first = next((c.get("text", "") for c in answer.get("content") or [] if c.get("type") == "text"), "")
                self.run = first.split()[2] if first.startswith("Autopilot run ") else None
            return plain(answer)
        body = {"run": self.run, "args": args or {}}
        if self.control is not None:
            return plain(self.post(f"{self.control}/call/{name}", body))
        # (a front that started no run works on the run going: Codex starts one of these for each of its subagents,
        # the frames' describer among them; the agent service runs one at a time)
        return plain(self.first_taker(f"call/{name}", body, "going")[1])

    def call(self, name, args):
        """The call's result, or "still going" once it has taken WAIT_SECONDS (wait gives it then)."""
        if name in FILE_TOOLS:
            return self.forward(name, args)
        with self.lock:
            pending = self.pending
            if name == "wait":
                if pending is None:
                    return text("Nothing is going: no call is waiting for its result.")
            elif pending is not None:
                return text(f"{pending[2]} is still going ({time.time() - pending[3]:.0f} s): call wait for its "
                            "result first.", error=True)
            else:
                box = {}
                thread = threading.Thread(target=lambda: box.update(result=self.forward(name, args)), daemon=True)
                thread.start()
                pending = self.pending = (thread, box, name, time.time())
        thread, box, tool, started = pending
        thread.join(WAIT_SECONDS)
        with self.lock:
            if thread.is_alive():
                return text(f"{tool} is still going ({time.time() - started:.0f} s): call wait for its result.")
            if self.pending is pending:
                self.pending = None
            return box.get("result") or text(f"{tool} gave no result.", error=True)

    def handle(self, msg):
        method, mid = msg.get("method"), msg.get("id")
        if mid is None:
            return  # (a notification: initialized, cancelled)
        if method == "initialize":
            params = msg.get("params") or {}
            self.client = client_name(params.get("clientInfo"))
            self.send({"jsonrpc": "2.0", "id": mid, "result": {
                "protocolVersion": params.get("protocolVersion") or PROTOCOL,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": "autopilot", "version": _version()},
                "instructions": ATTACHED_INSTRUCTIONS if self.attached else INSTRUCTIONS}})
        elif method == "tools/list":
            self.send({"jsonrpc": "2.0", "id": mid, "result": {"tools": tools(attached=self.attached)}})
        elif method == "tools/call":
            params = msg.get("params") or {}

            def run():
                try:
                    result = self.call(params.get("name"), params.get("arguments"))
                except Exception as e:  # noqa: BLE001 - the client is told
                    result = text(f"Autopilot failed: {type(e).__name__}: {e}", error=True)
                self.send({"jsonrpc": "2.0", "id": mid, "result": result})

            threading.Thread(target=run, daemon=True).start()
        elif method == "ping":
            self.send({"jsonrpc": "2.0", "id": mid, "result": {}})
        else:
            self.send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": f"No method {method}"}})

    def serve(self, lines):
        for line in lines:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if isinstance(msg, dict):
                self.handle(msg)


def _version():
    from . import __version__

    return __version__


def main(attach=None):
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    out, sys.stdout = sys.stdout, sys.stderr  # (whatever else prints goes to stderr: stdout is the protocol's)
    connection = None
    if attach:
        with open(attach, encoding="utf-8") as f:
            connection = json.load(f)
    Server(out=out, connection=connection).serve(sys.stdin)  # ends when the client closes stdin
    return 0
