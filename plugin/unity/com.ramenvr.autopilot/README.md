# Autopilot for Unity (com.ramenvr.autopilot)

Autopilot is AI playtesting for Unity 2022.3 LTS and Unity 6 games. You describe what to test in one box, and an AI
agent plays your game to check it; the agent service reads which of four types of prompt your words are:

- **Exploration**, e.g. "Explore the crafting system in Lvl_Main.": it plays and reports the bugs it finds (EXPLORED).
- **Testing**, e.g. "Go through the currently open level and tell me everything that looks wrong.": a test in your own
  words that asks for the problems to be reported. It plays as an exploration and ends GAME TESTED.
- **Verification**, e.g. "Verify that the tree shakes when you hit it with an axe.", or a question ("Can the player
  swim?"). The answer is CONFIRMED, DENIED or INCONCLUSIVE, with the evidence.
- **Goal**, e.g. "Kill the boss while keeping your health above 20% of its maximum value.": the agent tries to do it
  and says whether it managed.

For every prompt the agent enters play mode by itself, reads your project's code, tries things in the game and plays;
when it is done, play mode ends by itself and the window shows its report.

Autopilot learns your game as it plays: after every run the agent service saves what it learned, so later runs are
faster and play better.

This package is the game-side half. It runs inside the Unity editor (or a Development player build), connects out over
a WebSocket to the agent (the Autopilot brain from this repository), and lets the brain read the scene, press keys and
call the game's code. The brain does the thinking; this package only looks and acts.

## Requirements

- Unity 2022.3 LTS or Unity 6.
- The agent service (the brain, `autopilot serve`) on your machine. The window starts it by itself when you connect (see
  [The agent service](#the-agent-service)): a release download carries its installer in the package's `AgentService~/`
  folder, and the first-run setup installs it once per computer. From a repository checkout, set **Agent service
  command** (e.g. `C:\path\to\autopilot\.venv\Scripts\python.exe -m autopilot`) or run `python -m autopilot serve` yourself.
- Optional, used when the project has them: the Input System package (to press keys and drive input actions), uGUI,
  TextMeshPro and UI Toolkit (to read and click UI). Nothing else is needed.

## Install

### Install the bundled service and Unity package

The Unity package includes the matching Python service and its installer. [Download com.ramenvr.autopilot-1.0.2-cli.tgz](https://github.com/zamgune/autopilot/releases/download/v1.0.2-cli/com.ramenvr.autopilot-1.0.2-cli.tgz)

Keep the `.tgz` in a stable location because your project references this file. Open **Window > Package Manager** (under **Window > Package Management** in
some Unity 6 releases). Choose **+ > Add package from tarball...** in 2022.3, or **+ > Install package from tarball...**
in Unity 6. Select the `.tgz`, then open **Window > Autopilot** and follow the [first-run setup](#first-run-setup).

To build the same distribution from a clean checkout of this fork with Python 3.12+ and `uv` installed, run the command
below at the repository root. `dist/Autopilot-Unity.zip` contains the `.tgz`.

```sh
python tools/package_release.py dist --no-unreal --unversioned
```

### Install from Git or a checkout

In Package Manager, use **+** and the Git URL option. Enter
`https://github.com/zamgune/autopilot.git?path=/plugin/unity/com.ramenvr.autopilot#main`.
For a local checkout, use the disk option and select `plugin/unity/com.ramenvr.autopilot/package.json`.

A source install needs the service from the same revision. For a Git URL install, copy the package's `hash` from
`Packages/packages-lock.json`, replace `COMMIT` below with that value, and keep the command running while testing.

```sh
uvx --python 3.12 --from "autopilot[claude,codex] @ https://github.com/zamgune/autopilot/archive/COMMIT.zip" autopilot serve
```

For a local checkout, run `uv run --python 3.12 --extra claude --extra codex autopilot serve` at its root instead.
Open **Window > Autopilot** and connect to the local service.

## Use it in the editor

1. Open **Window > Autopilot**. The first time, a short setup comes first (see [First-run setup](#first-run-setup)).
2. Once it is set up the editor connects by itself: it starts the agent service on this computer when none runs (or uses
   the one running), then connects to it. There is no Connect button: while it isn't connected the window shows the
   **Agent service** card (*Not connected*) with what is going on (see [Connecting](#connecting)).
3. Type what to test in the box (*Describe what to test: a claim to verify, a goal to reach, or what to explore*) and
   press **Run**, at the box's lower right. **Time limit** shows before it, always: the whole time the run may take, in
   minutes, and an exploration's minutes of play (30 unless you change it, 1 to 600; kept per project in
   `UserSettings/AutopilotPlaytest.json`, so the box starts with the one you set last). Nothing guesses the kind as
   you type: **Run** sends your words as typed (a `start` whose run is
   `{"kind": "auto", "prompt": ..., "time_limit_minutes": ...}`), the agent service's decision
   model reads which kind of run they ask for, and says so (`run_kind`) before the run plays; until then the run's id
   line reads *RUN*, then *VERIFY*, *GOAL* or *EXPLORE*.

   Play mode plays the scene open in the editor, unless the prompt means another ("Explore the Harbor scene", "in
   level 2", `Assets/Scenes/Harbor.unity`): the agent picks it from the project's scenes before it starts playing. It
   plays as play mode's start scene: the scenes open in the editor and their unsaved changes stay as they are, and your
   own Play plays them again afterwards.

The agent enters play mode by itself when it needs the game. While a run plays you can:

- watch it on the **Live** tab (the current stage on a track, which follows the agent service's activity frames, or its
  status lines for an older one; what the agent is doing now: while the run gets the game ready, *Indexing files
  (12/40)* as the project index is written and *Waiting for play mode* while play mode is entered, as Unreal's tab says
  them; its **Harness attempts**: a
  filled square each one finished, an outlined one the one going, on one row (at most 24; when they don't all fit, the latest), and the feed's last 10 lines, one line each, no folds);
- read the run as a transcript on the **Feed** tab, grouped by stage: a heading per stage (Launch, Analyze, Probe, Build
  harness, Play, Results, Report) with its count of lines, and under it what the agent did and said, one line per event
  of the agent service's activity feed (`activity`): its time (m:ss), a small mark for its kind (? the
  agent's words, R read, S searched, W wrote, P tried in the game, E episode, L looked, ! report or alert, > what you
  steered, * restart, M the game's memory refined), its words and, for a call that came back (a probe, an episode, a
  look), the result's words on the same line in the muted colour. Under a line, a fold for each detail ("+ the code",
  "the output", "what was written", "the edit and its evidence",
  "the summary", "the whole message"…) opens a read-only mono box with it, at most 320 px tall, scrolling past that,
  its text selectable. The status lines, findings, what you steered and the result are among them, each under its
  stage; an agent service too old to send activity frames gives the status lines alone, as before. A run's feed is kept
  with it, so a run reopened from the History shows it whole; a window that connects again while a run goes asks for
  the events it missed (`activity_since`), and an event sent twice is shown once;
- see each of the policy's decisions on the **Policy log** tab (only when the brain runs on this machine, and **Show
  policy log** is on: the Settings page's **Policy log** switch, or Preferences > Autopilot): filter by
  harness and decision (SWITCH, START, KEEP, HELD, DROPPED), *Find an action…* by name, pick a step (*PAUSED ON H2 #18*,
  **Follow live** to follow again; ◀ ▶ beside **Copy step JSON** step through the rows), the footer summing the harness
  picked (*H2: …*) or, with All and nothing picked, *All harnesses: …*;
- **Steer** it while the game is playing (the Rollout stage): type a hint in the box under the run (*Send a steering prompt to Autopilot*,
  e.g. "go round the rocks to the left first") and press **Steer** or Enter; you can type at any stage, but **Steer** sends only once the game is playing (its tooltip says so before), keeping what you typed;
- **Stop** it early (it reads *Stopping…* and the steer box locks until the run ends; a project index being written
  stops too); the agent still reports what it found.

To start a test with your signed-in terminal account, choose **Claude Code CLI** or **Codex CLI** in **New run > Run
with** and press **Run**. The agent service starts the chosen CLI with a connection to this test. Sign in once using
`claude auth login` or `codex login` on the service's machine. For Jev, choose Aura or enter your TypeSafe key during
setup. **Configured agent** uses your usual Autopilot agent. Unity remembers the choice per project, and **Run again**
uses the selection saved with the original test.

A run started from your own Claude Code or Codex (`autopilot connect claude|codex`, see [Usage](#usage)) shows here as
one started from the box: the agent service says so with a `run_started` message, and the
window shows it live, with **Stop** and **Steer** as above, its result going into the History. Under its request a
line says where it came from (*Started from Claude Code*, *Started from Codex*, else *Started from your coding agent*).
One whose kind the agent service reads (`"kind": "auto"`) shows its `prompt` as its request, its kind blank until the
`run_kind` comes, as a run from the box does.
The first-run setup, when it is open, stays open; a run already going is never replaced.

Before its result the agent saves what it learned (*Saving what it learned to memory*, part of the Report stage); when
the result says the run changed what is remembered (`memory.updated`), the feed ends with a **Memory** line (*Memory
updated*).

When the run is over the **Result** tab shows the run's id line (*RUN 20261016-235900 // VERIFY // …*) with **Run
again** (a circular arrow, filled with the accent; it starts the run as it was, its own time limit too), **Copy report** (a clipboard, outlined) and **Run folder** (a folder,
outlined) as icons at its right, on one row at any width (their names are their tooltips), the prompt close under it, then the verdict over its reason, then the
run's recording when the agent
service made one (`recording` in the result): a player as wide as the page (230 px tall, 200 in a narrow window, the video
letterboxed in it), with a row under it: a play / pause button (its glyph), a scrub line, the time and the length, and
**Show in File Explorer** (the same folder icon as Run folder; *Show in Finder*, *Show in folder*) that shows the MP4 in File Explorer,
Finder or your file manager (selected, on Windows and macOS). Nothing plays by itself: the VideoPlayer (on a hidden object,
into a RenderTexture, in edit mode too) is made only when you press Play or scrub, and let go when the page changes. The
recording is copied into the run's folder (`recording.mp4`, off the main thread), so it outlives a Reset memory; until
the copy is done, or when it fails, it plays from where the agent service wrote it. A project without the Video module
(`com.unity.modules.video`) plays the recording's frames (the JPEGs the agent service keeps beside the MP4, the
result's `recording.frames` at its `frames_fps`) in the same box as a flipbook, as Godot's dock does, with the same
play / pause, scrub line and time (they are copied into the run's folder, `recording/`, off the main thread; without
the Image Conversion module, or the frames, Play opens the MP4 in your video player). A run with no recording says why in its place, in one
muted line: *No recording:* and the result's `no_recording` (for example *the game can't be recorded here: it runs
without rendering*), or, when the result has neither `recording` nor `memory` (an agent service older than recording
every run ran it), *No recording.* The line is decided when the result comes and kept with the run (`no_recording`), so
the History shows it too.
Then the evidence and the findings (no findings section at all when there are none). **Show in level** frames the
scene view on a finding, **Not a bug** tells the agent it was wrong, and **Run folder** opens the saved report
(`Logs/Playtest/<time>/result.json` in your project). Earlier runs stay in the **History** (a side rail when the
window is wide, a dropdown under the **History** button, which says how many runs it holds, when it is narrow), each
with its time, how long it took and its harness attempts (*Oct 19 13:25 · 03:26 · 2 attempts*). The History (the last
50 runs) is kept in `UserSettings/AutopilotRuns/` (one file per run; per user, like the window's settings), so closing
the window or the editor loses nothing; a run's policy log is kept only until the editor closes. A run still going when
the editor closed shows *NO RESULT* (*The editor closed before the result came.*).

Every page but the setup's and the removal's ends with a status line: one short line (what a click did, or why it
couldn't: *Copied the report*, *Not connected: connect first*, *Told the agent: … is not a bug*, *Showing …*, *Couldn’t
save the usage to …*), its whole text in its tooltip, gone after 8 s or at the next message. The steer box is on the
**Feed** tab too while the run plays, the same box as the Live page's.

Runs always play in real time. Every page of the window reflows with its size: rows wrap or stack when it is
narrow (down to about 300 px; the header's Settings gear and **New run** go on a line of their own, and the
Live page's stage names wrap onto more lines rather than being cut), a page's content is at most so wide and centred beyond it (the composer's column 620 px, the Settings page
640, the setup 600, the agent service card 540; a run's Live and Result pages use the window's width), and a page
scrolls vertically when the window is short. The composer's foot keeps *● CONNECTED*, the address and **Disconnect** on
one row at the right (the address shortens when narrow).

The project index, a text description of your scenes, prefabs, ScriptableObjects and input actions in
`Library/AutopilotCodex/` that the agent reads to understand the game, is written by itself before play mode starts
whenever it is missing or older than your assets (there is no button for it; `CodexExport.Run` exports it from the
command line, see [The codex export](#the-codex-export)). It is written a file at a time across the editor's updates,
so the editor stays responsive: the Live page counts the files (*Indexing files (12/40)*), the `session` status says
`loading` meanwhile, and **Stop** ends it (it writes no `summary.txt` then, so it is written again next time; play
mode is entered all the same, and the run stops from there).

While the agent service plays the game in the editor (a run, or play mode it entered), a script error doesn't pause
it: the Console's **Error Pause** is turned off and put back as it was once no run has gone on for 10 s (what it was is
kept in EditorPrefs for the project, so an editor that crashed or closed mid-run puts it back the next time it opens,
as does removing the package), as Godot's plugin tells its debugger to continue. The run reports the error itself (the
`log_error` detector).

The main toolbar has an Autopilot button that opens the window. Unity 6.3 and later use the main toolbar API. On
earlier editor versions the package looks for the toolbar's UI Toolkit tree. **Window > Autopilot** also opens the window.

When a bundled font can't be read the window says so on its status line as it opens (*A bundled font couldn’t be read:
using the editor’s font*), as Unreal's tab and Godot's dock do.

Entering play mode reloads scripts (unless Enter Play Mode Settings turn that off). The connection then closes with
code 1012 and comes straight back, resuming the same run, so a run carries on across play mode changes (a recording
the agent asked for too: it goes on once the game plays again).

### Usage

**Settings** is the gear beside **New run** above the **History** (lit while the page shows; also in the window tab's
menu). Its **Rerun setup**, on the Account card, opens the first-run setup from its first step (Account: sign in with
Aura, the Aura app, or BYOK); while a run goes it is greyed and opens nothing (its tooltip: *Finish or stop the run
first*), as is the window tab's menu item.

The Settings page starts with the **Account** card: how
Autopilot runs on this computer, from the agent service's `account` frame and
`preferences.json`. **Aura**: the email, what Autopilot billed the account this period when the agent service says
(*Billing period (Oct 1 – Oct 31): $12.34 USD*; nothing when Aura couldn't say: the account frame's `bill` is null),
under it Aura's words in red when the team is out of credits (the frame's `out_of_credits`, with **Add credits** first in
the card's buttons when Aura names the page; the Aura app's card shows them too, and the composer shows them in a red
callout over the prompt box, Run staying as it is: Aura checks again when a run starts), the agent service's line (*Agent service: running* and its address, *starts when Autopilot
connects*, *not installed* with **Install**, a stop with **View logs**), **Rerun setup** (the setup from its first step; also with the Aura app
and BYOK, not while a run goes) and **Sign out** (a red text button, confirmed in place: the sign-in is the
computer's, so every editor here signs out). **Aura app**: the Aura app's agent service (you sign out in the app).
**BYOK**: the saved keys' last four characters as the agent service reports them, with the Coding Agent and LLM
provider its runs use (the frame's `coding_agent` and `llm_provider`; *OpenCode* and *Anthropic* when an older agent
service doesn't say; a provider the window doesn't know is shown as its id) (*Saved on this computer: Decision
provider ••••a91f · Coding Agent: OpenCode (Anthropic) ••••3c2e*, *… · Coding Agent: Claude Code (Anthropic) ••••3c2e*);
none saved, just the tag and the buttons. **Not set up**:
a primary **Rerun setup**. An Aura sign-in that ended shows **Signed out** with **Sign in again** (the browser sign-in, in
place) and **Rerun setup**. The window asks the agent service for the account again (`{"type": "account"}`) on every
connection, when the Settings page opens, after the setup's **Finish** or **Rerun setup**, after Use with
Aura changes and after signing out, so the corner never shows an account from before a choice. Below it
**This project · N runs** lists the project's runs with usage (Time, Prompt, Billed; rows to read, not to pick). For runs through Aura it shows what Autopilot was billed (the agent service's `bill` message,
sent once a run through Aura is finished, before its result): *$0.59* in the run's Billed column (two decimals;
*< $0.01* below a cent; *—* for a run Aura didn't bill), and *Billed by Aura $X.XX* (the sum of the runs' bills) beside
the heading. The tokens each model used are kept with the run (not shown on the page). A bill that says
`"billed": false` (Aura bills it later, or didn't) is kept but not shown; BYOK runs have none. A run's usage and bill
are kept with it in the History and in `UserSettings/AutopilotUsage.json` (per user and project, across editor
restarts; entries from before bills load as before). Then **Share runs to improve Autopilot**, where sharing is turned on or off later with its switch, *Data sharing on*
or *Data sharing off* beside it (*Enabling this sends
recordings, screenshots, and agent traces and memories to the developers of Autopilot. For runs from your own Claude
Code or Codex, the agent traces are its conversation during the run.*, as in the setup; a change applies from the next
connection). Then **Research agent**, where the research agent (a model that reads the game's code while harnesses
play) is turned on or off with the same switch, *Research agent on* or *Research agent off* beside it (*Enabling the
research agent allows Autopilot to achieve more difficult goals with fewer attempts, but at a higher cost.*). Until
you flip it, it shows what runs do by default: until you choose, runs through the Aura app (Use with Aura) go without
it; runs on this plugin's own agent service have it, signed in to Aura or on your own keys. Once flipped, your choice
is saved and wins either way; a change applies from the next connection). Then **Design brief**: how the game is meant to work (the design, the rules players are told, e.g. *the
player has three lives; each coin is worth 10 points*), a box saved as you type, sent with every run (the composer's
`start` as `design_brief`, an Explore run's as `brief`, as Unreal's Design Brief): explorations report behavior that
differs from it as a bug. Then **Policy log**, a switch (*Policy log shown* / *Policy log hidden*): whether the hello
asks the agent service for every policy step (`inspect`) for the Policy log tab (on by default; a change connects
again when no run is going). Then **Autopilot in Claude Code & Codex**: how to run Autopilot through your own Claude Code or Codex, on
your Claude or ChatGPT subscription: the commands that set each up, run once in a terminal (`autopilot connect claude`,
`autopilot connect codex`, each with **Copy**). Each of these five cards is its title, a line on what it is for, then its control. Last,
**Uninstall Autopilot** (its **Uninstall** button), which removes Autopilot from this computer (see [Removing
Autopilot](#removing-autopilot)).

### First-run setup

Until it is done the window shows a short setup in place of its content (**Rerun setup** on the Settings page or in
the window tab's menu opens it again). A stepper on top shows where you are (`1 ACCOUNT`, `2 AGENT SERVICE`,
`3 PRIVACY SETTINGS`, and `4 FIRST RUN` unless the agent service has said it remembers this game already; a step done
shows its choice: `1 ACCOUNT · AURA`, `· AURA APP` or `· BYOK`). Every step has the same column, whatever it holds:
the window's width up to 600 px, centred beyond it (the scroll bar's room is kept on every step; the bar shows only on
a step that scrolls).

In short: choose **Create Account** (or **Sign in**) with Aura, the recommended way; the agent service is set up, then
you sign up or sign in in your browser; *Agent service ready*, **Continue**; choose whether to share your runs; then,
optionally, a first exploration run (10 minutes unless changed), which is safe to skip. Then type your first prompt
(see [Use it in the editor](#use-it-in-the-editor)). In detail:

1. **Account.** *Sign in with Aura* (*Autopilot is maintained by the developers behind Aura, the multiagent AI solution to Unreal and Unity game development. You can use your Aura subscription to run Autopilot. New users get free Aura credits.*) is billed to your Aura plan:
   - **Create Account** (the default), the Aura app installed or not, opens Aura's account creation page in your
     browser (on the Aura server the agent service signs in to: `AUTOPILOT_AURA_SERVER` in autopilot.env, else the
     package's, else https://app.tryaura.dev); the card then says *Finish creating your Aura account* and *Once you
     have created your account, sign in and start using Autopilot*, with **Sign in**.
   - **Sign in** signs in through your browser. The window never sees Aura's tokens: it asks the
     agent service (`aura_sign_in`), which opens Aura's sign-in page and keeps the sign-in
     for the computer. When the agent service isn't running yet the card (*Getting ready*) first installs it
     (*Setting up the agent service…*) and starts it (*Starting the agent service…*). Then *Finish signing in in your browser…* with the
     page's link (**Copy link**, **Open again**, **Cancel**); signed in, *Signed in as* your email with **Continue**
     and **Use another account**; a problem says what happened in one line with **Try again**.
   - *Bring your own keys (BYOK)* (folded, **Show** opens it and **Hide** folds it again; dimmed after signing in;
     hidden while a sign-in gets ready or waits, back with **Cancel**): **Decision provider API key**, under it **Coding Agent** (a dropdown: *Claude
     Code*, *Codex* or *OpenCode*), and under that **LLM provider API key**, each full width, the LLM key with its
     provider in a dropdown before it on its row. Claude Code shows *Anthropic* there and Codex *OpenAI*, locked;
     OpenCode lets you pick *Anthropic*, *OpenAI*, *OpenCode Zen*, *OpenRouter*, *DeepSeek* or *Gemini*. Switching the
     agent sets the provider it needs; back on OpenCode, the provider last picked for it comes back. The dropdowns start
     on what the agent service's `account` frame says (`coding_agent`, and `llm_provider` for OpenCode), else what
     `autopilot.env` names (`AUTOPILOT_AGENT`, `AUTOPILOT_OPENCODE_PROVIDER`), else OpenCode with Anthropic (an agent or provider
     the window doesn't know: OpenCode, Anthropic). **Use my keys** merges them into `Autopilot/autopilot.env` in your
     user settings folder, which the agent service reads before each run: `TYPESAFE_API_KEY`, always `AUTOPILOT_AGENT`
     (`claude`, `codex` or `opencode`), `AUTOPILOT_OPENCODE_PROVIDER` (`anthropic`, `openai`, `opencode`, `openrouter`,
     `deepseek` or `google`) only with OpenCode (with Claude Code or Codex that line is left as it is), and the LLM key
     under the shown provider's name (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENCODE_API_KEY`, `OPENROUTER_API_KEY`,
     `DEEPSEEK_API_KEY` or `GEMINI_API_KEY`). A key left empty (it is set where the agent service runs) keeps its
     line, and other providers' key lines are kept. Then step 2.
2. **Agent service** (*Set up the agent service*). Ready at once when it runs (*Agent service ready*, its address,
   **Continue**). Otherwise it is started, or, when it isn't on this computer (*Not installed on this computer*, *One-time
   installation takes approximately 1 minute.*), **Install** runs the installer the package carries (*Installing the agent service…*), then starts it; **I’ll do it later** goes on. A problem shows its message with **View logs** and **Try again**. A repository checkout has no
   installer: *No agent service found*, with the **Agent service command** field and **Start**, and *Or run
   `autopilot serve`* with **Copy**.
3. **Privacy settings.** **Share my runs to improve Autopilot** (a switch; *Enabling this sends recordings, screenshots, and
   agent traces and memories to the developers of Autopilot. For runs from your own Claude Code or Codex, the agent
   traces are its conversation during the run.*; on by default; the code export itself is never sent);
   a recap of steps 1 and 2 (e.g. *Account: Aura · you@studio.com*, or *BYOK*; *Agent service: Ready*); **Finish** when the agent service has
   said it remembers this game (its `memory_state`: earlier runs, notes or code of its own), else **Next**. Both save
   and connect as below.
4. **First run** (*Let Autopilot learn your game*), only when the agent service remembers nothing about this game. It
   waits for the connection and the `memory_state` (*Connecting to the agent service…*, *Checking what Autopilot
   remembers about this game…*, **Skip**); when the memory turns out to hold something, the setup closes by itself.
   With it empty, an Explore run is offered (tag *Recommended*: *You’ve never used Autopilot with this game before. To
   help Autopilot understand your game, use this first exploration run and watch it improve. Autopilot still works if
   you skip this step.*) with its **Minutes** (Explore's own setting, 10 unless changed): **Explore and finish** starts
   it (as the composer would for *Explore*) and shows it live; **Skip and finish** closes the setup.

**Finish** (or **Next**) writes `Autopilot/preferences.json` in your user settings folder (`%LOCALAPPDATA%` on Windows,
`~/Library/Application Support` on macOS, `$XDG_CONFIG_HOME` or `~/.config` elsewhere; the folder `brain.json` is in):
`{"version": 1, "onboarded": true, "share_data": true|false, "access": "aura"|"own_keys"}` (merged with what is
there), shared by the Unreal, Unity and Godot plugins, then connects. `"research_agent": true|false` is absent until
you choose on the Settings page's Research agent switch (merged the same way; a value that isn't a boolean counts as
not chosen); until you choose, runs through the Aura app (Use with Aura) go without it; runs on this plugin's own
agent service have it, signed in to Aura or on your own keys. Every connection's hello
says `share` from it, and `research` only once you have chosen (left out, the agent service decides); a change applies from the next connection (the window connects again by itself when no run is going). Until the setup is
done the editor doesn't connect by itself; from then on it always does (see [Connecting](#connecting)).

Set up, but the agent service is gone from this computer (`uv tool uninstall autopilot` by hand, say): the first time
the window opens in an editor session, when it starts the agent service itself (Use with Aura off, the URL left at
the default), none is running and the `autopilot` command isn't
found, it opens the setup at step 2 instead of the connect screen, so **Install** (or, in a repository checkout, what
to do) shows; **Finish** goes on as usual. Never in batch mode or a command-line run.

### Connecting

Once the first-run setup is done the editor connects by itself, from its update (`EditorHost.AutoConnect`), whenever
it isn't connected or trying: when the editor opens the project, and again after the agent service went away. Not after
**Disconnect** (or **Stop** while it is trying): then it stays disconnected for this editor session, and the **Agent service** card
says *Not connected* with **Reconnect** (the notice over a run says *Not connected to the agent service* with
**Reconnect**). Never in batch mode or a command-line run (those connect as their command line says), nor once Autopilot
is removed from this computer, nor after the agent service refused the connection (**Try again**).

While it isn't connected the card says what is going on: *Connecting to the agent service…* (**Stop**), *Starting the
agent service…* or *Installing the agent service…* (**Stop**), *The agent service isn’t installed on this
computer.* (**Install**), *No agent service found.* (a repository checkout: **Try again**), *Can’t reach the agent
service, retrying* with its address (**Try now**), a problem with **Try again** and **View logs**, the agent service's
own words when it refused the connection, or, using the Aura app, *Aura isn’t running on this machine.* (**Start Aura**,
**Try now**; *Couldn’t start Aura* on the status line when it won't start), or *Aura isn’t installed on this machine.*
(**Get Aura**: the download page). Disconnected by hand, **Back to the last run** returns to the run shown before.
Under its title, *Start the agent service with `autopilot serve`* (with the Aura app: *Start the Aura
app: it runs the agent service.*). **Advanced** (folded) holds the three settings below: Agent service URL, Access
token, Agent service command.

### Driven by another editor plugin (Aura)

Another editor plugin can carry the window's connection to the brain in place of its WebSocket: Aura's, so that the
runs Aura's chat asks for play in this editor through the Aura app's agent service. It opens a link with
`Autopilot.Playtest.Editor.HostLink` (`Editor/HostLink.cs`), which it finds by name and calls through reflection, as it
can't reference this package: `Open(by, send, close)` gives it every frame the window sends (JSON text, on the main
thread; a hello begins a new connection to the brain), it hands over the brain's frames with `Receive(json)`, says its
connection ended with `Closed(reason)` (a new hello follows after a moment) and stops with `Close()`, which first
handles the frames handed over before it (a run's result passed on just before: its report is written). The frames are the
protocol's, unchanged. While it carries the connection the editor doesn't connect by itself or start an agent service,
the first-run setup isn't shown, and the runs show in the window as its own. A script reload closes the link with 1012;
once both plugins are back the other one opens it again and the session is resumed over it (the card says *Waiting for
Aura…* meanwhile; **Stop** goes back to the editor's own connection). The members take and return .NET's own types
only and don't change; `HostLink.Version` counts what is added.

### The agent service

With Use with Aura off and the URL left at the default, connecting (by itself, **Reconnect**, the first-run setup)
makes sure the agent service runs (`Editor/AgentService.cs`):

A release download *carries its own agent service*: its `AgentService~/` folder has the installer and an
`agent-service.json` naming the commit (and the wheel's sha256) it was built from. Such a package always runs the
agent service it carries; one without it (a repository checkout) does as described after "Otherwise" below.

- One announced in `Autopilot/brain.json` whose process is alive is used: several editors share one, and a second is
  never started while one runs. When the package carries its own, it must be that one: brain.json's `"commit"` must
  equal `AgentService~/agent-service.json`'s. One from another commit, or one that doesn't say (older than saying it),
  is stopped and replaced, unless the **Agent service command** setting is set or this editor is connected to it: the
  window stops the process brain.json names, only when it still runs, is an agent service's (`autopilot` or the
  Python it runs under: `python`, `pythonw`, `python3`, `.exe` or not) and is the process that wrote brain.json: it
  started no later than brain.json's `"started"` + 5 s (a process id reused after the agent service ended belongs to a
  process started after that). Anything else (another program, a process started later, a brain.json without
  `"started"`, a start time that can't be read) is left alone and used, with a line in the Console saying why. It waits for it to end (up to 5 s), removes brain.json when it still names it, logs
  *[Autopilot] An older agent service was running (pid N): replacing it with this plugin's*, and goes on as if none ran.
  This happens at most once per editor session: when the one replaced comes back, or stopping it fails, it is used.
  Until it is replaced (the next connect, or the first-run setup) the window doesn't show it as running.
- Else the `autopilot` command: the **Agent service command** setting (a whole command line, e.g. a repository's
  `.venv` Python with `-m autopilot`) when set, never updated or replaced. Else, when the package carries its own, only
  where the installer puts it (`%USERPROFILE%\.local\bin\autopilot.exe`, `~/.local/bin/autopilot`); an `autopilot`
  on `PATH` is not used (when it is the only one there, the package's is installed first, once per editor session).
  Otherwise `autopilot` on `PATH`, else where the installer puts it. It is started as
  `<autopilot> serve --port 0 --exit-with <this editor's process id>`, with no window, its output appended to
  `Autopilot/logs/agent-service-<yyyyMMdd-HHmmss>.log` (the 5 newest are kept), and the window waits (up to 30 s) for
  `brain.json` to name a process it started, then connects to it. It ends with the editor that started it; another
  editor still connected starts a new one on its next connect.
- The output goes to the log through the shell (`cmd.exe /d /s /c "... >> log 2>&1"` on Windows, an `exec ... >> log`
  sh script elsewhere), not through pipes the editor reads: a script reload (entering play mode, a recompile) never
  stalls or breaks it, and what is going is kept across reloads.
- **Install** runs `powershell -NoProfile -ExecutionPolicy Bypass -File <package>/AgentService~/install.ps1`
  (Windows) or `sh <package>/AgentService~/install.sh`, with no window, its output in
  `Autopilot/logs/install-<time>.log`; once it ends, the command is found again and started. `AgentService~/` (Unity
  skips folders whose names end in `~`) is put in the package by `tools/package_release.py`; a repository checkout has
  none.
- Updating: when the command found is the installer's own and the installer last installed another agent service than
  the package carries (`Autopilot/agent-service.json`, kept by the installer, against `AgentService~/agent-service.json`:
  their `sha256`), the package's is installed first, once per editor session; if that fails, the one installed starts
  instead. An **Agent service command** setting is never replaced, nor (in a package that doesn't carry its own) an
  `autopilot` on `PATH`.
- A problem (*The agent service stopped (see <log>)*, *Couldn’t install the agent service (see <log>)*) shows on the
  connect screen with **View logs** and **Try again**.

### Settings

The connect screen's Advanced, the composer and the Settings page remember their values per user and per project in
`UserSettings/AutopilotPlaytest.json` (Unity's per-user folder, normally not committed). The same settings are on
Unity's own **Preferences > Autopilot** page (Edit > Preferences; Unity > Settings on macOS), as Unreal's Editor
Preferences > Plugins > Autopilot: Connection (Agent Service URL, Access Token, Use with Aura), Agent Service (Agent
service command), Runs (Default Explore Minutes, Design Brief) and Policy Log (Show Policy Log), each with Unreal's
words as its tooltip; a change there is the window's too (URL, token and Use with Aura from the next connection):

| setting | meaning |
|---|---|
| Agent service URL (`url`) | where the brain listens. Left at the default `ws://127.0.0.1:8765/v1/bridge`, the brain running on this machine, on whatever port it uses (`python -m autopilot serve` says where it listens in `Autopilot/brain.json` in your user settings folder); any `ws://` or `wss://` URL of a brain you run yourself works |
| Use with Aura (`use_with_aura`) | off by default. On (the first-run setup's **Use Aura app**), the window connects to the agent service the [Aura](https://www.tryaura.dev/) app runs on this machine instead: runs go through Aura, on your Aura plan, and what the agent learns about each game is kept with Aura across your team's machines. Off, the window never connects to Aura's |
| Access token (`token`) | optional. When set, it is sent as `Authorization: Bearer <token>` when connecting. When empty, the `AUTOPILOT_PLAYTEST_TOKEN` environment variable is used if set; otherwise no token is sent. The local brain needs none |
| Agent service command (`service_command`) | empty by default: the `autopilot` command is found by itself. A whole command line that starts it, `serve ...` going after it, e.g. `C:\path\to\autopilot\.venv\Scripts\python.exe -m autopilot` in a repository checkout |
| `prompt`, `explore_minutes` | the composer's text and the first-run setup's Explore minutes (Default Explore Minutes, 1 to 600) (a `turn_based`, `mode` or `brief` an older version saved is ignored: runs are always real time, and the agent service reads the kind from the words; so are `auto_connect`, `auto_start_service` and `hide_aura_notice`: the editor always connects, and starts the agent service, by itself) |
| Design brief (`design_brief`) | how the game is meant to work; sent with every run when it says anything (empty by default) |
| Show policy log (`show_policy_log`) | `true` by default: the hello asks for the policy steps (`inspect`) for the Policy log tab; never in batch mode |

## Removing Autopilot

**Uninstall**, in the Settings page's last box (**Uninstall Autopilot**; greyed while a run goes), removes
Autopilot from this computer, for every project and editor: confirmed in place (*Uninstall Autopilot from this computer?*, **Uninstall** or **Cancel**),
with what it removes (*Removes the agent service, your keys, your Aura sign-in and your setup choices, for every
project.*) and **[Optional] Delete Autopilot’s memories for all projects in this machine across all editors. I understand that this action is destructive
and deletes Autopilot’s memories for my entire team.** (unticked each time it asks: left unticked, the memories are kept, `--keep-memory`).
**Uninstall** leaves the agent service and runs its own uninstall, `autopilot uninstall --yes [--keep-memory] --log <log>`
(`autopilot/uninstall.py`), with the command the window starts the agent service with, with no window, from the
system's temporary folder, its output in `<temp>/autopilot-uninstall-<yyyyMMdd-HHmmss>.log`. It stops the agent
services running (never the Aura app's), sends the shared runs not sent yet, signs out of Aura, removes the
`Autopilot/` settings folder (your keys, Aura sign-in, setup choices, logs and, with the box ticked, what Autopilot
learned; the Aura app's `aura-brain.json` stays), and uninstalls the `autopilot` command. With the box ticked the memories are deleted on this computer and also the copy kept with Aura for
your team (and this install's copy kept for your own keys), and it also removes what the package keeps about the runs
in each project the agent service has run (the files below but the window's settings), and the window forgets its runs
at once. **Shared run data already sent is never removed**; shared runs not sent yet that can't be sent now go with the
rest. When the command isn't on this computer, the window empties the settings folder itself (`memory/` too with the
box ticked; never `aura-brain.json`). In a terminal, `autopilot uninstall` (or `python -m
autopilot uninstall` in a repository checkout) does the same, asking first.

Until **Set up again** (or the editor restarts) the window shows how it went in place of everything else, and nothing
starts, installs or connects to the agent service: *Uninstalling Autopilot from this computer…*, then *Uninstallation
successful*, under it what is left to do (*To finish, remove the Autopilot package from each project in Window >
Package Manager.*) (**Set up again**, the first-run setup from its first step, and **View logs**; **View logs** first when the
log names files it couldn't remove), or *Couldn’t uninstall Autopilot from this computer.* (**View logs**, **Try again**,
**Cancel**).

To remove it from a project too, remove the package in the Package Manager (**Autopilot > Remove**, or its line in
`Packages/manifest.json`; a `"testables"` entry you added stays). Run In Background is put back then if a run had turned
it on (it stays on if the editor crashed during a run: Player Settings > Resolution and Presentation). Removing the
package leaves the project's files from it, which you can delete:

- `UserSettings/AutopilotPlaytest.json` (the window's settings, the access token too) and
  `UserSettings/AutopilotUsage.json` (the runs' usage and bills), `UserSettings/AutopilotRuns/` (the run history)
- `Library/AutopilotCodex/` (the project index) and `Logs/AutopilotCodexExport.log`
- `Logs/Playtest/` (each run's report, files and recording)
- the scripting symbols `AUTOPILOT_PLAYTEST` or `AUTOPILOT_NO_LEGACY_INPUT`, if you defined them

Your own scripts never reference the package unless your assembly definitions name `Autopilot.Playtest` (its assemblies
aren't auto-referenced), so the project compiles without it; CI that calls `-executeMethod
Autopilot.Playtest.Editor.Headless.Run` or `CodexExport.Run` needs the package.

## Player builds

The runtime half (`Runtime/`, assembly `Autopilot.Playtest`) is compiled only into the editor and **Development** builds,
or into any build where you define the scripting symbol `AUTOPILOT_PLAYTEST`. A release build contains none of it.

A player build stays offline unless it is started with `-Playtest=...` (or `-PlaytestUrl=... -PlaytestConnect`). Its
first scene gets a hidden `PlaytestBuildInfo` object at build time, holding the project's productGUID and where the
project and the editor were, so the player can serve the project index when it runs on the build machine.

## Headless and CI

Both a Development player build and the editor itself can run a test with no window and no graphics, then exit with
the result. Start the brain first (`python -m autopilot serve`), then:

A player build:

```
Game.exe -batchmode -nographics -logFile game.log ^
    -Playtest=explore -PlaytestMinutes=20 -PlaytestUrl=ws://127.0.0.1:8765/v1/bridge -PlaytestReportDir=C:\ci\playtest
```

The editor (it opens the scene, enters play mode and connects; never pass `-quit`):

```
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod Autopilot.Playtest.Editor.Headless.Run ^
    -Playtest=verify -PlaytestClaim="Verify that ..." -PlaytestUrl=ws://127.0.0.1:8765/v1/bridge -PlaytestMap=Main
```

A batch-mode editor started with `-Playtest=...` does the same even without `-executeMethod`.

| argument | meaning |
|---|---|
| `-Playtest=verify\|goal\|explore` | the kind of run |
| `-PlaytestClaim=...` | the claim to verify (`verify`) |
| `-PlaytestGoal=...` | the goal to play (`goal`) |
| `-PlaytestMinutes=N` | the time budget (`explore`) |
| `-PlaytestTimeLimit=N` | the whole run's time limit in minutes (`verify`, `goal`; default: the agent service's, 30) |
| `-PlaytestBrief=...`, `-PlaytestBriefFile=path` | how the game is meant to work (`explore`, optional) |
| `-PlaytestUrl=...` | the brain's URL (default `ws://127.0.0.1:8765/v1/bridge`) |
| `-PlaytestToken=...` | an optional bearer token (else `AUTOPILOT_PLAYTEST_TOKEN`, else none) |
| `-PlaytestReportDir=path` | where `result.json` (and any report files the brain sends) are written. Default: the editor's `Logs/Playtest/<time>`, a player's `<persistentDataPath>/Playtest/<time>` |
| `-PlaytestMap=scene` (or `-PlaytestScene=`) | the scene to play: a name or path (a player also takes a build index). It replaces the build's first scene and is what a restart reopens |
| `-PlaytestStayOpen` | keep running after the result instead of exiting |
| `-PlaytestEditMode` | editor only: connect in edit mode and let the agent enter play mode, as the window does |
| `-PlaytestConnect` | with `-PlaytestUrl` and no `-Playtest`: connect and wait for the brain to start a run |
| `-PlaytestConnectTimeout=seconds` | how long to wait for the brain's welcome (default `120`; `0` waits for ever). With none in that time the game writes `result.json` (`{"error": "Couldn’t connect to the playtest service at <url> within <n> s (<why>)", "exit_code": 1}`) to the report folder and exits 1, instead of retrying for ever. In the editor the time counts from entering play mode, across the script reload |
| `-PlaytestProject=path`, `-PlaytestUnity=path` | player only: the project folder and the Unity editor used to build the project index (default: the ones recorded at build time) |
| `-PlaytestShare=0\|1` | whether this run's data is sent to the developers of Autopilot; without it, `share_data` in `Autopilot/preferences.json` (on when the file doesn't say). A headless game never shows the first-run setup |
| `-PlaytestResearch=0\|1` | whether this run uses the research agent (sent as the hello's `research`, a choice); without it, `research_agent` in `Autopilot/preferences.json`, and when that is absent the hello leaves `research` out: off through the Aura app (Use with Aura), on with this plugin's own agent service |
| `-PlaytestSteps=`, `-PlaytestAttempts=` | passed on to the brain for brains that use them; Autopilot ignores them |

Exit codes:

| code | meaning |
|---|---|
| `0` | CONFIRMED, goal achieved, or exploration finished |
| `2` | DENIED |
| `3` | INCONCLUSIVE |
| `1` | goal not achieved, or an error (for example the brain refused the connection with an `error` message, or none answered within `-PlaytestConnectTimeout`) |

The brain can also set the code itself (`exit_code` in its result). A batch-mode game is held to 60 frames a second
unless it sets its own frame rate.

## Troubleshooting

- **"Can’t reach the agent service, retrying"**: the brain isn't running or listens elsewhere. With the URL at its
  default (and not using the Aura app), the window starts it; else start `autopilot serve` (from a repository,
  `python -m autopilot serve`) and check the URL and port under **Advanced**. The plugin keeps retrying with a growing delay (up
  to 30 s); **Try now** retries at once.
- **"The agent service stopped (see …)"** or **"Couldn’t install the agent service (see …)"**: **View logs** shows
  what it wrote (`Autopilot/logs/` in your user settings folder). From a repository checkout there is no installer:
  set **Agent service command**.
- **A red message after connecting**: the brain sent an `error` message and closed (for example, a token it doesn't
  accept). The message is shown as the brain wrote it; fix the cause and press **Try again**.
- **Keys do nothing in code that reads `UnityEngine.Input`**: the package routes those reads through the bridge when
  it compiles your scripts (see [Input](#input-what-can-be-driven)). Check `context`'s `input.legacy_input_reads`
  (0: nothing was routed). An assembly built with the scripting define `AUTOPILOT_NO_LEGACY_INPUT`, or a release
  build, isn't routed. Input actions need the Input System (Active Input Handling: *Input System Package* or *Both*).
- **The game slows to a crawl when the editor isn't in front**: Run In Background was off. The package turns it on
  for the length of each run in the editor (Player Settings, Resolution and Presentation) and turns it back off after.
- **A headless run exits 1 with "Couldn’t connect to the playtest service"**: no brain welcomed it within
  `-PlaytestConnectTimeout` (120 s by default). Start the brain first, check the URL, or pass a longer timeout
  (`0` waits for ever).
- **Reset memory…** (under the History, or in the window tab's menu) makes the agent forget what earlier runs
  learned about this project, confirmed in place in the Memory footer (the history dropdown's in a narrow window): *Reset memory?*
  *The agent forgets what it has learned about this game.* with **Reset memory** and **Cancel**; then *Resetting
  memory…*, then *Memory reset successful* (the count then says *· empty*) or *Memory not reset:* and
  why. A reset also takes the runs before it off the History: their files move to
  `UserSettings/AutopilotRuns/.reset-backup/<time>/` (kept, never listed again; the Settings page's usage keeps them). (The frames are still `clear_memory` / `memory_cleared`.) It works between runs only: while a run is going the
  button is greyed (*A run is going: reset memory once it is done*) and pressing it says so on the status line.
- **Logs**: every status line is also written to the Unity console (and a player's log) prefixed `[Autopilot]`, like
  the plugin's own warnings.

## Security

- The runtime is compiled only into the editor and Development builds (its define constraint
  `UNITY_EDITOR || DEVELOPMENT_BUILD || AUTOPILOT_PLAYTEST`), unless a project defines `AUTOPILOT_PLAYTEST` on purpose.
- It stays inactive until you connect in the window or pass `-Playtest` (or `-PlaytestUrl -PlaytestConnect`).
- It only connects out, to the URL you give. It opens no port.
- Connect it only to a brain you trust: the brain can call methods and set fields in the running game, and read the
  project's scripts and settings.

## Source map

| path | what it does |
|---|---|
| `Runtime/PlaytestClient.cs` | the WebSocket connection, the hello, frames from the brain, results, reconnecting, the command line |
| `Runtime/Bootstrap.cs` | starts the client in a player build and ticks it every frame; `PlaytestBuildInfo`, `PlayerHost` |
| `Runtime/Ops.cs` | answers each request (`op`) from the brain: `context`, `find`, `session`, `key`, `move`... |
| `Runtime/Reflect.cs` | `rc`: reading and writing fields, calling methods, describing objects, over C# reflection |
| `Runtime/Paths.cs` | the object paths the brain uses (`Main:/Level/Gem@2`, `...\|Component`) |
| `Runtime/Hands.cs` | the hands: virtual keyboard, mouse and gamepad on the Input System, the same keys for `LegacyInput`; input actions; `pointer` |
| `Runtime/LegacyInput.cs` | `UnityEngine.Input`'s reads with the bridge's keys in them (the game's calls are pointed here) |
| `CodeGen/LegacyInputRewriter.cs` | the IL post-processor that points the game's `UnityEngine.Input` calls at `LegacyInput`, and its `GUI` / `GUILayout` controls at `ImguiGUI` / `ImguiGUILayout` |
| `Runtime/View.cs` | `view` (what the player sees, sonar) and `watch` (with `drawn`: how a thing is drawn) |
| `Runtime/ViewSpace.cs` | `view`'s spatial reads, as Unreal's: things' own boxes, the player's body probing the room (openings, ways up and their lanes, point blank), a thing's top surface and lane, lights, effects, highlights and what is held |
| `Runtime/Imgui.cs` | IMGUI (`OnGUI`) controls noted as they are drawn and clicked for `screen`, `ui` and `ui_act` (the shims the rewriter points GUI and GUILayout calls at) |
| `Runtime/Net.cs` | context's `net`: Netcode for GameObjects or Mirror, through reflection |
| `Runtime/Player.cs` | finding the player, `pose`, `movement` |
| `Runtime/ScreenUI.cs` | `screen` (text on screen), `ui` and `ui_act` (uGUI and UI Toolkit controls) |
| `Runtime/Detectors.cs` | bug detectors: log errors, NaN, falling out of the world, long falls, hitches, HUD counters, teleports, objects piling up, numbers out of their range or above their maximum |
| `Runtime/Media.cs` | `record` (the `frame` messages) and `capture`: the game's view read back without waiting, encoded off the main thread |
| `Runtime/Account.cs` | the agent service's `account` frame (with its period `bill`, `out_of_credits` and BYOK `coding_agent` and `llm_provider`), the Coding Agents (`CodingAgents`) and LLM providers (`LlmProviders`), the Aura sign-in's states (`aura_sign_in` frames), and bills as shown (`Billing`: money, the period's dates) |
| `Runtime/Preferences.cs` | `Autopilot/preferences.json` (sharing, the research agent, the first-run setup's choices) and `Autopilot/autopilot.env` (own model keys, the Coding Agent and the LLM provider) |
| `Runtime/Codex.cs` | serving the project's files and the export (`codex_manifest`, `codex_read`) |
| `Runtime/Frame.cs` | conversion to the kit frame (centimetres, Z up) |
| `Runtime/Json.cs` | a small JSON reader and writer |
| `Runtime/Loaded.cs` | the loaded objects of a type across supported editor versions (`FindObjectsByType`'s overloads changed in 6000.4) |
| `Editor/EditorHost.cs` | the client in the editor: play mode as the session, surviving script reloads |
| `Editor/HostLink.cs` | another editor plugin (Aura's) carrying the connection to the brain in place of the WebSocket |
| `Editor/Headless.cs` | the editor in batch mode (`-executeMethod Autopilot.Playtest.Editor.Headless.Run`) |
| `Editor/CodexExport.cs` | the project index export to `Library/AutopilotCodex/` (in steps across the editor's updates, counted, stopped by Stop) |
| `Editor/ErrorPause.cs` | the Console's Error Pause held off while the agent plays, put back after (also after a crash) |
| `Editor/AutopilotSettingsProvider.cs` | Preferences > Autopilot: the window's settings on Unity's own Preferences page |
| `Editor/BuildHook.cs` | adds `PlaytestBuildInfo` to Development builds |
| `Editor/LegacyAxes.cs` | the project's Input Manager axes, for `LegacyInput` (and baked into builds) |
| `Editor/RunInBackground.cs` | Run In Background on while a run plays in the editor, put back after (and when the package is removed) |
| `Editor/PlaytestSettings.cs` | the per-user settings file (with the Design Brief and Show Policy Log) |
| `Editor/PlaytestWindow.cs` | **Window > Autopilot**; hosts `AutopilotView` |
| `Editor/AutopilotView.cs` (with `.Live.cs`, `.Result.cs`), `AutopilotPolicyView.cs`, `AutopilotUI.cs`, `UI/Autopilot.uss` | the window's UI Toolkit content, widgets and theme (Aura's Flatness look: one amber, square controls, 8 px plates; IBM Plex Sans and IBM Plex Mono in `UI/Fonts`, SIL OFL) |
| `Editor/AutopilotRecording.cs` | the run's recording on its Result page (VideoPlayer into a RenderTexture; without the Video module, its frames as a flipbook) |
| `Editor/AutopilotSession.cs` | the run history, kept across script reloads (SessionState) and editor restarts (`UserSettings/AutopilotRuns/`) |
| `Editor/AutopilotUsage.cs`, `AutopilotView.Settings.cs` | the Settings page: this project's runs and their Aura bills (each run's tokens per model kept), the ledger in `UserSettings/AutopilotUsage.json` |
| `Editor/AutopilotView.Setup.cs` | the first-run setup (account, agent service, privacy settings, the first run) and the browser sign-in |
| `Editor/AutopilotView.Account.cs` | the Settings page's Account card |
| `Editor/AgentService.cs` | finding, installing and starting the agent service; following `brain.json` |
| `Editor/Uninstall.cs`, `AutopilotView.Remove.cs` | Uninstall Autopilot from this computer (`autopilot uninstall`, or the settings folder emptied here), the removed state, the Settings page's Uninstall Autopilot box and the page saying how it went; the setup's Agent service step when the agent service is gone |
| `Editor/AutopilotText.cs` | turns the brain's status lines into the words the window uses |
| `Editor/AutopilotToolbar.cs` | the main toolbar's button (Unity 6.3 and later through its API, earlier through the toolbar's UI Toolkit tree) |
| `Tests~/` | the live tests and the test game they make (see Tests, below); the `~` keeps Unity from importing them |
| `Tests/Editor/` | EditMode unit tests (usage, preferences and autopilot.env, recording's scaling and messages, the account frames, the agent service's command lines, `HostLinkTests`: the connection carried by another plugin (the hello first and requests answered over the link, a script reload's 1012 and the session resumed over the next link, a new hello after the other plugin's connection ended, a disconnect ending the link, `HostLink` found by name), `UninstallTests`: removing Autopilot from this computer (its command line and result, the settings folder emptied without it, memory/ kept when asked, the project's run files deleted, nothing started while removed, the old-named folder not moved back, the uninstall run with a stand-in command on Windows), `HistoryTests`: the run history kept across editor restarts (a run's file written and read back, the newest 50 kept, a run going when the editor closed shown as NO RESULT, a run already here kept newest, the files of runs no longer kept deleted; every test's history in a temporary folder), `ComposerTests`: the kind read from the composer's words, the start frame, the memory line, the report stage, the recording's copy, the no-recording line (decided from the result, kept with the run), steering only at the Rollout and a run started from Claude Code or Codex (`run_started`: shown as the window's own, with where it came from, never over a run going), `ActivityTests`: the activity feed (each `activity` frame a line under its stage, a call and its result one line with two folds, a frame sent again changing nothing, the Feed tab's headings with their counts, `activity_since`, a run saved and read back with its events); and `LayoutTests`: every page laid out at 320x900, 360x640, 560x480, 900x400 and 1400x900 in an editor panel of its own, checked to fit and to say the design's words, with Rerun setup (in the header and on the Settings page, greyed mid-run), the selected run's row, the composer's placeholder, the stage names never cut from 300 px up, the setup's four steps in one column, the run's id line with its three icons
on one row (and the prompt close under it), the composer's link and Disconnect on one row, the recording's 230 px box, the no-recording line wrapping in its place, no findings header without findings, and the steer box greyed outside the Rollout, its hint fitting from 300 px up, Remove Autopilot: the Settings page's box, its confirmation and the pages saying how the removal went, and the Settings page's Claude Code and Codex card: its words, its place and its Copy buttons; and a page swapped (Live to Result and back) settled in its first layout: UI Toolkit lays out and applies the geometry fits in the update that first draws it, so it doesn't flash as Unreal's Slate did before SSettle); `ParityTests.cs`: OnGUI's controls read and clicked in an IMGUI pass of the test's own (the rewriter's routes matching the shims, the test game's `MenuImgui` routed when the project has it), the Console's Error Pause held off and put back (also after an editor that closed mid-run), the Design Brief sent with runs, Show Policy Log and the Preferences page saving where the window reads, the project index written in steps with its count and stopped, the Live page's now-line, the bundled fonts' notice, the recording's frames as a flipbook, the toolbar button put into an older toolbar; compiled only where the package is testable |

The customer-facing window speaks of an agent, harnesses, policy steps, actions and observations; keep internal names out of it
(`AutopilotText.Scrub` removes the ones that slip through status lines).

## How the brain sees the game

### Paths (`rc`, `find`, `transforms`, `watch`, `view`)

| path | names |
|---|---|
| `Main:/Level/Pickups/Gem@2` | a GameObject: `<scene>:/<root>/<child>/...` |
| `/Level/Pickups/Gem@2` | the same, in any loaded scene (DontDestroyOnLoad last) |
| `Main:/Player\|PlayerMover` | a component: `<object path>\|<type>` (`@n` for the n-th of a type; a base type or interface matches too) |
| `type:UnityEngine.Physics` | a type, for static methods and properties (full name, or a short name when unique) |
| `asset:Material/Red#4` | an object outside the scenes, by the handle the bridge gave it when it sent it |

Names escape `\ / | @` (and `:` in a scene's name) with `\`. Objects sharing a name under one parent are numbered when
first seen, in sibling order, and keep their number while they live: the first is plain (`Gem`), the others `Gem@1`,
`Gem@2`... A number is never given to another object, so `Gem@1` stays that gem, or is gone, while its siblings come
and go. A reloaded scene numbers afresh.

### Frames

Every op but `rc` speaks the kit's frame (Unreal's): kit `(X, Y, Z) = 100 * (z, x, y)` (centimetres, Z up), yaw =
`eulerAngles.y`, pitch = `-eulerAngles.x`, roll = `-eulerAngles.z`. `rc` is native: Unity's axes, metres, vectors as
lowercase `{"x","y","z"}`, quaternions `{"x","y","z","w","euler"}`.

### rc

`call`, `property` (read one, read all, write) and `describe`, over C# reflection: public and private fields
(auto-properties by their name), properties, methods (overloads by the argument names given; out and ref values come
back by name; a method returning `IEnumerator` is started as a coroutine). A GameObject's path reads its own state
(name, tag, layer, active, position, components, children) and, for a name it doesn't have, the member of its
components (the game's scripts first) or calls a method of theirs. Values: numbers, strings, booleans, enums by name,
vectors and colors as objects, references as paths (`""` for none; a Transform field as its GameObject's path), lists,
dictionaries, structs and serializable classes as objects of their fields, UnityEvents as their persistent calls.
Unity's getters that copy or leak (`material`, `mesh`...) are never read. A missing object is
`Object <path> does not exist`.

### The other ops

| op | here |
|---|---|
| `context` | Unreal's fields plus `project_dir`, `engine`, `input` (what the bridge can drive), `scenes`, `build_scenes`, `launch_scene`, `player_found_by`, `imgui` (`calls`: the game's OnGUI controls routed through the bridge; `drawn`: how many it drew); `{"player": path}` names the player when it can't be found (see below). `controls` lists the Input System's actions and, as Unreal's, the legacy Input Manager's axes: a button as `legacy:<Name>` (`digital`), any other as `legacy-axis:<Name>`, with their keys (FKey names; `MouseX`, `MouseY`, `MouseWheelAxis`). `net`: how the game is networked, from Netcode for GameObjects' NetworkManager or Mirror (through reflection): `{"mode": "standalone" \| "listen_server" \| "dedicated_server" \| "client"}`, a client's with `server_world` `""` (its server is another process) and `clients` |
| `find`, `transforms`, `pose`, `movement` | as the protocol's Engines section; `find {"things": true}` lists the scene's things (objects that aren't plain groups, at the top or under a plain group: Unreal's actors), `{"inactive": true}` inactive objects too. The player: the object `context` named, else tagged `Player`, else with a `PlayerInput`, else with a `CharacterController`, else the main camera's top object with a Rigidbody. `pose.at` is its body's middle (the CharacterController's or capsule's centre), the eye the main camera. `movement` reads the CharacterController (half height, radius, step, slope) and the player's script fields (walk speed: `walkSpeed`/`moveSpeed`/`speed`...; jump: `jumpSpeed`/`jumpForce`, or `jumpHeight` as `sqrt(2 g h)`; gravity: `gravity`, else `Physics.gravity` (`Physics2D.gravity` without the Physics module, else 9.81); air control: `airControl`), with `sources` saying where each came from |
| `session` | editor: `start`/`stop` enter and leave play mode (`map` sets the scene it starts in; ); `restart` is a Stop and a Play of the launch scene (or `map`), and `start` with a `map` while another scene plays is too, so nothing the game carried between scenes stays (statics stay only when the project doesn't reload scripts for play mode). Player: `restart` reloads the launch scene (or `map`: a name, path or build index), `start` with a `map` switches to it; reopening the build's first scene also drops the game's DontDestroyOnLoad objects, as a new launch would; a scene the build hasn't is refused (404) before anything changes. Answers say the `scene`; `status` says `loading` while the project index is written or play mode is entered (editor) or a scene loads (player). Objects kept with DontDestroyOnLoad stay across a reload, as they do for the game itself |
| `pause` | `Time.timeScale` 0 (restored after), audio paused; a networked client (its server in another process) can't: `{"paused", "why"}` |
| `key` | an Input System action's name (`Player/Jump`, `Jump`) where a key name is expected: pressed, held (leased) and released as the action (so the kit's `game.press` / `game.hold` take action names); else Unreal's FKey names (letters, `One`..`Zero`, `SpaceBar`, `LeftShift`, `Escape`, arrows, `F1`..`F12`, `LeftMouseButton`.., `MouseX`/`MouseY` (pixels of mouse movement), `MouseWheelAxis`, `Gamepad_*`) or Input System key names; `press` (down, up 50 ms and 2 frames later), `hold` (leased; with `seconds`, a timed hold: let go after that much game time, no renewal; the hello's `features` has `timed_hold`), `release` (an unknown key name is an error, as Unreal's), `release_all` |
| `input` | an Input System action by `Map/Action` or name: presses what it is bound to on the bridge's devices (a 2D composite's keys by the value's sign, a gamepad stick with its value); `legacy:<Name>`, a legacy Input Manager button, presses its first key (as the `key` op) |
| `move` | through the game's move action (a Vector2 action named like Move): its gamepad stick (any direction), else its keys (8 ways); else, or with `"via": "controller"`, the CharacterController (or Rigidbody) moved at the walk speed. Answers `{"moved", "via"}` |
| `watch` | each object's transform against its parent, every frame (`moved`, `turned`, `changed size`). With `drawn` (as Unreal's): a thing's root and its `parts` (3) biggest shown meshes (not ones a skeleton carries), each by its own path, sampled in the thing's frame, and how the thing is drawn: `part shown` / `part hidden` (a mesh), `effect started` / `effect stopped` (a particle system or VFX Graph effect), `light on` / `light off` (its lights and a helper's beside it), `animation started` / `animation ended` (its Animator's clips), `went limp` (a ragdoll: an Animator off, most of three or more rigidbodies moving by physics), each change with its `asset`; the answer says `drawn: true` |
| `view` | as Unreal's: things with a visible renderer (grouped to the topmost object that isn't a plain group; the ground, wider than `floor_size` (4000) and flat, left out) in the camera's frustum and in line of sight: `name`, `path`, `class` (the game's script on it), `label` (its first text), `center`, `size` (the box around it), `box` (its own box: `center`, `size` along its own axes and `rotation`, when it is turned) and `tilt` (`pitch`, `roll` when tipped), `yaw`, `distance`, `screen_x`/`screen_y`, `top_above_feet`/`bottom_above_feet`, `crosshair_on` and `crosshair_part` (the bone whose collider the view's line meets), `body_center`/`body_shown` (a character with a skinned mesh and colliders: the middle of what shows of its body), `blocks_player`, `speed`, `look` (`parts`: mesh names; `texts`; `animation`; `effects`: `{asset, scale}` of particle systems and VFX Graph effects playing; `lights`: `{on, brightness}`, its own lit lights and a light on an object with no body of its own beside it; `highlighted`: a mesh drawn with more materials than it has submeshes (an outline or overlay pass, as outline packages add) or a material or shader named outline or highlight; `limp`), its top surface (`top_slope_degrees`; for a slope 5 to 50 degrees a body could walk up, `low_end`, `high_end` (each a place seen: `at`, `turn`, `distance`, `height_above_feet`), `uphill` and `lane`: `width`, `middle`, `foot`, `top`, `left`/`right` (`open`, `wall`, `drop` with its `depth`) and `below`); `held` (the things hanging from the player, a bone of its skinned mesh or its camera, as the camera draws them (its culling mask): `name`, `path`, `class`, `look.parts`, `attached_at`, `center`, `size`, `distance`, `screen_x`/`screen_y`, `in_view`), `camera` (horizontal `fov`), `scene.sun` (the brightest directional light's intensity), `oriented: true`, `body` (`velocity`, `feet_z`, `on_ground`, `falling`, `standing_on`, `floor_slope_degrees`, `touching`, `overhead`: what is straight above the head, when it is in view, touched or within 10 cm, and its `clearance`). With `openings` (and `openings_reach`): `body.openings` (each doorway the body could take: `at`, `turn`, `distance`, `width`, `yaw`, `depth_beyond`, `floor_step`, `floor_above_feet`, `reachable_straight` or `blocked_at`/`blocked_by`, `passable`, `into`, `wall`, `sides`), `body.free` (how far the body goes at its knees and chest each 15 degrees) and `body.under`; with `ways_up`: `body.ways_up` (ramps and stairs up from the body's floor, also seen from the side: `turn`, `distance_to_foot`, `rises_by`, `top_distance`, `foot`, `top`, `seen_from`, `reachable_straight`, `lane`, `inside`). With `sonar` (`sonar_reach`, `sonar_turns`): `body.sonar.ways`, how far the player's body could walk each way (its capsule, a little thinner, swept half a metre at a time a step above the floor; a walkable rise is climbed; a ray down onto the floor each half metre finds drops deeper than 60 cm or two steps, and floors steeper than its slope limit): `{"turn", "clear", "by": wall\|drop\|steep\|reach, "thing"?, "normal"?, "drop"?, "rise"?}`, at most 16 ways and 512 sweeps a look, and `point_blank` (how far past the body's side a line at its knees and one at its chest meet something, within 1.5 body widths: `knee`, `chest`, `thing`, `normal`, `within`). Every probe is the player's own body's: lines stopped by what its layer collides with (triggers never), its CharacterController's capsule (step offset, slope limit): never navigation data. A project without the Physics module lists what it can't tell in `omitted` |
| `screen` | texts on screen-space canvases (uGUI Text, TextMeshPro with its tags taken off) keyed `<canvas>.<object>`, UI Toolkit text elements keyed `<UIDocument object>.<element>`, and IMGUI's (`OnGUI`, see below) labels and boxes keyed `<script>.Label` / `<script>.Box` (`#2`... for more) and its buttons' and toggles' words under their ids; `{"ui": true}` adds the `ui` list |
| `ui`, `ui_act` | uGUI Selectables (Button, Toggle, Slider, Scrollbar, Dropdown, InputField and their TextMeshPro kinds), UI Toolkit Button, Toggle, Slider, DropdownField, TextField, and IMGUI's buttons, toggles and text fields (`<script>.<words>`, `<script>.TextField`; `"imgui": true`, `screen_x`/`screen_y`); sliders say their `min` and `max`, text fields their `placeholder`; clicks go in as the input module sends them (pointer enter, down, up, click), `set` gives a value, `drag` drags and drops (a UI Toolkit element too: pressed over it, moved to the element `to`, let go there), `right_click` (UI Toolkit: down, up and its context click) |
| `timestep` | `Time.captureFramerate` |
| `detectors` | Only while a run uses the game (one is going, or the agent service asked the game something in the last 30 s): off in your own play sessions, so your game plays at its normal speed. `log_error` (errors, exceptions, failed asserts in the log), `nan`, `out_of_world` (100 m below the level's lowest solid thing, or `kill_y`; or 10 km away), `long_fall` (8 s), `hitch` (0.75 s), `hud_fraction` (`a/b` on screen with `a > b > 0`), and as Unreal's: `teleport` (`suspicion`: a character, the player or one with a CharacterController, moved farther in one frame than `teleport_factor` (3) times its speed or walk speed allows, at least `teleport_min` (1000 cm); not right after the bridge's own `rc`, which moves things for a verification's setup), `actor_growth` (the objects of one kind, by the game's script on them or their name, at least doubling and 50 more over `growth_seconds` (120), counted every 10 s of game time), `clamp` (a game script's number field outside its `[Range]` or `[Min]`: `{property, value, clamp: "[min, max]"}`), `over_max` (a number field above its sibling maximum, `health` and `maxHealth`/`healthMax`, `m_` and `_` kept: `{property, value, max_property, max}`), checked every `property_every` (0.5 s). Thresholds: `long_fall_seconds`, `hitch_seconds`, `hud_every`, `kill_y`, `teleport_factor`, `teleport_min`, `growth_seconds`, `property_every` |
| `record`, `capture` | `record` wants `action` `start` or `stop` (none is an error). The Game view (a player's screen) with its UI, taken at the end of a frame (`ScreenCapture.CaptureScreenshotIntoRenderTexture`), read back with `AsyncGPUReadback` (the game never waits on the GPU), scaled, JPEG-encoded, base64'd and written as JSON on a worker thread; a frame due while the one before is still in flight is skipped (a gap in `n`). Frames only while play mode runs and isn't paused (nor the game's `Time.timeScale` 0); a recording pauses when play mode stops and goes on when it plays again on the same connection, across the script reload. `-nographics` (no graphics device), no game running, or a build without the Screen Capture or Image Conversion module: 409 `no picture: ...`; the hello says `media` whenever both modules are in. A still while the editor is paused: 409 |
| `codex_manifest`, `codex_read` | `Packages/manifest.json`, `ProjectSettings/*.asset` (and `ProjectVersion.txt`), `Assets/**/*.cs`, `*.inputactions`, `*.asmdef` (also in embedded packages, `Packages/<name>/`), and the export in `Library/AutopilotCodex/`; folders `Library` (but the export), `Temp`, `Logs`, `obj`, `Build(s)`, `.git`, `node_modules`, `.*` and `*~` are skipped anywhere; the same limits as Unreal's. The export: in the editor, run in place (scenes are opened one by one in edit mode; in play mode only the loaded ones are written, live); from a player, the editor that built it runs `CodexExport.Run` headless on the project (refused while the project is open in an editor) |

### Input: what can be driven

With the **Input System** package (Active Input Handling: Input System or Both) the bridge adds its own virtual
keyboard, mouse and gamepad and queues state events on them: the game's actions, PlayerInput and `Keyboard.current`
reads all see them, in the editor whichever window has focus.

**While a run plays, the game gets the bridge's input alone.** Your own keyboard, mouse, pen, touchscreen, gamepads and
joysticks are disabled for it, and enabled again when the run ends or play mode does, so nothing you press (or a
controller in your hands) mixes with the agent's input. The editor itself still answers you: its windows don't read the
Input System, so you can stop the run or play mode as usual. A `PlayerInput` that was paired with one of your devices is
paired with the bridge's instead, in the same control scheme, and back afterwards. In the editor the run also uses a
copy of the Input System's settings that sends all input to the game whatever has focus (*Play Mode Input Behavior*:
All Device Input Always Goes To Game View, *Background Behavior*: Ignore Focus): with the default settings keyboard
and mouse input reach the game only while the Game view has focus, and during a run the focus is usually elsewhere.
The copy isn't saved: your project's Input System settings never change. Between runs, with the agent service
connected, your input reaches the game as usual.

The **legacy Input Manager** (`UnityEngine.Input`) reads the operating system's own events, and nothing can be queued
on it. So the package's IL post-processor (`CodeGen/LegacyInputRewriter.cs`) points your scripts' calls to its key,
button, axis and mouse reads (`GetKey`, `GetKeyDown`/`Up`, `GetMouseButton*`, `GetButton*`, `GetAxis`, `GetAxisRaw`,
`mousePosition`, `mouseScrollDelta`, `anyKey`, `anyKeyDown`, `inputString`, `ResetInputAxes`) at `LegacyInput`
(`Runtime/LegacyInput.cs`), which has the same members:

- **Outside a run** (no brain connected) each is `UnityEngine.Input`'s own: the game behaves as it always did.
- **Connected** it is what a person does together with what the bridge holds, except while a run plays, when it is
  what the bridge holds alone: keys and mouse buttons by KeyCode,
  the gamepad as joystick 1 (an Xbox pad's buttons and axes on Windows), the wheel, and the mouse at the pointer
  (`pointer` op). Axes and buttons follow the project's Input Manager axes (ProjectSettings/InputManager.asset: their
  keys, sensitivity, gravity, snap, dead zone, invert; a build carries them). With the Input Manager off (Active Input
  Handling: Input System), `UnityEngine.Input` throws: during a run the game's reads see only the bridge's input.
- **Which code**: every assembly compiled in the editor and into Development builds (or with `AUTOPILOT_PLAYTEST`),
  but Unity's and the package's own (`Unity.*`, `UnityEngine.*`, `UnityEditor.*`, `Autopilot.*`). A release build, and
  an assembly compiled with the scripting define `AUTOPILOT_NO_LEGACY_INPUT`, are left as the compiler made them.
  `context`'s `input.legacy_input_reads` counts the calls routed (each routed assembly carries
  `[assembly: LegacyInputRewritten(<calls>)]`).

`pointer` puts the mouse somewhere without moving across to it (no Mouse X/Y on the way): `{"screen_x", "screen_y"}`
(-1 to 1 from the middle, y up, as `view` gives things), `{"world": <kit point>}` or `{"object": <path>}` (where the
main camera shows it). Both input paths see it (`Input.mousePosition`, `Mouse.current.position`). Press
`LeftMouseButton` there to click; hold it and point again to drag.

**IMGUI (`OnGUI`)** draws without objects to read, so the same IL post-processor points your scripts' calls to `GUI`'s
and `GUILayout`'s `Label`, `Box`, `Button`, `RepeatButton`, `Toggle`, `TextField` and `TextArea` (every overload) at
`ImguiGUI` / `ImguiGUILayout` (`Runtime/Imgui.cs`), with the calling class's name pushed as one more argument. They
draw the control as Unity does and note it as it is drawn (its words, where it is on screen, the script that drew it),
for `screen`, `ui` and `ui_act`: a click is given by the control at its next drawing that isn't a layout pass (the
button returns true once, the toggle the other value, the text field the text set). An assembly with routed controls
carries `[assembly: ImguiRewritten(<calls>)]` (`context`'s `imgui.calls`). Unity calls no `OnGUI` in a batch-mode
player or editor: there nothing is drawn, so nothing is read or clicked (`imgui.drawn` stays 0).

`hello` and `context` say what can be driven (`input.handling`, `keys`, `actions`, `legacy_input_reads`, `pointer`,
`notes`). With neither (the Input Manager only, and nothing routed) the brain falls back to `rc` calls, `move` (which
then moves the CharacterController) and UI clicks.

## The codex export

`Library/AutopilotCodex/`: `summary.txt` (written last), `build_scenes.txt`, `scripts.txt` (class, base class, file),
`scenes/<path>.txt` (each object by its bridge path with its position, its components and their serialized values,
references by path, UnityEvent listeners: what a button calls), `prefabs/<path>.txt`, `data/<path>.txt` (the game's
ScriptableObject assets), `input/<path>.txt` (action maps, actions, bindings, composites, control schemes; and the
legacy Input Manager's axes). Headless:

```
Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod Autopilot.Playtest.Editor.CodexExport.Run -out <project>/Library/AutopilotCodex
```

## Tests

The scripts in `Tests~/` play the brain's side with the repository's `autopilot.bridge` against a headless game they make
for themselves. From the repository root, with its Python environment installed:

```
python plugin/unity/com.ramenvr.autopilot/Tests~/run_all.py
```

What it needs: Windows, Unity 6 with Windows build support (the editor from `AUTOPILOT_UNITY`, the path to `Unity.exe`;
default `C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Unity.exe`), signed in with a license that runs batch
mode, and free local ports 18901 to 18961. Nothing is downloaded: the Input System and uGUI come with the editor. It
opens no window and runs one Unity process at a time, so it can run beside an open editor. It takes about 3 minutes
(under a minute to make the game, then 10 to 50 s per test; longer the first time Unity compiles shaders and scripts
on a machine).

1. `testgame.py` makes the test game in a new temporary folder: `TestCourse/` (its scripts, input actions and the
   editor script that builds it) copied into a throwaway Unity project, with this package referenced from disk.
   The editor builds it headless: a menu scene (uGUI button, toggle, slider, dropdown and text field, a UI Toolkit
   label and button, an OnGUI box with a label, buttons, a toggle and a text field) and a level (a floor with drops at
   its edges, a wall, a block with a lamp's light by it, four spinning orbs to pick up, a gate that swings open on E, a
   ramp up to a raised platform, a wall with a doorway, a low canopy, a beacon with sparks playing and an outline, a
   `Hazard` script that misbehaves on request, a first-person player on a CharacterController holding a torch, a HUD
   counting the orbs), the project index, and a Development player.
2. `smoke.py ci`: command-line runs of the player and the editor against a stub brain: a `verify` run's report files
   and exit code 2, a refused token (exit 1), and no brain at all with `-PlaytestConnectTimeout=5` (exit 1 and
   `result.json` with the error).
3. `smoke.py player` and `smoke.py editor`: every op against the game, in the player and in the editor's play mode
   (with the script reload when entering play mode); the player also the OnGUI controls routed (and not drawn: a
   batch-mode player calls no OnGUI), the detectors teleport, clamp, over_max and actor_growth, watch with `drawn`, the
   legacy Input Manager's controls, a session start with a map, and the protocol's errors; the editor also a script
   error not pausing the game with Error Pause on.
4. `kit_live.py` and `sonar_live.py`: the brain's movement controllers and `view`'s sonar on the player; and `view`'s
   spatial reads (the ramp as a way up with its lane, `walk_to` up it to the platform, the ramp's slope, ends, tilt and
   own box, the doorway's opening, point blank, the torch held, the lamp's light, the beacon's sparks and outline, the
   canopy overhead).
5. The test game is deleted (`--keep` keeps it; `--only smoke-player,kit` runs some of them).

The EditMode unit tests in `Tests/Editor/` need a project that lists the package as testable
(`"testables": ["com.ramenvr.autopilot"]` in its `Packages/manifest.json`, with `com.unity.test-framework`):

```
Unity.exe -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults results.xml
```

Among them, `AgentServiceTests` and `AccountTests` cover finding the `autopilot` command, which agent service a
package that carries its own runs (the setting kept, its own installed rather than `PATH`'s, updated when another is
installed, one running from another commit replaced, only an agent service's process stopped; a package without one
as before), the start and install command lines, `brain.json`'s process matched to the one started, the logs kept, and the `account` / `aura_sign_in` frames
driving the window's states. Two more run only when asked:

- `AgentServiceLiveTests`, with `RG_PYTHON` set to a Python: starts `Tests~/fake_autopilot.py` as the Agent service
  command for real (no window, its output in the log, its `brain.json` found and used, a second start not made, its
  end noticed), and stops one only when it wrote the `brain.json` naming it (not with a `"started"` from before it
  started, nor without one), all in a temporary folder, never your `Autopilot` folder.
- `AccountLiveTests`: `python plugin/unity/com.ramenvr.autopilot/Tests~/account_standin.py --unity <Unity.exe> --project
  <project>` runs a stand-in agent service answering `account` and `aura_sign_in` frames as `autopilot/account.py` does
  (no Aura, no browser) and the test against it: the welcome's `account` feature, a sign-in that works, signing out, a
  cancelled sign-in.

Each script also runs alone: without `--exe` / `--project` it makes the test game itself and deletes it afterwards,
or it takes a game you made with `testgame.py --dir <folder>` (`smoke.py player --exe <folder>/Build/OrbCourse.exe`,
`smoke.py editor --project <folder>/OrbCourse`). The Python unit tests of the Unity seam are in the repository's
`tests/test_unity.py` (`python -m pytest tests/test_unity.py`).
