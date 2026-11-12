"""How this Autopilot runs and the Aura sign-in the plugins ask for (autopilot/account.py): the account frame, a sign-in
the agent service does for the plugin (the plugin never holds Aura's tokens), cancelling it, signing out."""

import json
import os
import threading
import time
import types

from autopilot import account, aura, aura_login

FETCH_BILL = account.fetch_bill  # (the real one: conftest's no_aura_bill stands in for it in every test)
FETCH_CREDITS = account.fetch_credits  # (the same)


class Link:
    def __init__(self):
        self.sent = []
        self.event = threading.Event()

    def send(self, frame):
        self.sent.append(frame)
        self.event.set()

    def frames(self, kind):
        return [f for f in self.sent if f.get("type") == kind]


def wait_for(link, kind, state=None, seconds=5):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if any(f for f in link.frames(kind) if state is None or f.get("state") == state):
            return
        time.sleep(0.02)
    raise AssertionError(f"no {kind} {state}: {link.sent}")


def test_the_account_says_how_runs_go(monkeypatch, own_preferences):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.delenv("TYPESAFE_API_KEY", raising=False)
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    assert account.state() == {"type": "account", "mode": "none", "signed_in": False, "email": None,
                               "keys": {"decision": None, "llm": None}, "coding_agent": "opencode",
                               "llm_provider": "anthropic", "share_data": True, "supports_cli_launch": True}
    monkeypatch.setenv("TYPESAFE_API_KEY", "ts-123456789a91f")
    s = account.state()
    assert s["mode"] == "own_keys" and s["keys"] == {"decision": "••••a91f", "llm": None}
    own_preferences.write_text(json.dumps({"access": "aura"}))
    assert account.state()["mode"] == "aura" and not account.state()["signed_in"]
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600,
                                       "email": "dev@game.test"})
    s = account.state()
    assert s["signed_in"] and s["email"] == "dev@game.test"


def test_a_sign_in_the_plugin_asks_for_is_done_here_and_reported(monkeypatch, own_preferences):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    own_preferences.write_text(json.dumps({"access": "aura"}))

    def sign_in(server, open_browser=None, say=None, cancelled=None, **k):
        open_browser(f"{server}/auth/cli?port=1&state=s&code_challenge=c")
        session = {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600,
                   "email": "dev@game.test"}
        aura_login.save(server, session)
        return session

    opened = []
    monkeypatch.setattr(aura_login, "sign_in", sign_in)
    monkeypatch.setattr("webbrowser.open", lambda url: opened.append(url) or True)
    link = Link()
    assert account.handle(link, {"type": "aura_sign_in"}, account.SignIn(link))
    wait_for(link, "account")
    states = [f["state"] for f in link.frames("aura_sign_in")]
    assert states == ["waiting", "signed_in"] and link.frames("aura_sign_in")[1]["email"] == "dev@game.test"
    assert link.frames("aura_sign_in")[0]["url"].startswith(f"{aura.DEFAULT_URL}/auth/cli?") and opened
    assert link.frames("account")[-1]["signed_in"]
    assert not any("token" in json.dumps(f) for f in link.sent)  # (the plugin never sees a token)


def test_a_sign_in_cancelled_in_the_editor_or_failing_says_why(monkeypatch):
    monkeypatch.setattr("webbrowser.open", lambda url: True)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))

    def waits(server, open_browser=None, say=None, cancelled=None, **k):
        open_browser("u")
        while not cancelled():
            time.sleep(0.01)
        raise aura_login.SignInError("Sign-in cancelled.")

    monkeypatch.setattr(aura_login, "sign_in", waits)
    link, flow = Link(), None
    flow = account.SignIn(link)
    account.handle(link, {"type": "aura_sign_in"}, flow)
    wait_for(link, "aura_sign_in", "waiting")
    assert not flow.start()  # (one at a time: the plugin asking again is told the one going)
    assert [f["state"] for f in link.frames("aura_sign_in")] == ["waiting", "waiting"]
    account.handle(link, {"type": "aura_sign_in_cancel"}, flow)
    wait_for(link, "aura_sign_in", "error")
    assert link.frames("aura_sign_in")[-1]["reason"] == "cancelled"

    for raised, reason in (("Sign-in timed out: nothing came back from the browser.", "timeout"),
                           ("Sign-in cancelled in the browser.", "denied"),
                           ("Aura couldn't be reached (ConnectionError).", "unreachable"),
                           ("Aura refused the sign-in (HTTP 400). Try again.", "refused")):
        def fails(server, open_browser=None, say=None, cancelled=None, _raised=raised, **k):
            raise aura_login.SignInError(_raised)

        monkeypatch.setattr(aura_login, "sign_in", fails)
        link = Link()
        account.handle(link, {"type": "aura_sign_in"}, account.SignIn(link))
        wait_for(link, "aura_sign_in", "error")
        assert link.frames("aura_sign_in")[-1]["reason"] == reason, raised


def test_a_sign_in_made_before_the_setup_records_aura_is_told(monkeypatch, own_preferences):
    """The first-run setup signs in on step 1 and records the choice only at Finish: the account frame sent in
    between must already say signed in (it once said "none", and the Settings page showed signed out)."""
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600,
                                       "email": "dev@game.test"})
    s = account.state()
    assert s["mode"] == "none" and s["signed_in"] and s["email"] == "dev@game.test"
    own_preferences.write_text(json.dumps({"access": "aura"}))  # (Finish)
    assert account.state()["mode"] == "aura" and account.state()["signed_in"]


def test_signing_out_forgets_the_sign_in_for_every_editor(monkeypatch, own_preferences):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    monkeypatch.setattr(aura_login.requests, "post", lambda *a, **k: None)
    own_preferences.write_text(json.dumps({"access": "aura"}))
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600})
    link = Link()
    account.handle(link, {"type": "aura_sign_out"}, account.SignIn(link))
    assert aura_login.saved(aura.DEFAULT_URL) is None
    assert link.frames("account")[-1] == {"type": "account", "mode": "aura", "signed_in": False, "email": None,
                                          "bill": None, "out_of_credits": None,
                                          "keys": {"decision": None, "llm": None},
                                          "coding_agent": "opencode", "llm_provider": "anthropic", "share_data": True,
                                          "supports_cli_launch": True}


def test_the_period_s_bill_is_asked_of_aura_in_the_background_and_told_again(monkeypatch, own_preferences,
                                                                             no_aura_bill):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    own_preferences.write_text(json.dumps({"access": "aura"}))
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600})
    no_aura_bill["bill"] = {"usd": 12.34, "runs": 38, "from": "2026-10-01", "to": "2026-10-31"}
    link = Link()
    account.send_state(link)
    deadline = time.time() + 5
    while len(link.frames("account")) < 2 and time.time() < deadline:
        time.sleep(0.02)
    first, again = link.frames("account")
    assert first["bill"] is None and again["bill"] == no_aura_bill["bill"]  # (the frame never waits on Aura)
    link.sent.clear()
    account.send_state(link)  # (asked a moment ago: told as it was, not asked again)
    time.sleep(0.1)
    assert [f["bill"] for f in link.frames("account")] == [no_aura_bill["bill"]]
    no_aura_bill["bill"] = {**no_aura_bill["bill"], "usd": 12.93, "runs": 39}
    account.forget_bill()  # (a run finished)
    link.sent.clear()
    account.send_state(link)
    deadline = time.time() + 5
    while len(link.frames("account")) < 2 and time.time() < deadline:
        time.sleep(0.02)
    assert link.frames("account")[-1]["bill"]["runs"] == 39


def test_the_bill_is_asked_with_the_sign_in_and_nothing_without_one(monkeypatch):
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    asked = []

    class Http:
        def __init__(self, status, body):
            self.status, self.body = status, body

        def get(self, url, headers=None, timeout=None):
            asked.append((url, headers))
            return types.SimpleNamespace(status_code=self.status, json=lambda: self.body)

    assert FETCH_BILL(http=Http(200, {"usd": 1})) is None and not asked  # (signed out: Aura isn't asked)
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600})
    body = {"usd": 12.3456789, "runs": 38, "from": "2026-10-01", "to": "2026-10-31", "period": "month",
            "scope": "user"}
    assert FETCH_BILL(http=Http(200, body)) == {"usd": 12.345679, "runs": 38, "from": "2026-10-01",
                                                "to": "2026-10-31"}
    assert asked[-1] == (f"{aura.DEFAULT_URL}/api/autopilot/usage", {"Authorization": "Bearer a"})
    assert FETCH_BILL(http=Http(401, {"error": "no"})) is None
    assert FETCH_BILL(http=Http(200, {"usd": "lots"})) is None


def test_the_bill_is_asked_with_the_website_sign_in_when_aura_refuses_the_aura_apps(monkeypatch):
    """The production Aura app beside an Autopilot for a preview server: the app's session gets a 401 there, and the
    bill was then never shown although the user had signed in with the browser."""
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: ("http://127.0.0.1:41200", "prod-app-token"))
    aura_login.save(aura.DEFAULT_URL, {"access_token": "web", "refresh_token": "r", "expires_at": time.time() + 3600})
    asked = []

    class Http:
        def get(self, url, headers=None, timeout=None):
            asked.append(headers["Authorization"])
            ok = headers["Authorization"] == "Bearer web"
            return types.SimpleNamespace(status_code=200 if ok else 401, json=lambda: {"usd": 2.5, "runs": 3})

    assert FETCH_BILL(http=Http())["usd"] == 2.5
    assert asked == ["Bearer prod-app-token", "Bearer web"]


def test_the_teams_credits_are_asked_with_the_bill_and_told_until_it_has_some_again(monkeypatch, own_preferences,
                                                                                    no_aura_bill):
    monkeypatch.delenv("AUTOPILOT_AURA_URL", raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    own_preferences.write_text(json.dumps({"access": "aura"}))
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600})
    out = account.out_of_credits_notice("Your team is out of Aura credits. Add credits at https://aura.test/account/"
                                        "pricing to keep playtesting.")
    no_aura_bill["credits"] = (True, out)
    link = Link()
    account.send_state(link)
    deadline = time.time() + 5
    while len(link.frames("account")) < 2 and time.time() < deadline:
        time.sleep(0.02)
    first, again = link.frames("account")
    assert first["out_of_credits"] is None and again["out_of_credits"] == out  # (the frame never waits on Aura)
    # Aura not saying (unreachable, say) leaves it as it was; Aura saying the team has credits clears it
    for answer, told in (((False, None), out), ((True, None), None)):
        no_aura_bill["credits"] = answer
        account.forget_bill()
        link.sent.clear()
        account.send_state(link)
        time.sleep(0.3)
        assert link.frames("account")[-1]["out_of_credits"] == told
    # Runs with the user's own keys: never
    own_preferences.write_text(json.dumps({"access": "own_keys"}))
    account.note_credits("Your team is out of Aura credits.")
    assert "out_of_credits" not in account.state()


def test_the_teams_credits_are_asked_as_a_run_would_be_admitted(monkeypatch):
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    asked = []

    class Http:
        def __init__(self, status, body):
            self.status, self.body = status, body

        def post(self, url, headers=None, json=None, timeout=None):
            asked.append((url, headers, json))
            return types.SimpleNamespace(status_code=self.status, json=lambda: self.body)

    assert FETCH_CREDITS(http=Http(200, {"valid": True})) == (False, None) and not asked  # (signed out)
    aura_login.save(aura.DEFAULT_URL, {"access_token": "a", "refresh_token": "r", "expires_at": time.time() + 3600})
    assert FETCH_CREDITS(http=Http(200, {"valid": True, "user_id": "u", "team_id": 5})) == (True, None)
    assert asked[-1] == (f"{aura.DEFAULT_URL}/api/autopilot/validate-access", {"Authorization": "Bearer a"},
                         {"mode": "playtest"})
    words = ("Your team is out of premium Aura credits. Turn on data sharing in Aura's settings to playtest "
             "on your plan's free allowance, or add credits at https://aura.test/account/pricing.")
    assert FETCH_CREDITS(http=Http(402, {"valid": False, "code": "NO_CREDITS", "error": words})) == (
        True, {"message": words, "url": "https://aura.test/account/pricing"})
    for status, body in ((503, {"code": "UNAVAILABLE", "retryable": True}), (403, {"code": "NO_TEAM"}),
                         (401, {"code": "UNAUTHENTICATED"}), (200, {"valid": "maybe"})):
        assert FETCH_CREDITS(http=Http(status, body)) == (False, None)
    assert account.out_of_credits_notice("Your team is out of Aura credits.") == {
        "message": "Your team is out of Aura credits.", "url": None}


def test_the_setups_keys_are_the_next_runs_as_saved_now(monkeypatch, tmp_path):
    """BYOK keys changed in the setup after a run (or kept in the editor's environment) once stayed in the agent
    service until it restarted: the run used the old key, and every decision call got a 401."""
    user_env = tmp_path / "autopilot.env"
    monkeypatch.setenv("TYPESAFE_API_KEY", "ts-from-the-editors-environment")
    monkeypatch.setenv("ANTHROPIC_API_KEY", "an-from-the-editors-environment")
    user_env.write_text("TYPESAFE_API_KEY=ts-first-key-0001\nAUTOPILOT_AURA_SERVER=https://aura.test\n")
    monkeypatch.setenv("AUTOPILOT_AURA_SERVER", "")
    account.load_user_settings()
    assert os.environ["TYPESAFE_API_KEY"] == "ts-first-key-0001"
    assert os.environ["ANTHROPIC_API_KEY"] == "an-from-the-editors-environment"
    user_env.write_text("TYPESAFE_API_KEY=ts-fixed-key-0002\nANTHROPIC_API_KEY=an-key-0003\n")  # (setup again)
    assert account.state()["keys"] == {"decision": "••••0002", "llm": "••••0003"}  # (before the run, as it'll be)
    account.load_user_settings()
    assert os.environ["TYPESAFE_API_KEY"] == "ts-fixed-key-0002" and os.environ["ANTHROPIC_API_KEY"] == "an-key-0003"
    assert "AUTOPILOT_AURA_SERVER" not in os.environ  # (taken out of autopilot.env: gone)
    user_env.write_text("")
    account.load_user_settings()
    assert "TYPESAFE_API_KEY" not in os.environ


def test_a_downloads_settings_replace_what_an_earlier_download_wrote(tmp_path):
    """The downloads' installer (account.apply_download_settings): a download built for one Aura server once kept the
    server an earlier download had written in autopilot.env, so its sign-in opened the old server's page."""
    user_env, carried, none = tmp_path / "autopilot.env", tmp_path / "settings.env", str(tmp_path / "none.env")
    user_env.write_text("AUTOPILOT_AURA_SERVER=https://feature.aura.test\nTYPESAFE_API_KEY=ts-key-0001\n")
    carried.write_text("AUTOPILOT_AURA_SERVER=https://main.aura.test\n")
    account.apply_download_settings(str(carried))
    assert user_env.read_text() == "TYPESAFE_API_KEY=ts-key-0001\nAUTOPILOT_AURA_SERVER=https://main.aura.test\n"
    account.apply_download_settings(str(carried))  # (installed again: the same)
    assert user_env.read_text() == "TYPESAFE_API_KEY=ts-key-0001\nAUTOPILOT_AURA_SERVER=https://main.aura.test\n"
    account.apply_download_settings(none)  # (a download for Aura's own server carries none: the old one goes)
    assert user_env.read_text() == "TYPESAFE_API_KEY=ts-key-0001\n"
    assert not (tmp_path / account.DOWNLOAD_SETTINGS).exists()
    account.apply_download_settings(str(carried))
    user_env.write_text("AUTOPILOT_AURA_SERVER=https://mine.aura.test\n")  # (changed by hand since: kept)
    account.apply_download_settings(none)
    assert user_env.read_text() == "AUTOPILOT_AURA_SERVER=https://mine.aura.test\n"


def test_the_llm_key_told_is_the_picked_providers(monkeypatch, tmp_path):
    user_env = tmp_path / "autopilot.env"
    monkeypatch.setenv("ANTHROPIC_API_KEY", "an-from-the-editors-environment")
    user_env.write_text("TYPESAFE_API_KEY=ts-key-0001\nANTHROPIC_API_KEY=an-key-0002\n")
    s = account.state()
    assert s["llm_provider"] == "anthropic" and s["keys"] == {"decision": "••••0001", "llm": "••••0002"}
    user_env.write_text("TYPESAFE_API_KEY=ts-key-0001\nANTHROPIC_API_KEY=an-key-0002\n"
                        "AUTOPILOT_OPENCODE_PROVIDER=openai\nOPENAI_API_KEY=sk-proj-key-0003\n")
    s = account.state()
    assert s["llm_provider"] == "openai" and s["keys"] == {"decision": "••••0001", "llm": "••••0003"}
    user_env.write_text("AUTOPILOT_OPENCODE_PROVIDER=openai\n")  # (no OpenAI key: none told, not Anthropic's)
    assert account.state()["keys"]["llm"] is None


def test_the_coding_agent_decides_the_llm_provider(tmp_path):
    """Claude Code runs on Anthropic's keys and Codex on OpenAI's, whatever OpenCode's provider says; OpenCode on the
    one picked for it. The frame tells both, and the key of that provider."""
    user_env = tmp_path / "autopilot.env"
    keys = "ANTHROPIC_API_KEY=an-key-0001\nOPENAI_API_KEY=sk-key-0002\nOPENROUTER_API_KEY=or-key-0003\n"
    for agent, provider, llm in (("claude", "anthropic", "••••0001"), ("codex", "openai", "••••0002"),
                                 ("opencode", "openrouter", "••••0003")):
        user_env.write_text(f"AUTOPILOT_AGENT={agent}\nAUTOPILOT_OPENCODE_PROVIDER=openrouter\n" + keys)
        s = account.state()
        assert (s["coding_agent"], s["llm_provider"], s["keys"]["llm"]) == (agent, provider, llm)
    user_env.write_text("AUTOPILOT_AGENT=nonsense\n")  # (an agent there is no such: the default's)
    assert account.state()["coding_agent"] == "opencode"


def test_other_frames_are_left_to_the_serve_loop():
    assert not account.handle(Link(), {"type": "start"}, account.SignIn(Link()))


def test_a_plugin_without_files_saves_the_setup_through_the_agent_service(monkeypatch, own_preferences, tmp_path):
    """Roblox Studio's plugin can't write Autopilot/'s files: its first-run setup sends them (save_setup), and they are
    saved as the other plugins save them: preferences.json merged, autopilot.env's lines replaced, the rest kept."""
    env = tmp_path / "autopilot.env"
    env.write_text("AUTOPILOT_AURA_SERVER=https://staging.test\nAUTOPILOT_AGENT=codex\nTYPESAFE_API_KEY=old\n")
    own_preferences.write_text(json.dumps({"share_data": True, "other": 1}))
    monkeypatch.setattr(account, "_user_env_path", lambda: str(env))
    monkeypatch.setattr(account, "_from_user_env", {})
    for name in ("TYPESAFE_API_KEY", "AUTOPILOT_AGENT", "AUTOPILOT_OPENCODE_PROVIDER", "OPENROUTER_API_KEY",
                 "AUTOPILOT_AURA_URL", "AUTOPILOT_AURA_SERVER"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setattr(aura, "client_session", lambda *a, **k: (None, None))
    link = Link()
    link.loopback = True
    assert account.handle(link, {"type": "save_setup", "access": "own_keys", "share": False, "coding_agent": "opencode",
                                 "llm_provider": "openrouter", "keys": {"decision": "ts-1234567890ab",
                                                                        "llm": "or-abcdefgh9876"}}, None)
    assert link.frames("setup_saved") == [{"type": "setup_saved", "ok": True}]
    lines = env.read_text().splitlines()
    assert "AUTOPILOT_AURA_SERVER=https://staging.test" in lines
    assert {"AUTOPILOT_AGENT=opencode", "AUTOPILOT_OPENCODE_PROVIDER=openrouter", "TYPESAFE_API_KEY=ts-1234567890ab",
            "OPENROUTER_API_KEY=or-abcdefgh9876"} <= set(lines)
    assert "TYPESAFE_API_KEY=old" not in lines and "AUTOPILOT_AGENT=codex" not in lines
    prefs = json.loads(own_preferences.read_text())
    assert prefs == {"share_data": False, "other": 1, "version": 1, "onboarded": True, "access": "own_keys"}
    state = link.frames("account")[-1]
    assert state["mode"] == "own_keys" and state["keys"] == {"decision": "••••90ab", "llm": "••••9876"}
    assert state["coding_agent"] == "opencode" and state["llm_provider"] == "openrouter"
    # A key left out keeps its line; a bad one is refused and nothing changes; another machine's editor can't save
    assert account.save_setup({"access": "own_keys", "coding_agent": "claude", "keys": {}}) == (True, None)
    assert "TYPESAFE_API_KEY=ts-1234567890ab" in env.read_text() and "AUTOPILOT_AGENT=claude" in env.read_text()
    assert account.save_setup({"access": "own_keys", "keys": {"llm": "has space"}})[0] is False
    assert account.save_setup({"access": "maybe"})[0] is False
    far = Link()
    far.loopback = False
    account.handle(far, {"type": "save_setup", "access": "aura"}, None)
    assert far.frames("setup_saved")[0]["ok"] is False
    assert json.loads(own_preferences.read_text())["access"] == "own_keys"
