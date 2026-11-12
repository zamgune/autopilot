"""Run the user's installed, signed-in CLI with just this playtest's MCP server."""

import contextlib
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import tomllib
from pathlib import Path

NAMES = {"claude": "Claude Code", "codex": "Codex"}
LOGIN = {"claude": "claude auth login", "codex": "codex login"}
# The CLI needs the user's home/keychain, OS runtime and network configuration. The agent service can be in an
# Aura run here: its keys, provider URLs, billing and model overrides must never become the CLI's configuration.
INHERIT = frozenset("""
    PATH PATHEXT HOME USER USERNAME LOGNAME USERPROFILE HOMEDRIVE HOMEPATH APPDATA LOCALAPPDATA
    SYSTEMROOT WINDIR COMSPEC PROGRAMFILES PROGRAMFILES(X86) PROGRAMW6432 PROGRAMDATA ALLUSERSPROFILE
    COMPUTERNAME PROCESSOR_ARCHITECTURE TMP TEMP TMPDIR LANG LANGUAGE SHELL
    XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME XDG_STATE_HOME XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS
    HTTPS_PROXY HTTP_PROXY ALL_PROXY NO_PROXY SSL_CERT_FILE SSL_CERT_DIR NODE_EXTRA_CA_CERTS REQUESTS_CA_BUNDLE
    CODEX_HOME CODEX_CA_CERTIFICATE CLAUDE_CONFIG_DIR CLAUDE_CODE_GIT_BASH_PATH
""".split())


def cli_env():
    env = {k: v for k, v in os.environ.items() if k.upper() in INHERIT or k.upper().startswith("LC_")}
    # GUI apps on macOS commonly get only the system PATH. On Windows the service may predate the user's npm install.
    home = Path.home()
    extra = [home / ".local/bin", home / ".volta/bin"]
    if os.name == "nt":
        if env.get("APPDATA"):
            extra.append(Path(env["APPDATA"]) / "npm")
        if env.get("PROGRAMFILES"):
            extra.append(Path(env["PROGRAMFILES"]) / "nodejs")
    else:
        extra.extend([Path("/opt/homebrew/bin"), Path("/usr/local/bin"), home / ".npm-global/bin"])
        extra.extend(sorted((home / ".nvm/versions/node").glob("*/bin"), reverse=True))
    env["PATH"] = os.pathsep.join([env.get("PATH", os.defpath), *(str(p) for p in extra if p.is_dir())])
    return env


def executable(client, path):
    if client not in NAMES:
        raise ValueError("Choose Claude Code CLI or Codex CLI.")
    found = shutil.which(client, path=path)
    if found:
        binary = Path(found)
        if os.name != "nt" or binary.suffix.lower() == ".exe":
            return [str(binary)]
        # Bypass npm's cmd/PowerShell shim: it would reinterpret quotes and other characters in the MCP arguments.
        name = {"codex": "@openai/codex", "claude": "@anthropic-ai/claude-code"}[client]
        package = binary.parent / "node_modules" / name
        try:
            entry = json.loads((package / "package.json").read_text(encoding="utf-8"))["bin"]
            script = package / (entry[client] if isinstance(entry, dict) else entry)
        except (OSError, ValueError, KeyError, TypeError):
            script = None
        node = binary.with_name("node.exe")
        node = str(node) if node.is_file() else shutil.which("node.exe", path=path)
        if script and script.is_file():
            if script.suffix.lower() == ".exe":
                return [str(script)]
            if node and script.suffix.lower() in (".js", ".cjs", ".mjs"):
                return [node, str(script)]
        native = shutil.which(client + ".exe", path=path)
        if native:
            return [native]
    raise RuntimeError(f"{NAMES[client]} CLI was not found. Install the official CLI, run `{LOGIN[client]}` "
                       "in a terminal, then restart the Autopilot service.")


def codex_store(env):
    """Keep a user-selected OS keyring when ignoring unrelated home configuration (plugins, servers, providers)."""
    folder = env.get("CODEX_HOME") or Path.home() / ".codex"
    try:
        with open(Path(folder) / "config.toml", "rb") as f:
            value = tomllib.load(f).get("cli_auth_credentials_store")
    except (OSError, ValueError):
        return []
    if value in ("file", "keyring", "auto", "ephemeral"):
        return ["-c", "cli_auth_credentials_store=" + json.dumps(value)]
    return []


def arguments(client, server, env, config_dir):
    from .. import connect

    if client == "claude":
        describer = {"description": connect.DESCRIBER_ABOUT, "prompt": connect.describer_instructions(),
                     "tools": ["mcp__autopilot__frames"]}
        return ["-p", "--setting-sources", "", "--strict-mcp-config", "--mcp-config",
                json.dumps({"mcpServers": {"autopilot": server}}, ensure_ascii=False),
                "--tools", "Agent", "--allowedTools", "Agent(frame-describer),mcp__autopilot__*",
                "--agents", json.dumps({"frame-describer": describer}, ensure_ascii=False),
                "--permission-mode", "dontAsk", "--disable-slash-commands"]

    from .codex import LOCKDOWN

    config = dict(LOCKDOWN)
    # The external agent uses a separate, unbriefed agent to describe frames. Both see the same pinned MCP endpoint.
    role = Path(config_dir).resolve() / "frame_describer.toml"
    role.write_text(connect.codex_describer(), encoding="utf-8")
    config.update({"agents.enabled": True, "features.multi_agent": True, "features.multi_agent_v2": True,
                   "agents.frame_describer.description": connect.DESCRIBER_ABOUT,
                   "agents.frame_describer.config_file": str(role), "features.apps": False,
                   "features.plugins": False, "features.hooks": False, "approval_policy": "never",
                   "mcp_servers.autopilot.command": server["command"], "mcp_servers.autopilot.args": server["args"],
                   "mcp_servers.autopilot.required": True, "mcp_servers.autopilot.startup_timeout_sec": 60,
                   "mcp_servers.autopilot.default_tools_approval_mode": "approve"})
    args = ["exec", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", *codex_store(env)]
    for key, value in config.items():  # strings, arrays, integers and booleans are TOML values in their JSON spelling
        args.extend(["-c", f"{key}={json.dumps(value, ensure_ascii=False)}"])
    if server.get("env"):
        # Codex splits the left side on literal dots and does not unquote it. A TOML table keeps env names intact.
        entries = ", ".join(f"{json.dumps(k, ensure_ascii=False)} = {json.dumps(v, ensure_ascii=False)}"
                            for k, v in server["env"].items())
        args.extend(["-c", "mcp_servers.autopilot.env={" + entries + "}"])
    return [*args, "-"]


def _login_problem(client, status, output):
    """Recognize definitive signed-out/API-key states. Enterprise login methods may evolve."""
    if client == "claude":
        try:
            auth = json.loads(output)
        except ValueError:
            auth = {}
        if isinstance(auth, dict):
            if auth.get("authMethod") in ("api_key", "api_key_helper"):
                return "API key login"
            if auth.get("loggedIn") is False or auth.get("authMethod") == "none":
                return "login required"
    elif "using an api key" in output.lower() or "using api key" in output.lower():
        return "API key login"
    elif status != 0 and "not logged in" in output.lower():
        return "login required"
    return None


class Child:
    def __init__(self, proc, client):
        self.proc = proc
        self.client = client
        self.tail = b""
        self.reader = threading.Thread(target=self._drain, daemon=True)
        self.reader.start()

    def _drain(self):
        # Some CLIs print the full prompt and transcripts on stderr. Bound memory and never put raw output in a report.
        with self.proc.stdout as pipe:
            while chunk := pipe.read1(8192):
                self.tail = (self.tail + chunk)[-8192:]

    def failure(self):
        code = self.proc.poll()
        if code is None:
            return None
        self.reader.join(timeout=0.25)
        if code == 0:
            return f"{NAMES[self.client]} CLI finished without reporting a playtest result."
        output = self.tail.lower()
        hint = ""
        if b"autopilot: api key login" in output:
            hint = f" An API key is active. Sign in with your account using `{LOGIN[self.client]}`, then retry."
        elif any(s in output for s in (b"autopilot: login required", b"not logged in", b"unauthorized", b"401")):
            hint = f" Sign in with your account using `{LOGIN[self.client]}`, then retry."
        elif any(s in output for s in (b"usage limit", b"rate limit", b"insufficient_quota")):
            hint = " Check your CLI account's usage limit."
        elif any(s in output for s in (b"unexpected argument", b"unknown option", b"unrecognized argument")):
            hint = " Update the installed CLI and retry."
        elif b"mcp" in output and any(s in output for s in (b"failed", b"timeout", b"not ready")):
            hint = " The CLI could not connect to this run's Autopilot tools."
        elif b"autopilot: cli startup failed" in output:
            hint = " Check that the CLI opens in a terminal, then retry."
        return f"{NAMES[self.client]} CLI exited with status {code} before reporting a playtest result.{hint}"


@contextlib.contextmanager
def launch(client, prompt, server, cwd):
    from .. import processes

    env = cli_env()
    command = executable(client, env["PATH"])
    # The supervisor checks login without a model call, then runs the CLI. Its Windows job also owns MCP children
    # if the CLI itself crashes. All processes share the isolated POSIX group. Stop works during either stage.
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    with tempfile.TemporaryDirectory(dir=cwd) as files, tempfile.TemporaryFile(dir=files) as source:
        args = arguments(client, server, env, files)
        wrapper = [sys.executable, "-I", os.path.abspath(__file__), client, str(len(command)), *command, *args]
        source.write(prompt.encode("utf-8"))
        source.seek(0)
        proc = processes.track(subprocess.Popen(wrapper, cwd=cwd, env=env, stdin=source, stdout=subprocess.PIPE,
                                                stderr=subprocess.STDOUT, creationflags=flags, **processes.GROUP))
        child = Child(proc, client)
        try:
            yield child
        finally:
            if os.name != "nt" and proc.poll() is not None:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)  # the CLI may have left an MCP server in its group
                except ProcessLookupError:
                    pass
            else:
                processes.kill(proc)
            proc.wait()
            child.reader.join(timeout=2)


def _supervise(argv):
    client, count = argv[0], int(argv[1])
    command, args = argv[2:2 + count], argv[2 + count:]
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    if os.name == "nt":
        # Launched as an isolated script, also in an uninstalled source checkout.
        sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
        from autopilot import processes

        processes.contain()
    status_args = ["--setting-sources", "", "auth", "status", "--json"] if client == "claude" else ["login", "status"]
    try:
        status = subprocess.run([*command, *status_args], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=20, creationflags=flags)
        output = status.stdout if client == "claude" else status.stdout + status.stderr
        problem = _login_problem(client, status.returncode, output.decode("utf-8", "replace"))
        if problem:
            print("autopilot: " + problem, file=sys.stderr)
            return 1
        # With CREATE_NO_WINDOW a Windows descendant must explicitly receive these streams. Otherwise the CLI sees
        # a fresh console input instead of the UTF-8 prompt file handed to this supervisor.
        return subprocess.call([*command, *args], stdin=sys.stdin, stdout=sys.stdout, stderr=sys.stderr,
                               creationflags=flags)
    except (OSError, subprocess.TimeoutExpired):
        print("autopilot: CLI startup failed", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(_supervise(sys.argv[1:]))
