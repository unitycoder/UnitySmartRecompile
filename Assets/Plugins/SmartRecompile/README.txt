SMART SCRIPT REFRESH - UNITY 6000.7 TEST

INSTALL
1. Copy Editor/SmartScriptRefreshWindow.cs into Assets/Editor/ of your Unity project.
2. In Visual Studio, turn OFF: Tools > Options > Tools for Unity > Refresh Unity's AssetDatabase on save (if present).
3. Unity: Tools > Smart Script Refresh. Switch Enabled on.
4. Save a comment-only change in an existing script; check the DEFERRED event.
5. Change an actual C# value or statement; check REFRESH QUEUED, then compilation.
6. Use Refresh Now to apply changes manually; switch tool OFF to restore automatic refreshing.

IMPORTANT LIMITATIONS
- Experimental, not tested in your Unity installation.
- The tool blocks ALL automatic asset refreshes while on. Other assets are not automatically imported.
- Explicit AssetDatabase.Refresh calls (from Unity, Visual Studio, or plugins) bypass the block.
- Deferred comment-only files can still be compiled when a later refresh occurs.
- If Roslyn assemblies aren't available in the Unity Editor process, the tool uses a conservative
  line comparator: blank lines, leading/trailing whitespace and ordinary // comments only.
- Preprocessor directives are always treated conservatively (as real changes).
- A domain reload triggered by other sources can still occur; the tool does not stop it.
- This is intentionally not a production-ready editor extension or a guaranteed compilation bypass.
- Tracks .cs scripts in Assets only; not Packages or generated files outside Assets.
- Disable before starting a project build or modifying important imported assets.

RECOVERY
If Unity stops detecting file changes unexpectedly, switch the tool OFF, and use Assets > Refresh.
If a domain reload interrupts the tool, reopen Tools > Smart Script Refresh and check status.
If the refresh lock somehow stays active after an Editor crash, restarting Unity should reset
its native lock state. No source files are ever overwritten by this tool.
