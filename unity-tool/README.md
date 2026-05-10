# Shadertoy2Unity — Unity Editor Tool

A Unity Editor window that converts [Shadertoy](https://www.shadertoy.com) GLSL shaders into Unity ShaderLab shaders using the **glsl2hlsl** command-line tool from this repository.

---

## Features

- Convert shaders directly from a **Shadertoy URL** (downloads via the public API)
- Or paste **raw GLSL** code (e.g. the `mainImage` function you copy from the editor)
- Optional **property extraction** — auto-exposes uniform variables in the Unity Inspector
- Optional **raymarching mode** — adapts the shader to run on a 3-D mesh instead of a screen quad
- Saves the resulting `.shader` file straight into your project and refreshes the AssetDatabase

---

## Prerequisites

1. **Unity 2020.1 or newer** (uses `EditorWindow`, `AssetDatabase`, `WebClient`)
2. The **glsl2hlsl binary** built from this repository

### Building the binary

From the **repository root**:

```bash
# Debug build (faster to compile)
cargo build

# Release build (recommended for production use)
cargo build --release
```

The binary will be at:

| Platform       | Path                                |
|----------------|-------------------------------------|
| Linux / macOS  | `target/release/glsl2hlsl`          |
| Windows        | `target\release\glsl2hlsl.exe`      |

> **macOS / Linux:** make the binary executable if needed:
> ```bash
> chmod +x target/release/glsl2hlsl
> ```

---

## Installation

Copy (or symlink) the `Assets/Shadertoy2Unity` folder into your Unity project's `Assets` folder:

```
<YourUnityProject>/
  Assets/
    Shadertoy2Unity/          ← copy this folder here
      Editor/
        Shadertoy2UnityWindow.cs
```

The folder **must** live inside `Assets/` (or a `Packages/` local package) so Unity's Editor compilation picks it up.

---

## Usage

1. Open the tool via the Unity menu: **Window → Shadertoy2Unity**

2. **Configure the binary path** (first time only):
   - Expand the *glsl2hlsl Binary Settings* foldout
   - Click **…** and browse to the compiled `glsl2hlsl` / `glsl2hlsl.exe` binary

3. **Choose input mode:**

   | Mode | What to enter |
   |------|---------------|
   | *From Shadertoy URL* | A full URL like `https://www.shadertoy.com/view/XsBXWt` or just the ID `XsBXWt` |
   | *From GLSL Code* | Paste the raw GLSL source from the Shadertoy code editor |

4. **Shader Name** — the filename that will be created in your project (e.g. `FractalLand` → `FractalLand.shader`)

5. **Options:**
   - *Extract Properties* — detect `float`/`vec` uniforms and expose them in the Inspector
   - *Raymarch / Raytrace Mode* — generate world-space vertex/fragment boilerplate for marched geometry

6. **Output Folder** — where the `.shader` file is written (default: `Assets/Shaders/Converted`). Click **…** to browse.

7. Click **Convert Shader**.

   The shader is saved, the AssetDatabase is refreshed, and the file is highlighted (pinged) in the Project window.

---

## How it works

```
Shadertoy URL
    │
    ▼  (HTTP GET to Shadertoy public API)
GLSL source code
    │
    ▼  (written to OS temp directory)
/tmp/Shadertoy2Unity/input.glsl
    │
    ▼  (glsl2hlsl binary invoked)
/tmp/Shadertoy2Unity/input.glsl.shader
    │
    ▼  (copied into Unity project)
Assets/Shaders/Converted/<ShaderName>.shader
```

The underlying conversion is handled entirely by the **glsl2hlsl** Rust library; the Unity C# script is a thin UI wrapper that drives the CLI tool and manages file I/O inside the Unity project.

---

## CLI flags used

| Unity Option           | Flag passed to binary |
|------------------------|-----------------------|
| Extract Properties ON  | *(default)*           |
| Extract Properties OFF | `--no-props`          |
| Raymarch Mode ON       | *(default)*           |
| Raymarch Mode OFF      | `--no-raymarch`       |

---

## Troubleshooting

| Symptom | Solution |
|---------|----------|
| *"glsl2hlsl binary not found"* | Set the binary path in the Settings foldout |
| *"Shadertoy API error: Shader not found"* | Check the shader is **public** on Shadertoy |
| Shader compiles but renders incorrectly | Try toggling *Raymarch Mode* |
| Conversion fails with a parse error | The shader may use features not yet supported by glsl2hlsl (see main README) |

---

## Tested on

- Unity 2022 LTS (Windows / macOS)
- Unity 2021 LTS (Windows)

---

## License

Same as the parent repository — see [LICENSE](../LICENSE).
