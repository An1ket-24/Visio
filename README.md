# Visio Riser Generator — POC (C# / .NET 10)

Generates a mechanical **riser diagram** as a real `.vsdx` file, straight from JSON.

```
Selection Navigator data  ->  riser spec (JSON)  ->  layout engine  ->  .vsdx
```

* The `.vsdx` is the **only** output — no SVG, PNG or intermediate files.
* The machine that generates it needs **no Visio, no Office, no COM, no registry access**.
* Works on Windows, Linux and macOS: the project has zero NuGet dependencies and no
  platform-specific code.
* Every generated file is re-opened and validated before the command reports success.
* Same spec + same template → **byte-identical** file (safe to snapshot/diff).

---

## 1. Requirements

| To… | You need |
|---|---|
| Build / run from source | .NET **SDK 10.0** or later |
| Run a published build | .NET **Runtime 10.0** (or publish self-contained, below) |
| Open the result | Visio 2013+, Visio for the web, or any `.vsdx` reader |

Nothing else. `dotnet build` restores offline — there are no package references.

## 2. Quick start

From the repository root, **on any OS**:

```bash
dotnet run --project VisioGenerator -- VisioGenerator/samples/chw-riser.json out/chw-riser.vsdx
```

Expected output:

```
Visio: /…/out/chw-riser.vsdx
Page : 11.2 x 16.75 in, 85 shapes
Check: package valid (parts, relationships, XML, shapes)
```

Try the small samples too:

```bash
dotnet run --project VisioGenerator -- VisioGenerator/samples/minimal.json out/minimal.vsdx
dotnet run --project VisioGenerator -- VisioGenerator/samples/smoke.json   out/smoke.vsdx
```

### Build a standalone folder you can copy to another machine

```bash
dotnet publish VisioGenerator -c Release -o out/publish
```

`out/publish` is self-contained in the sense that it carries the template and samples with
it — copy that one folder anywhere and run:

| OS | Command |
|---|---|
| Windows | `out\publish\VisioGenerator.exe` |
| Linux / macOS | `./out/publish/VisioGenerator` |

It needs the .NET 10 runtime installed. To remove even that dependency:

```bash
dotnet publish VisioGenerator -c Release -r linux-x64  --self-contained true -o out/linux
dotnet publish VisioGenerator -c Release -r osx-arm64 --self-contained true -o out/macos
dotnet publish VisioGenerator -c Release -r win-x64   --self-contained true -o out/win
```

The executable finds `templates/` and `samples/` **next to itself**, not relative to where
you happen to be standing, so it works from any working directory.

## 3. Where the output goes

| Invocation | Output |
|---|---|
| `VisioGenerator spec.json out/x.vsdx` | exactly that path, relative to the **current directory** |
| `VisioGenerator spec.json` | `./out/<spec name>.vsdx` |
| `VisioGenerator` (no args) | `./out/chw-riser.vsdx`, using the bundled sample spec |
| `VisioGenerator --check x.vsdx` | validates only, writes nothing |

Missing directories are created automatically, so `out/2026/oct/riser.vsdx` just works.
Pass an explicit output path when it matters — the default is relative to your shell, not to
the project folder.

## 4. Spec format

```jsonc
{
  "title": "Chilled Water & Controls Riser Diagram",
  "subtitle": "POC  |  Sample building, 8 occupied floors + roof + basement",
  "project": "POC-001  Rev A",

  "floors":  [ { "name": "ROOF" }, { "name": "L8" }, … { "name": "B1" } ],

  "systems": [
    { "id": "chws", "short": "CHWS", "name": "Chilled Water Supply",
      "color": "#2F5597", "dashed": false }
  ],

  "nodes": [
    { "id": "AHU-3", "label": "AHU-3\nVAV, L3", "kind": "ahu", "floor": 6 }
  ],

  "links": [
    { "from": "CH-1", "to": "AHU-3", "medium": "chws" }
  ]
}
```

| Field | Type | Default | Meaning |
|---|---|---|---|
| `title` | string | `"Riser Diagram"` | Heading, top-left |
| `subtitle` | string | `""` | Second line (omitted when blank) |
| `project` | string | `""` | Top-right corner (omitted when blank) |
| `floors[]` | `{ name }` | — | Building bands, **index 0 = top** (ROOF first, B1 last) |
| `systems[]` | `{ id, short, name, color, dashed }` | — | One vertical trunk per entry; `id` is what links refer to |
| `nodes[]` | `{ id, label, kind, floor, system? }` | — | Equipment; `floor` is an index into `floors` |
| `links[]` | `{ from, to, medium }` | — | `medium` must equal a `systems[].id` |

Layout rules applied by the engine:

* `floors[]` order is top → bottom; every node's `floor` indexes into it.
* A node joins a trunk if a link of that medium touches it, or if `node.system` names one.
* Trunk extents come from the branch heights, so a trunk never dangles past its last branch.
* Equipment sits in the right-hand column; 1–2 items on a floor sit side by side, 3+ stack.
* Page size grows with floor count, trunk count and label text — content never overflows.
* `kind` selects the fill: `chiller`, `ahu`, `pump`, `boiler`, `tower`, `panel`, `equipment`.

### Bad input handling

| Input | Behaviour |
|---|---|
| `floors` empty | error, exit 1: `Spec must contain at least one floor.` |
| duplicate `nodes[].id` | error, exit 1: `Duplicate node id 'A'.` |
| unknown `kind` | falls back to the grey `equipment` fill |
| unknown `medium`, out-of-range `floor`, unknown node id in a link | that link/trunk is silently skipped |
| malformed JSON | error, exit 1, with the parser message (comments and trailing commas are allowed) |

## 5. How the `.vsdx` is built

The generator does **not** synthesise the whole package. It opens a Visio-authored package
(`VisioGenerator/templates/blank.vsdx`) and:

1. replaces `visio/pages/page1.xml` with the generated shapes,
2. patches the page size in `visio/pages/pages.xml`,
3. copies every other part **byte-for-byte**, timestamps included.

Reason: Visio validates each part against its own schema on open, and rejects a package with
*"the file format is invalid or has become corrupted"* if a part it did not write deviates —
even when the ZIP, the relationships and the XML are all structurally valid. A hand-built
container was tried first and failed exactly this way. A Visio-authored container is
known-good; only the page content is ours.

The resulting package has the same **16 parts** as the template, in the same order, with no
orphans. Because the template is copied verbatim, page-level content is the only variable —
which is also why the same spec always produces the same bytes.

See `VisioGenerator/templates/README.txt` for the template's origin and licence.

## 6. Validation

Every run re-opens the file it just wrote and checks it, and exits non-zero if anything is
off. `--check <file>` runs the same checks on any existing file, including ones you did not
generate:

```bash
dotnet run --project VisioGenerator -- --check out/chw-riser.vsdx
```

| Checked | Failure example |
|---|---|
| ZIP/OPC readable, no duplicate parts | truncated download |
| Required parts present | missing `visio/pages/page1.xml` |
| Every XML part well-formed | half-written page |
| Content types cover every part, overrides resolve | new part added without an override |
| Every relationship target exists (relative paths, `..`) | `page2.xml` typo |
| Every part reachable from `_rels/.rels` | orphaned `windows.xml` |
| Page `<Rel r:id>` declared in the rels part | orphan page |
| Shape IDs numeric and unique | duplicated ID |
| `PinX/PinY/Width/Height` numeric, finite, non-zero | `NaN` from a bad layout number |
| Geometry rows numeric, no locale commas | `1,5` instead of `1.5` |

Because the container is copied from a Visio-authored file, those parts are valid by
construction. What the validator cannot prove is that *our page part* satisfies Visio's
schema — only Visio can. Open each new drawing shape set once in Visio before shipping.

Exit codes: `0` success · `1` usage / spec / template problem · `2` written file failed
validation.

### Bisect kit

`out/` can hold a ladder of files that localises a failure in one pass:

| File | What it proves if it opens |
|---|---|
| `00-control-original.vsdx` | the container itself is fine (it *is* the template) |
| `01-smoke.vsdx` | our page part with 1 floor / 5 shapes is accepted |
| `02-minimal.vsdx` | 3 floors, 1 trunk, 2 devices |
| `03-chw-riser.vsdx` | the full 85-shape drawing |

Regenerate it (PowerShell / bash):

```powershell
Copy-Item VisioGenerator\templates\blank.vsdx out\00-control-original.vsdx
dotnet run --project VisioGenerator -- VisioGenerator/samples/smoke.json   out/01-smoke.vsdx
dotnet run --project VisioGenerator -- VisioGenerator/samples/minimal.json out/02-minimal.vsdx
dotnet run --project VisioGenerator -- VisioGenerator/samples/chw-riser.json out/03-chw-riser.vsdx
```

If `00` fails, the template is the problem. If `01` fails, the problem is the shape XML in
`VsdxWriter.ShapeXml`. If `01` passes and `03` fails, it is content-specific (volume, a cell
value, a colour).

## 7. Project layout

```
VisioGenerator/
├─ VisioGenerator.slnx          solution
├─ README.md
├─ .gitattributes               marks .vsdx as binary (git must not rewrite it)
├─ VisioGenerator/
│  ├─ VisioGenerator.csproj     net10.0, template + samples copied to output
│  ├─ Program.cs                CLI: [spec.json] [out.vsdx] · --check · --help
│  ├─ Model.cs                  JSON contract (RiserSpec …) — the Selection Navigator boundary
│  ├─ Layout.cs                 deterministic layout → renderer-neutral Diagram (all geometry)
│  ├─ VsdxWriter.cs             template-based writer: page part + page size, rest copied
│  ├─ VsdxValidator.cs          post-write package gate
│  ├─ templates/
│  │  ├─ blank.vsdx             Visio-authored container (16 parts)
│  │  └─ README.txt             origin + licence
│  └─ samples/
│     ├─ chw-riser.json         10 floors, 4 systems, 12 devices, 30 links → 85 shapes
│     ├─ minimal.json           3 floors, 1 system, 2 devices            → 17 shapes
│     └─ smoke.json             1 floor, no systems, 1 device             → 5 shapes
└─ out/                         generated files (git-ignored)
```

Layout is a pure function of the spec, so output is reproducible — safe to snapshot in tests
and to diff between equipment-selection revisions.

## 8. Cross-platform notes

| Concern | How it is handled |
|---|---|
| Visio / Office / COM | not used anywhere — pure managed code |
| Windows-only APIs | none (no registry, GDI, WinForms, P/Invoke) |
| Paths | `Path.Combine` + `AppContext.BaseDirectory`; MSBuild items use `/` |
| Culture | geometry is formatted with `InvariantCulture`, so a `de-DE` machine cannot emit `1,5` |
| ZIP entry names | always forward slashes, as OPC requires |
| Line endings | `.gitattributes` marks `.vsdx` as binary, so git cannot corrupt the template |
| Output determinism | template timestamps preserved → identical spec gives identical SHA-256 |
| File locks | output is written to a temp-free path and re-opened read-only for validation |

Verified in this repo: Release build and `dotnet publish` on Windows with .NET SDK 10.0.301,
then the publish folder copied outside the repo and run from an unrelated working directory
with no arguments, with an explicit spec, into a nested output directory, and through
`--check` — all exit 0. Linux/macOS use the same code paths and have no platform-specific
branches.

## 9. Using a JCI template

The writer only needs two parts inside the template: `visio/pages/page1.xml` and
`visio/pages/pages.xml`. Everything else is preserved untouched.

1. In Visio, build a one-page drawing with the JCI page size, title block, styles and theme
   you want (A1/A3, JCI fonts, standard line weights).
2. Save as `.vsdx` and drop it in as `VisioGenerator/templates/blank.vsdx`
   (keep it named `blank.vsdx`; the writer patches its `page1.xml`).
3. Rebuild — the new template is copied to the output folder automatically.
4. Run one sample and open it: page size is set by us, styles/masters come from the template.

Caveat: generated shapes reference style IDs `0` (`LineStyle`/`FillStyle`/`TextStyle`). Any
Visio-authored file has those, so a normal Visio save works; a hand-trimmed ZIP may not.

## 10. Wiring it to Selection Navigator

1. Export the selection: equipment (tag, type, capacity, floor, system membership) plus
   piping/network relationships.
2. Map equipment type → `kind`, system → `systems[]`, relationship → `links[]`
   (`medium` = system id).
3. `POST /api/riser` with the spec → stream back the `.vsdx`.
4. Store the spec next to the selection so a later revision re-renders identically.

An ASP.NET Core endpoint is ~10 lines around `LayoutBuilder.Build` + `VsdxWriter.Write`, and
because nothing platform-specific is involved it can run in a Linux container.

## 11. Why not just use the Visio COM API?

Both routes are valid; pick per deployment target.

| | Pure `.vsdx` writer (this repo) | `Microsoft.Office.Interop.Visio` |
|---|---|---|
| Visio licence needed | No | Yes, on every machine that generates |
| Runs in a service / container / Linux | Yes | No (Office automation in a service is unsupported) |
| Uses JCI stencils / smart shapes | No — plain geometry | Yes — full masters, connection points, data graphics |
| Effort for a POC | Done | ~a day, plus deployment constraints |

A hybrid is common: keep this layout engine and add a `ComRenderer` that consumes the same
`Diagram` but drops real stencil masters instead of rectangles. Layout stays testable; only
the last mile changes.

## 12. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| *"We can't open your file because the file format is invalid or has become corrupted."* | A part Visio did not write deviates from its schema. Work through the bisect kit in §6. If `00` fails, the template got modified — re-copy it from a Visio save. |
| `The 'templates' folder must sit next to the executable.` | You ran the `.exe`/binary outside its build folder, or copied only the executable. Copy the whole publish folder, or use `dotnet run`. |
| `Spec not found: nope.json` | Input paths are relative to your **current directory**, not the project. Use an absolute path or `cd` first. |
| `INVALID: … file does not exist` on `--check` | Nothing was generated at that path — check the output directory rule in §3. |
| Output lands somewhere unexpected | Default is `./out/` in the shell's current directory; pass an explicit output path. |
| `Spec must contain at least one floor.` | `floors[]` is empty or missing. |
| File opens but is empty-looking | Check the shape count in the console: `0` means every node/link was dropped (bad `floor` index, `medium` not matching a system id, or unknown node ids). |

Run `dotnet run --project VisioGenerator -- --help` for the CLI summary.

## 13. Known limits (deliberate POC cuts)

* Plain geometry shapes — no Visio masters, connection points or shape data.
* One page; no automatic sheet splitting above ~14 floors.
* Straight-line branches only; no pipe-size-driven line weights or pump/valve symbols.
* No label collision avoidance and no title block / revision table.
* Validation proves package integrity, not Visio's schema acceptance.

## 14. Suggested next steps

1. Open `out/chw-riser.vsdx` in Visio once; confirm no "repair" prompt.
2. Swap in a JCI-standard template (§9) and a real Selection Navigator export.
3. Add a floating title block + revision table, and per-floor scaling.
4. Add PDF export (Visio `ExportAsFixedFormat`, or a server-side converter).
5. Snapshot the generated `.vsdx` in tests — byte-identical output makes this trivial.

## 15. Third-party attribution

`VisioGenerator/templates/blank.vsdx` is the sample drawing shipped with the **`vsdx`**
Python package (<https://pypi.org/project/vsdx/>, BSD licence, © Dave Howard). It is a
genuine Microsoft Visio 16 file and is used only as a known-good container. Details in
`VisioGenerator/templates/README.txt`.
