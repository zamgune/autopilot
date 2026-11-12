"""Runs through Aura (autopilot/aura.py): the run started with Aura, the model settings pointed at its relay for the run
only, the run finished; refusals in Aura's words; the connection's token and mode; OpenCode given the relay's URL."""

import os
import time
import types

import pytest

from autopilot import aura, bridge, cli, vision
from autopilot.agents import opencode


class Answer:
    def __init__(self, status, body):
        self.status_code, self._body, self.text = status, body, str(body)

    def json(self):
        return self._body


class FakeAura:
    """Aura's API as requests.Session sees it: /api/autopilot/runs and /api/autopilot/runs/finish."""

    def __init__(self, start=None):
        self.calls = []
        self.start = start or Answer(200, {
            "run_id": "r_1", "token": "grt1.run-token", "expires_at": "",
            "relay": {"anthropic": "https://aura.test/api/autopilot/llm/anthropic",
                      "typesafe": "https://aura.test/api/autopilot/llm/typesafe"},
            "finish": "https://aura.test/api/autopilot/runs/finish"})

    def post(self, url, json=None, headers=None, timeout=None):
        self.calls.append((url, json, headers))
        if url.endswith("/api/autopilot/runs"):
            return self.start
        return Answer(200, {"billed": True, "usd": 0.5, "charged_usd": 0.6})


@pytest.fixture
def through_aura(monkeypatch):
    monkeypatch.setenv("AUTOPILOT_AURA_URL", "https://aura.test/")
    for k in aura.RUN_ENV:
        monkeypatch.delenv(k, raising=False)


def test_a_run_uses_the_relay_while_it_plays_then_finishes_and_puts_the_settings_back(through_aura, monkeypatch):
    monkeypatch.setenv("TYPESAFE_API_KEY", "the-users-own")
    monkeypatch.setenv("ANTHROPIC_AUTH_TOKEN", "a-sign-in")
    http = FakeAura()
    # (Aura's own tools may ask for any kind of run, not only verifications)
    aura.Run("aura-session", "explore", aura.AURA_VERIFICATION, project="Farm", http=http).start()
    assert http.calls[0][1] == {"kind": "explore", "mode": "aura_verification", "project": "Farm",
                                "project_key": "Farm"}
    http.calls.clear()
    run = aura.Run("aura-session", "verify", None, project="Farm", http=http).start()
    url, body, headers = http.calls[0]
    assert url == "https://aura.test/api/autopilot/runs" and headers["Authorization"] == "Bearer aura-session"
    assert body == {"kind": "verify", "mode": "playtest", "project": "Farm", "project_key": "Farm"}
    with run:
        assert os.environ["TYPESAFE_BASE_URL"] == "https://aura.test/api/autopilot/llm/typesafe"
        assert os.environ["TYPESAFE_API_KEY"] == os.environ["ANTHROPIC_API_KEY"] == "grt1.run-token"
        assert os.environ["ANTHROPIC_BASE_URL"] == "https://aura.test/api/autopilot/llm/anthropic"
        assert "ANTHROPIC_AUTH_TOKEN" not in os.environ
    assert os.environ["TYPESAFE_API_KEY"] == "the-users-own" and os.environ["ANTHROPIC_AUTH_TOKEN"] == "a-sign-in"
    assert "TYPESAFE_BASE_URL" not in os.environ and "ANTHROPIC_API_KEY" not in os.environ
    assert http.calls[-1][0] == "https://aura.test/api/autopilot/runs/finish"
    assert http.calls[-1][2]["Authorization"] == "Bearer grt1.run-token" and run.finished["billed"] is True


def test_aura_refusing_a_run_says_why_and_a_signed_out_user_gets_the_sign_in(through_aura, monkeypatch):
    http = FakeAura(Answer(402, {"valid": False, "code": "NO_CREDITS", "error": "Out of Aura credits."}))
    with pytest.raises(aura.Refused) as e:
        aura.Run("aura-session", "goal", http=http).start()
    assert (e.value.status, e.value.code, e.value.message) == (402, "NO_CREDITS", "Out of Aura credits.")
    with pytest.raises(aura.Refused) as e:
        aura.Run(None, "goal", http=http).start()
    assert e.value.status == 401
    assert aura.refusal_words(e.value) == "Sign in to Aura, then start the run again."


def test_the_session_is_the_connections_else_the_aura_clients(monkeypatch):
    monkeypatch.setenv("AUTOPILOT_AURA_CLIENT_PORT", "18800")
    asked = []

    class Client:
        def get(self, url, timeout=None):
            asked.append(url)
            return Answer(200, {"sessionToken": "from-the-client"}) if ":18800/" in url else Answer(404, {})

    assert aura.session_token(types.SimpleNamespace(auth_token="from-the-plugin"), http=Client()) == "from-the-plugin"
    assert not asked
    assert aura.session_token(types.SimpleNamespace(auth_token=None), http=Client()) == "from-the-client"
    assert asked == ["http://127.0.0.1:18800/api/validateSessionToken"]

    class SignedOut:
        def get(self, url, timeout=None):
            return Answer(401, {})

    assert aura.client_session(http=SignedOut()) == ("http://127.0.0.1:18800", None)


def test_the_connection_says_its_token_and_mode():
    ws = types.SimpleNamespace(request=types.SimpleNamespace(headers={
        "Authorization": "Bearer abc.def", "X-Autopilot-Mode": "Aura_Verification"}))
    assert bridge.connection_auth(ws) == ("abc.def", "aura_verification")
    assert bridge.connection_auth(types.SimpleNamespace(request=types.SimpleNamespace(headers={}))) == (None, None)
    assert bridge.connection_auth(object()) == (None, None)


def test_opencode_is_given_a_moved_providers_url_as_it_wants_it(monkeypatch):
    monkeypatch.delenv("ANTHROPIC_BASE_URL", raising=False)
    assert opencode.base_url("anthropic") is None
    monkeypatch.setenv("ANTHROPIC_BASE_URL", "https://aura.test/api/autopilot/llm/anthropic/")
    assert opencode.base_url("anthropic") == "https://aura.test/api/autopilot/llm/anthropic/v1"
    monkeypatch.setenv("OPENAI_BASE_URL", "https://proxy.test/v1")
    assert opencode.base_url("openai") == "https://proxy.test/v1"


class Plugin:
    """Enough of a link for run_prompt: what it was sent."""

    def __init__(self):
        self.hello = {"project": "Farm"}
        self.auth_token, self.mode = "aura-session", None
        self.sent = []

    def send(self, frame):
        self.sent.append(frame)

    def status(self, text):
        self.sent.append({"type": "status", "text": text})


def test_a_refused_run_is_the_runs_result_and_nothing_is_played(through_aura, monkeypatch, tmp_path):
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: FakeAura(
        Answer(403, {"valid": False, "code": "NOT_ON_PLAN", "error": "Autopilot comes with Aura Pro."})))
    monkeypatch.setattr(cli, "prepare", lambda *a, **k: pytest.fail("the game was prepared for a refused run"))
    link = Plugin()
    assert cli.run_prompt(link, "Verify that the door opens") == cli.EXIT["INCONCLUSIVE"]
    result = link.sent[-1]["result"]
    assert result["verdict"] == "INCONCLUSIVE" and result["reason"] == "Autopilot comes with Aura Pro."
    # A plugin's verify run asks for the claim alone ("start" frame kind "verify"): still a verdict, not a goal
    link = Plugin()
    assert cli.run_prompt(link, "The bag holds at most 5 logs.", kind="verify") == cli.EXIT["INCONCLUSIVE"]
    result = link.sent[-1]["result"]
    assert result["verdict"] == "INCONCLUSIVE" and result["claim"] == "The bag holds at most 5 logs."


def test_without_aura_a_run_starts_none(monkeypatch):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    assert not aura.enabled()
    assert cli.kind_of("Verify that x") == "verify" and cli.kind_of("Explore the game for bugs") == "explore"
    assert cli.kind_of("Sell three crops") == "goal"


def test_without_aura_a_run_never_asks_aura_and_plays_on_the_users_own_keys(monkeypatch, tmp_path):
    # (the open-source release, or anyone with their own model keys: Aura never sees the run, so never bills it)
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setenv("TYPESAFE_API_KEY", "the-users-own")
    monkeypatch.setenv("ANTHROPIC_API_KEY", "the-users-anthropic")
    for name in ("TYPESAFE_BASE_URL", "ANTHROPIC_BASE_URL"):
        monkeypatch.delenv(name, raising=False)

    def no_aura(*a, **k):
        raise AssertionError("a run without Aura called Aura")

    monkeypatch.setattr(cli.aura.requests, "Session", no_aura)
    monkeypatch.setattr(cli.aura.requests, "get", no_aura)
    monkeypatch.setattr(cli.aura.requests, "post", no_aura)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    keys = []

    def implement(*a, **k):
        keys.append((os.environ.get("TYPESAFE_API_KEY"), os.environ.get("ANTHROPIC_API_KEY"),
                     os.environ.get("TYPESAFE_BASE_URL"), os.environ.get("ANTHROPIC_BASE_URL")))
        return {"outcome": "achieved", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=implement))
    for prompt in ("Verify that the gate opens", "Explore the game for bugs", "Sell three crops"):
        cli.run_prompt(Plugin(), prompt)
    assert keys == [("the-users-own", "the-users-anthropic", None, None)] * 3


def test_an_exploration_gets_its_minutes_and_the_game_closes_before_the_result(monkeypatch, tmp_path):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    asked = []

    def implement(game, project, prompt, **k):
        asked.append((prompt, k.get("play_seconds"), k.get("timeout")))
        return {"outcome": "explored", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=implement))

    class Editor(Plugin):
        def request(self, op, args=None, timeout=None):
            self.sent.append({"type": "request", "op": op, "args": args})
            return {"running": False}

    run = {"kind": "explore", "minutes": 2, "brief": "the farm"}
    assert cli.play_minutes(run) == 2 and cli.play_minutes({"kind": "explore"}) == 10
    assert cli.play_minutes({"kind": "goal", "minutes": 2}) is None
    prompt = cli.prompt_of(run)
    assert prompt == "Explore the game for bugs: the farm (2 minutes of play)"
    link = Editor()
    link.hello = {"project": "Farm", "share": False, "host": "editor"}
    cli.run_prompt(link, prompt, kind="explore", play_minutes=cli.play_minutes(run))
    assert asked == [(prompt, 120, cli.default_timeout())]
    # Every harness has run: the game closes (the editor's play session ends), then the result is sent
    kinds = [(f["type"], f.get("op"), f.get("args")) for f in link.sent if f["type"] in ("request", "result")]
    assert kinds == [("request", "session", {"action": "stop"}), ("result", None, None)]
    # A long exploration's budget leaves time to read the game before and report after
    cli.run_prompt(Editor(), prompt, kind="explore", play_minutes=60)
    assert asked[-1][1:] == (3600, 3600 + cli.PAST_PLAY_SECONDS)
    # Other runs have no minutes of play; a game run on its own (no editor) stays open
    link = Editor()
    link.hello = {"project": "Farm", "share": False, "host": "game"}
    cli.run_prompt(link, "Sell three crops")
    assert asked[-1][1:] == (None, None) and not [f for f in link.sent if f["type"] == "request"]


def test_a_run_gets_the_time_limit_its_start_frame_asks_for(monkeypatch, tmp_path):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.delenv("AUTOPILOT_TIMEOUT", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    asked = []

    def implement(game, project, prompt, **k):
        asked.append((k.get("play_seconds"), k.get("timeout")))
        return {"outcome": "achieved", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=implement))
    # The Settings page's time limit, in minutes (none, or one that isn't a number: the agent service's own)
    assert cli.time_limit({"kind": "verify", "time_limit_minutes": 45}) == 45 * 60
    assert cli.time_limit({"kind": "goal", "time_limit_minutes": "90"}) == 90 * 60
    assert cli.time_limit({"kind": "goal"}) is None and cli.time_limit(None) is None
    assert cli.time_limit({"time_limit_minutes": "soon"}) is None and cli.time_limit({"time_limit_minutes": 0}) is None
    # (kept to what a run may ask for)
    assert cli.time_limit({"time_limit_minutes": 0.2}) == 60
    assert cli.time_limit({"time_limit_minutes": 10 ** 6}) == 36000
    link = Plugin()
    link.hello = {"project": "Farm", "share": False, "host": "game"}
    cli.run_prompt(link, "Sell three crops", timeout=cli.time_limit({"time_limit_minutes": 45}))
    assert asked[-1] == (None, 45 * 60)
    # An exploration gets its minutes of play and 15 more, or the time limit when that is longer
    cli.run_prompt(link, "Explore the game", kind="explore", play_minutes=10, timeout=5 * 60)
    assert asked[-1] == (600, 600 + cli.PAST_PLAY_SECONDS)
    cli.run_prompt(link, "Explore the game", kind="explore", play_minutes=10, timeout=120 * 60)
    assert asked[-1] == (600, 120 * 60)


def test_a_run_of_the_users_own_words_plays_the_kind_jev_reads(monkeypatch, tmp_path):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.delenv("AUTOPILOT_TIMEOUT", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    read = {}
    monkeypatch.setattr(cli, "classify", lambda prompt, client=None: read[prompt])
    asked = []

    def implement(game, project, prompt, **k):
        asked.append((prompt, k.get("play_seconds"), k.get("timeout")))
        return {"outcome": "explored", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=implement))
    words = "Lvl_HordeArea test this game and find all the bugs with animation"
    read[words] = ("explore", True)
    # The composer's words, kind and all (docs/protocol.md, Runs): its Time limit is an exploration's minutes of play
    run = {"kind": "auto", "prompt": words, "time_limit_minutes": 20, "design_brief": "Hordes come every 2 minutes"}
    assert cli.prompt_of(run) == words and cli.explore_minutes(run) == 20 and cli.play_minutes(run) is None
    assert cli.explore_minutes({"kind": "goal", "time_limit_minutes": 20}) is None
    link = Plugin()
    link.hello = {"project": "Farm", "share": False, "host": "game"}
    cli.run_prompt(link, words, timeout=cli.time_limit(run), explore_minutes=cli.explore_minutes(run),
                   design_brief=run["design_brief"])
    assert asked[-1] == (f"Test the game: {words} (20 minutes of play)\nDesign brief: Hordes come every 2 minutes",
                         1200, 1200 + cli.PAST_PLAY_SECONDS)
    # The plugin is told the kind Jev read before the run plays, and the result is a test's
    told = [f for f in link.sent if f["type"] == "run_kind"]
    assert told == [{"type": "run_kind", "kind": "explore", "tested": True}]
    assert link.sent[-1]["result"]["tested"] is True
    # The same Time limit is a goal's whole budget
    read["Reach the test chamber"] = ("goal", False)
    link = Plugin()
    link.hello = {"project": "Farm", "share": False, "host": "game"}
    cli.run_prompt(link, "Reach the test chamber", timeout=cli.time_limit(run),
                   explore_minutes=cli.explore_minutes(run))
    assert asked[-1] == ("Reach the test chamber", None, 1200)
    assert [f for f in link.sent if f["type"] == "run_kind"] == [{"type": "run_kind", "kind": "goal", "tested": False}]
    assert "tested" not in link.sent[-1]["result"]


def test_through_aura_jev_reads_the_kind_once_the_run_has_started(through_aura, monkeypatch, tmp_path):
    http = FakeAura()
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: http)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    monkeypatch.setattr(cli.aura, "session_tokens", lambda link, where: iter(["aura-session"]))
    keys = []

    def classify(prompt, client=None):
        keys.append(os.environ.get("TYPESAFE_API_KEY"))  # (Jev through the run's relay)
        return "verify", False

    monkeypatch.setattr(cli, "classify", classify)
    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(
        implement=lambda *a, **k: {"outcome": "CONFIRMED", "summary": "", "evidence": []}))
    link = Plugin()
    link.hello = {"project": "Farm", "share": False, "host": "game"}
    cli.run_prompt(link, "The gate opens")
    assert http.calls[0][1]["kind"] == "goal"  # (not known yet: told as a goal, billed the same)
    result = [f["result"] for f in link.sent if f["type"] == "result"][-1]
    assert keys == ["grt1.run-token"] and result["verdict"] == "CONFIRMED"


def test_the_default_time_limit_is_read_at_each_run(monkeypatch):
    from autopilot.agents import session

    monkeypatch.delenv("AUTOPILOT_TIMEOUT", raising=False)
    assert session.default_timeout() == session.TIMEOUT == 30 * 60
    # autopilot.env is loaded before every run, after the agent service started: what it says now counts
    monkeypatch.setenv("AUTOPILOT_TIMEOUT", "3600")
    assert session.default_timeout() == 3600
    for wrong in ("an hour", "-5", "0"):
        monkeypatch.setenv("AUTOPILOT_TIMEOUT", wrong)
        assert session.default_timeout() == session.TIMEOUT


def test_a_run_ends_saying_what_it_added_to_the_memory(monkeypatch, tmp_path):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    asked = []

    def implement(game, project, prompt, **k):
        asked.append(prompt)
        os.makedirs(cli.codexmod.project_dir("Farm"), exist_ok=True)
        with open(os.path.join(cli.codexmod.project_dir("Farm"), "notes.md"), "w") as f:
            f.write("The gate opens with E")
        return {"outcome": "CONFIRMED", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=implement))
    link = Plugin()
    link.hello = {"project": "Farm", "share": False}
    cli.run_prompt(link, "Can the gate be opened?", kind="verify")
    assert asked == ["Verify: Can the gate be opened?"]  # (the agent reads it as a claim)
    statuses = [f.get("text") for f in link.sent if f["type"] == "status"]
    result = link.sent[-1]["result"]
    assert statuses[-1] == cli.SAVING_MEMORY and link.sent[-1]["type"] == "result"
    assert result["memory"] == {"updated": True, "notes": True, "lib": False}
    assert result["claim"] == "Can the gate be opened?" and "recording" not in result  # (the plugin can't capture)
    assert result["no_recording"].startswith("the game can't be recorded here")  # (and the report says why)
    # Nothing new learned: no status, and the result says so
    link = Plugin()
    link.hello = {"project": "Farm", "share": False}
    cli.run_prompt(link, "Can the gate be opened?", kind="verify")
    assert cli.SAVING_MEMORY not in [f.get("text") for f in link.sent if f["type"] == "status"]
    assert link.sent[-1]["result"]["memory"]["updated"] is False


def test_a_goal_worded_as_a_claim_gets_a_goals_result(monkeypatch, tmp_path):
    # Aura's run_playtest asks for a goal ("start" frame kind "goal") in words a claim would use; the agent may report
    # a verdict, and the result is still a goal's (Aura reads `reached` and `notes` from a goal run's result)
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    report = {"outcome": "CONFIRMED", "summary": "Jev walked and jumped as the stock character.", "evidence": ["e"]}
    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(implement=lambda *a, **k: report))
    link = Plugin()
    link.hello = {"project": "Farm", "share": False}
    goal = "Confirm the player spawns as the stock character and can jump"
    assert cli.run_prompt(link, goal, kind="goal") == 0
    result = link.sent[-1]["result"]
    assert result["kind"] == "goal" and result["reached"] and result["goal"] == goal
    assert result["notes"] == ["The agent walked and jumped as the stock character.", "e"]
    # a goal run that fails is a goal not reached, with why
    monkeypatch.setattr(cli, "run_prompt", lambda *a, **k: (_ for _ in ()).throw(RuntimeError("the game closed")))
    link = Plugin()
    link.hello = {"project": "Farm", "run": {"kind": "goal", "goal": goal}}
    link.current = link
    link.next_message = lambda timeout=None: (_ for _ in ()).throw(KeyboardInterrupt)
    monkeypatch.setattr(cli, "send_memory_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "send_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "SignIn", lambda link: None)
    with pytest.raises(KeyboardInterrupt):
        cli.cmd_serve(link, types.SimpleNamespace(path=None, model=None))
    for _ in range(100):  # (the run goes in its own thread)
        if any(f["type"] == "result" for f in link.sent):
            break
        time.sleep(0.02)
    result = next(f["result"] for f in link.sent if f["type"] == "result")
    assert result["kind"] == "goal" and not result["reached"] and result["notes"] == ["The run failed: the game closed"]


def test_runs_through_aura_always_use_claude_code(through_aura, monkeypatch, tmp_path):
    monkeypatch.setenv("AUTOPILOT_AGENT", "opencode")
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", FakeAura)
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    picked = []

    def select(name=None):
        picked.append(name)
        return types.SimpleNamespace(implement=lambda *a, **k: {"outcome": "achieved", "summary": "", "evidence": []})

    monkeypatch.setattr(cli.agents, "select", select)
    assert cli.run_prompt(Plugin(), "Sell three crops") == 0
    assert picked == ["claude"]
    monkeypatch.delenv("AUTOPILOT_AURA_URL")
    cli.run_prompt(Plugin(), "Sell three crops")
    assert picked == ["claude", None]  # (without Aura: AUTOPILOT_AGENT's choice)


def test_editor_cli_keeps_aura_for_jev_and_uses_the_selected_login(through_aura, monkeypatch, tmp_path):
    from autopilot.agents import external, logged_cli

    http = FakeAura()
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: http)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda *args: ("Farm", object()))

    def jev():
        assert os.environ["TYPESAFE_BASE_URL"] == http.start.json()["relay"]["typesafe"]
        assert os.environ["TYPESAFE_API_KEY"] == http.start.json()["token"]
        return None

    def implement(*args, desk, **kwargs):
        assert desk.cli_client == "codex"
        assert not {"TYPESAFE_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY"}.intersection(logged_cli.cli_env())
        return {"outcome": "achieved", "summary": "The gate opened.", "evidence": []}

    monkeypatch.setattr(cli.runtime, "jev_client", jev)
    monkeypatch.setattr(external, "implement", implement)
    monkeypatch.setattr(cli.agents, "select", lambda *args: pytest.fail("used Aura's coding agent"))
    assert cli.run_prompt(Plugin(), "Open the gate", kind="goal", cli_client="codex") == 0
    assert http.calls[-1][0].endswith("/runs/finish")


def test_the_plugin_is_told_what_aura_billed_a_run_before_its_result(through_aura, monkeypatch, tmp_path,
                                                                     no_aura_bill):
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", FakeAura)
    monkeypatch.setattr(cli.aura, "client_session", lambda *a, **k: (None, None))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(
        implement=lambda *a, **k: {"outcome": "achieved", "summary": "", "evidence": []}))
    link = Plugin()
    assert cli.run_prompt(link, "Sell three crops") == 0
    kinds = [f["type"] for f in link.sent if f["type"] != "status"]
    assert kinds[-3:] == ["bill", "result", "account"]
    # (what was charged, after markup)
    assert [f for f in link.sent if f["type"] == "bill"] == [{"type": "bill", "usd": 0.6, "billed": True}]
    # (and without Aura, nothing is billed: no bill frame)
    monkeypatch.delenv("AUTOPILOT_AURA_URL")
    link = Plugin()
    cli.run_prompt(link, "Sell three crops")
    assert not [f for f in link.sent if f["type"] in ("bill", "account")]


def test_a_shared_run_with_own_keys_keeps_its_memory_with_aura_for_this_install(monkeypatch, tmp_path,
                                                                              no_install_memory, no_training_uploads):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(
        implement=lambda *a, **k: {"outcome": "achieved", "summary": "", "evidence": []}))
    done = []

    class Kept:
        def restore(self, folder):
            done.append(("restore", os.path.basename(folder)))

        def save(self, folder):
            done.append(("save", os.path.basename(folder)))

    no_install_memory["kept"] = Kept()
    link = Plugin()
    link.hello = {"project": "Farm", "project_id": "9fe07ce6-6c8f", "share": True}
    assert cli.run_prompt(link, "Sell three crops") == 0
    deadline = time.time() + 5
    while len(done) < 2 and time.time() < deadline:
        time.sleep(0.02)
    assert done == [("restore", "Farm"), ("save", "Farm")]  # (fetched before the run, saved after it)
    assert no_install_memory["asked"] == ["9fe07ce6-6c8f"]  # (by the project's id, as Aura keeps it)
    assert no_training_uploads[-1][1] == "9fe07ce6-6c8f"  # (its shared run data under the same key)
    link.hello = {"project": "Farm", "project_id": "9fe07ce6-6c8f", "share": False}
    cli.run_prompt(link, "Sell three crops")
    assert no_install_memory["asked"] == ["9fe07ce6-6c8f"] and len(done) == 2  # (not shared: kept here only)


def test_the_memory_is_saved_before_the_run_is_finished_and_kept_by_the_projects_id(through_aura, tmp_path):
    order = []

    class Http(FakeAura):
        def post(self, url, json=None, headers=None, timeout=None):
            order.append(url.rsplit("/", 1)[-1])
            if url.endswith("/api/autopilot/runs"):
                self.calls.append((url, json, headers))
                return Answer(200, {**self.start._body, "memory": "https://aura.test/api/autopilot/memory"})
            return Answer(200, {"billed": True})

    run = aura.Run("aura-session", "goal", project="Farm", project_key="{ABCD-1234}", http=Http()).start()
    assert run.memory is not None and run.memory.url == "https://aura.test/api/autopilot/memory"
    saved = []
    run.memory.restore = lambda folder: saved.append(("restore", folder)) or 0
    run.memory.save = lambda folder: (saved.append(("save", folder)), order.append("save"))
    run.restore_memory(str(tmp_path))
    with run:
        pass
    assert saved == [("restore", str(tmp_path)), ("save", str(tmp_path))]
    assert order == ["runs", "save", "finish"]
    assert run.memory.headers["Authorization"] == "Bearer grt1.run-token"


def test_two_projects_of_one_name_through_aura_keep_their_own_memory_folder(through_aura, monkeypatch, tmp_path):
    """The Aura client names a project it can't place "game": two such projects (different ids) each restore into,
    and save from, their own folder, and Aura is told the project's name and id."""
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    sessions = []
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: sessions.append(FakeAura()) or sessions[-1])
    restored = []
    monkeypatch.setattr(aura.Run, "restore_memory", lambda self, folder: restored.append(folder))
    for pid in ("aaaaaaaa-1111", "bbbbbbbb-2222", "aaaaaaaa-1111"):
        link = Plugin()
        link.hello = {"project": "game", "project_id": pid}
        cli.start_aura_run(link, "verify", "game")
    assert [os.path.basename(f) for f in restored] == ["game", "game-bbbbbbbb", "game"]
    bodies = [s.calls[0][1] for s in sessions]
    assert [(b["project"], b["project_key"]) for b in bodies] == [
        ("game", "aaaaaaaa-1111"), ("game", "bbbbbbbb-2222"), ("game", "aaaaaaaa-1111")]


def test_a_brain_for_aura_says_where_it_is_as_auras_and_takes_it_back_when_its_owner_ends(tmp_path):
    import json
    import socket
    import subprocess
    import sys
    import time

    from autopilot import processes

    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    settings = tmp_path / "settings"
    env = {**os.environ, "LOCALAPPDATA": str(settings), "XDG_CONFIG_HOME": str(settings),
           "AUTOPILOT_AURA_URL": "https://aura.test", "AUTOPILOT_MEMORY": str(tmp_path / "memory")}
    owner = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"], **processes.GROUP)
    command = [sys.executable, "-m", "autopilot", "serve", "--port", str(port), "--exit-with", str(owner.pid)]
    autopilot = subprocess.Popen(command, cwd=os.path.dirname(os.path.dirname(os.path.abspath(__file__))), env=env,
                             **processes.GROUP)
    folder = settings / "Autopilot" if sys.platform != "darwin" else None
    try:
        if folder is None:  # (macOS: the user's own Application Support, not one a test can point elsewhere)
            return
        for _ in range(100):
            if (folder / "aura-brain.json").exists():
                break
            time.sleep(0.1)
        said = json.loads((folder / "aura-brain.json").read_text(encoding="utf-8"))
        assert said["url"] == f"ws://127.0.0.1:{port}/v1/bridge"
        assert not (folder / "brain.json").exists()  # (never where an Autopilot of the user's own says it is)
        processes.kill(owner)
        owner.wait(timeout=10)
        autopilot.wait(timeout=20)
        assert not (folder / "aura-brain.json").exists()
    finally:
        processes.kill(autopilot)
        processes.kill(owner)


def test_aura_flag_uses_auras_own_server_unless_another_is_set(monkeypatch):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    aura.use_default()
    assert aura.api_url() == aura.DEFAULT_URL
    monkeypatch.setenv("AUTOPILOT_AURA_URL", "https://staging.aura.test")
    aura.use_default()
    assert aura.api_url() == "https://staging.aura.test"


def test_aura_flag_parses_on_every_command_that_plays():
    for argv in (["serve", "--aura"], ["play", "Verify that x", "--aura"], ["run", "h.py", "--aura"]):
        assert cli.parser().parse_args(argv).aura
    assert not cli.parser().parse_args(["serve"]).aura


def test_a_chosen_aura_server_is_used_without_choosing_aura(monkeypatch):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setenv("AUTOPILOT_AURA_SERVER", "https://preview.aura.test/")
    assert not aura.enabled() and aura.default_url() == "https://preview.aura.test"
    aura.use_default()
    assert aura.api_url() == "https://preview.aura.test"
    from autopilot import training

    monkeypatch.delenv("AUTOPILOT_TRAINING_URL", raising=False)
    assert training.endpoint() == "https://preview.aura.test/api/autopilot/training"


def test_a_runs_trace_goes_to_aura_with_its_start_cleaned_and_only_when_there_is_one(through_aura):
    # (the Aura client's start frame says which chat, tool call and tool asked: Aura files the run's model calls there)
    assert aura.trace_of({"thread_id": " t1 ", "tool_call_id": "c1", "tool": "verification_agent", "extra": "x"}) == {
        "thread_id": "t1", "tool_call_id": "c1", "tool": "verification_agent"}
    assert aura.trace_of({"thread_id": "x" * 300})["thread_id"] == "x" * 200
    assert aura.trace_of({"thread_id": "", "tool_call_id": 7}) is None
    assert aura.trace_of(["t1"]) is None and aura.trace_of(None) is None
    http = FakeAura()
    aura.Run("aura-session", "verify", aura.AURA_VERIFICATION, project="Farm", http=http,
             trace={"thread_id": "t1", "tool_call_id": "c1", "tool": "verification_agent"}).start()
    assert http.calls[0][1] == {"kind": "verify", "mode": "aura_verification", "project": "Farm", "project_key": "Farm",
                                "trace": {"thread_id": "t1", "tool_call_id": "c1", "tool": "verification_agent"}}
    http.calls.clear()
    aura.Run("aura-session", "verify", project="Farm", http=http, trace={"tool": 3}).start()
    assert "trace" not in http.calls[0][1]


def test_a_run_refused_for_credits_tells_the_plugins_chat_page_and_settings_at_once(through_aura, monkeypatch,
                                                                                    tmp_path):
    words = "Your team is out of Aura credits. Add credits at https://aura.test/account/pricing to keep playtesting."
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.account.aura, "app_running", lambda: True)  # (signed in: the Aura app's session)
    monkeypatch.setattr(cli.aura.requests, "Session",
                        lambda: FakeAura(Answer(402, {"valid": False, "code": "NO_CREDITS", "error": words})))
    link = Plugin()
    cli.run_prompt(link, "Open the gate", kind="goal")
    deadline = time.time() + 5
    while cli.account._bill["asking"] and time.time() < deadline:  # (the bill asked in the background: done here)
        time.sleep(0.02)
    assert link.sent[-1]["type"] == "account"  # (after the result, without waiting for Aura to be asked again)
    assert link.sent[-1]["out_of_credits"] == {"message": words, "url": "https://aura.test/account/pricing"}
    assert [words in str(f["result"]) for f in link.sent if f["type"] == "result"] == [True]
    # A run Aura starts: the team has credits again
    cli.account.note_credits(None)
    assert cli.account.state()["out_of_credits"] is None
    # Another refusal is no word on credits
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: FakeAura(
        Answer(403, {"valid": False, "code": "NO_TEAM", "error": "Not part of an Aura team."})))
    link = Plugin()
    cli.run_prompt(link, "Open the gate", kind="goal")
    assert not [f for f in link.sent if f["type"] == "account"]


def test_run_prompt_passes_the_trace_on_when_it_starts_the_run_with_aura(through_aura, monkeypatch, tmp_path):
    started = FakeAura(Answer(402, {"valid": False, "code": "NO_CREDITS", "error": "Out of Aura credits."}))
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: started)
    trace = {"thread_id": "t1", "tool_call_id": "c1", "tool": "verification_agent"}
    cli.run_prompt(Plugin(), "The gate opens.", kind="verify", trace=trace)
    assert started.calls[0][1]["trace"] == trace
    started.calls.clear()
    cli.run_prompt(Plugin(), "The gate opens.", kind="verify")
    assert "trace" not in started.calls[0][1]


def test_the_serve_loop_takes_the_trace_from_the_start_frames_run(monkeypatch):
    asked = []
    monkeypatch.setattr(cli, "run_prompt", lambda *a, **k: asked.append(k))
    link = Plugin()
    trace = {"thread_id": "t1", "tool_call_id": "c1", "tool": "run_playtest"}
    link.hello = {"project": "Farm", "run": {"kind": "goal", "goal": "Open the gate", "trace": trace}}
    link.current = link
    link.next_message = lambda timeout=None: (_ for _ in ()).throw(KeyboardInterrupt)
    monkeypatch.setattr(cli, "send_memory_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "send_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "SignIn", lambda link: None)
    with pytest.raises(KeyboardInterrupt):
        cli.cmd_serve(link, types.SimpleNamespace(path=None, model=None))
    for _ in range(100):  # (the run goes in its own thread)
        if asked:
            break
        time.sleep(0.02)
    assert asked and asked[0]["trace"] == trace and asked[0]["kind"] == "goal"


def picked_model_aura(agent):
    """Aura answering a run that asked for a model with the agent that plays it (`agent`), and an OpenRouter and an
    OpenAI relay beside its own two."""
    return FakeAura(Answer(200, {
        "run_id": "r_1", "token": "grt1.run-token", "expires_at": "",
        "relay": {"anthropic": "https://aura.test/api/autopilot/llm/anthropic",
                  "typesafe": "https://aura.test/api/autopilot/llm/typesafe",
                  "openai": "https://aura.test/api/autopilot/llm/openai/v1",
                  "openrouter": "https://aura.test/api/autopilot/llm/openrouter/v1"},
        "finish": "https://aura.test/api/autopilot/runs/finish",
        **({"agent": agent} if agent else {})}))


def test_a_run_asks_for_the_model_picked_in_auras_chat_and_opencode_plays_it_through_auras_relay(through_aura,
                                                                                                 monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    monkeypatch.setenv("OPENROUTER_API_KEY", "the-users-own")
    monkeypatch.setenv("AUTOPILOT_MODEL", "the-users-model")
    http = picked_model_aura({"name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro"})
    run = aura.Run("aura-session", "goal", aura.AURA_VERIFICATION, project="Farm", http=http,
                   model="  deepseek-v4-pro ").start()
    assert http.calls[0][1]["model"] == "deepseek-v4-pro"
    assert run.agent_name == "opencode"
    with run:
        assert os.environ["AUTOPILOT_OPENCODE_PROVIDER"] == "openrouter"
        assert os.environ["AUTOPILOT_MODEL"] == "deepseek/deepseek-v4-pro"
        assert os.environ["OPENROUTER_BASE_URL"] == "https://aura.test/api/autopilot/llm/openrouter/v1"
        assert os.environ["OPENROUTER_API_KEY"] == "grt1.run-token"
        assert opencode.current_provider() == "openrouter"
        assert opencode.base_url("openrouter") == "https://aura.test/api/autopilot/llm/openrouter/v1"
        # (Claude, which describes the frames, still goes through Aura's relay)
        assert os.environ["ANTHROPIC_BASE_URL"] == "https://aura.test/api/autopilot/llm/anthropic"
    assert os.environ["OPENROUTER_API_KEY"] == "the-users-own" and os.environ["AUTOPILOT_MODEL"] == "the-users-model"
    assert "AUTOPILOT_OPENCODE_PROVIDER" not in os.environ and "OPENROUTER_BASE_URL" not in os.environ


def test_a_picked_gemini_model_gets_the_run_token_in_every_google_key_opencode_may_read(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    monkeypatch.setenv("GEMINI_API_KEY", "the-users-own")
    http = picked_model_aura({"name": "opencode", "provider": "google", "model": "gemini-3.1-pro-preview"})
    http.start._body["relay"]["google"] = "https://aura.test/api/autopilot/llm/google/v1beta"
    run = aura.Run("aura-session", "goal", http=http, model="gemini-3.1-pro-preview").start()
    with run:
        assert opencode.current_provider() == "google"
        assert opencode.base_url("google") == "https://aura.test/api/autopilot/llm/google/v1beta"
        for k in opencode.PROVIDER_KEYS["google"]:
            assert os.environ[k] == "grt1.run-token"
    assert os.environ["GEMINI_API_KEY"] == "the-users-own" and "GOOGLE_GENERATIVE_AI_API_KEY" not in os.environ


def test_a_picked_claude_model_plays_with_claude_code_and_auto_or_none_with_autopilots_own(through_aura, monkeypatch):
    http = picked_model_aura({"name": "claude", "provider": "anthropic", "model": "claude-sonnet-5"})
    run = aura.Run("aura-session", "goal", http=http, model="sonnet-5").start()
    assert run.agent_name == "claude"
    with run:
        assert os.environ["AUTOPILOT_MODEL"] == "claude-sonnet-5" and "AUTOPILOT_OPENCODE_PROVIDER" not in os.environ
    # Aura names no agent (Auto, a model it can't relay): Claude Code on Autopilot's own model, as AUTOPILOT_MODEL says
    monkeypatch.setenv("AUTOPILOT_MODEL", "claude-opus-5-5")
    http = picked_model_aura(None)
    run = aura.Run("aura-session", "goal", http=http, model="auto").start()
    assert http.calls[0][1]["model"] == "auto" and run.agent_name == "claude"
    with run:
        assert os.environ["AUTOPILOT_MODEL"] == "claude-opus-5-5"
    # (a run the Autopilot tab started asks for no model)
    http = picked_model_aura(None)
    aura.Run("aura-session", "goal", http=http, model="  ").start()
    assert "model" not in http.calls[0][1]


def test_only_an_agent_this_brain_can_play_through_auras_relay_is_taken():
    relay = {"anthropic": "a", "typesafe": "t", "openrouter": "o"}
    assert aura.agent_of({"name": "opencode", "provider": "openrouter", "model": " x/y "}, relay) == {
        "name": "opencode", "provider": "openrouter", "model": "x/y"}
    assert aura.agent_of({"name": "opencode", "provider": "openai", "model": "gpt"}, relay) is None  # (no relay)
    assert aura.agent_of({"name": "opencode", "provider": "google", "model": "g"}, {**relay, "google": "g"})
    assert aura.agent_of({"name": "opencode", "provider": "mistral", "model": "m"}, {**relay, "mistral": "m"}) is None
    assert aura.agent_of({"name": "claude", "provider": "openrouter", "model": "x"}, relay) is None
    assert aura.agent_of({"name": "codex", "provider": "openai", "model": "gpt"}, {**relay, "openai": "o"})
    assert aura.agent_of({"name": "aider", "provider": "openai", "model": "gpt"}, {**relay, "openai": "o"}) is None
    assert aura.agent_of({"name": "claude", "provider": "anthropic", "model": ""}, relay) is None
    assert aura.agent_of("opencode", relay) is None and aura.agent_of(None, relay) is None
    assert aura.model_of("x" * 300) == "x" * aura.MAX_MODEL and aura.model_of(7) is None


def test_an_openai_model_plays_on_codex_through_auras_openai_relay(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "codex_ready", lambda: True)
    monkeypatch.setenv("CODEX_API_KEY", "the-users-own")
    http = picked_model_aura({"name": "codex", "provider": "openai", "model": "gpt-6.1-sol"})
    run = aura.Run("aura-session", "goal", http=http, model="gpt-6.1-sol").start()
    assert run.agent_name == "codex"
    with run:
        from autopilot.agents import codex

        assert codex.api_key() == "grt1.run-token" and os.environ["CODEX_API_KEY"] == "grt1.run-token"
        assert os.environ["OPENAI_BASE_URL"] == "https://aura.test/api/autopilot/llm/openai/v1"
        assert os.environ["AUTOPILOT_MODEL"] == "gpt-6.1-sol" and "AUTOPILOT_OPENCODE_PROVIDER" not in os.environ
    assert os.environ["CODEX_API_KEY"] == "the-users-own" and "OPENAI_BASE_URL" not in os.environ
    # Without Codex installed, OpenCode plays it (it needs Bun); with neither, Claude Code on Autopilot's own model
    monkeypatch.setattr(aura, "codex_ready", lambda: False)
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    run = aura.Run("aura-session", "goal", http=http, model="gpt-6.1-sol").start()
    assert run.agent == {"name": "opencode", "provider": "openai", "model": "gpt-6.1-sol"}
    with run:
        assert os.environ["AUTOPILOT_OPENCODE_PROVIDER"] == "openai"
        assert os.environ["OPENAI_API_KEY"] == "grt1.run-token"
    monkeypatch.setattr(aura, "bun_ready", lambda: False)
    said = []
    assert aura.Run("aura-session", "goal", http=http, model="gpt-6.1-sol", say=said.append).start().agent is None
    assert said and "Bun" in said[0]
    assert aura.agent_of({"name": "codex", "provider": "openrouter", "model": "x"}, {"openrouter": "o"}) is None


def test_without_bun_a_model_opencode_plays_falls_back_to_claude_code_and_says_so(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: False)
    said = []
    http = picked_model_aura({"name": "opencode", "provider": "openai", "model": "gpt-6.1-sol"})
    run = aura.Run("aura-session", "goal", http=http, model="gpt-6.1-sol", say=said.append).start()
    assert run.agent_name == "claude" and run.agent is None
    assert said and "gpt-6.1-sol" in said[0] and "Bun" in said[0]
    with run:
        assert "AUTOPILOT_OPENCODE_PROVIDER" not in os.environ and "OPENAI_BASE_URL" not in os.environ


def test_a_required_agent_is_never_swapped_for_claude(through_aura, monkeypatch):
    """A run on the team's free allowance plays Aura's free-tier model alone (`required`): without Bun it isn't played
    on Claude (and the model isn't named); the run Aura started is finished at once, with nothing to bill."""
    monkeypatch.setattr(aura, "bun_ready", lambda: False)
    said = []
    http = picked_model_aura({"name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro",
                              "required": True})
    with pytest.raises(aura.Refused) as e:
        aura.Run("aura-session", "goal", http=http, say=said.append).start()
    assert e.value.code == "AGENT_UNAVAILABLE" and "Bun" in e.value.message and "deepseek" not in e.value.message
    assert not said and http.calls[-1][0].endswith("/runs/finish")
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    run = aura.Run("aura-session", "goal", http=http).start()
    assert run.agent_name == "opencode" and run.agent["required"] is True


def test_run_prompt_plays_with_the_agent_aura_names_and_has_auras_claude_describe_frames(through_aura, monkeypatch,
                                                                                        tmp_path):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    http = picked_model_aura({"name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro"})
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: http)
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    seen = {}

    def implement(*a, describer=None, **k):
        seen.update(provider=opencode.current_provider(), describer=describer.provider,
                    proxied=os.environ.get("OPENROUTER_BASE_URL"))
        return {"outcome": "achieved", "summary": "", "evidence": []}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: seen.update(agent=name) or types.SimpleNamespace(
        implement=implement, current_provider=opencode.current_provider))
    assert cli.run_prompt(Plugin(), "Sell three crops", aura_model="deepseek-v4-pro") == 0
    assert http.calls[0][1]["model"] == "deepseek-v4-pro"
    assert seen["agent"] == "opencode" and seen["provider"] == "openrouter" and seen["describer"] == "anthropic"
    assert seen["proxied"].startswith("http://127.0.0.1:")  # (the run's proxy counts the agent's calls)


class GoingOnAura(FakeAura):
    """Aura with POST /api/autopilot/runs/free: `free` answers it (the free part's token and agent by default)."""

    def __init__(self, free=None, start=None):
        super().__init__(start or picked_model_aura(None).start)
        self.free = free or Answer(200, {"token": "grt1.free-token", "cap_usd": 2, "agent": {
            "name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro", "required": True}})

    def post(self, url, json=None, headers=None, timeout=None):
        if url.endswith("/api/autopilot/runs/free"):
            self.calls.append((url, json, headers))
            return self.free
        return super().post(url, json=json, headers=headers, timeout=timeout)


def test_a_run_goes_on_on_the_free_allowance_with_the_agent_and_key_aura_answers(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    http = GoingOnAura()
    run = aura.Run("aura-session", "goal", http=http).start()
    with run:
        assert run.agent_name == "claude" and os.environ["ANTHROPIC_API_KEY"] == "grt1.run-token"
        os.environ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:9/anthropic"  # (a proxy the run's telemetry set)
        assert run.go_on_free() is True
        assert http.calls[-1][0] == "https://aura.test/api/autopilot/runs/free"
        assert http.calls[-1][2]["Authorization"] == "Bearer grt1.run-token"
        assert run.agent_name == "opencode" and run.agent["required"] is True
        # Every key is the free part's from now on; the API roots set on entering (or a proxy's) stay
        assert os.environ["TYPESAFE_API_KEY"] == os.environ["ANTHROPIC_API_KEY"] == "grt1.free-token"
        assert os.environ["OPENROUTER_API_KEY"] == "grt1.free-token"
        assert os.environ["OPENROUTER_BASE_URL"] == "https://aura.test/api/autopilot/llm/openrouter/v1"
        assert os.environ["AUTOPILOT_MODEL"] == "deepseek/deepseek-v4-pro"
        assert os.environ["ANTHROPIC_BASE_URL"] == "http://127.0.0.1:9/anthropic"
    assert "OPENROUTER_API_KEY" not in os.environ  # (put back when the run ends)
    assert http.calls[-1][2]["Authorization"] == "Bearer grt1.free-token"  # (the finish)


def test_a_run_doesnt_go_on_unless_aura_says_so(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    for answer in (Answer(409, {"code": "NOT_AT_LIMIT", "error": "no"}), Answer(404, "Not Found"),
                   Answer(200, {"token": "grt1.free-token"}), Answer(503, {"code": "UNAVAILABLE"})):
        run = aura.Run("aura-session", "goal", http=GoingOnAura(answer)).start()
        assert run.go_on_free() is None and run.run_token == "grt1.run-token" and run.agent is None
    words = "Your team is out of Aura credits. Add credits at https://aura.test/account/pricing to keep playtesting."
    refused = GoingOnAura(Answer(402, {"code": "NO_CREDITS", "error": words}))
    run = aura.Run("aura-session", "goal", http=refused).start()
    with pytest.raises(aura.Refused) as e:
        run.go_on_free()
    assert (e.value.code, e.value.message) == ("NO_CREDITS", words)
    monkeypatch.setattr(aura, "bun_ready", lambda: False)  # (its required agent can't play here: refused, never Claude)
    run = aura.Run("aura-session", "goal", http=GoingOnAura()).start()
    with pytest.raises(aura.Refused) as e:
        run.go_on_free()
    assert e.value.code == "AGENT_UNAVAILABLE"


def run_going_on(monkeypatch, tmp_path, http, first):
    """run_prompt through `http`, its first agent ending with `first`, the next one achieving; what each agent saw."""
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: http)
    monkeypatch.setattr(cli, "prepare", lambda link, *a, **k: ("Farm", object()))
    monkeypatch.setattr(cli.runtime, "jev_client", lambda: None)
    seen = []

    def implement(game, project, asked, run_id=None, describer=None, timeout=None, **k):
        run_dir = tmp_path / project / "runs" / run_id
        seen.append({"agent": os.environ.get("AUTOPILOT_OPENCODE_PROVIDER") and "opencode" or "claude",
                     "key": os.environ.get("TYPESAFE_API_KEY"), "asked": asked, "timeout": timeout,
                     "describer": describer.provider, "proxied": os.environ.get("OPENROUTER_BASE_URL"),
                     "seen_by": describer.route and {"root": describer.route[1], "key": describer.route[2],
                                                     "model": describer.route[3], "named": describer.named},
                     "going_on": (run_dir / "going_on.json").exists()})
        run_dir.mkdir(parents=True, exist_ok=True)
        if len(seen) == 1:
            time.sleep(0.05)  # (the first agent takes some of the run's time: Windows' clock would see none go by)
        return dict(first) if len(seen) == 1 else {"outcome": "achieved", "summary": "The gate is open.",
                                                   "evidence": [], "episodes": 3, "seconds": 40}

    monkeypatch.setattr(cli.agents, "select", lambda name=None: types.SimpleNamespace(
        implement=implement, current_provider=opencode.current_provider))
    link = Plugin()
    cli.run_prompt(link, "Open the gate", kind="goal")
    return seen, link


CUT_OFF = {"outcome": "not_achieved", "summary": "No report (the agent ended without one). Last words: API Error: 402",
           "evidence": [], "episodes": 2, "seconds": 120, "stopped": None}


def test_a_run_its_premium_credits_stopped_goes_on_from_where_it_was(through_aura, monkeypatch, tmp_path):
    http = GoingOnAura()
    seen, link = run_going_on(monkeypatch, tmp_path, http, CUT_OFF)
    assert [s["agent"] for s in seen] == ["claude", "opencode"]
    assert [s["key"] for s in seen] == ["grt1.run-token", "grt1.free-token"]
    assert seen[1]["asked"] == seen[0]["asked"] and seen[1]["going_on"]  # (the same request, from where it was)
    assert seen[1]["timeout"] < (seen[0]["timeout"] or cli.default_timeout()) and seen[1]["describer"] == "anthropic"
    assert seen[1]["proxied"].startswith("http://127.0.0.1:")  # (the run's proxy counts the new agent's calls too)
    result = [f for f in link.sent if f["type"] == "result"][-1]["result"]
    assert result["reached"] is True
    # Nothing tells the user: no model, no credits
    said = " ".join(str(f.get("text", "")) for f in link.sent if f["type"] == "status").lower()
    assert "deepseek" not in said and "credit" not in said and "free" not in said


def test_a_run_goes_on_only_when_its_agent_stopped_by_itself(through_aura, monkeypatch, tmp_path):
    for first in ({"outcome": "achieved", "summary": "Done.", "evidence": []},  # (it reported)
                  dict(CUT_OFF, stopped="stopped"),  # (the user stopped it)
                  dict(CUT_OFF, stopped="timed out"),  # (out of its time)
                  dict(CUT_OFF, outcome="explored")):  # (its minutes of play were up)
        http = GoingOnAura()
        seen, _ = run_going_on(monkeypatch, tmp_path, http, first)
        assert len(seen) == 1 and not any(c[0].endswith("/runs/free") for c in http.calls)
    http = GoingOnAura(Answer(409, {"code": "NOT_AT_LIMIT"}))  # (Aura says it wasn't its credits)
    seen, link = run_going_on(monkeypatch, tmp_path, http, CUT_OFF)
    assert len(seen) == 1 and [f for f in link.sent if f["type"] == "result"][-1]["result"]["reached"] is False


def test_a_run_with_no_free_allowance_left_either_ends_in_auras_words(through_aura, monkeypatch, tmp_path):
    words = "Your team is out of Aura credits. Add credits at https://aura.test/account/pricing to keep playtesting."
    monkeypatch.setattr(cli.account.aura, "app_running", lambda: True)
    http = GoingOnAura(Answer(402, {"code": "NO_CREDITS", "error": words}))
    seen, link = run_going_on(monkeypatch, tmp_path, http, CUT_OFF)
    assert len(seen) == 1
    assert words in str([f for f in link.sent if f["type"] == "result"][-1]["result"])
    assert [f for f in link.sent if f["type"] == "account"][-1]["out_of_credits"]["message"] == words


def test_the_serve_loop_takes_the_model_from_the_start_frames_run(monkeypatch):
    asked = []
    monkeypatch.setattr(cli, "run_prompt", lambda *a, **k: asked.append(k))
    link = Plugin()
    link.hello = {"project": "Farm", "run": {"kind": "goal", "goal": "Open the gate", "model": "auto",
                                             "disable_premium_models": True}}
    link.current = link
    link.next_message = lambda timeout=None: (_ for _ in ()).throw(KeyboardInterrupt)
    monkeypatch.setattr(cli, "send_memory_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "send_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "SignIn", lambda link: None)
    with pytest.raises(KeyboardInterrupt):
        cli.cmd_serve(link, types.SimpleNamespace(path=None, model=None))
    for _ in range(100):  # (the run goes in its own thread)
        if asked:
            break
        time.sleep(0.02)
    assert asked and asked[0]["aura_model"] == "auto" and asked[0]["disable_premium_models"] is True


def test_disable_premium_models_goes_to_aura_with_the_model_only(through_aura):
    # (Aura's chat's "Disable Premium Models": Auto in a chat it has picked nothing for plays the free-tier model)
    http = FakeAura()
    aura.Run("aura-session", "goal", http=http, model="auto", disable_premium_models=True).start()
    assert http.calls[0][1]["model"] == "auto" and http.calls[0][1]["disable_premium_models"] is True
    for kwargs in ({"model": "auto"}, {"disable_premium_models": True}, {"model": "auto", "disable_premium_models": 1}):
        http.calls.clear()
        aura.Run("aura-session", "goal", http=http, **kwargs).start()
        assert "disable_premium_models" not in http.calls[0][1]


def test_run_prompt_passes_disable_premium_models_on_to_aura(through_aura, monkeypatch, tmp_path):
    started = FakeAura(Answer(402, {"valid": False, "code": "NO_CREDITS", "error": "Out of Aura credits."}))
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: started)
    cli.run_prompt(Plugin(), "Open the gate", kind="goal", aura_model="auto", disable_premium_models=True)
    assert started.calls[0][1]["disable_premium_models"] is True


def test_the_serve_loop_takes_the_thinking_level_from_the_start_frames_run(monkeypatch):
    asked = []
    monkeypatch.setattr(cli, "run_prompt", lambda *a, **k: asked.append(k))
    link = Plugin()
    link.hello = {"project": "Farm", "run": {"kind": "goal", "goal": "Open the gate", "model": "opus-5.5",
                                             "thinking_level": "xhigh"}}
    link.current = link
    link.next_message = lambda timeout=None: (_ for _ in ()).throw(KeyboardInterrupt)
    monkeypatch.setattr(cli, "send_memory_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "send_state", lambda *a, **k: None)
    monkeypatch.setattr(cli.account, "SignIn", lambda link: None)
    with pytest.raises(KeyboardInterrupt):
        cli.cmd_serve(link, types.SimpleNamespace(path=None, model=None))
    for _ in range(100):  # (the run goes in its own thread)
        if asked:
            break
        time.sleep(0.02)
    assert asked and asked[0]["thinking_level"] == "xhigh"


def test_the_thinking_level_goes_to_aura_with_the_model_only(through_aura):
    http = FakeAura()
    aura.Run("aura-session", "goal", http=http, model="opus-5.5", thinking_level="xhigh").start()
    assert http.calls[0][1]["model"] == "opus-5.5" and http.calls[0][1]["thinking_level"] == "xhigh"
    for kwargs in ({"model": "opus-5.5"}, {"thinking_level": "xhigh"},
                   {"model": "opus-5.5", "thinking_level": "turbo"}):
        http.calls.clear()
        aura.Run("aura-session", "goal", http=http, **kwargs).start()
        assert "thinking_level" not in http.calls[0][1]


def test_run_prompt_passes_the_thinking_level_on_to_aura(through_aura, monkeypatch, tmp_path):
    started = FakeAura(Answer(402, {"valid": False, "code": "NO_CREDITS", "error": "Out of Aura credits."}))
    monkeypatch.setattr(cli.codexmod, "MEMORY", str(tmp_path))
    monkeypatch.setattr(cli.aura.requests, "Session", lambda: started)
    cli.run_prompt(Plugin(), "Open the gate", kind="goal", aura_model="opus-5.5", thinking_level="max")
    assert started.calls[0][1]["thinking_level"] == "max"


def test_the_agent_thinks_as_hard_as_aura_says_for_the_run_alone(through_aura, monkeypatch):
    from autopilot.agents import session

    relay = {"anthropic": "https://aura.test/a"}
    assert aura.agent_of({"name": "claude", "provider": "anthropic", "model": "m", "effort": "xhigh"},
                         relay)["effort"] == "xhigh"
    assert "effort" not in aura.agent_of({"name": "claude", "provider": "anthropic", "model": "m", "effort": "turbo"},
                                         relay)
    monkeypatch.setenv("AUTOPILOT_EFFORT", "low")  # (the user's own, put back after the run)
    run = aura.Run("aura-session", "goal", http=FakeAura())
    run.relay, run.run_token = {"typesafe": "https://aura.test/t", "anthropic": "https://aura.test/a"}, "run-token"
    run.agent = {"name": "claude", "provider": "anthropic", "model": "claude-opus-5-5", "effort": "xhigh"}
    with run:
        assert session.effort("claude-opus-5-5") == "xhigh"
        run.agent = {"name": "claude", "provider": "anthropic", "model": "claude-opus-5-5"}
        run._agent_env()  # (as going on free does: that model wasn't given the level)
        assert session.effort("claude-opus-5-5") == "low"
    assert os.environ["AUTOPILOT_EFFORT"] == "low"
    monkeypatch.delenv("AUTOPILOT_EFFORT")
    run.agent = {"name": "claude", "provider": "anthropic", "model": "claude-opus-5-5", "effort": "xhigh"}
    with run:
        run.agent = {"name": "claude", "provider": "anthropic", "model": "claude-opus-5-5"}
        run._agent_env()
        assert "AUTOPILOT_EFFORT" not in os.environ


# -- the model that describes the run's frames (its answer's `vision`) ------------------------------------------------

GOOGLE_RELAY = "https://aura.test/api/autopilot/llm/google/v1beta"


def vision_aura(agent, vision):
    """picked_model_aura with a Google relay too, its answer naming `vision`."""
    http = picked_model_aura(agent)
    http.start._body["relay"]["google"] = GOOGLE_RELAY
    if vision is not None:
        http.start._body["vision"] = vision
    return http


def test_only_a_vision_model_auras_relay_has_is_taken():
    relay = {"anthropic": "a", "typesafe": "t", "openrouter": "o", "google": "g"}
    assert aura.vision_of({"provider": "google", "model": " gemini-3.1-flash-lite "}, relay) == {
        "provider": "google", "model": "gemini-3.1-flash-lite"}
    assert aura.vision_of({"provider": "anthropic", "model": "claude-sonnet-5-5"}, relay)["model"] == \
        "claude-sonnet-5-5"
    assert aura.vision_of({"provider": "openai", "model": "gpt-6-mini"}, relay) is None  # (no relay for it)
    assert aura.vision_of({"provider": "deepseek", "model": "d"}, {**relay, "deepseek": "d"}) is None
    for bad in (None, "google", [], {}, {"provider": "google"}, {"provider": "google", "model": "  "},
                {"provider": "google", "model": 7}, {"provider": 3, "model": "m"}, {"model": "gemini"}):
        assert aura.vision_of(bad, relay) is None
    assert aura.vision_of({"provider": "google", "model": "g"}, None) is None


def test_a_run_aura_names_no_vision_for_is_described_by_opus_through_the_anthropic_relay(through_aura, monkeypatch):
    for answer in (None, {"provider": "mistral", "model": "pixtral"}, "gemini"):
        run = aura.Run("aura-session", "goal", http=vision_aura(None, answer)).start()
        assert run.vision is None and cli.describer_model(run, "openrouter") == ("anthropic", None)
        with run:
            assert "GOOGLE_BASE_URL" not in os.environ and "GEMINI_API_KEY" not in os.environ
            route, _ = vision.route("anthropic", None, cli.describer_model(run, "anthropic")[1])
            assert route[1:4] == ("https://aura.test/api/autopilot/llm/anthropic", "grt1.run-token", vision.MODEL)
    assert cli.describer_model(None, "openai") == ("openai", None)  # (the user's own keys: the agent's provider)


def test_the_vision_provider_gets_the_relay_and_run_token_beside_the_agents_and_both_are_put_back(through_aura,
                                                                                                  monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    monkeypatch.setenv("GEMINI_API_KEY", "the-users-own")
    monkeypatch.setenv("GOOGLE_API_KEY", "the-users-other")
    http = vision_aura({"name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro"},
                       {"provider": "google", "model": "gemini-3.1-flash-lite"})
    run = aura.Run("aura-session", "goal", http=http, model="deepseek-v4-pro").start()
    assert run.vision == {"provider": "google", "model": "gemini-3.1-flash-lite"}
    assert cli.describer_model(run, "openrouter") == ("google", "gemini-3.1-flash-lite")
    with run:
        assert os.environ["OPENROUTER_BASE_URL"] == "https://aura.test/api/autopilot/llm/openrouter/v1"
        assert os.environ["GOOGLE_BASE_URL"] == GOOGLE_RELAY
        for k in vision.ROUTES["google"][2]:
            assert os.environ[k] == "grt1.run-token"  # (none of the user's own is used instead)
        route, _ = vision.route("google", None, "gemini-3.1-flash-lite")
        assert route == ("gemini", GOOGLE_RELAY, "grt1.run-token", "gemini-3.1-flash-lite", None)
    assert os.environ["GEMINI_API_KEY"] == "the-users-own" and os.environ["GOOGLE_API_KEY"] == "the-users-other"
    assert "GOOGLE_BASE_URL" not in os.environ and "GOOGLE_GENERATIVE_AI_API_KEY" not in os.environ
    # An OpenAI one beside Claude Code: its key variables (Codex's too) are the run's
    monkeypatch.setenv("OPENAI_API_KEY", "the-users-own")
    http = vision_aura(None, {"provider": "openai", "model": "gpt-6-mini"})
    run = aura.Run("aura-session", "goal", http=http).start()
    with run:
        assert os.environ["OPENAI_BASE_URL"] == "https://aura.test/api/autopilot/llm/openai/v1"
        assert os.environ["OPENAI_API_KEY"] == os.environ["CODEX_API_KEY"] == "grt1.run-token"
        assert "AUTOPILOT_OPENCODE_PROVIDER" not in os.environ
    assert os.environ["OPENAI_API_KEY"] == "the-users-own" and "OPENAI_BASE_URL" not in os.environ


def test_going_on_free_switches_the_describer_to_the_free_parts_vision_and_key(through_aura, monkeypatch):
    monkeypatch.setattr(aura, "bun_ready", lambda: True)
    free = Answer(200, {"token": "grt1.free-token", "agent": {
        "name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro", "required": True},
        "vision": {"provider": "google", "model": "gemini-3.1-flash-lite"}})
    start = vision_aura(None, {"provider": "anthropic", "model": "claude-sonnet-5-5"}).start
    run = aura.Run("aura-session", "goal", http=GoingOnAura(free, start)).start()
    with run:
        assert run.vision == {"provider": "anthropic", "model": "claude-sonnet-5-5"}
        os.environ["GOOGLE_BASE_URL"] = "http://127.0.0.1:9/google"  # (a proxy the run's telemetry may have set)
        assert run.go_on_free() is True
        assert run.vision == {"provider": "google", "model": "gemini-3.1-flash-lite"}
        assert os.environ["GEMINI_API_KEY"] == os.environ["GOOGLE_API_KEY"] == "grt1.free-token"
        assert os.environ["GOOGLE_BASE_URL"] == GOOGLE_RELAY  # (the run hadn't set Google's root: now it does)
    # A free part Aura names no vision for is described by Opus again
    free = Answer(200, {"token": "grt1.free-token", "agent": {
        "name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro", "required": True}})
    run = aura.Run("aura-session", "goal", http=GoingOnAura(free, start)).start()
    with run:
        assert run.go_on_free() is True and run.vision is None
        assert cli.describer_model(run, "openrouter") == ("anthropic", None)


def test_the_describer_follows_the_vision_aura_names_before_and_after_the_run_goes_on(through_aura, monkeypatch,
                                                                                    tmp_path):
    free = Answer(200, {"token": "grt1.free-token", "agent": {
        "name": "opencode", "provider": "openrouter", "model": "deepseek/deepseek-v4-pro", "required": True},
        "vision": {"provider": "google", "model": "gemini-3.1-flash-lite"}})
    start = vision_aura(None, {"provider": "anthropic", "model": "claude-sonnet-5-5"}).start
    seen, _ = run_going_on(monkeypatch, tmp_path, GoingOnAura(free, start), CUT_OFF)
    assert [s["describer"] for s in seen] == ["anthropic", "google"]
    first, then = seen[0]["seen_by"], seen[1]["seen_by"]
    assert first["model"] == "claude-sonnet-5-5" and first["named"] and first["key"] == "grt1.run-token"
    assert first["root"].startswith("http://127.0.0.1:")  # (through the run's proxy: counted there)
    assert then["model"] == "gemini-3.1-flash-lite" and then["named"] and then["key"] == "grt1.free-token"
    assert then["root"].startswith("http://127.0.0.1:") and then["root"].endswith("/google")
    # An older Aura names none: Opus 5.5 on both parts, as before
    seen, _ = run_going_on(monkeypatch, tmp_path, GoingOnAura(), CUT_OFF)
    assert [(s["describer"], s["seen_by"]["model"], s["seen_by"]["named"]) for s in seen] == [
        ("anthropic", vision.MODEL, False)] * 2
