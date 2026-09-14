# Building a player that uses Phone Mass Controllers

`PmcHost` serves controller files from plain paths on disk — the **original bytes**.
That works in the editor because `Assets/` is right there, but a built player has no
`Assets/` folder: everything under it was imported (and possibly converted — `.png`
becomes a texture, `.html` a TextAsset). The only directories a player can read raw
files from are `Application.streamingAssetsPath` and absolute paths on the target
machine.

So for a controller page to survive a player build, its files have to land in
`StreamingAssets`. The package does that for you.

## What the build preprocessor does for you

`PmcBuildPreprocessor` (`IPreprocessBuildWithReport` / `IPostprocessBuildWithReport`)
runs automatically on every player build:

- It scans the **currently open scenes and every enabled scene in the build profile**
  for `PmcHost` components (including inactive objects and prefab instances).
- It also scans `Assets/**/*.cs` for **string literals** — `ServeDirectory("/prefix/",
  "Assets/…")` calls and `ControllerDir = "Assets/…"` assignments — which covers hosts
  created in code (`gameObject.AddComponent<PmcHost>()`).
- Every directory found is copied verbatim to
  `Assets/StreamingAssets/pmc/<basename>` (the `.meta` files are not copied), and the
  package's `Web/` SDK to `Assets/StreamingAssets/pmc/web` — without it `/pmc/pmc.js`
  would 404 in the build.
- After the build (success or failure) the staged tree is removed again and the
  project is put back exactly as it was, including restoring any pre-existing files
  that were shadowed.

At runtime, a `ControllerDir` of `Assets/Foo/Bar` resolves to
`Application.streamingAssetsPath + "/pmc/Bar"` — same `<basename>` rule. Absolute
paths and `Application.streamingAssetsPath` paths are used as-is and need no staging.

Watch the build log for `Phone Mass Controllers: staged N controller file(s) …`.
Directories that don't exist are warned about and skipped; pointing a `ControllerDir`
at `Assets/` itself is refused (staging the whole project is never what you want),
as are two different source dirs that would share one `<basename>`.

To keep the staged tree after a build (e.g. to inspect it), set the env var
`PMC_KEEP_STREAMINGASSETS=1` or the menu toggle
**Splatter → Phone Mass Controllers → Keep staged StreamingAssets after build**.

## Limitations

- Only **string literals** are found. `ServeDirectory("/x/", someVar)` or a
  `ControllerDir` computed at runtime can't be seen — keep the literal in a script,
  or use the manual fallback below.
- Only **scenes in the build** (and open ones) are scanned for `PmcHost` components.
  A host prefab that is never instantiated in a built scene is not discovered —
  put the literal in a script instead.
- Two directories with the same basename collide: `Assets/A/ui` and `Assets/B/ui`
  both stage as `StreamingAssets/pmc/ui`. The first one wins and the other is
  warned about — rename one of them.
- `user://`-style and absolute paths live outside the build entirely and need
  nothing.
- The scan runs at build time and doesn't validate what your server actually
  serves — it errs on the side of staging.

## Manual fallback

If a served directory is fully dynamic, keep those files under
`Assets/StreamingAssets/` yourself (they are always shipped byte-for-byte) and set
`ControllerDir`/`ServeDirectory` to the absolute path, e.g.
`Application.streamingAssetsPath + "/pmc/controller"`. Paths that are already
inside `StreamingAssets` are never re-staged.
