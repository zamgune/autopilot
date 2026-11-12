# Autopilot {version}

> By the developers of Aura, [www.tryaura.dev](https://www.tryaura.dev/)

Autopilot is a continually learning real-time general playtesting system: it plays your game to explore it, test it,
verify claims about it and reach goals in it.

| Download | For |
|---|---|
| `{stem}-Unreal<x.y>-Win64.zip` | Unreal Engine x.y (5.4-5.8) on Windows (prebuilt: Blueprint-only projects work too) |
| `{stem}-Unreal<x.y>-Mac.dmg` | Unreal Engine x.y (5.4-5.8) on macOS, Apple silicon or Intel (prebuilt too) |
| `{stem}-Unity.zip` | Unity 2022.3 LTS or Unity 6 on Windows or macOS |
| `{stem}-Godot.zip` | Godot 4.7 or later on Windows or macOS |
| `{stem}-Roblox.zip` | Roblox Studio on Windows or macOS |

`SHA256SUMS.txt` lists the downloads' hashes. Installing the agent service needs the internet once.

## Install

### Unreal Engine

1. Unzip the download (on macOS, open the `.dmg`) and copy `Autopilot/` into your project's `Plugins/` folder (create
   a `Plugins/` folder if your project doesn't have one yet), so that you have
   `<YourProject>/Plugins/Autopilot/Autopilot.uplugin`. Use the download for your project's Unreal version. On macOS,
   the disk image and the plugin's binaries are signed and notarized by Apple; you can eject the disk image once
   `Autopilot/` is copied.
2. Open the project.
3. Click the orange **Autopilot** icon on the level editor's toolbar (or Window > Tools > Autopilot).

### Unity

1. Unzip the download, and keep `com.ramenvr.autopilot-<version>.tgz` where it is: your project refers to it.
2. Open Window > Package Manager (under Window > Package Management in some Unity 6 releases). Choose
   + > Add package from tarball... in Unity 2022.3, or + > Install package from tarball... in Unity 6, and select the `.tgz`.
3. Open Window > Autopilot.

### Godot

1. Unzip the download and move its `addons/autopilot/` folder into your project's `addons/` folder.
2. Enable **Autopilot** in Project > Project Settings > Plugins. Its dock appears on the right.
3. Commit the autoload and project id (`autopilot/project_id`) it adds to `project.godot`.

### Roblox Studio

1. Unzip the download (its `README.txt` has these steps too). Install the agent service once: run
   `agent_service/install.ps1` (Windows: right-click > Run with PowerShell) or `sh agent_service/install.sh` (macOS)
   from the unzipped folder. A Roblox plugin can't run programs, so it can't install or start the agent service for
   you.
2. Copy `Autopilot.rbxmx` into Studio's Plugins folder: `%LOCALAPPDATA%\Roblox\Plugins` on Windows,
   `~/Documents/Roblox/Plugins` on macOS (Studio's Plugins tab > Plugins Folder opens it).
3. Before each session, start the agent service in a terminal and leave it running: `autopilot serve`. Right after
   installing, open a new terminal for it: the one you installed from doesn't have the `autopilot` command yet.
4. Open your place. The **Autopilot** dock appears on the right (Plugins > Autopilot) and connects by itself. Studio
   needn't stay in front while a run plays. For the run's recording and stills, turn on Studio's MCP server once
   (Assistant > … > Settings > MCP Servers > Enable Studio as MCP server): the agent service takes the game's pictures
   through it. The [setup guide](../../docs/roblox-studio-setup.md) has the details.

### Updating

Put the new download's plugin in place of the old one (Unreal: replace `Plugins/Autopilot/`; Unity: install the new
tarball as above; Godot: replace `addons/autopilot/`; Roblox: replace `Autopilot.rbxmx` and run the new installer),
then open the project. The plugin installs the agent service it
carries over an older one, and stops an older one that is still running, by itself.

### Uninstalling

1. In Autopilot, go to Settings and click Uninstall (Roblox Studio: run `autopilot uninstall` in a terminal). By default, Autopilot keeps your project's memories; you can choose to delete memories instead (this is a destructive action).
2. Remove the plugin from each project: Unreal, close the editor and delete `Plugins/Autopilot/`; Unity, remove the Autopilot package in the Package Manager; Godot, disable Autopilot in Project > Project Settings > Plugins, then delete `addons/autopilot/`; Roblox, delete `Autopilot.rbxmx` from Studio's Plugins folder.

## First-run setup

The first time you open the Autopilot tab:

1. **Account** (Welcome to Autopilot): choose how to run the agent.
   - **Sign in with Aura** (recommended): billed to your Aura plan. Click **Create Account** if you don't have
     an Aura account yet, or **Sign in** if you do. Autopilot sets up the agent service first (once per computer, about
     a minute), then opens Aura's sign-up or sign-in page in your browser: finish there.
   - **Bring your own keys (BYOK)**: a **Decision provider API key** (e.g.
     [TypeSafe](https://typesafe.ai)), a **Coding Agent**, and its **LLM provider** and **LLM provider API key**:
     Claude Code takes an [Anthropic](https://console.anthropic.com) key, Codex an
     [OpenAI](https://platform.openai.com) key, OpenCode one from Anthropic, OpenAI, [OpenCode Zen](https://opencode.ai),
     [OpenRouter](https://openrouter.ai), [DeepSeek](https://platform.deepseek.com) or
     [Gemini](https://aistudio.google.com). Then **Use my keys**, and **Install** the agent service (once per
     computer, about a minute).
2. **Agent service ready**: click **Continue**. From then on, Autopilot starts the agent service with your editor.
3. **Privacy settings**: whether to share your runs to improve Autopilot (on by default). As the developers of
   Autopilot, we appreciate your leaving it on.
4. **Let Autopilot learn your game** (optional), when it knows nothing about this project yet: **Explore and finish**
   explores it for the minutes you set (10 by default), so later runs start knowing it; **Skip and finish** closes the
   setup, and you can send your own prompt straight away.

Then prompt Autopilot: see [Using Autopilot](#using-autopilot) for the kinds of prompts to try.

Your choices apply to every project and engine on the computer. To change them, press **Rerun setup** on the tab's
**Settings** page (the gear).

## Using Autopilot

The main way to use Autopilot is the **Autopilot plugin** in your editor (Unreal, Unity or Godot, on Windows or macOS).
It uses your Aura plan, or your own keys. You can also use it:

- **In Aura**, for Unreal and Unity. It uses your Aura plan.
- **From your own Claude Code or Codex**, on your own Claude or ChatGPT plan (see
  [From your own Claude Code or Codex](#from-your-own-claude-code-or-codex)).

A prompt is one of four types:

- **Exploration**, e.g. "Explore the crafting system in Lvl_Main."
- **Testing**, e.g. "Go through the currently open level and tell me everything that looks wrong."
- **Verification**, e.g. "Verify that the tree shakes when you hit it with an axe."
- **Goal**, e.g. "Kill the boss while keeping your health above 20% of its maximum value."

For every prompt, Autopilot starts the game for you (Play In Editor in Unreal, Play Mode in Unity, the main scene in
Godot), reads your project's code, tries things in the game, and plays according to your prompt. When it is done, the
game closes by itself and Autopilot shows its report.

## Running the agent service yourself

1. Find the installer in the plugin: `<YourProject>/Plugins/Autopilot/AgentService/` (Unreal), `AgentService~/` in the
   package's folder under your project's `Library/PackageCache/` (Unity), or `addons/autopilot/agent_service/` (Godot).
2. Run `install.ps1` on Windows (`powershell -ExecutionPolicy Bypass -File install.ps1`) or `install.sh` on macOS and
   Linux. It installs the `autopilot` command.
3. Run `autopilot serve` before you open the editor. Autopilot connects to it instead of starting its own.

`autopilot login` signs in to Aura from a terminal (`autopilot logout` signs out); `autopilot uninstall` removes
Autopilot from the computer (see [Uninstalling](#uninstalling)).

## From your own Claude Code or Codex

Autopilot can also run from your own Claude Code or Codex, on your Claude or ChatGPT plan: your agent writes the
harness, and Autopilot plays the game open in your editor. With the Autopilot tab connected, run once in a terminal
(the **Autopilot in Claude Code & Codex** card on the **Settings** page has these to copy):

```
autopilot connect claude
autopilot connect codex
```

Then ask your agent in a new session, for example "Use Autopilot to verify that the door opens with the red key". The
run shows in the Autopilot tab like any other. `autopilot connect claude --remove` (or `codex`) takes it out again.

## If something goes wrong

- **Can't reach the agent service**: the tab says why it couldn't start it. If you run it yourself, check that
  `autopilot serve` is running at the address in Autopilot's settings.
- **Couldn't install the agent service** or **The agent service stopped**: **View logs** shows why, then **Try again**.
  The logs are in `Autopilot/logs/` in your user settings folder (`%LOCALAPPDATA%` on Windows,
  `~/Library/Application Support` on macOS, `~/.config` on Linux).
- **A sign-in that was denied, timed out or couldn't reach Aura**: **Try again**.
- **A message in red after connecting**: the agent service says why it refused; fix that, then **Try again**.

Documentation: https://www.tryaura.dev/documentation/autopilot/

Source: https://github.com/zamgune/autopilot
