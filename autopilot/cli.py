"""Autopilot from the command line.

    python -m autopilot serve [--port 8765] [--project PATH] [--aura]
        Wait for the editor's Autopilot tab to connect, then run each Verify, Play or Explore it starts.
    python -m autopilot play "Sell three crops at the market stall" [--port 8765] [--project PATH]
        Wait for the plugin to connect, then research, write and run a harness for one prompt and exit with its
        result (see EXIT).
    python -m autopilot codex --project PATH [--name NAME]
        Build a project's codex (the game's code as text) without the game running.
    python -m autopilot run memory/<project>/runs/<run>/harness.py [--seconds 60] [--no-restart]
        Play a saved harness again, with no language model (Jev decides), e.g. to watch it.
    python -m autopilot uninstall [--keep-memory] [--yes]
        Remove Autopilot from this computer: its settings, keys, sign-in, memory and command (autopilot/uninstall.py).
        Shared run data already sent stays.
    python -m autopilot mcp
        Autopilot's tools for your own Claude Code or Codex (an MCP server on stdio: autopilot/mcp.py), which then
        works on the game open in the editor on your own plan (`serve`, started by the plugin, runs it).
    python -m autopilot connect claude|codex [--remove]
        Set up your own Claude Code or Codex for that: the MCP server, a skill and a frame describer
        (autopilot/connect.py); --remove takes them out.

--aura signs in with Aura: runs are billed to your Aura plan (autopilot/aura.py), instead of using your own keys. So
does choosing Aura in a plugin's first-run setup (read before every run: autopilot/account.py), whose sign-in the plugin
asks this agent service for. Without the Aura app, a run with no sign-in opens Aura's sign-in in the browser; `autopilot
login` signs in ahead of time and `autopilot logout` signs out (autopilot/aura_login.py).

PATH is any engine's project: an Unreal .uproject (or its folder), a Unity project folder (the one with
ProjectSettings/), or a Godot project.godot (or its folder).
"""

import argparse
import contextlib
import json
import os
import re
import sys
import threading
import time

from . import (
    account,
    agents,
    aura,
    aura_login,
    aura_memory,
    bridge,
    engines,
    memory,
    processes,
    runtime,
    telemetry,
    training,
)
from . import codex as codexmod
from . import levels as levelsmod
from .agents.session import Steering, cut_off, default_timeout, go_on, scrub
from .agents.session import model as agent_model
from .kit.game import Game
from .words import change_text, plain_words  # noqa: F401 (what the user reads; also cli.plain_words)

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # the repository (where .env is looked for)
# The model keys the plugins' first-run setup saves (docs/protocol.md, "Sharing a run's data"), in the user settings
# folder: read for keys the environment and .env don't set
USER_ENV = "autopilot.env"

# Exit codes by outcome: 0 confirmed or achieved, 2 denied, 3 inconclusive, 1 not achieved or failed
EXIT = {"CONFIRMED": 0, "DENIED": 2, "INCONCLUSIVE": 3, "achieved": 0, "not_achieved": 1, "explored": 0}
VERDICTS = ("CONFIRMED", "DENIED", "INCONCLUSIVE")
# The status a run ends with when it added to the game's memory (before its result; the plugins' Report stage)
SAVING_MEMORY = "Saving what it learned to memory"
# An exploration's budget beyond its minutes of play: reading the game and writing the harness before, reporting after
PAST_PLAY_SECONDS = 15 * 60
# The time limit a run may ask for (time_limit_minutes): from a few minutes to a long day's run
TIME_LIMIT_MINUTES = (1.0, 600.0)
# What a run leaves going after its result (its memory saved with Aura, its shared data sent): `autopilot play`
# waits for it before it exits, as a thread pool takes no work once the main thread has ended
AFTER_RUN = []
AFTER_RUN_SECONDS = 600.0
# A Reset memory waits for that first (it reads the run's files, and on Windows a folder with a file open can't move),
# up to RESET_WAIT_SECONDS; then a file still in use is tried again RESET_TRIES times, a second apart
RESET_WAIT_SECONDS = 120.0
RESET_TRIES = 5
# The longest a run waits, once it reads the game's code, for a game that says it is still starting (Unreal's play
# session loading its level: Unreal's Analyze stage reads "Waiting for PIE"). Its reading and probing time counts
# from the end of the wait (agents/session.py, RESEARCH_SECONDS); past this the game is read as it is
GAME_START_SECONDS = 600.0


class Feed:
    """The latest run's activity events (docs/protocol.md, `activity`), kept from the run's start until the next one
    starts, for a plugin that asks for them again: a hello with `activity_since`, or the plugin back after a reload
    (a rebind), gets every event it hasn't got, in order."""

    def __init__(self):
        self.events = []
        self.lock = threading.Lock()

    def start(self):
        """A run starts: its feed begins empty."""
        with self.lock:
            self.events = []

    def add(self, link, frame):
        """An event of the run going now: kept, and sent to the plugin."""
        with self.lock:
            self.events.append(frame)
        try:
            link.send(frame)
        except Exception:
            pass  # (the plugin went: it asks for the feed when it is back)

    def replay(self, link, since=0):
        """Send the plugin the events whose id is above `since` (0: all), in order; how many were sent."""
        with self.lock:
            frames = [f for f in self.events if f.get("id", 0) > since]
        for frame in frames:
            try:
                link.send(frame)
            except Exception:
                break
        return len(frames)

    def watch_rebind(self, link):
        """Send the whole feed again when this link's plugin comes back from a reload (bridge.Link.rebind): what was
        sent before the reload is gone with the old scripts, and what was held meanwhile goes out first."""
        rebound = getattr(getattr(link, "current", link), "rebound", None)
        if isinstance(rebound, list) and not any(getattr(fn, "feed", None) is self for fn in rebound):
            def again():
                self.replay(link, 0)
            again.feed = self
            rebound.append(again)


class LiveLink:
    """The plugin's link as it is now. A plugin that reconnects (Connect pressed again, a dropped connection) comes
    back as a new bridge.Link; requests go to the newest one, so a run carries on across the reconnect. Only the same
    game's, while a run goes: run_holds."""

    def __init__(self, first):
        self._first = first
        self._state = getattr(first, "server_state", None) or {"current": first}

    @property
    def current(self):
        return self._state.get("current") or self._first

    def __getattr__(self, name):
        return getattr(self.current, name)


@contextlib.contextmanager
def run_holds(link):
    """For the time of a run the link is its game's: another game's plugin is refused (bridge.serve_local)."""
    state = getattr(link, "_state", None)
    if isinstance(state, dict):
        state["run"] = getattr(link, "hello", None) or {}
    try:
        yield
    finally:
        if isinstance(state, dict):
            state.pop("run", None)


# -- the project on disk ------------------------------------------------------------------------------------------

def remember_project(project, path, engine=None):
    """Remember where the project is on this machine (in memory/<project>/project.json) when path is given; return
    the remembered path (None when none is)."""
    folder = codexmod.project_dir(project)
    os.makedirs(folder, exist_ok=True)
    file = os.path.join(folder, "project.json")
    info = {}
    if os.path.exists(file):
        with open(file, encoding="utf-8") as f:
            info = json.load(f)
    if path:
        found, canonical = engines.find_project(path, engine.name if engine else None)
        info["project"] = os.path.abspath(canonical or path)
        if engine or found:
            info["engine"] = (engine or found).name
        with open(file, "w", encoding="utf-8") as f:
            json.dump(info, f, indent=1)
    return info.get("project")


def find_project(game, project, name=None):
    """Ask the running game where its project is (this brain runs on the same machine as the editor), unless the
    path is remembered already. project: its memory folder's name; name: the project's own (by default, the same).
    Returns the path found, or None."""
    if remember_project(project, None):
        return None
    found = engines.of(game).find_project(game, name or project)
    if found:
        print(f"Found the project: {found}")
    return found


def choose_level(link, asked=None):
    """The level the run starts in (a levels.Level): `asked` (the start frame's `level`) when it is one of the
    project's, else the one open in the editor (Level.asked empty). Whether the request means another is the agent's
    to decide (its briefing lists the levels; restart_level moves there): a `level` that names none of the list's is
    passed on to it (Level.hint). The plugin lists the project's levels (the `levels` op); one from before it lists
    none, and plays only the level open in the editor."""
    level = levelsmod.Level()
    try:
        level.levels, level.open, level.playing = levelsmod.listing(link.request("levels", {}, timeout=30))
        listed = True
    except Exception as e:  # (a plugin from before levels: "unknown op")
        print(f"note: the plugin lists no levels: {e}")
        listed = False
    if asked:
        level.asked = levelsmod.resolve(asked, level.levels) or ""
        level.hint = "" if level.asked else str(asked).strip()
    if level.asked and not listed and ((link.hello or {}).get("host") or "editor") != "game":
        # (an editor plugin from before levels plays its open level whatever map it is asked for)
        level.failed = "this version of the Autopilot plugin plays only the level open in the editor: update it"
    return level


def start_level(link, game, level):
    """Start the game (when it isn't running) in the level chosen (choose_level); a level that can't be played
    leaves the game in the editor's own (level.failed says why)."""
    running = bool(link.request("session", {"action": "status"}).get("running"))
    if level.asked and not level.failed:
        if running and levelsmod.same(level.playing, level.asked):
            level.playing_asked = True
        else:
            link.status("Starting the game")
            try:
                link.request("session", {"action": "restart" if running else "start", "map": level.asked},
                             timeout=60)
                level.playing_asked = True
            except Exception as e:
                level.failed = str(getattr(e, "error", None) or e)
                print(f"note: couldn't play {level.asked}: {level.failed}")
                running = bool(link.request("session", {"action": "status"}).get("running"))
        if level.playing_asked:
            game.level = level.asked  # (every restart starts it over)
            level.playing = level.asked
    if not running and not level.playing_asked:
        link.status("Starting the game")
        link.request("session", {"action": "start"}, timeout=60)
    if not level.playing_asked:
        level.playing = level.playing or level.open


def resume_if_failed(game):
    """An exit callback (ExitStack.push) that lets the game prepare() paused play on when the run fails before its
    agent has (each agent's session lets it go when it ends): no frozen game is left in the editor."""
    def done(failed, *_):
        if failed is not None:
            with contextlib.suppress(Exception):
                game.pause(False)
    return done


def level_alert(level):
    """What the user must know when the level asked for couldn't be played (the result's alerts), or None."""
    if not level or not level.asked or level.playing_asked:
        return None
    return (f"The run played the editor's own level, not {levelsmod.short(level.asked)}: {level.failed}. To "
            "play it, open it in the editor and run again.")


def wait_for_start(link, seconds=GAME_START_SECONDS):
    """Waits while the game says it is still starting (`session` status `loading`: a play session or a scene still
    loading, as every engine's plugin says) or is too busy to answer, up to `seconds`. Returns the seconds waited. A
    game that doesn't say (an older plugin) is ready."""
    t0 = time.time()
    while time.time() - t0 < seconds and not getattr(link, "closed", None):
        asked = time.time()
        try:
            if not link.request("session", {"action": "status"}, timeout=30).get("loading"):
                break
        except Exception:
            if time.time() - asked < 25:
                break  # (an error answer, or the game gone: not one too busy loading to answer in time)
        time.sleep(0.5)
    return time.time() - t0


def prepare(link, path=None, level=None):
    """The game, started if needed in the level the run plays (choose_level), and its codex (built on first use; the
    level snapshot refreshed every time). path: the project on this machine when given by hand (--project);
    otherwise it is remembered or asked of the game. level: the level the run asks for (its start frame's), if any."""
    project = codexmod.project_of(link.hello)  # (its memory folder's name)
    game = Game(link, codexmod.Codex.for_project(project))
    name = (link.hello or {}).get("project") or "game"
    path = remember_project(project, path or find_project(game, project, name), game.engine)
    # In the editor the game may not be running yet (Unreal's Play In Editor, Unity's play mode, Godot's scene)
    game.level_info = choose_level(link, level)
    try:
        start_level(link, game, game.level_info)
    except Exception as e:
        print(f"note: couldn't start the session: {e}")
    if game.level_info.playing_asked:
        link.status(f"Playing the level {levelsmod.short(game.level)}")
    playing = game.level_info.playing or "the editor's own"
    print(f"Level: {playing}" + (f" (asked for {game.level_info.asked})" if game.level_info.asked else ""))
    for _ in range(80):  # up to 20 s for the player to appear
        try:
            if game.context(fresh=True).get("character"):
                break
        except Exception:
            pass
        time.sleep(0.25)
    link.status("Reading the game's code")
    waited = wait_for_start(link)
    if waited >= 1.0:
        print(f"note: the game took {waited:.0f} s more to start")
    game.ready_at = time.time()  # (the run's reading and probing time counts from here)
    # The game holds still while its code is read and the run is set up (the agent's session keeps it paused between
    # tool calls; until then it played on unwatched, and an enemy could hit the waiting player)
    try:
        game.pause(True)
    except Exception as e:
        print(f"note: couldn't pause the game: {e}")
    # The export the plugin runs now (a first one, or again after assets were saved) streams in while the agent works
    # (codex.Indexer): the run starts on the codex there is, the level it plays exported first
    game.codex = codexmod.build(project, path=path, game=game, export=True, stream=True,
                                first_map=game.level_info.playing or game.level)
    indexer = getattr(game.codex, "indexer", None)
    if indexer is not None and indexer.running:
        print(f"codex: the export goes on while the run works ({codexmod.progress(indexer.status())})")
    elif not game.engine.has_export(game.codex.root):
        print(game.engine.no_export_note)
    return project, game


# -- what the user reads ------------------------------------------------------------------------------------------

def result_frame(report, prompt, kind=None):
    """The `result` the plugin is sent for a report (docs/protocol.md, Runs): a verdict for a claim, else a goal's.
    kind: the run's (verify, goal or explore), which decides the result's shape: a goal worded as a claim ("Confirm
    the player spawns...") may be reported CONFIRMED and is still a goal's result, reached. Without one the prompt
    and the outcome decide."""
    outcome = report.get("outcome", "")
    code = EXIT.get(outcome, 1)
    summary = plain_words(report.get("summary", ""))
    evidence = [plain_words(e) for e in report.get("evidence", []) or []]
    findings = [plain_words(f) for f in report.get("findings", []) or []]
    # Every direct change to the game's state (not player input) the run made: the reader must know the outcome
    # was played on a game that was changed
    changes = [dict(c, **({"why": plain_words(c["why"])} if c.get("why") else {}), text=change_text(c))
               for c in report.get("level_modified", []) or [] if isinstance(c, dict)]
    # What the user must know to trust the outcome or to get a better one (the game's picture couldn't be looked at)
    alerts = {"alerts": [str(a) for a in report["alerts"]]} if report.get("alerts") else {}
    if kind == "verify" or (kind is None and (re.match(r"(?i)\s*verify", prompt) or outcome in VERDICTS)):
        # (a claim reported as a goal: achieved is shown true)
        verdict = outcome if outcome in VERDICTS else "CONFIRMED" if outcome == "achieved" else "INCONCLUSIVE"
        return {"verdict": verdict, "reason": summary, "claim": prompt, "evidence": evidence, "changes": changes,
                "exit_code": EXIT[verdict], **alerts}
    return {"kind": "goal", "reached": outcome in ("achieved", "CONFIRMED"), "goal": prompt,
            "notes": [summary] + evidence[:8] + findings[:8], "changes": changes, "exit_code": code, **alerts}


def prompt_of(run):
    """The prompt a `start` frame's run asks for."""
    kind = run.get("kind")
    if kind == "verify":
        return run.get("claim", "")
    if kind == "goal":
        return run.get("goal", "")
    if kind == "explore":
        return ("Explore the game for bugs" + (f": {run['brief']}" if run.get("brief") else "")
                + f" ({play_minutes(run):g} minutes of play)")
    if kind == "auto":  # (the words as typed: Jev reads their kind)
        return run.get("prompt", "")
    return run.get("goal") or run.get("claim") or ""


def explore_minutes(run):
    """A `kind: auto` start frame's minutes of play, should Jev read it as an exploration: its time limit (10 when it
    gives none); None for any other run."""
    if run.get("kind") != "auto":
        return None
    try:
        return max(1.0, float(run.get("time_limit_minutes") or 10))
    except (TypeError, ValueError):
        return 10.0


def play_minutes(run):
    """An explore `start` frame's minutes of play (10 when it gives none); None for any other run."""
    if run.get("kind") != "explore":
        return None
    try:
        minutes = float(run.get("minutes") or 10)
    except (TypeError, ValueError):
        minutes = 10.0
    return max(1.0, minutes)


def time_limit(run):
    """A run's time limit in seconds, as its `start` frame (or a game's command-line run) asks for it
    (time_limit_minutes, docs/protocol.md, Runs: the plugins' composer sends it with every run it starts);
    None when it doesn't, and the agent service's own (AUTOPILOT_TIMEOUT, else 30 minutes) is the run's."""
    try:
        minutes = float((run or {}).get("time_limit_minutes") or 0)
    except (TypeError, ValueError):
        return None
    if minutes <= 0:
        return None
    low, high = TIME_LIMIT_MINUTES
    return min(max(minutes, low), high) * 60


# -- runs ---------------------------------------------------------------------------------------------------------

# What Jev is asked to read a prompt as (docs/protocol.md, Runs): the kind of run it asks for. A test is an exploration
# asked for in the user's own words ("Lvl_HordeArea test this game and find all the bugs..."): it plays as one, and the
# plugins show its result as GAME TESTED
KIND_INSTRUCTIONS = (
    "`request` is what a game developer typed to an automated playtester of their game. Choose the kind of run it asks "
    "for. A level or map named in it doesn't change the kind.")
KIND_CRITERIA = {
    "verify": "It states something about the game, or asks a question about it, to be shown true or false (\"Verify "
              "that doors open\", \"Can the player swim?\", \"Check the jump height is 3 m\").",
    "goal": "It asks the player to do or get something in the game, however short it is: an action, a thing to "
            "collect, reach, open, start, win or beat (\"Reach the test chamber\", \"Sell three crops\", \"Pick up 3 "
            "keys\", \"Load the second map\", \"Beat the boss\"). It doesn't ask for problems to be found.",
    "explore": "It asks to explore or playtest the game, or one area or feature of it (a level, a menu, the "
               "tutorial, a weapon), or to find, look for or hunt bugs in it, and says no more: it doesn't ask for a "
               "report, a list, a write-up or fixes (\"Explore\", \"Find bugs in the shop\", \"Look for bugs in the "
               "intro\", \"Playtest the boss fight\", \"Playtest the second level\").",
    "test": "It asks, in its own words, for the game, a level or a part of it to be gone over, and itself asks for the "
            "problems to be reported: a report, a list of issues, fixes, every mismatch or glitch written up "
            "(\"Lvl_HordeArea test this game and find all the bugs with animation, make a report with fixes\", "
            "\"Go through the level and tell me everything that looks wrong\", \"Go over the menus and report every "
            "place they disagree with the game\"). It isn't a claim to check, even when it names what to compare.",
}
KIND_TIMEOUT = 8.0  # seconds Jev has to answer (a cold server takes over a second to wake)
# The rule used when Jev can't be asked (no key, no network): the words a prompt starts with
EXPLORE_WORDS = re.compile(r"(?i)\s*(explore|find bugs|look for bugs|hunt for bugs|bug hunt|playtest)\b")
VERIFY_WORDS = re.compile(r"(?i)\s*(verify|check|confirm|make sure|ensure|is it true)\b")


def classify(prompt, client=None):
    """The kind of run a prompt asks for, as Jev reads it: (kind, tested). kind: verify, explore or goal, as Aura
    bills it; tested: an exploration asked for as a test in the user's own words. When Jev can't be asked, the words
    the prompt starts with decide (kind_by_words), and it is never a test. client: Jev's (by default a new one)."""
    try:
        client = client or runtime.jev_client(warm=False)
        answer = client.system_one(state={"request": prompt.strip()},
                                   questions={"kind": runtime.Choice(instructions=KIND_INSTRUCTIONS,
                                                                     criteria=KIND_CRITERIA)},
                                   timeout=KIND_TIMEOUT,
                                   retry=runtime.RetryPolicy(max_retries=1, backoff_initial=0.2, timeout=KIND_TIMEOUT))
        picked = answer.choices["kind"].choice
    except Exception as e:
        print(f"note: Jev couldn't read the prompt's kind ({type(e).__name__}: {e}); its first words decide")
        return kind_by_words(prompt), False
    if picked == "test":
        return "explore", True
    return (picked, False) if picked in ("verify", "explore", "goal") else (kind_by_words(prompt), False)


def kind_of(prompt):
    """The kind of run a prompt asks for, as Aura bills it (classify): verify, explore or goal."""
    return classify(prompt)[0]


def told_kind(link, prompt):
    """classify(prompt), told to the plugin as it is known (a run_kind frame: docs/protocol.md, Runs)."""
    kind, tested = classify(prompt)
    try:
        link.send({"type": "run_kind", "kind": kind, "tested": tested})
    except Exception:
        pass
    return kind, tested


def kind_by_words(prompt):
    """A prompt's kind by its first words, for when Jev can't be asked: explore (it starts with Explore, Find
    bugs...), verify (it starts with Verify, Check, Confirm... or is a question) or goal."""
    if EXPLORE_WORDS.match(prompt):
        return "explore"
    return "verify" if VERIFY_WORDS.match(prompt) or prompt.strip().endswith("?") else "goal"


def as_claim(prompt):
    """A claim's prompt as the agent reads one: starting with "Verify" ("Can the player swim?" -> "Verify: Can the
    player swim?")."""
    return prompt if re.match(r"(?i)\s*verify", prompt) else f"Verify: {prompt.strip()}"


def start_aura_run(link, kind, project, path=None, trace=None, model=None, disable_premium_models=False,
                   thinking_level=None):
    """With AUTOPILOT_AURA_URL set: the run, started with Aura (autopilot/aura.py), to play inside of; project: the
    project's name; trace: the start frame's run "trace" (where in Aura the run was asked for), passed on to Aura;
    model: its run "model" (the model picked in Aura's chat), which Aura answers with the agent and model to play,
    disable_premium_models its run "disable_premium_models" (Aura's chat's "Disable Premium Models", for Auto), and
    thinking_level its run "thinking_level" (the level picked in Aura's chat for that model).
    Its memory is kept in the project's own folder (codex.project_of). Raises aura.Refused, with what to tell the
    user, when Aura won't start it (a signed-out user signs in on Aura's website)."""
    hello = getattr(link, "hello", None) or {}
    folder = codexmod.project_of({**hello, "project": hello.get("project") or project})
    where = path or remember_project(folder, None)

    def start(token):
        try:
            return aura.Run(token, kind, getattr(link, "mode", None), project,
                            say=lambda text: link.status(text), project_key=hello.get("project_id") or project,
                            trace=trace, model=model, disable_premium_models=disable_premium_models,
                            thinking_level=thinking_level).start()
        except aura.Refused as e:
            print(f"Aura refused the run: HTTP {e.status} {e.code} {e.message}")
            raise

    # Each session this machine has, in turn (aura.session_tokens): one Aura doesn't take (the Aura app's, signed in to
    # another Aura server) gives way to the next
    run = None
    for token in aura.session_tokens(link, where):
        try:
            run = start(token)
            break
        except aura.Refused as e:
            if e.status != 401 or getattr(link, "auth_token", None):
                raise aura.Refused(e.status, aura.refusal_words(e), e.code) from None
    if run is None:
        # No session Aura takes: sign in on Aura's website (the browser opens), then go on; never on the Aura app's own
        # sign-in page, which is blank outside the app. Only for an editor, where someone is there to do it; a game run
        # on its own (CI) is told how
        if hello.get("host") == "game":
            raise aura.Refused(401, "Sign in to Aura first: run `autopilot login`, then start the run again.",
                               "UNAUTHENTICATED") from None
        aura_login.forget(aura.api_url())  # (a kept session Aura no longer takes)
        try:
            session = aura_login.sign_in(aura.api_url(), say=lambda text: link.status(text))
        except aura_login.SignInError as s:
            raise aura.Refused(401, f"{s} Sign in to Aura, then start the run again.", "UNAUTHENTICATED") from None
        try:
            run = start(session["access_token"])
        except aura.Refused as e2:
            raise aura.Refused(e2.status, aura.refusal_words(e2), e2.code) from None
    run.restore_memory(codexmod.project_dir(folder))
    return run


GO_ON_SECONDS = 60  # the least time left for a run to go on on the free allowance (go_on_aura_free)


def describer_model(run, provider):
    """(the provider, the model or None) that describe the run's frames: through Aura, the model Aura named for the
    run (or its free part: run.vision), else Aura's Claude (vision.MODEL through its Anthropic relay), whatever the
    agent plays; with the user's own keys, a model of the agent's provider."""
    if run is None:
        return provider, None
    if run.vision:
        return run.vision["provider"], run.vision["model"]
    return "anthropic", None


def go_on_aura_free(link, run, tel, report, left, game, project, asked, run_id, activity, stop, steering,
                    play_seconds, model):
    """When Aura says the run's premium credits stopped its agent (the relay's 402: aura.Run.go_on_free), the run goes
    on on the team's free allowance: a new session of the agent Aura names (Auto's free-tier model) picks it up from
    its folder (agents/session.go_on), with `left` seconds of its time limit. Nothing tells the user. Returns the
    run's report, or None when it doesn't go on; a team with no free allowance left either gets Aura's words as the
    run's result (and the plugins' out-of-credits notice)."""
    if left < GO_ON_SECONDS:
        return None
    try:
        if not run.go_on_free():
            return None
    except aura.Refused as e:
        if e.code == "NO_CREDITS":
            account.note_credits(e.message)
        return dict(report, summary=e.message)
    tel.add_provider(run.agent["provider"])
    # (frames are described from now on by the model Aura named for the free part, on its key: a new describer)
    seen_by, named = describer_model(run, run.agent["provider"])
    tel.add_provider(seen_by)
    go_on(os.path.join(codexmod.project_dir(project), "runs", run_id), report)
    agent = agents.select(run.agent_name)
    later = agent.implement(game, project, asked, run_id=run_id, say=lambda text: link.status(plain_words(text)),
                            activity=activity, client=tel.jev(runtime.jev_client()), model=model, timeout=left,
                            stop=stop, steering=steering,
                            play_seconds=min(play_seconds, left) if play_seconds else play_seconds,
                            describer=tel.describer(model or agent_model(seen_by), provider=seen_by, model=named))
    # (the run's time is both sessions'; its episodes are counted on from the first's already)
    for field in ("seconds", "tool_seconds"):
        if isinstance(later.get(field), (int, float)) and isinstance(report.get(field), (int, float)):
            later[field] = round(report[field] + later[field], 1)
    return later


def close_game(link):
    """End the editor's play session the run played in (Unreal's Play In Editor, Unity's Play mode, Godot's playing
    scene) once its harnesses are done: the plugins' Report stage shows the result without the game still open. A
    game run on its own (hello host "game") stays open."""
    if ((getattr(link, "hello", None) or {}).get("host") or "editor") == "game":
        return
    try:
        link.request("session", {"action": "stop"}, timeout=30)
    except Exception as e:
        print(f"note: couldn't close the game: {e}")


def load_user_keys():
    """The model keys the plugins' first-run setup saved, as saved now (autopilot/account.py)."""
    account.load_user_settings()


def run_prompt(link, prompt, path=None, model=None, timeout=None, stop=None, sent=None, steering=None, kind=None,
               play_minutes=None, trace=None, desk=None, level=None, aura_model=None, disable_premium_models=False,
               feed=None, explore_minutes=None, design_brief=None, thinking_level=None, cli_client=None):
    """Research, write and run a harness for prompt; send the plugin the result and return the exit code.

    path: the project on this machine (--project); model: the language model; timeout: the agent's budget in
    seconds (the run's time limit, time_limit(); by default the agent service's, session.default_timeout()); stop():
    true once the run must end; sent: a list the result frame is appended to; steering: a Steering the plugin's steer
    frames go into; kind: the run's kind (verify, explore or goal; by default the one Jev reads the prompt as,
    classify(), told to the plugin as a run_kind frame); explore_minutes: for a run whose kind Jev reads, its minutes
    of play should it be an exploration (the composer's Time limit), and design_brief what an exploration is told of
    how the game should behave;
    play_minutes: an exploration's minutes of play (its episodes end once they are up, counted from the first one);
    trace: where in Aura the run was asked for (the start frame's run "trace"), for a run through Aura; desk: the
    user's own agent works on the run, through `autopilot mcp` (an agents.external.Desk; its agent, whatever
    AUTOPILOT_AGENT or Aura says); cli_client: the signed-in CLI the editor asks this service to launch for this run;
    level: the level to play (the start frame's `level`: a path or a name; by default
    the one open in the editor, and the agent moves to the one the request means: autopilot/levels.py); aura_model:
    the model picked in Aura's chat (the start frame's run `model`), for a run through Aura: Aura answers with the
    agent and model that play it (autopilot/aura.py), and the model that describes frames (else Aura's Claude);
    disable_premium_models: the start frame's run `disable_premium_models` (Aura's chat's "Disable Premium Models"),
    passed on with it: Auto in a chat it has picked nothing for yet plays the free-tier model; thinking_level: the
    start frame's run `thinking_level` (the level picked in Aura's chat), passed on with it too: Aura answers with how
    hard the agent thinks; feed: a Feed that
    keeps the run's activity events for a plugin that asks for them again (by default they are only sent).

    The plugin is told the tokens the run uses as it goes (telemetry.RunTelemetry). When the run's data is shared
    (telemetry.sharing, and for a run through Aura the user's Aura account), it is kept whole in the run's
    telemetry/ folder and sent once the plugin has the result (autopilot/training.py).
    """
    run = upload = kept = learned = recording = missing = None
    credits_refused = False
    if feed is not None:
        feed.start()
        activity = lambda frame: feed.add(link, frame)  # noqa: E731
    else:
        def activity(frame):
            try:
                link.send(frame)
            except Exception:
                pass
    load_user_keys()  # (before the budget: autopilot.env may set AUTOPILOT_TIMEOUT; before Jev: its key)
    account.follow_preferences()  # (the first-run setup may have changed how runs go since the last)
    if cli_client is not None:
        from .agents.external import Desk

        if cli_client not in ("claude", "codex"):
            raise ValueError("Unknown CLI selection. Choose Claude Code CLI or Codex CLI in Autopilot.")
        missing = decision_key_missing(in_editor=True)
        if missing:
            raise RuntimeError(missing)
        desk = Desk(cli_client=cli_client)
        model = None  # the signed-in CLI uses its own model setting
    tested = False
    if not kind and not aura.enabled():  # (through Aura, Jev is reached once the run has started: below)
        kind, tested = told_kind(link, prompt)

    def settle():
        """What the agent is asked, its minutes of play and its budget, once the run's kind is known."""
        nonlocal play_minutes, timeout
        if kind == "explore" and not play_minutes and explore_minutes:  # (a run of any kind's time limit)
            play_minutes, timeout = max(1.0, float(explore_minutes)), None
        asked = as_claim(prompt) if kind == "verify" else prompt  # (what the agent is asked)
        if kind == "explore" and explore_minutes:  # (the user's own words: said as an exploration)
            asked = (f"{'Test the game' if tested else 'Explore the game for bugs'}: {prompt.strip()} "
                     f"({play_minutes:g} minutes of play)")
            if design_brief:
                asked += f"\nDesign brief: {design_brief}"
        play_seconds = play_minutes * 60 if play_minutes else None
        if play_seconds:  # (a long exploration needs more than the usual budget)
            timeout = max(timeout or default_timeout(), play_seconds + PAST_PLAY_SECONDS)
        return asked, play_seconds

    try:
        if aura.enabled():
            # (a kind not known yet is told to Aura as a goal: Aura bills every kind the same)
            run = start_aura_run(link, kind or "goal", (link.hello or {}).get("project") or "game", path,
                                 trace=trace, model=aura_model, disable_premium_models=disable_premium_models,
                                 thinking_level=thinking_level)
            account.note_credits(None)  # (Aura started it: the team has credits)
    except aura.Refused as e:
        report = {"outcome": "INCONCLUSIVE" if kind == "verify" else "not_achieved",
                  "summary": e.message, "evidence": []}
        if e.code == "NO_CREDITS":  # (the plugins' chat page and Settings say so until the team has credits again)
            account.note_credits(e.message)
            credits_refused = True
    else:
        with run or contextlib.nullcontext(), contextlib.ExitStack() as setup:
            if not kind:
                kind, tested = told_kind(link, prompt)
            asked, play_seconds = settle()
            project, game = prepare(link, path, level)
            setup.push(resume_if_failed(game))
            indexer = getattr(getattr(game, "codex", None), "indexer", None)
            if indexer is not None:  # (an export still going when the run ends is the next run's)
                setup.callback(indexer.stop)
            # (runs through Aura use the agent Aura names, Claude Code unless the model picked in Aura's chat needs
            # OpenCode, whatever AUTOPILOT_AGENT says; the user's own agent works on a run started from it, through
            # Aura or not)
            if desk is not None:
                from .agents import external as agent
            else:
                agent = agents.select(run.agent_name if run else None)
            run_id = time.strftime("%Y%m%d-%H%M%S")
            root = codexmod.project_dir(project)
            share = telemetry.sharing(getattr(link, "hello", None)) and (run is None or bool(run.training))
            hello = getattr(link, "hello", None) or {}
            key = hello.get("project_id") or hello.get("project") or project  # (the project's key, as Aura keeps it)
            # With the user's own keys, a shared run's memory is kept with Aura for this install, as a run through
            # Aura's is for the team (what other folders saved is fetched first; saved after the result)
            if run is None and share:
                kept = aura_memory.install_memory(key, say=lambda text: link.status(text))
                if kept:
                    kept.restore(root)
            info = {"through_aura": bool(run), "aura_run": getattr(run, "run_id", None)}
            before = memory.snapshot(root)
            provider = getattr(agent, "current_provider", lambda: "anthropic")()
            # (through Aura, frames are described by the model Aura named for the run, else its Claude, whatever the
            # agent plays: that relay is proxied too)
            seen_by, named = describer_model(run, provider)
            with telemetry.RunTelemetry(link, os.path.join(root, "runs", run_id), run_id, project, prompt, kind,
                                        share, providers=tuple(dict.fromkeys((provider, seen_by))),
                                        info=info) as tel:
                # (describe_frames: the recording's frames, described by a vision model of the agent's provider, or
                # the one Aura named)
                describer = tel.describer(model or agent_model(seen_by), provider=seen_by, model=named)
                started = time.time()
                report = agent.implement(game, project, asked, run_id=run_id,
                                         say=lambda text: link.status(plain_words(text)), activity=activity,
                                         client=tel.jev(runtime.jev_client()), model=model, timeout=timeout,
                                         stop=stop, steering=steering, play_seconds=play_seconds,
                                         describer=describer, **({"desk": desk} if desk is not None else {}))
                # A run through Aura whose agent stopped before it reported, its premium credits used up: it goes on
                # on the team's free allowance with the agent Aura names, from where it was (go_on_aura_free)
                if run is not None and desk is None and cut_off(report) and not (stop and stop()):
                    left = (timeout or default_timeout()) - (time.time() - started)
                    report = go_on_aura_free(link, run, tel, report, left, game=game, project=project, asked=asked,
                                             run_id=run_id, activity=activity, stop=stop, steering=steering,
                                             play_seconds=play_seconds, model=model) or report
                alert = level_alert(getattr(game, "level_info", None))
                if alert:  # (the user must know the run played another level than the one asked for)
                    report = dict(report, alerts=[alert] + list(report.get("alerts") or []))
                tel.report = report
                learned = memory.learned(root, before, run_id)
                if learned["updated"]:
                    with contextlib.suppress(Exception):
                        link.status(SAVING_MEMORY)
            recording = tel.recording()
            missing = None if recording else tel.no_recording()
            close_game(link)  # (every harness has run, and the recording has stopped)
            if share:
                upload = (root, project if run else key, run)
    print(json.dumps(report, indent=1))
    if desk is not None:
        desk.end(report)  # (a run that ended before its session opened: the start call is told why)
    frame = scrub(result_frame(report, prompt, kind))  # (no secret leaves in what the model wrote)
    if tested:
        frame["tested"] = True  # (the plugins show it as GAME TESTED)
    # What the run added to the memory (the plugins say so at the end of the feed) and its recording (their report
    # plays it): both on this machine, never scrubbed
    if learned is not None:
        frame["memory"] = learned
    if recording:
        frame["recording"] = recording
    elif missing:
        frame["no_recording"] = missing  # (why there is none: the plugins' report says so where it would play)
    if run is not None and "usd" in (run.finished or {}):  # (what Aura billed the run, for the Settings page)
        # (charged_usd: what was charged, plan markup and discounts included; usd: the run's cost, from older Auras)
        usd = run.finished.get("charged_usd", run.finished["usd"])
        try:
            link.send({"type": "bill", "usd": usd, "billed": bool(run.finished.get("billed"))})
        except Exception:
            pass
        account.forget_bill()  # (the period's bill has changed: asked again with the next account frame)
    if sent is not None:
        sent.append({"type": "result", "result": frame})
    try:
        link.send({"type": "result", "result": frame})
    except Exception:
        pass
    AFTER_RUN[:] = [t for t in AFTER_RUN if t.is_alive()]  # (`autopilot serve` runs many)
    if kept:
        saving = threading.Thread(target=kept.save, args=(upload[0],), name="memory-save", daemon=False)
        saving.start()
        AFTER_RUN.append(saving)
    if upload:
        sending = training.upload_later(*upload)
        if sending is not None:
            AFTER_RUN.append(sending)
    if run is not None or credits_refused:
        account.send_state(link)  # (with the period's bill, and whether the team has credits, as they are now)
    return frame["exit_code"]


def send_memory_state(link, hello=None):
    """Tell the plugin what is remembered about its project (memory_state, docs/protocol.md)."""
    project = codexmod.project_of(hello or link.hello)
    try:
        link.send({"type": "memory_state", **memory.state(codexmod.project_dir(project))})
    except Exception:
        pass


def reset_memory(link, sleep=time.sleep):
    """The plugin's Reset memory (clear_memory, docs/protocol.md), between runs: its memory_cleared frame, which says
    why when the memory couldn't be reset (it then stays as it was: memory.forget moves all or nothing)."""
    going = [t for t in AFTER_RUN if t.is_alive()]
    if going:
        link.status("Resetting memory…")  # (the words the plugins show: no more while it waits)
        deadline = time.time() + RESET_WAIT_SECONDS
        for thread in going:
            thread.join(max(0.0, deadline - time.time()))
    folder = codexmod.project_dir(codexmod.project_of(link.hello))
    for attempt in range(RESET_TRIES):
        try:
            return {"type": "memory_cleared", "ok": True, "files": memory.forget(folder)}
        except memory.InUse as e:
            print(f"Memory not reset yet: {e}")
            if attempt + 1 < RESET_TRIES:
                sleep(1.0)
                continue
            return {"type": "memory_cleared", "ok": False,
                    "message": f"a file of an earlier run is in use ({e.path}), so nothing was moved. Try again in a "
                               "moment."}
        except Exception as e:  # noqa: BLE001 - told to the plugin, which waits for the answer
            print(f"Memory not reset: {type(e).__name__}: {e}")
            return {"type": "memory_cleared", "ok": False, "message": f"the agent service couldn't move the files: {e}"}


def same_run(a, b):
    """Whether two `run`s that hellos carried are the same command-line run."""
    return json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)


def answer_again(link, hello, done):
    """Send a finished run's result again to a plugin that lost it.

    A game started with a run on its command line quits on the result; if it lost it (it closed, or the connection
    dropped, before reading it) it reconnects with the same hello. Within bridge.REPEAT_SECONDS it gets that run's
    result again, never a second run. done: [(when, the run, its result frame, the connection it went to)]. Returns
    whether the result was sent again.
    """
    for when, run, frame, to in done:
        if to is link.current:
            continue  # (the result went to this connection already)
        if hello.get("run") and same_run(hello["run"], run) and time.time() - when < bridge.REPEAT_SECONDS:
            print("The plugin asked again for the run it had: its result goes again (the run isn't run again)")
            try:
                link.send({"type": "status", "text": "This run has finished already: its result again"})
                link.send(frame)
            except Exception:
                pass
            return True
    return False


# -- commands -----------------------------------------------------------------------------------------------------

def cmd_codex(args):
    eng, path = engines.find_project(args.path)
    if not eng:
        print(f"No Unreal, Unity, Godot or Roblox project at {args.path}")
        return 1
    project = args.name or eng.project_name(path)
    remember_project(project, path, eng)
    cx = codexmod.build(project, path=path, engine=eng)
    print(f"The codex is at {cx.root}")
    return 0


def cmd_play(link, args):
    asked, sent = link.hello.get("run"), []
    with run_holds(link):
        code = run_prompt(link, args.prompt, args.path, args.model, sent=sent, timeout=time_limit(asked),
                          level=getattr(args, "level", None) or (asked or {}).get("level"))
    if asked:
        # A game started with a run on its command line quits on the result: let it close first (closing at once can
        # cost it the result: a WebSocket client may drop what is buffered on close, as Godot's does). One that lost
        # it and connects again asking for its run gets the result again
        done = [(time.time(), asked, sent[0], link.current)] if sent else []
        seen = link.current
        deadline = time.time() + 10
        while not link.closed and time.time() < deadline:
            if link.current is not seen:  # (a new connection)
                seen = link.current
                answer_again(link, link.hello or {}, done)
            time.sleep(0.1)
    deadline = time.time() + AFTER_RUN_SECONDS
    for thread in AFTER_RUN:
        thread.join(max(0.0, deadline - time.time()))
    return code


def finish_after_run(say=print):
    """Wait for what the runs left going (their memory being saved, their data being sent: AFTER_RUN) before this
    process ends, up to AFTER_RUN_SECONDS (processes.exit_with: the editor that started it has closed)."""
    deadline = time.time() + AFTER_RUN_SECONDS
    told = False
    while time.time() < deadline:
        going = [t for t in AFTER_RUN if t.is_alive()]  # (again each time: one may start just after a result)
        if not going:
            return
        if not told:
            say(f"Finishing what the last run left going (its data being saved and sent), up to "
                f"{AFTER_RUN_SECONDS / 60:.0f} minutes")
            told = True
        going[0].join(min(1.0, max(0.0, deadline - time.time())))


def cmd_run(link, args):
    project = codexmod.project_of(link.hello)
    game = Game(link, codexmod.Codex.for_project(project))
    root = codexmod.project_dir(project)
    if root not in sys.path:
        sys.path.insert(0, root)  # (so the harness can `import lib`)
    harness = runtime.load_harness(args.harness)
    try:
        run = start_aura_run(link, "goal", link.hello.get("project") or "game") if aura.enabled() else None
    except aura.Refused as e:
        print(e.message)
        return 1
    with run or contextlib.nullcontext():
        if not args.no_restart:
            game.restart()
        game.pause(False)
        trace = os.path.join(os.path.dirname(os.path.abspath(args.harness)), f"replay-{time.strftime('%H%M%S')}.jsonl")
        rt = runtime.Runtime(game, harness, trace, client=runtime.jev_client(), max_seconds=args.seconds,
                             say=link.status)
        result = rt.run()
    print(runtime.summarize(trace))
    return 0 if (result.get("status") or {}).get("state") == "done" else 1


def regress_on(game, project, args, say=print):
    """Replay the project's regressions (or --regressions') on game without a model (autopilot/regress.py): 0 when
    every one passes. --json: the results there."""
    from . import regress

    root = codexmod.project_dir(project)
    base = getattr(args, "regressions", None) or regress.folder(project, root)
    regs = regress.listing(base) if os.path.isdir(base) else []
    if not regs:
        say(f"No regressions at {base}")
        summary = regress.summary([])
    else:
        say(f"Replaying {len(regs)} regression(s) from {base}, without a model")
        results = regress.replay_all(game, regs, project_root=root, frozen_lib=getattr(args, "frozen_lib", False),
                                     seconds=getattr(args, "seconds", None), say=say,
                                     retries=getattr(args, "retries", regress.RETRIES))
        summary = regress.summary(results)
        say(f"{summary['passed']}/{summary['total']} passed"
            + (f" ({len(summary['flaky'])} flaky: passed only on a retry)" if summary["flaky"] else ""))
    if getattr(args, "json", None):
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=1)
    return 0 if summary["passed"] == summary["total"] else 1


def cmd_regress(link, args):
    project = codexmod.project_of(link.hello)
    game = Game(link, codexmod.Codex.for_project(project))
    try:
        with run_holds(link):  # (the replays drive the game: another game's plugin can't take the link over)
            return regress_on(game, project, args)
    finally:
        if getattr(args, "game_process", None) is not None:
            processes.kill(args.game_process)


def launch_headless(args, url, say=print):
    """Start the project's game headless (its engine's launch_command), connecting to url, a moment after this process
    listens; the Popen is kept on args.game_process. False when the engine has no headless game (Roblox Studio): then
    nothing starts, and the editor's plugin is waited for, as with --no-launch."""
    eng, path = engines.find_project(args.path)
    if not eng:
        raise SystemExit(f"No Unreal, Unity, Godot or Roblox project at {args.path}")
    log = os.path.join(HERE, "logs", f"regress-{time.strftime('%Y%m%d-%H%M%S')}.log")
    os.makedirs(os.path.dirname(log), exist_ok=True)
    try:
        cmd = eng.launch_command(path, url, log, exe=getattr(args, "engine", None))
    except NotImplementedError as e:
        say(f"{e}. Waiting for the plugin instead: open the project in its editor, with the plugin connecting to {url}")
        return False

    def start():
        time.sleep(1.5)
        import subprocess

        args.game_process = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                             **processes.GROUP)
        processes.track(args.game_process)

    threading.Thread(target=start, daemon=True).start()
    return True


def decision_key_missing(in_editor=False):
    """Why a run with the user's own keys can't start (its decision model has no key; a run through Aura needs none),
    or None. Told to the user's own agent (`autopilot mcp`): the key goes in the file, never into its conversation."""
    if aura.enabled() or (os.environ.get("TYPESAFE_API_KEY") or "").strip():
        return None
    if in_editor:
        return "Jev needs a TypeSafe API key or an Aura sign-in. Set one up in the Autopilot window and run again."
    return ("Autopilot needs a key for its decision model, the one that plays the game (TypeSafe: "
            f"https://typesafe.ai). Add the line TYPESAFE_API_KEY=<the key> to {account._user_env_path()}, or enter it "
            "in the Autopilot tab's account setup in the editor (or choose Aura there). Don't paste the key into this "
            "conversation. Then call start_run again.")


def external_run(params):
    """The run a start call of the user's own agent asks for (autopilot/agents/external.py), as a `start` frame's
    (docs/protocol.md, Runs)."""
    # (the user's words as they asked their agent: "Use Autopilot to verify that ..." is "verify that ...")
    request = re.sub(r"(?i)^\s*(please\s+)?(use|ask|have|get)\s+autopilot\s+(to\s+)?", "",
                     str(params.get("request") or "")).strip()
    request = request[:1].upper() + request[1:]
    # (with no kind given, Jev reads the request's as the run starts)
    kind = params.get("kind") if params.get("kind") in ("verify", "goal", "explore") else "auto"
    run = {"kind": kind}
    if kind == "verify":
        run["claim"] = request
    elif kind == "goal":
        run["goal"] = request
    elif kind == "explore":
        run["brief"] = EXPLORE_WORDS.sub("", request, count=1).strip(" :,-.") or request
        if params.get("minutes"):
            run["minutes"] = params["minutes"]
    else:
        run["prompt"] = request
    if params.get("time_limit_minutes"):
        run["time_limit_minutes"] = params["time_limit_minutes"]
    elif kind == "auto" and params.get("minutes"):  # (an exploration's minutes: its time limit, should it be one)
        run["time_limit_minutes"] = params["minutes"]
    if str(params.get("level") or "").strip():
        run["level"] = str(params["level"]).strip()
    return run


def cmd_serve(link, args):
    """Run what the Autopilot tab starts, one run at a time, until stopped (Ctrl+C). Frames from the plugin are read
    while a run plays: Stop ends it, Steer passes the user's words on. args.control (an agents.external.Control): runs
    the user's own agent starts (`autopilot mcp`), one at a time with the tab's."""
    run = asked = link.hello.get("run")  # (a game started with a run on its command line)
    send_memory_state(link)
    account.send_state(link)
    sign_in = account.SignIn(link)  # (the plugin's requests to sign in to Aura: autopilot/account.py)
    done = []  # the command-line run's result, for a plugin that lost it and asks again (answer_again)
    going = None  # (thread, stop event, steering) of the run going now
    lock = threading.Lock()  # (going, as the tab's frames and the user's own agent's start calls see it)
    feed = Feed()  # the latest run's activity events, for a plugin that asks for them again (hello activity_since)
    feed.watch_rebind(link)

    def start(run, prompt, stop, steering, desk=None):
        nonlocal done
        sent = []
        try:
            with run_holds(link):
                run_prompt(link, prompt, args.path, args.model, timeout=time_limit(run), sent=sent, stop=stop.is_set,
                           steering=steering, kind=run.get("kind") if run.get("kind") in aura.KINDS else None,
                           play_minutes=play_minutes(run), trace=run.get("trace"), desk=desk, level=run.get("level"),
                           aura_model=run.get("model"),
                           disable_premium_models=run.get("disable_premium_models") is True,
                           thinking_level=run.get("thinking_level"), cli_client=run.get("cli_client"),
                           feed=feed, explore_minutes=explore_minutes(run),
                           design_brief=str(run.get("design_brief") or "").strip() or None)
        except Exception as e:
            print(f"The run failed: {type(e).__name__}: {e}")
            if desk is not None:
                desk.end(f"The run failed: {e}")
            # (in the shape of the run's kind: a goal's failure is a goal not reached, with why)
            kind = run.get("kind") if run.get("kind") in aura.KINDS else kind_by_words(prompt)
            failed = {"outcome": "INCONCLUSIVE" if kind == "verify" else "not_achieved",
                      "summary": f"The run failed: {e}", "evidence": []}
            sent.append({"type": "result", "result": scrub(result_frame(failed, prompt, kind))})
            try:
                link.send(sent[-1])
            except Exception:
                pass
        if run is asked and sent:
            done = [(time.time(), run, sent[-1], link.current)]
        send_memory_state(link)  # (what the run added)

    def begin(run, prompt, desk=None):
        """Start the run unless one is going; whether it started."""
        nonlocal going
        with lock:
            if going is not None and going[0].is_alive():
                return False
            stop, steering = threading.Event(), Steering()
            thread = threading.Thread(target=start, args=(run, prompt, stop, steering, desk), daemon=True)
            going = (thread, stop, steering)
            thread.start()
            return True

    def start_external(params, desk):
        """A start call of the user's own agent (autopilot/agents/external.py): None once its run has started, else
        why it hasn't."""
        if not str(params.get("request") or "").strip():
            return "Say what to playtest: the request is empty."
        load_user_keys()
        account.follow_preferences()
        missing = decision_key_missing()
        if missing:
            return missing
        run = external_run(params)
        if not begin(run, prompt_of(run), desk):
            return ("A run is going in the editor: stop it there (or let it finish), then call start_run again.")
        try:  # (the tab shows the run as one of its own: docs/protocol.md, run_started)
            link.send({"type": "run_started", "run": dict(run, by=desk.client or "mcp")})
        except Exception:
            pass
        return None

    control = getattr(args, "control", None)
    if control is not None:
        control.starter = start_external

    while True:
        if run and going is None:
            prompt = prompt_of(run)
            if prompt:
                begin(run, prompt)
            run = None
        with lock:
            if going is not None and not going[0].is_alive():
                going = None
        msg = link.next_message(timeout=1.0)
        if msg is None:
            continue
        kind = msg.get("type")
        if kind == "hello":  # (a plugin that connected again: a new link)
            answer_again(link, msg, done)
            send_memory_state(link, msg)
            account.send_state(link)
            feed.watch_rebind(link)
            if msg.get("activity_since") is not None:  # (the tab asks for the run's feed, from where it was)
                print(f"hello: activity since {msg.get('activity_since')}")
                try:
                    feed.replay(link, int(msg.get("activity_since") or 0))
                except (TypeError, ValueError):
                    feed.replay(link, 0)
        elif account.handle(link, msg, sign_in):
            pass
        elif kind == "start":
            if going is not None:
                link.send({"type": "status", "text": "A run is going: steer it, or stop it first"})
            else:
                run = msg.get("run") or {}
        elif kind == "stop" and going is not None:
            going[1].set()
            link.status("Stopping")
        elif kind == "steer" and going is not None:
            if going[2].add(msg.get("text")):
                link.status("Steering: " + str(msg.get("text")).strip()[:120])
        elif kind == "clear_memory":
            with lock:  # (a start call of the user's own agent waits while the memory moves)
                if going is not None and going[0].is_alive():
                    answer = {"type": "memory_cleared", "ok": False,
                              "message": "A run is going: stop it or let it finish first."}
                else:
                    answer = reset_memory(link)
            try:
                link.send(answer)
            except Exception:
                pass  # (the plugin went while it waited: the memory_state it gets when back says how it went)
            send_memory_state(link)
        elif kind == "closed":
            print("The plugin disconnected; waiting for it to come back")
            time.sleep(1.0)


HTTP_HELP = ("where plugins that can't keep a WebSocket open talk over HTTP (Roblox Studio's, when its DataModels' "
             "few sockets are taken): default the next port after --port (any free port with --port 0)")
SERVER_HELP = f"the Aura server (default: AUTOPILOT_AURA_URL, else AUTOPILOT_AURA_SERVER, else {aura.DEFAULT_URL})"


def parser():
    ap = argparse.ArgumentParser(prog="autopilot",
                                 description="Automated playtesting for Unreal, Unity, Godot and Roblox.")
    sub = ap.add_subparsers(dest="cmd", required=True)
    project_help = ("the game's project on this machine: a .uproject, a Unity project folder, a project.godot, or a "
                    "Roblox place (a Rojo project folder or a .rbxl/.rbxlx)")
    s = sub.add_parser("serve", help="run what the editor's Autopilot tab starts")
    s.add_argument("--port", type=int, default=8765)
    s.add_argument("--http-port", type=int, help=HTTP_HELP)
    s.add_argument("--project", dest="path", help=project_help)
    s.add_argument("--model", help="the language model (default: AUTOPILOT_MODEL)")
    s.add_argument("--aura", action="store_true",
                   help="sign in with Aura: run on your Aura plan instead of your own keys")
    s.add_argument("--exit-with", type=int, metavar="PID",
                   help="end once the process PID has ended (for a program that starts Autopilot and owns it)")
    p = sub.add_parser("play", help="run one prompt and exit with its result")
    p.add_argument("prompt")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--http-port", type=int, help=HTTP_HELP)
    p.add_argument("--project", dest="path", help=project_help)
    p.add_argument("--model", help="the language model (default: AUTOPILOT_MODEL)")
    p.add_argument("--level", help="the level to play: a path or a name (default: the one the prompt means, as the "
                                   "agent decides, else the one open in the editor)")
    p.add_argument("--aura", action="store_true",
                   help="sign in with Aura: run on your Aura plan instead of your own keys")
    c = sub.add_parser("codex", help="build a project's codex without the game running")
    c.add_argument("--project", dest="path", required=True, help=project_help)
    c.add_argument("--name", help="the project's name in memory/ (default: from its path)")
    r = sub.add_parser("run", help="play a saved harness again (no language model)")
    r.add_argument("harness")
    r.add_argument("--seconds", type=float, default=60)
    r.add_argument("--port", type=int, default=8765)
    r.add_argument("--no-restart", action="store_true", help="go on from where the game is")
    r.add_argument("--aura", action="store_true",
                   help="sign in with Aura: run on your Aura plan instead of your own keys")
    rg = sub.add_parser("regress", help="replay the regressions (harnesses of CONFIRMED claims) without a model: "
                                        "starts the game headless, or waits for the editor (--no-launch)")
    rg.add_argument("--project", dest="path", help=project_help)
    rg.add_argument("--regressions", help="a regressions folder (default: the project's, AUTOPILOT_REGRESSIONS or "
                                          "memory/<project>/regressions)")
    rg.add_argument("--seconds", type=float, help="each replay's longest episode (default: 1.5x the recorded one)")
    rg.add_argument("--frozen-lib", action="store_true",
                    help="use each regression's own lib.py snapshot, not the project's lib.py now")
    rg.add_argument("--json", help="write the results there")
    rg.add_argument("--retries", type=int, default=1,
                    help="replay a failed regression again this many times; one that then passes is reported flaky "
                         "(default 1; 0: no retries)")
    rg.add_argument("--port", type=int, default=8765)
    rg.add_argument("--engine", help="the game's executable (default: the engine adapter's)")
    rg.add_argument("--no-launch", action="store_true", help="don't start the game: wait for the editor's plugin")
    li = sub.add_parser("login", help="sign in to Aura on its website (for runs with Aura without the Aura app)")
    li.add_argument("--server", help=SERVER_HELP)
    lo = sub.add_parser("logout", help="sign out of Aura (the sign-in `autopilot login` kept)")
    lo.add_argument("--server", help=SERVER_HELP)
    u = sub.add_parser("uninstall", help="remove Autopilot from this computer (shared run data already sent stays)")
    u.add_argument("--keep-memory", action="store_true", help="keep what Autopilot learned about your games")
    u.add_argument("--yes", action="store_true", help="remove without asking")
    u.add_argument("--log", help=argparse.SUPPRESS)  # (where a plugin keeps this one's output: the command's removal
    # on Windows, after this has ended, says there how it went)
    mcp_parser = sub.add_parser("mcp", help="Autopilot's tools for your own Claude Code or Codex (an MCP server on "
                                            "stdio): they work on the game open in the editor, on your own plan")
    mcp_parser.add_argument("--attach", metavar="FILE", help=argparse.SUPPRESS)
    co = sub.add_parser("connect", help="set up your own Claude Code or Codex for Autopilot: its MCP server, skill and "
                                        "frame describer (autopilot/connect.py)")
    co.add_argument("client", choices=("claude", "codex"))
    co.add_argument("--remove", action="store_true", help="take out what connect set up")
    from . import evolve as evolvemod  # (autopilot/evolve.py: its own options)

    evolvemod.add_parser(sub)
    return ap



def main(argv=None):
    args = parser().parse_args(argv)
    if args.cmd == "uninstall":  # (before contain(): on Windows, what removes the command must outlive this process)
        from . import uninstall

        return uninstall.main(args)
    if args.cmd == "mcp":  # (started by the user's Claude Code or Codex: stdout is the protocol's, the service runs)
        from . import mcp

        return mcp.main(args.attach)
    processes.contain()
    if getattr(args, "exit_with", None):
        processes.exit_with(args.exit_with, finish=finish_after_run)
    processes.keep_responsive()
    from dotenv import load_dotenv

    load_dotenv(os.path.join(HERE, ".env"))
    load_user_keys()
    if args.cmd == "evolve":  # (no game link of its own, and not a run: the bench starts each prompt's brain and game)
        from . import evolve

        return evolve.main(args)
    # An Autopilot the Aura app starts (AUTOPILOT_AURA_URL set for it) says where it listens under Aura's own name,
    # which only plugins set to Use with Aura read; one started with --aura is the user's own Autopilot, found as any
    # other
    aura.mark_app(aura.enabled() and not getattr(args, "aura", False))
    announce_as = aura.BRAIN_FILE if aura.from_app() else bridge.BRAIN_FILE
    # --aura: runs go through Aura whatever the first-run setup says (Aura's own server unless AUTOPILOT_AURA_URL names
    # another); else the setup's choice decides, before every run (autopilot/account.py)
    if getattr(args, "aura", False):
        aura.use_default()
    account.fix_at_start()
    account.follow_preferences()
    if args.cmd in ("login", "logout"):
        server = (args.server or "").rstrip("/") or account.server()
        return aura_login.main_login(server) if args.cmd == "login" else aura_login.main_logout(server)
    if args.cmd == "codex":
        return cmd_codex(args)
    if args.cmd == "connect":
        from . import connect

        return (connect.disconnect if args.remove else connect.connect)(args.client)

    # The agent service takes runs from the user's own Claude Code or Codex too (`autopilot mcp` finds it by brain.json)
    extra = {}
    if args.cmd == "serve":
        from .agents.external import Control

        args.control = Control()
        extra = {"control": args.control.url}
    if args.cmd == "regress" and args.path and not args.no_launch:
        launch_headless(args, f"ws://127.0.0.1:{args.port}/v1/bridge")
    print(f"Waiting for the plugin on ws://127.0.0.1:{args.port}/v1/bridge ...")
    http_port = getattr(args, "http_port", None)
    if http_port is None:
        http_port = args.port + 1 if args.port else 0
    link = bridge.serve_local(port=args.port, role="service" if args.cmd == "serve" else "cli", announce_as=announce_as,
                              extra=extra, http_port=http_port)
    print(f"Connected: {link.hello.get('project')} ({link.hello.get('host')}, {engines.for_hello(link.hello).title})")
    link = LiveLink(link)
    return {"play": cmd_play, "run": cmd_run, "serve": cmd_serve, "regress": cmd_regress}[args.cmd](link, args)


if __name__ == "__main__":
    sys.exit(main())
