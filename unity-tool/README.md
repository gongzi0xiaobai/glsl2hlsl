# Shadertoy2Unity — Unity Editor Tool

A Unity Editor window that converts [Shadertoy](https://www.shadertoy.com) GLSL shaders into
Unity ShaderLab shaders.  
**No external binary or Rust toolchain required** — the entire conversion runs in C# inside Unity.

---

## Features

- Convert shaders directly from a **Shadertoy URL** (downloads via the public API)
- Or paste **raw GLSL** code (e.g. the `mainImage` function from Shadertoy's editor)
- **Property extraction** — `#define` macros and top-level `const` variables are detected and exposed as Inspector-editable shader properties
- **Raymarching mode** — adapts the shader to run on a 3-D mesh with world-space ray-origin / ray-direction input instead of a screen-space quad
- Saves the resulting `.shader` file straight into your project and refreshes the AssetDatabase
- **Self-contained** — all GLSL-to-HLSL logic lives in `GlslConverter.cs`; nothing to install

---

## Prerequisites

**Unity 2020.1 or newer** — that is all.  No Rust, no `cargo`, no external tools.

---

## Installation

Copy (or symlink) the `Assets/Shadertoy2Unity` folder into your Unity project's `Assets` folder:

```
<YourUnityProject>/
  Assets/
    Shadertoy2Unity/
      Editor/
        GlslConverter.cs          ← conversion logic
        Shadertoy2UnityWindow.cs  ← editor UI
```

The folder **must** live inside `Assets/` (or a local `Packages/` package) so Unity's Editor
compilation picks it up.

---

## Usage

1. Open the tool: **Window → Shadertoy2Unity**

2. **Choose input mode:**

   | Mode | What to enter |
   |------|---------------|
   | *From Shadertoy URL* | Full URL `https://www.shadertoy.com/view/XsBXWt` or just `XsBXWt` |
   | *From GLSL Code* | Paste the raw GLSL from the Shadertoy code editor |

3. **Shader Name** — filename to create (e.g. `FractalLand` → `FractalLand.shader`)

4. **Options:**
   - *Extract Properties* — convert `#define`/`const` numeric values to Inspector properties
   - *Raymarch / Raytrace Mode* — world-space vertex shader for marching against mesh geometry

5. **Output Folder** — where the `.shader` is written (default: `Assets/Shaders/Converted`).  
   Click **…** to browse.

6. Click **Convert Shader**.  
   The shader is saved, the AssetDatabase refreshed, and the file is highlighted in the Project window.

---

## How it works

```
Shadertoy URL
    │
    ▼  HTTP GET → Shadertoy public API
Raw GLSL source
    │
    ▼  GlslConverter.Transpile()   (pure C#, no external process)
ShaderLab text
    │
    ▼  File.WriteAllText + AssetDatabase.Refresh
Assets/Shaders/Converted/<ShaderName>.shader
```

### Conversion pipeline (GlslConverter.cs)

| Pass | What it does |
|------|-------------|
| 1 | Handles `#define`, `#version`, `#extension` and `precision` directives; extracts numeric `#define` values as shader properties |
| 2 | Word-level substitutions: GLSL types → HLSL types, simple function renames, built-in identifiers (`iTime`, `iChannel0`, …), precision-qualifier stripping, HLSL keyword escaping |
| 3 | Argument-aware function transformations: `atan(y,x)` → `atan2`, `texture(…)` → `tex2D`/`tex2Dbias`, `textureLod` → `tex2Dlod`, `texelFetch`, vector-comparison helpers (`lessThan`, `greaterThan`, …), single-value vector casts (`vec3(x)` → `((float3)x)`), matrix constructors (`mat2(…)` → `transpose(float2x2(…))`) |
| 4 | Logical-XOR operator: `^^` → `!=` (HLSL has no `^^`) |
| 5 | Adds `static` to global-scope variable declarations |
| 6 | Extracts top-level `const` variables as shader properties (when *Extract Properties* is on) |
| 7 | Transforms `mainImage(out vec4 fragColor, in vec2 fragCoord)` into a proper `frag()` entry point with gamma-correction |
| 8 | Restores preprocessor directives |
| 9 | Wraps everything in a ShaderLab template with Properties block, CGPROGRAM, compatibility macros, and vertex shader |

### Compatibility macros always injected

```hlsl
#define glsl_mod(x,y) (((x)-(y)*floor((x)/(y))))
#define texelFetch(ch, uv, lod) tex2Dlod(ch, ...)
#define textureLod(ch, uv, lod) tex2Dlod(ch, float4(uv, 0, lod))
#define iResolution float3(_Resolution, _Resolution, _Resolution)
#define iFrame      (floor(_Time.y / 60))
#define iDate       float4(2020, 6, 18, 30)
#define iSampleRate (44100)
#define iChannelTime       float4(_Time.y, ...)
#define iChannelResolution float4x4(...)
```

---

## GLSL → HLSL translation reference

### Types

| GLSL | HLSL |
|------|------|
| `vec2 / vec3 / vec4` | `float2 / float3 / float4` |
| `ivec2 / ivec3 / ivec4` | `int2 / int3 / int4` |
| `uvec2 / uvec3 / uvec4` | `uint2 / uint3 / uint4` |
| `bvec2 / bvec3 / bvec4` | `bool2 / bool3 / bool4` |
| `mat2 / mat3 / mat4` | `float2x2 / float3x3 / float4x4` |
| `samplerCube` | `samplerCUBE` |
| `mediump / highp / lowp` | *(stripped)* |

### Functions

| GLSL | HLSL |
|------|------|
| `mix` | `lerp` |
| `fract` | `frac` |
| `mod(x,y)` | `glsl_mod(x,y)` |
| `inversesqrt` | `rsqrt` |
| `atan(y,x)` | `atan2(y,x)` |
| `atan(x)` | `atan(x)` |
| `texture(ch,uv)` | `tex2D(ch,uv)` |
| `texture(ch,uv,bias)` | `tex2Dbias(ch,float4(uv,0,bias))` |
| `textureLod(ch,uv,lod)` | `tex2Dlod(ch,float4(uv,0,lod))` |
| `textureGrad` | `tex2Dgrad` |
| `textureCube` | `texCUBE` |
| `dFdx / dFdy` | `ddx / ddy` |
| `dFdxFine / dFdyFine` | `ddx_fine / ddy_fine` |
| `floatBitsToInt` | `asint` |
| `floatBitsToUint` | `asuint` |
| `intBitsToFloat / uintBitsToFloat` | `asfloat` |
| `lessThan(a,b)` | `(a < b)` |
| `greaterThan(a,b)` | `(a > b)` |
| `lessThanEqual(a,b)` | `(a <= b)` |
| `greaterThanEqual(a,b)` | `(a >= b)` |
| `equal(a,b)` | `(a == b)` |
| `notEqual(a,b)` | `(a != b)` |
| `not(a)` | `(!a)` |

### Built-in identifiers

| Shadertoy | Unity |
|-----------|-------|
| `iTime` | `_Time.y` |
| `iTimeDelta` | `unity_DeltaTime.x` |
| `iChannel0 … iChannel3` | `_MainTex … _FourthTex` |
| `iMouse` | `_Mouse` |
| `gl_FragCoord` | `(vertex_output.uv * _Resolution)` |
| `iResolution` | `float3(_Resolution, _Resolution, _Resolution)` *(macro)* |

---

## Known limitations

| Issue | Workaround |
|-------|-----------|
| **Matrix `*` not always wrapped in `mul()`** — without full type analysis the converter may miss matrix multiplications | Add explicit `mul(a,b)` calls where the result is wrong |
| **Complex macro bodies** are not re-parsed for type information | Manually adjust generated code |
| **`out` parameters** in helper functions are not auto-initialised to zero | Add `param = 0;` at the top of functions that have `out` parameters |
| **Geometry / compute shaders** are not handled | Only `mainImage` fragment shaders are supported |
| **`^^` (logical XOR)** is replaced with `!=`; valid only for boolean operands | Use `a != b` directly in the source |

---

## Suggested improvements (future work)

1. **Full type tracking** — propagate declared types through assignments so matrix `*` can always be detected and wrapped in `mul()`.
2. **`out` parameter zero-initialisation** — scan function signatures and insert `param = (type)0;` automatically.
3. **Multi-pass / buffer shaders** — Shadertoy supports up to 4 render passes; currently only `mainImage` is converted.
4. **Real-time `iDate`** — replace the hard-coded `float4(2020,6,18,30)` with a C# injection of the current UTC date at conversion time.
5. **URP / HDRP templates** — the current template targets the legacy built-in render pipeline; add URP (`HLSLPROGRAM` / `UnityPerMaterial`) variants.
6. **Preview window** — render a small live preview of the converted shader directly inside the Editor window using `Graphics.Blit`.
7. **Swizzle alias normalisation** — `.stpq` swizzle aliases are not yet remapped to `.xyzw`; can be done with a post-pass that detects `.` followed only by `s/t/p/q` characters.
8. **`iResolution` accuracy** — currently `_Resolution` is a single float (assumes square); expose `_ResolutionX` / `_ResolutionY` for non-square render targets.

---

## Troubleshooting

| Symptom | Solution |
|---------|----------|
| *"Shadertoy API error: Shader not found"* | Check the shader is **public** on Shadertoy |
| Shader compiles but renders incorrectly | Try toggling *Raymarch Mode*; check for matrix `*` operators that need `mul()` |
| Conversion output is empty / wrong | The GLSL may not contain a `mainImage` function — paste only the fragment shader body |
| Properties not appearing | Enable *Extract Properties* and make sure values are simple numeric literals |

---

## Tested on

- Unity 2022 LTS (Windows / macOS)
- Unity 2021 LTS (Windows)

---

## License

Same as the parent repository — see [LICENSE](../LICENSE).
