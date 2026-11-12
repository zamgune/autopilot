"""Signed-in editor launchers, exercised through real pipes/process trees with stand-ins for both CLIs."""

import json
import os
import socket
import sys
import time
import tomllib

import pytest

from autopilot.agents import logged_cli

# Every other CLI option comes from the unmodified launcher. The stand-in records these only inside tmp_path.
STAND_IN = """
import json, os, subprocess, sys, time
from pathlib import Path

args = sys.argv[1:]
mode = Path("mode").read_text(encoding="utf-8")
if "status" in args:
    if "auth" in args:
        method = "api_key" if mode == "api" else "claude.ai"
        print(json.dumps({"loggedIn": mode != "logged_out", "authMethod": method}))
    else:
        statuses = {"api":"Logged in using an API key - secret", "logged_out":"Not logged in"}
        print(statuses.get(mode, "Logged in using ChatGPT"), file=sys.stderr)
    sys.exit(int(mode == "logged_out"))
text = sys.stdin.buffer.read().decode("utf-8")
Path("received.json").write_text(json.dumps([args, text]), encoding="utf-8")
if mode in ("stay", "orphan"):
    listen = ("import socket,time; from pathlib import Path; "
              "s=socket.socket(); s.bind(('127.0.0.1',0)); s.listen(); "
              "Path('port').write_text(str(s.getsockname()[1])); time.sleep(60)")
    subprocess.Popen([sys.executable, "-c", listen])
    while not Path("port").exists():
        time.sleep(0.01)
    if mode == "stay":
        time.sleep(60)
if mode == "error":
    print("x" * 100000 + " unauthorized: extremely-private-result", file=sys.stderr)
    sys.exit(17)
"""


@pytest.fixture
def fake(tmp_path, monkeypatch):
    script = tmp_path / "CLI path with spaces.py"
    script.write_text(STAND_IN, encoding="utf-8")
    monkeypatch.setattr(logged_cli, "executable", lambda client, path: [sys.executable, str(script)])
    server = {"command": "C:/spaces & 한글/python.exe", "args": ["-I", "per run.py", "한글"],
              "env": {"PYTHONPATH": "C:/extra path"}}
    return server


def wait_for(get, timeout=12):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if value := get():
            return value
        time.sleep(0.04)
    pytest.fail("The stand-in CLI did not reach the expected state.")


def test_env_keeps_login_and_excludes_agent_service_credentials(monkeypatch, tmp_path):
    monkeypatch.setenv("CODEX_HOME", str(tmp_path))
    monkeypatch.setenv("CLAUDE_CONFIG_DIR", str(tmp_path / "claude"))
    for key in ("ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "OPENAI_API_KEY", "OPENAI_BASE_URL",
                "CODEX_API_KEY", "TYPESAFE_API_KEY", "AUTOPILOT_AURA_URL", "CLAUDE_CODE_OAUTH_TOKEN",
                "CLAUDE_CODE_USE_BEDROCK", "AWS_SESSION_TOKEN", "GITHUB_TOKEN", "CODEX_THREAD_ID"):
        monkeypatch.setenv(key, "from-the-service")
    env = logged_cli.cli_env()
    assert env["CODEX_HOME"] == str(tmp_path)
    assert env["CLAUDE_CONFIG_DIR"] == str(tmp_path / "claude")
    assert "from-the-service" not in env.values()
    (tmp_path / "config.toml").write_text('cli_auth_credentials_store = "keyring"\n', encoding="utf-8")
    assert logged_cli.codex_store(env) == ["-c", 'cli_auth_credentials_store="keyring"']


@pytest.mark.parametrize("client", ["claude", "codex"])
def test_prompt_on_stdin_private_tools_and_early_exit(fake, tmp_path, client):
    prompt = '한글 "$(<do not run>)" & %PATH%\n' * 2600
    (tmp_path / "mode").write_text("error", encoding="utf-8")
    with logged_cli.launch(client, prompt, fake, tmp_path) as child:
        failure = wait_for(child.failure)
        assert "17" in failure and logged_cli.LOGIN[client] in failure
        assert "extremely-private-result" not in failure and len(child.tail) <= 8192
    args, text = json.loads((tmp_path / "received.json").read_text(encoding="utf-8"))
    assert text == prompt and prompt not in args
    if client == "claude":
        assert "-p" in args and "--strict-mcp-config" in args
        assert json.loads(args[args.index("--mcp-config") + 1]) == {"mcpServers": {"autopilot": fake}}
        assert "mcp__autopilot__*" in args[args.index("--allowedTools") + 1]
    else:
        assert args[0] == "exec" and args[-1] == "-"
        assert "--ignore-user-config" in args and args[args.index("--sandbox") + 1] == "read-only"
        config = tomllib.loads("\n".join(args[i + 1] for i, arg in enumerate(args) if arg == "-c"))
        server = config["mcp_servers"]["autopilot"]
        assert {key: server[key] for key in fake} == fake
        assert server["default_tools_approval_mode"] == "approve" and config["features"]["shell_tool"] is False
        assert config["agents"]["enabled"] and config["features"]["multi_agent_v2"]
        assert config["agents"]["frame_describer"]["config_file"].endswith("frame_describer.toml")


@pytest.mark.parametrize("mode", ["api", "logged_out"])
@pytest.mark.parametrize("client", ["claude", "codex"])
def test_login_error_does_not_start_a_model_or_return_credentials(fake, tmp_path, client, mode):
    (tmp_path / "mode").write_text(mode, encoding="utf-8")
    with logged_cli.launch(client, "prompt", fake, tmp_path) as child:
        failure = wait_for(child.failure)
        assert logged_cli.LOGIN[client] in failure and "secret" not in failure
        assert ("API key" in failure) == (mode == "api")
    assert not (tmp_path / "received.json").exists()


@pytest.mark.parametrize("mode", ["stay", "orphan"])
def test_stopping_or_cli_exiting_ends_mcp_children(fake, tmp_path, mode):
    (tmp_path / "mode").write_text(mode, encoding="utf-8")
    with logged_cli.launch("codex", "hello", fake, tmp_path) as child:
        output = tmp_path / "port"
        port = int(wait_for(lambda: output.exists() and output.read_text(encoding="utf-8")))
        if mode == "orphan":
            assert "without reporting" in wait_for(child.failure)
        else:
            assert child.failure() is None
    # A dead child may briefly retain a zombie PID on POSIX, but its listening socket must close on every platform.
    with socket.socket() as probe:
        probe.settimeout(0.25)
        assert probe.connect_ex(("127.0.0.1", port)) != 0


def test_missing_cli_gives_install_and_login_help(monkeypatch):
    monkeypatch.setattr(logged_cli.shutil, "which", lambda *a, **k: None)
    with pytest.raises(RuntimeError, match="claude auth login"):
        logged_cli.executable("claude", "")


@pytest.mark.skipif(os.name != "nt", reason="npm's cmd shim is Windows-specific")
def test_windows_npm_cmd_uses_nodes_package_entrypoint(tmp_path, monkeypatch):
    npm = tmp_path / "npm & paths"
    package = npm / "node_modules/@openai/codex"
    package.mkdir(parents=True)
    script = package / "first.mjs"
    script.touch()
    (package / "package.json").write_text('{"bin":{"codex":"first.mjs"}}', encoding="utf-8")
    shim = npm / "codex.cmd"
    shim.touch()
    node = npm / "node.exe"
    node.touch()
    monkeypatch.setattr(logged_cli.shutil, "which", lambda name, path: str(shim))
    assert logged_cli.executable("codex", str(npm)) == [str(node), str(script)]
