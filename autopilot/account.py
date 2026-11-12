"""How this Autopilot runs, as the plugins' first-run setup and Settings page show it, and the Aura sign-in they ask for
(docs/protocol.md, "Account").

    account frame     {"type": "account", "mode": "aura"|"own_keys"|"none", "email"?, "signed_in", "keys": {...},
                      "llm_provider", "share_data", "bill"?, "out_of_credits"?}: sent after every welcome, sign-in,
                      sign-out, run through Aura, and when the plugin asks ("account"). "keys" are the BYOK
                      keys, masked, and "llm_provider" whose LLM key "llm" is ("anthropic" or "openai"). With Aura,
                      "bill" is what Aura billed for Autopilot this billing period ({"usd", "runs", "from", "to"}, or
                      null until Aura says), and "out_of_credits" Aura's words when the team has no credits left for a
                      run ({"message", "url"}: where to add some, when Aura names it), else null
    bill frame        {"type": "bill", "usd", "billed"}: what Aura billed a run, sent before its result
    aura_sign_in      the plugin asks for a sign-in on Aura's website: the browser opens (autopilot/aura_login.py), and
                      the plugin is told "waiting" (with the page's url), then "signed_in" or "error"
    aura_sign_in_cancel, aura_sign_out
    save_setup        {"type": "save_setup", "access": "aura"|"own_keys", "share", "coding_agent", "llm_provider",
                      "keys": {"decision"?, "llm"?}}: the first-run setup's choices, from a plugin that can't write the
                      files below itself (Roblox Studio's plugins have no file access), saved here as the other
                      plugins save them; answered {"type": "setup_saved", "ok", "message"?}, then the account frame.
                      Only from this machine. Saved credentials and access mode apply before the next run;
                      saving and reporting them never changes an active run's environment
    save_preferences  {"type": "save_preferences", "share": bool}: explicitly change the saved sharing preference
                      from a local editor; answered {"type": "preferences_saved", "ok", "message"?}, then account

The plugin never holds Aura's tokens: the sign-in is this agent service's, kept for the computer
(Autopilot/aura-session.json), so every editor's Autopilot and every `autopilot serve` use it.

Which way runs go is the first-run setup's choice (Autopilot/preferences.json "access"), read before every run:
"aura" sends them through Aura (autopilot/aura.py) unless Autopilot was started with AUTOPILOT_AURA_URL or --aura, which
always does.
"""

import json
import os
import re
import threading
import time

import requests

from . import aura, aura_login, bridge

PREFERENCES = "preferences.json"
USER_ENV = "autopilot.env"  # the BYOK keys (and settings) the first-run setup saved, for the agent service
_from_user_env = {}  # what load_user_settings last set from it
BILL_SECONDS = 60.0  # how long the period's bill is told as last asked before Aura is asked again
BILL_TIMEOUT = 10.0
# (with it, "credits": Aura's out-of-credits words for that server's account, None while it has credits or until known)
_bill = {"value": None, "at": 0.0, "server": None, "asking": False, "credits": None}
_bill_lock = threading.Lock()
_from_preferences = False  # AUTOPILOT_AURA_URL was set here from the setup's choice (so it can be taken back)
# Whether Aura was chosen when Autopilot started (AUTOPILOT_AURA_URL or --aura): kept whatever the setup says
_fixed = None


def _preferences_path():
    return bridge.brain_file(PREFERENCES)


def preferences():
    """The first-run setup's choices ({} when there are none)."""
    try:
        with open(_preferences_path(), encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def chose_aura():
    """Whether the user chose, in a plugin's first-run setup, to run Autopilot through Aura."""
    return preferences().get("access") == "aura"


def fix_at_start():
    """Note whether runs go through Aura whatever the setup says (AUTOPILOT_AURA_URL set, or --aura given)."""
    global _fixed
    _fixed = aura.enabled()


def follow_preferences():
    """Before each run: through Aura when the setup says so, else not (unless fixed at start)."""
    global _from_preferences
    if _fixed:
        return
    if chose_aura():
        if not aura.enabled():
            os.environ["AUTOPILOT_AURA_URL"] = aura.default_url()
            _from_preferences = True
    elif _from_preferences:
        os.environ.pop("AUTOPILOT_AURA_URL", None)
        _from_preferences = False


def _user_env_path():
    return bridge.brain_file(USER_ENV)


def user_settings():
    """The keys and settings the plugins' first-run setup saved (Autopilot/autopilot.env; an empty value is none)."""
    from dotenv import dotenv_values

    try:
        return {k: v for k, v in dotenv_values(_user_env_path()).items() if v}
    except (OSError, ValueError):
        return {}


DOWNLOAD_SETTINGS = "download-settings.env"  # what the download installed last carried (its settings.env)
_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=")


def apply_download_settings(carried):
    """The downloads' installer, once it has installed the wheel: the settings the download carries (carried: the
    settings.env beside the installer, e.g. the Aura server it was built for; none when it isn't there) go into
    autopilot.env over what it says, and those an earlier download carried that this one doesn't are taken out again,
    unless they were changed since. What it carried is kept (Autopilot/download-settings.env) for the next one."""
    from dotenv import dotenv_values

    new = {k: v for k, v in dotenv_values(carried).items() if v} if os.path.exists(carried) else {}
    path = _user_env_path()
    record = os.path.join(os.path.dirname(path), DOWNLOAD_SETTINGS)
    old = dotenv_values(record) if os.path.exists(record) else {}
    now = dotenv_values(path) if os.path.exists(path) else {}
    drop = set(new) | {k for k, v in old.items() if k not in new and now.get(k) == v}
    lines = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            lines = [line.rstrip("\r\n") for line in f]
    kept = [line for line in lines if not ((m := _ENV_LINE.match(line)) and m.group(1) in drop)]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if new or kept != lines:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.writelines(f"{line}\n" for line in kept + [f"{k}={v}" for k, v in new.items()])
        if os.name != "nt":
            os.chmod(path, 0o600)
    if new:
        with open(record, "w", encoding="utf-8", newline="\n") as f:
            f.writelines(f"{k}={v}\n" for k, v in new.items())
    elif os.path.exists(record):
        os.remove(record)


def load_user_settings():
    """Before every run (never during one: a run through Aura has its own keys set): autopilot.env's keys and settings,
    over the environment Autopilot was started with (the setup is where the user chose them; an editor's environment
    may hold keys of its own). One changed or removed there since the last run is changed or removed here too."""
    values = user_settings()
    for k, v in _from_user_env.items():
        if k not in values and os.environ.get(k) == v:
            os.environ.pop(k, None)
    os.environ.update(values)
    _from_user_env.clear()
    _from_user_env.update(values)


def server():
    """The Aura server sign-ins are for."""
    return aura.api_url() or aura.default_url()


def masked(value):
    value = (value or "").strip()
    return f"••••{value[-4:]}" if len(value) >= 8 else None


def fetch_bill(link=None, http=requests):
    """What Aura billed for Autopilot this billing period (GET /api/autopilot/usage): {"usd", "runs", "from", "to"};
    None when signed out or Aura didn't say. Each session in turn, as a run's (aura.session_tokens)."""
    for token in aura.session_tokens(link, None, http, server()):
        try:
            r = http.get(f"{server().rstrip('/')}/api/autopilot/usage", headers={"Authorization": f"Bearer {token}"},
                         timeout=BILL_TIMEOUT)
            if r.status_code == 401:
                continue
            data = r.json() if r.status_code == 200 else None
        except (requests.RequestException, ValueError):
            return None
        break
    else:
        return None
    if not isinstance(data, dict) or not isinstance(data.get("usd"), (int, float)):
        return None
    return {"usd": round(float(data["usd"]), 6), "runs": int(data.get("runs") or 0), "from": data.get("from"),
            "to": data.get("to")}


def out_of_credits_notice(message):
    """The account frame's "out_of_credits" for Aura's refusal words: {"message", "url"}, url the page they name to add
    credits at (None when they name none)."""
    url = re.search(r"https?://\S+", message or "")
    return {"message": message, "url": url.group(0).rstrip(".,;)") if url else None}


def fetch_credits(link=None, http=requests):
    """Whether the signed-in user's team has credits for a run (POST /api/autopilot/validate-access: premium, then the
    free allowance; the check a run starts with, without starting one): (True, None) when it has, (True, the
    out_of_credits notice) when Aura says NO_CREDITS, (False, None) when Aura didn't say (signed out, another refusal,
    not reachable). Each session in turn, as the bill's."""
    for token in aura.session_tokens(link, None, http, server()):
        try:
            r = http.post(f"{server().rstrip('/')}/api/autopilot/validate-access",
                          headers={"Authorization": f"Bearer {token}"}, json={"mode": "playtest"}, timeout=BILL_TIMEOUT)
            if r.status_code == 401:
                continue
            data = r.json() if r.status_code in (200, 402) else None
        except (requests.RequestException, ValueError):
            return False, None
        break
    else:
        return False, None
    if r.status_code == 200 and isinstance(data, dict) and data.get("valid") is True:
        return True, None
    if r.status_code == 402 and isinstance(data, dict) and data.get("code") == "NO_CREDITS":
        return True, out_of_credits_notice(str(data.get("error") or "Your team is out of Aura credits."))
    return False, None


def note_credits(message):
    """What a run Aura started (None: the team has credits) or refused for credits (Aura's words) says, for the next
    account frame: told at once, not a minute later."""
    with _bill_lock:
        if _bill["server"] != server():
            _bill.update(value=None, at=0.0, server=server())
        _bill["credits"] = out_of_credits_notice(message) if message else None


def out_of_credits():
    """Aura's out-of-credits notice as last known (None while the team has credits, or until known)."""
    with _bill_lock:
        return _bill["credits"] if _bill["server"] == server() else None


def bill():
    """The period's bill as last asked (None until asked); stale after BILL_SECONDS or a run (forget_bill)."""
    with _bill_lock:
        return _bill["value"] if _bill["server"] == server() else None


def bill_stale():
    with _bill_lock:
        return _bill["server"] != server() or time.time() - _bill["at"] > BILL_SECONDS


def forget_bill():
    with _bill_lock:
        _bill["at"] = 0.0


def coding_agent(saved=None):
    """The Coding Agent BYOK runs use (autopilot/agents): the setup's AUTOPILOT_AGENT in autopilot.env, else the
    environment's, else the default."""
    from . import agents

    saved = user_settings() if saved is None else saved
    name = (saved.get("AUTOPILOT_AGENT") or os.environ.get("AUTOPILOT_AGENT") or "").strip().lower()
    return name if name in agents.NAMES else agents.DEFAULT


def llm_provider(saved=None):
    """The LLM provider BYOK runs use: Anthropic for Claude Code, OpenAI for Codex, and OpenCode's own
    (AUTOPILOT_OPENCODE_PROVIDER: the setup's in autopilot.env, else the environment's;
    autopilot/agents/opencode.py)."""
    from .agents import opencode

    saved = user_settings() if saved is None else saved
    agent = coding_agent(saved)
    if agent == "claude":
        return "anthropic"  # (Claude Code takes Anthropic's keys only)
    if agent == "codex":
        return "openai"  # (Codex takes OpenAI's keys only)
    name = "AUTOPILOT_OPENCODE_PROVIDER"
    return (saved.get(name) or os.environ.get(name) or "").strip().lower() or opencode.DEFAULT_PROVIDER


def llm_keys(provider):
    """The environment variables the provider's key may be in."""
    from .agents import opencode

    return opencode.PROVIDER_KEYS.get(provider) or (provider.upper().replace("-", "_") + "_API_KEY",)


def state():
    """The account frame."""
    prefs = preferences()
    access = prefs.get("access")
    saved = user_settings()  # (the next run's keys: the setup's, else the environment's)
    setting = lambda name: saved.get(name) or os.environ.get(name)
    provider = llm_provider(saved)
    frame = {"type": "account", "keys": {"decision": masked(setting("TYPESAFE_API_KEY")),
                                         "llm": masked(next(filter(None, map(setting, llm_keys(provider))), None))},
             "coding_agent": coding_agent(saved), "llm_provider": provider, "supports_cli_launch": True,
             "share_data": prefs.get("share_data") is not False}
    # A kept sign-in is told whatever the mode: the plugin's first-run setup signs in before it records the choice
    kept = aura_login.saved(server())
    if _fixed or (aura.enabled() and not _from_preferences) or access == "aura":
        signed_in = bool(kept) or aura.app_running()
        frame.update(mode="aura", signed_in=signed_in, email=(kept or {}).get("email"),
                     bill=bill() if signed_in else None, out_of_credits=out_of_credits() if signed_in else None)
    else:
        mode = "own_keys" if access == "own_keys" or any(frame["keys"].values()) else "none"
        frame.update(mode=mode, signed_in=bool(kept), email=(kept or {}).get("email"))
    return frame


def send_state(link):
    """Send the account frame; with Aura, the period's bill and whether the team has credits are asked for in the
    background when stale, and the frame sent again once Aura says (the serve loop never waits on Aura)."""
    frame = state()
    try:
        link.send(frame)
    except Exception:
        pass
    if frame["mode"] == "aura" and frame["signed_in"] and bill_stale():
        _refresh_bill(link)


def _refresh_bill(link):
    with _bill_lock:
        if _bill["asking"]:
            return
        _bill["asking"] = True

    def ask():
        changed = False
        try:
            asked_of, value = server(), fetch_bill(link)
            known, credits = fetch_credits(link)
            with _bill_lock:
                if _bill["server"] != asked_of:
                    _bill["credits"] = None
                if known:  # (Aura not saying leaves what it said last)
                    changed = _bill["credits"] != credits
                    _bill["credits"] = credits
                changed = changed or _bill["server"] != asked_of or _bill["value"] != value
                _bill.update(value=value, at=time.time(), server=asked_of)
        finally:
            with _bill_lock:
                _bill["asking"] = False
        if changed:
            try:
                link.send(state())
            except Exception:
                pass

    threading.Thread(target=ask, name="aura-bill", daemon=True).start()


class SignIn:
    """The plugin's sign-in requests: one at a time, in a thread of its own (the serve loop keeps reading)."""

    def __init__(self, link):
        self.link = link
        self.cancel = threading.Event()
        self.thread = None
        self.waiting = None  # the "waiting" frame of the sign-in going on, for a plugin that asks again

    def _send(self, **frame):
        frame = {"type": "aura_sign_in", **frame}
        self.waiting = frame if frame.get("state") == "waiting" else None
        try:
            self.link.send(frame)
        except Exception:
            pass

    def start(self):
        """Start a sign-in; one already going is told again (a plugin that lost it: a script reload, a new tab)
        rather than started twice. Returns whether a new one started."""
        if self.thread is not None and self.thread.is_alive():
            if self.waiting:
                try:
                    self.link.send(self.waiting)
                except Exception:
                    pass
            return False
        self.cancel = threading.Event()
        self.thread = threading.Thread(target=self._run, name="aura-sign-in", daemon=True)
        self.thread.start()
        return True

    def _run(self):
        def opened(url):
            import webbrowser

            self._send(state="waiting", url=url)
            try:
                return webbrowser.open(url)
            except Exception:
                return False

        try:
            session = aura_login.sign_in(server(), open_browser=opened, say=lambda text: None,
                                         cancelled=self.cancel.is_set)
            self._send(state="signed_in", email=session.get("email"))
        except aura_login.SignInError as e:
            text = str(e)
            reason = ("cancelled" if self.cancel.is_set() else "denied" if "cancelled in the browser" in text
                      else "timeout" if "timed out" in text else "unreachable" if "couldn't be reached" in text
                      else "refused")
            words = {"denied": "Sign-in was cancelled in the browser.",
                     "timeout": "Nothing came back from the browser in 5 minutes.",
                     "unreachable": "Couldn't reach Aura. Check your connection and try again."}
            self._send(state="error", reason=reason, message=words.get(reason, text))
        except Exception as e:  # noqa: BLE001
            self._send(state="error", reason="refused", message=f"The sign-in failed: {type(e).__name__}: {e}")
        send_state(self.link)

    def stop(self):
        self.cancel.set()

    def sign_out(self):
        self.stop()
        aura_login.sign_out(server())
        send_state(self.link)


# The Coding Agents and OpenCode providers the setup offers (the plugins' first-run setup)
SETUP_AGENTS = ("opencode", "claude", "codex")
SETUP_PROVIDERS = ("anthropic", "openai", "opencode", "openrouter", "deepseek", "google")


def _set_env_lines(path, values):
    """Replace these keys' lines in an env file (adding those it lacks), leaving every other line as it is."""
    lines = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            lines = [line.rstrip("\r\n") for line in f]
    kept = [line for line in lines if not ((m := _ENV_LINE.match(line)) and m.group(1) in values)]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.writelines(f"{line}\n" for line in kept + [f"{k}={v}" for k, v in values.items()])
    if os.name != "nt":
        os.chmod(path, 0o600)


def _save_preferences(changes):
    prefs = {**preferences(), **changes}
    path = _preferences_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(prefs, f, indent=1)
    os.replace(path + ".tmp", path)


def save_setup(frame):
    """Save the first-run setup's choices a plugin sends (save_setup, above): preferences.json (onboarded, share_data,
    access; its other keys kept) and, for BYOK, autopilot.env's AUTOPILOT_AGENT, AUTOPILOT_OPENCODE_PROVIDER (OpenCode
    only) and the keys given (a key not given keeps its line). Returns (ok, why not)."""
    access = frame.get("access")
    if access not in ("aura", "own_keys"):
        return False, "access must be aura or own_keys"
    env = {}
    if access == "own_keys":
        agent = str(frame.get("coding_agent") or "opencode").strip().lower()
        if agent not in SETUP_AGENTS:
            return False, f"unknown coding agent {agent}"
        env["AUTOPILOT_AGENT"] = agent
        provider = {"claude": "anthropic", "codex": "openai"}.get(agent)
        if provider is None:
            provider = str(frame.get("llm_provider") or "anthropic").strip().lower()
            if provider not in SETUP_PROVIDERS:
                return False, f"unknown LLM provider {provider}"
            env["AUTOPILOT_OPENCODE_PROVIDER"] = provider
        keys = frame.get("keys") if isinstance(frame.get("keys"), dict) else {}
        for given, name in ((keys.get("decision"), "TYPESAFE_API_KEY"), (keys.get("llm"), llm_keys(provider)[0])):
            value = str(given or "").strip()
            if value and not re.search(r"[\s'\"#]", value):
                env[name] = value
            elif value:
                return False, f"{name} has spaces or quotes: paste the key alone"
    try:
        if env:
            _set_env_lines(_user_env_path(), env)
        changes = dict(version=1, onboarded=True, access=access)
        if isinstance(frame.get("share"), bool):
            changes["share_data"] = frame["share"]
        _save_preferences(changes)
    except OSError as e:
        return False, f"couldn't write the settings: {e}"
    return True, None


def handle(link, frame, sign_in):
    """A plugin frame about the account; returns whether it was one."""
    kind = frame.get("type")
    if kind == "account":
        send_state(link)
    elif kind in ("save_setup", "save_preferences"):
        if getattr(link, "loopback", None) is not True:
            ok, why = False, "the setup is saved only for an editor on this computer"
        elif kind == "save_preferences":
            if not isinstance(frame.get("share"), bool):
                ok, why = False, "share must be a boolean"
            else:
                try:
                    _save_preferences({"share_data": frame["share"]})
                    ok, why = True, None
                except OSError as e:
                    ok, why = False, f"couldn't write the settings: {e}"
        else:
            ok, why = save_setup(frame)
        if ok and isinstance(frame.get("share"), bool) and getattr(link, "hello", None) is not None:
            link.hello["share"] = frame["share"]
        try:
            reply = "setup_saved" if kind == "save_setup" else "preferences_saved"
            link.send({"type": reply, "ok": ok, **({"message": why} if why else {})})
        except Exception:
            pass
        send_state(link)
    elif kind == "aura_sign_in":
        sign_in.start()  # (one going already: its waiting frame again)
    elif kind == "aura_sign_in_cancel":
        sign_in.stop()
    elif kind == "aura_sign_out":
        sign_in.sign_out()
    else:
        return False
    return True
