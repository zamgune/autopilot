"""The agent is the user's own Claude Code or Codex, on the user's own plan: Autopilot is an MCP server it uses
(`autopilot mcp`, autopilot/mcp.py). It can start a run from its terminal, or the editor can launch the signed-in CLI
with its MCP connection scoped to one run. Everything else is a run as the editor's Play makes
one (cli.run_prompt): the game started and its codex read, Jev deciding, the recording, steering, the result in the
Autopilot tab, the memory and (shared) the run's data.

    Control     in `autopilot serve` (the agent service the editor's plugin started): a localhost endpoint whose address
                (with a secret) brain.json gives as "control", which `autopilot mcp` finds it by. POST <control>/start
                starts a run with this module as its agent (cmd_serve decides whether one may start); POST
                <control>/call/<tool> is a tool call on the run going. Answers are an MCP tool result as JSON
                ({"content": [...], "isError"}).
    Desk        one run's: the start call's answer once its session is open, then each tool call handed to the session
                (one at a time, as every agent's), until the run ends.
    implement() the agent's entry point, as every agent's: the session opened, INSTRUCTIONS.md (the instructions every
                agent gets, and this route's tools) and REQUEST.md (the request and its briefing) written in the run's
                folder, the start call answered; then it waits while the user's agent calls the tools, until it
                reports, the user stops the run (the tab's Stop), the time is up or the agent stops calling.

Calls wait as long as they take here: `autopilot mcp` answers "still going" when one takes long (its client gives up
on a slow call: Codex after 60 s) and gives the result to the next `wait`.

Looking at the picture: the user's agent hands an episode's frames (`frames`, JPEGs) to a subagent of its own told
only open questions (docs/architecture.md, "Looking at the picture"), so the description comes from the user's plan
too. describe_frames (Autopilot's own vision model) stays a fallback, where the run has a key or Aura for one.
"""

import base64
import contextlib
import http.server
import json
import os
import secrets
import sys
import tempfile
import threading
import time
import traceback

from .. import engines, transcripts
from .files import FILE_TOOLS, FileTools
from .session import (
    PACKAGE,
    PROBING,
    READING,
    REPORT_FIELDS,
    WRITING,
    finish,
    first_message,
    open_session,
    system_prompt,
    watch_steering,
)

INSTRUCTIONS = "INSTRUCTIONS.md"  # in the run's folder: the instructions, as every agent's system prompt has them
REQUEST = "REQUEST.md"  # in the run's folder: the request and its briefing, as the start call answers them
# A run whose agent has called no tool for this long ends: the user's agent went away (closed, or on to other work)
IDLE_SECONDS = 20 * 60
# What the run waits for a call still going when it ends (the user stopped it: the episode ends soon)
END_GRACE = 30.0
# After the report, before the agent's transcript is kept (a shared run): its client writes the report's result first
TRANSCRIPT_DELAY = 3.0
GAME_TOOLS = ("probe", "run_harness", "snapshot", "describe_frames", "frames", "restart_level", "report")

EXTERNAL_TOOLS = """## Your tools here

You are the user's own coding agent, working through Autopilot's MCP server (`autopilot`). Its tools: the game's
(probe, run_harness, snapshot, restart_level, report, and frames and describe_frames, below), wait, and file tools for
this game's folder: read_file, list_files and search (this game's folder and the kit: autopilot/KIT.md,
autopilot/kit/), write_file and edit_file ({run}/harness.py and other files in {run}/, notes.md, lib.py: nothing else
may be changed). Paths are relative to this game's folder, which isn't your working directory: use these file tools
for it, never your own, and don't touch the user's project or the game's files with your own tools. Act on the game
only through the game's tools.

Call the game's tools one at a time: the game runs only inside a call and is paused between them. A call that takes
long (an episode, starting the game) answers "still going": call wait until it gives the result. Once report says
"Reported", the run is over: tell the user its outcome in a few lines (the Autopilot tab in their editor shows the
whole result).

## Looking at the picture here

Here describe_frames above means a describer subagent of yours, not a model of Autopilot's: hand it the moment, it
looks at the episode's frames (the `frames` tool) and answers your open questions. Don't call `frames` yourself:
reading frames while you know what you hope to see colours what you see. Brief it with this, filled in, and nothing
else (never the request, the claim, the harness or what you expect):

    Call the autopilot tool `frames` with episode=<n>, start=<game time>, end=<game time>, count=<up to {max_frames}>.
    Then describe the frames. {describer}
    Questions:
    1. <an open question: what moves and how, what is at a spot, what the text says; never yes/no>
    {sweep}

In Claude Code use the `frame-describer` subagent when there is one (Autopilot's Claude Code plugin adds it), else a
general-purpose one. In Codex spawn the `frame_describer` agent when there is one, else a new agent. Start it with
an empty conversation (`fork_turns="none"` where available). Hold its description against what true and false would
look like, written down before you asked. A request about how something looks is CONFIRMED or DENIED only on such a
description (report it with visual: true). If no subagent can take pictures, use describe_frames where the user
has a key (or Aura) for Autopilot's own describer.
"""

START = """Autopilot run {marker} started on {project}: the game is open in the user's editor and paused between your
calls. First read {instructions} with read_file, all of it (in parts: it is long): it is how to work and what the kit
is. The request and its briefing follow (also in {request}).

{message}"""

FRAMES_SPEC = (
    "frames",
    "For a describer subagent only (not the agent working on the request): up to 12 of an episode's frames, as "
    "images, spread over game times start-end. episode: its number (default: the latest); start, end: game times in "
    "it, in seconds (default: all of it); count: how many (default 8).",
    {"type": "object", "properties": {"episode": {"type": "integer"}, "start": {"type": "number"},
                                      "end": {"type": "number"}, "count": {"type": "integer"}}})


# describe_frames' fallback (Autopilot's own vision model): the first provider the user has a key for (Anthropic's
# through Aura: a run through Aura sets its relay as the Anthropic key)
FALLBACK_PROVIDERS = (("anthropic", ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")), ("openai", ("OPENAI_API_KEY",)),
                      ("openrouter", ("OPENROUTER_API_KEY",)), ("opencode", ("OPENCODE_API_KEY",)),
                      ("google", ("GEMINI_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY", "GOOGLE_API_KEY")))


def current_provider():
    """The provider whose vision model describe_frames falls back on (the run's telemetry routes its calls)."""
    for provider, keys in FALLBACK_PROVIDERS:
        if any((os.environ.get(k) or "").strip() for k in keys):
            return provider
    return "anthropic"


def text_result(text, error=False):
    return {"content": [{"type": "text", "text": text}], "isError": bool(error)}


class Desk:
    """One run's tool calls (autopilot/mcp.py's client: its name, e.g. claude-code or codex)."""

    def __init__(self, client=None, *, cli_client=None):
        self.cli_client = cli_client  # set only when the editor owns the CLI process
        self.client = ("claude-code" if cli_client == "claude" else cli_client) or client
        self.marker = None  # "<run id>-<nonce>": named in what the user's agent is told, so its transcript is found
        self.session = None
        self.files = None
        self.opening = None  # the start call's answer
        self.ready = threading.Event()  # set once the session is open, or the run ended before it opened
        self.lock = threading.Lock()  # one call at a time
        self.last_call = time.time()
        self.busy = False
        self.ended = None  # why the run ended: no call is taken after it
        self.reported_at = None

    def open(self, session, files, opening):
        self.session, self.files, self.opening = session, files, opening
        self.last_call = time.time()
        self.ready.set()

    def end(self, why):
        """The run has ended (why: words, or the report it ended with)."""
        if self.ended is None:
            if isinstance(why, dict):
                why = why.get("summary") or why.get("outcome") or "the run ended"
            self.ended = str(why or "the run ended")
        self.ready.set()

    def start_answer(self, timeout=None):
        """The start call's answer, once the session is open or the run ended before it did."""
        if not self.ready.wait(timeout):
            return text_result("The run is still starting.", error=True)
        if self.session is None:
            return text_result(f"The run didn't start: {self.ended or 'no reason given'}", error=True)
        return text_result(self.opening)

    def call(self, tool, args):
        """A tool call on this run: an MCP tool result (images for frames)."""
        if self.session is None:
            return text_result("No run has started: call start_run first.", error=True)
        if self.ended is not None:
            return text_result(f"This run has ended ({self.ended}). Tell the user its outcome; start_run starts "
                               "another.", error=True)
        s = self.session
        if tool in FILE_TOOLS:
            self.last_call = time.time()
            s.tool_call(tool, args, log=f"{tool}: {json.dumps(args)[:600]}")
            s.say(WRITING if tool in ("write_file", "edit_file") else READING)
            return text_result(self.files.call(tool, args))  # (no clock: file tools take no game time)
        if tool not in GAME_TOOLS:
            return text_result(f"No tool {tool!r} here.", error=True)
        with self.lock:
            self.busy, self.last_call = True, time.time()
            s.tool_call(tool, args, log=f"tool {tool}: {json.dumps(args)[:600]}")
            if tool in ("probe", "describe_frames"):
                s.say(PROBING)
            t0 = time.time()
            images = []
            try:
                if tool == "frames":
                    text, images = s.frames(args.get("episode"), args.get("start"), args.get("end"),
                                            args.get("count"))
                else:
                    text = self._game_tool(tool, args)
            except Exception:
                text = "Tool failed:\n" + traceback.format_exc(limit=5)
            s.tool_seconds += time.time() - t0
            self.busy, self.last_call = False, time.time()
            if tool == "frames" and self.marker and self.marker not in text:
                text += f" (run {self.marker})"  # (every answer names the run: the describer's transcript is found)
            if tool == "report" and s.report is not None:
                self.reported_at = time.time()
            content = [{"type": "text", "text": text + s.steer_note() + s.news() + "\n" + s.clock()}]
            content += [{"type": "image", "data": base64.standard_b64encode(jpeg).decode("ascii"),
                         "mimeType": "image/jpeg"} for jpeg in images]
            return {"content": content, "isError": False}

    def _game_tool(self, tool, args):
        s = self.session
        if tool == "probe":
            return s.probe(str(args.get("code") or ""))
        if tool == "run_harness":
            return s.run_harness(args.get("seconds", 30), args.get("restart", True), frames_fps=args.get("frames_fps"))
        if tool == "snapshot":
            return s.snapshot()
        if tool == "describe_frames":
            return s.describe_frames(args.get("questions") or [], args.get("episode"), args.get("start"),
                                     args.get("end"), args.get("frames"))
        if tool == "restart_level":
            return s.restart_level(args.get("level"))
        return s.do_report({k: args[k] for k in REPORT_FIELDS if k in args})

    def wait_idle(self, timeout):
        """Wait for a call still going to end (up to timeout s)."""
        if self.lock.acquire(timeout=timeout):
            self.lock.release()


class Control:
    """The endpoint `autopilot mcp` reaches this agent service by, at self.url (it carries a secret) until close().
    starter(params, desk): set by cmd_serve; starts a run for a start call (its desk), or returns why it won't."""

    def __init__(self):
        self.starter = None
        self.desk = None
        token = secrets.token_urlsafe(24)
        control = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                parts = self.path.strip("/").split("/")
                if len(parts) < 2 or not secrets.compare_digest(parts[0], token):
                    self.send_error(404)
                    return
                try:
                    body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                except ValueError:
                    body = None
                if not isinstance(body, dict):
                    self.send_error(400)
                    return
                if parts[1:] == ["start"]:
                    answer = control.start(body)
                elif parts[1] == "call" and len(parts) == 3:
                    answer = control.call(parts[2], body)
                else:
                    self.send_error(404)
                    return
                data = json.dumps(answer).encode("utf-8")
                try:
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                except OSError:
                    pass  # (the caller gave up waiting)

            def log_message(self, *a):
                pass

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}/{token}"
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def start(self, params):
        if self.starter is None:  # ("connected": autopilot/mcp.py tries another agent service running then)
            return {**text_result("Autopilot isn't connected to the editor yet: open the game's project with the "
                                  "Autopilot tab, then start_run again.", error=True), "connected": False}
        desk = Desk(client=str(params.get("client") or "") or None)
        try:
            refused = self.starter(params, desk)
        except Exception as e:  # noqa: BLE001 - the user's agent is told
            refused = f"{type(e).__name__}: {e}"
        if refused:
            return text_result(refused, error=True)
        self.desk = desk
        return desk.start_answer()

    def call(self, tool, body):
        desk = self.desk
        if desk is None:  # ("going": as "connected" above)
            return {**text_result("No run is going: call start_run first.", error=True), "going": False}
        if body.get("run") and body.get("run") != desk.marker:
            return text_result("That run isn't going any more (the agent service restarted, or another run "
                               "started): call start_run to start one.", error=True)
        return desk.call(tool, body.get("args") or {})

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


@contextlib.contextmanager
def launch_cli(desk):
    """Give only this CLI and its frame describer an MCP server attached to the editor's already-open run."""
    from . import logged_cli

    with tempfile.TemporaryDirectory(prefix="autopilot-cli-") as folder, contextlib.closing(Control()) as control:
        control.desk = desk
        connection = os.path.join(folder, "connection.json")
        with open(connection, "w", encoding="utf-8") as f:
            json.dump({"control": control.url, "run": desk.marker}, f)
        # The local capability stays in the file. PYTHONPATH also supports a service started from a source checkout.
        server = {"command": sys.executable, "args": ["-P", "-m", "autopilot", "mcp", "--attach", connection],
                  "env": {"PYTHONPATH": os.path.dirname(PACKAGE)}}
        cwd = os.path.join(folder, "agent")
        os.mkdir(cwd)
        desk.session.say(f"Starting {logged_cli.NAMES[desk.cli_client]} CLI")
        with logged_cli.launch(desk.cli_client, desk.opening, server, cwd) as child:
            yield child


def instructions_text(engine, run_rel):
    from .. import vision

    return (system_prompt(engine, run_rel) + "\n" + EXTERNAL_TOOLS.replace("{run}", run_rel)
            .replace("{max_frames}", str(vision.MAX_FRAMES))
            .replace("{describer}", " ".join(line.strip() for line in vision.SYSTEM.splitlines()
                                             if line.strip()))
            .replace("{sweep}", vision.SWEEP))


def implement(game, project, prompt, run_id=None, say=None, client=None, model=None, timeout=None, stop=None,
              steering=None, play_seconds=None, describer=None, desk=None, activity=None):
    """The run, worked on by the user's own agent through desk (a Desk, from the start call); returns the report, as
    every agent's implement() (claude_code.implement has the arguments; model is the user's agent's, unknown here)."""
    try:
        session = open_session(game, project, prompt, run_id, say=say, client=client, timeout=timeout, stop=stop,
                               steering=steering, play_seconds=play_seconds, describer=describer, activity=activity)
    except Exception as e:
        desk.end(f"{type(e).__name__}: {e}")  # the start call must finish even if no session opened
        raise
    session.looks_itself = True
    desk.marker = f"{session.run_id}-{secrets.token_hex(4)}"
    session.marker = desk.marker
    session.note(f"agent: the user's own ({desk.client or 'an MCP client'}), through autopilot mcp")
    files = FileTools(session.root, session.run_rel, PACKAGE)
    t0 = time.time()
    stopped = None
    done = watch_steering(session)
    try:
        game.pause(True)  # (in the try: a game that doesn't answer still gets cleaned up and reported)
        with open(os.path.join(session.run_dir, INSTRUCTIONS), "w", encoding="utf-8", newline="\n") as f:
            f.write(instructions_text(engines.of(game), session.run_rel))
        message = first_message(session)
        with open(os.path.join(session.run_dir, REQUEST), "w", encoding="utf-8", newline="\n") as f:
            f.write(message)
        desk.open(session, files, START.format(marker=desk.marker, project=project, message=message,
                                               instructions=f"{session.run_rel}/{INSTRUCTIONS}",
                                               request=f"{session.run_rel}/{REQUEST}"))
        runner = launch_cli(desk) if desk.cli_client and not session.stop() else contextlib.nullcontext()
        with runner as child:
            while session.report is None:
                if session.stop():
                    stopped = "stopped"
                    break
                if time.time() - t0 > session.budget:
                    stopped = "timed out"
                    break
                if child is not None:
                    stopped = child.failure()
                    if stopped:
                        break
                if not desk.busy and time.time() - desk.last_call > IDLE_SECONDS:
                    stopped = f"the agent called no tool for {IDLE_SECONDS / 60:.0f} minutes"
                    break
                time.sleep(0.25)
    except Exception as e:
        stopped = f"{type(e).__name__}: {e}"
        session.note(traceback.format_exc())
    finally:
        desk.end(stopped or "reported")
        if session.sandbox and (stopped == "stopped" or (desk.cli_client and stopped)):
            session.sandbox.stop()
        desk.wait_idle(END_GRACE)  # (a call going ends first: its episode sees the stop)
        done.set()
        if session.sandbox:
            session.sandbox.close()
        try:
            game.release_all()
            game.pause(False)
        except Exception:
            pass
    keep_transcript(session, desk, t0)
    return finish(session, t0, stopped, "(the user's own agent: its words are in its own conversation)",
                  agent="external", client=desk.client, model=model)


def keep_transcript(session, desk, started):
    """A shared run (its telemetry/ folder is there): the user's agent's conversation during the run, as its client
    keeps it on this computer, in telemetry/transcript.jsonl (autopilot/transcripts.py)."""
    from ..telemetry import TELEMETRY

    folder = os.path.join(session.run_dir, TELEMETRY)
    if not os.path.isdir(folder):
        return
    if desk.reported_at:
        time.sleep(max(0.0, desk.reported_at + TRANSCRIPT_DELAY - time.time()))
    try:
        kept = transcripts.keep(desk.marker, started, os.path.join(folder, transcripts.FILE))
        session.note(f"transcript: {kept or 'not found'}")
    except Exception as e:  # noqa: BLE001 - shared run data is never what a run waits on
        session.note(f"transcript couldn't be kept: {type(e).__name__}: {e}")
