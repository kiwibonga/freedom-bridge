# Freedom Bridge

A skill for coding agents that allows code to be generated, compiled and executed on the fly without reloading the domain, granting full programmatic control of the Unity Editor.

## Installation

1. You must first add the skill to your coding agent (follow the agent's specific instructions for this)

2. When the skill is detected successfully, ask the agent to perform the install:

```Please install the Freedom Bridge skill```

You may have to tab to the Unity window afterwards to trigger compilation.

## How it works

As long as script compilation succeeds, this plugin will run a lightweight HTTP server that can receive queries:

/exec -- Uses Mono.Evaluator to compile and run a C# string
/compile -- Creates a temporary script file to run a job (legacy method, causes multiple domain reloads)
/coroutine -- Uses Mono.Evaluator to compile and run a coroutine or async function
/pending -- Queries status of a pending compile or coroutine operation
/refresh -- Causes the asset database/scripts to refresh even if the editor is not focused
/logs -- Reads Unity console logs

This server will restart every time script compilation succeeds, and stay up if compilation fails. It will stop if you close Unity.

## What it can do

* Ask the agent anything: Start or stop play mode, load that scene in that folder I'm too lazy to open, run this menu item, find out the HP of that enemy next to the player, etc.
* 

## Changelog

2026/03/20 - Version 0.1: Initial release

## TODO / Wanted

* Bundle some default recipes with skill
	* Realtime debugging recipe (adjust Time settings, pause playmode, step, etc)
	* UI interface recipe (traverses Unity.UI elements, finds the interactable ones, maps them out to allow agent to trigger events on them)
	* Overlay recipe (spawns an overlay canvas with a UI panel); could build a cheat menu on the fly with clickable on-screen buttons
* Proper agent/subagent harness to write the injectable code in a throwaway context and avoid filling context with data retrieval work
* Fix port number confusion when user customizes port number (random port based on pid would be good)
* Editor interaction with Playwright (MCP?) -- to view inspector pane, debug editor UIs, etc.
* Improvement to coroutine capabilities; important for debugging -- need editor coroutines not affected by play mode or pause. Could also choose (Editor, Update, FixedUpdate, etc)
* Vision capabilities (pull screenshot through HTTP, editor pane screenshots, arbitrary region screenshots, texture viewer)
* Learn mode -- The skill should spell out instructions on creating recipes