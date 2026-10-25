<img src="https://img.shields.io/badge/VibeCoded-100%25-green" alt="AI Generated Content"/>

# Smart Recompile for Unity

An experimental Unity Editor tool that defers script compilation when you only change comments or formatting.

| | Default Unity behavior | Smart Recompile |
|---|---|---|
| Save a comment or blank line | May recompile | Defers automatic refresh |
| Save a code change | Recompiles | Triggers refresh and compilation |
| Other asset changes | Auto-import normally | Auto-import is deferred while enabled |
| Later manual refresh | Imports pending changes | May still compile deferred edits |

## Installation

1. Copy `Plugins/SmartRecompile/Editor/SmartRecompile.cs` into your project's `Assets` folder (preserving the `Editor` subfolder).
2. Open **Tools > UnityLibrary > Smart Recompile** in Unity.
3. Enable the tool. The window shows the active comparer, pending edits, compilation counts, and event log.
4. If available, disable Visual Studio's **Refresh Unity's AssetDatabase on save** option.

Tested target: **Unity 6000.7.0b6** (experimental; not broadly validated).

## How it works

The tool watches `.cs` files under `Assets`, compares their contents with Roslyn when available (otherwise a conservative text comparison), and blocks Unity's *automatic* asset refresh until a code change is detected. Use **Refresh Now** to import pending changes manually.

## Limitations

- **Not a true compilation bypass:** a later refresh can still recompile files changed only in comments or whitespace.
- All automatic asset imports are deferred while enabled, not just script imports.
- Explicit refreshes and external tooling can bypass the auto-refresh lock.
- Some edits are intentionally treated as code changes (such as files containing preprocessor directives); without Roslyn, detection is more conservative.
- Watches the `Assets` folder only, not packages.

Disable the tool to restore Unity's normal refresh behavior and refresh pending changes.

### Images

<img width="739" height="751" alt="image" src="https://github.com/user-attachments/assets/1dab2b07-acc3-4abb-bbbf-3cbcd6fb0473" />
