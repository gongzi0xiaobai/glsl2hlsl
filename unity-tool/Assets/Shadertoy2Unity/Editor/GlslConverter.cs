// GlslConverter.cs
// Self-contained GLSL (Shadertoy) → Unity ShaderLab converter.
// No external binary required – all transformation is done in C#.
//
// Approach: tokenise-then-transform.
//   Pass 1  – line-by-line preprocessor handling (#define / #version / #extension / precision)
//   Pass 2  – code-only simple substitutions (types, simple function renames, built-in idents)
//   Pass 3  – complex function-call transformations (atan, texture*, constructors, vec-comparisons)
//   Pass 4  – operator fixes (^^ → !=)
//   Pass 5  – add 'static' to global-scope variable declarations
//   Pass 6  – extract const globals as shader properties
//   Pass 7  – transform mainImage() into a fragment shader entry point
//   Pass 8  – restore preprocessor placeholder lines
//   Pass 9  – wrap everything in the ShaderLab template
//
// Known limitations vs. a full AST-based transpiler:
//   • Matrix '*' → mul() wrapping relies on type-declaration tracking; deeply aliased
//     matrix types may be missed.  Add explicit mul() calls where needed.
//   • Preprocessor macro bodies are not re-parsed for type information.
//   • Some exotic GLSL 4.x features (geometry shaders, compute, etc.) are not handled.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shadertoy2Unity
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Data types
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A shader property extracted from the GLSL source.</summary>
    public class ShaderProp
    {
        public string Name     { get; set; }
        public string PropType { get; set; }  // "Float", "Vector", "Color", "Range(0,1)"
        public string HlslType { get; set; }  // "float", "float4", …
        public string Default  { get; set; }  // value for the Properties block
        public bool   Toggle   { get; set; }  // prepend [ToggleUI]
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  GlslConverter
    // ─────────────────────────────────────────────────────────────────────────────

    public static class GlslConverter
    {
        // ══════════════════════════════════════════════════════════════════════════
        //  Mapping tables
        // ══════════════════════════════════════════════════════════════════════════

        // Types – ordered longest-first so mat4x4 matches before mat4, etc.
        private static readonly (string from, string to)[] s_typeMap =
        {
            // double vectors
            ("dvec4","double4"), ("dvec3","double3"), ("dvec2","double2"),
            // bool vectors
            ("bvec4","bool4"),   ("bvec3","bool3"),   ("bvec2","bool2"),
            // int vectors
            ("ivec4","int4"),    ("ivec3","int3"),     ("ivec2","int2"),
            // uint vectors
            ("uvec4","uint4"),   ("uvec3","uint3"),    ("uvec2","uint2"),
            // float vectors
            ("vec4","float4"),   ("vec3","float3"),    ("vec2","float2"),
            // double matrices
            ("dmat4","double4x4"),("dmat3","double3x3"),("dmat2","double2x2"),
            // float matrices (specific before general)
            ("mat4x4","float4x4"),("mat4x3","float4x3"),("mat4x2","float4x2"),
            ("mat3x4","float3x4"),("mat3x3","float3x3"),("mat3x2","float3x2"),
            ("mat2x4","float2x4"),("mat2x3","float2x3"),("mat2x2","float2x2"),
            ("mat4","float4x4"),  ("mat3","float3x3"),  ("mat2","float2x2"),
            // samplers
            ("samplerCube","samplerCUBE"),
            ("sampler2DShadow","sampler2D"),   // approximate
            ("sampler3D","sampler3D"),
        };

        // Simple function renames (no argument-count sensitivity needed).
        // NOTE: 'atan' and 'texture'/'textureLod' are handled in the complex pass.
        private static readonly Dictionary<string,string> s_funcRenames =
            new Dictionary<string,string>
        {
            {"mix",             "lerp"},
            {"fract",           "frac"},
            {"inversesqrt",     "rsqrt"},
            {"dFdxFine",        "ddx_fine"},
            {"dFdyFine",        "ddy_fine"},
            {"dFdxCoarse",      "ddx"},
            {"dFdyCoarse",      "ddy"},
            {"dFdx",            "ddx"},
            {"dFdy",            "ddy"},
            {"floatBitsToInt",  "asint"},
            {"floatBitsToUint", "asuint"},        // fix: missing in original Rust code
            {"intBitsToFloat",  "asfloat"},
            {"uintBitsToFloat", "asfloat"},
            {"refrac",          "refract"},       // common GLSL typo
            {"textureGrad",     "tex2Dgrad"},
            {"texture2DGrad",   "tex2Dgrad"},
            {"textureCube",     "texCUBE"},
            {"texture2D",       "tex2D"},
        };

        // Built-in Shadertoy identifiers → Unity equivalents.
        private static readonly Dictionary<string,string> s_builtinIdents =
            new Dictionary<string,string>
        {
            {"iTime",       "_Time.y"},
            {"iTimeDelta",  "unity_DeltaTime.x"},
            {"iChannel0",   "_MainTex"},
            {"iChannel1",   "_SecondTex"},
            {"iChannel2",   "_ThirdTex"},
            {"iChannel3",   "_FourthTex"},
            {"gl_FragCoord","(vertex_output.uv * _Resolution)"},
            {"iMouse",      "_Mouse"},
        };

        // GLSL identifiers that clash with HLSL keywords → append underscore.
        private static readonly HashSet<string> s_hlslKeywords = new HashSet<string>
        {
            "line","lineadj","linear","pass","Buffer","ByteAddressBuffer","BlendState",
            "AppendStructuredBuffer","asm","asm_fragment","cbuffer","centroid","class",
            "column_major","compile","compile_fragment","CompileShader","ComputeShader",
            "ConsumeStructuredBuffer","DepthStencilState","DepthStencilView","DomainShader",
            "dword","export","extern","fxgroup","GeometryShader","groupshared","HullShader",
            "inline","InputPatch","interface","LineStream","matrix","min16float","min10float",
            "min16int","min12int","min16uint","namespace","nointerpolation","noperspective",
            "NULL","OutputPatch","packoffset","pixelfragment","PixelShader","point",
            "PointStream","precise","RasterizerState","RenderTargetView","register",
            "row_major","RWBuffer","RWByteAddressBuffer","RWStructuredBuffer","RWTexture1D",
            "RWTexture1DArray","RWTexture2D","RWTexture2DArray","RWTexture3D","sample",
            "sampler","SamplerState","SamplerComparisonState","shared","snorm","stateblock",
            "stateblock_state","string","StructuredBuffer","tbuffer","technique","technique10",
            "technique11","Texture1D","Texture1DArray","Texture2D","Texture2DArray",
            "Texture2DMS","Texture2DMSArray","Texture3D","TextureCube","TextureCubeArray",
            "typedef","triangle","triangleadj","TriangleStream","unorm","unsigned","vector",
            "vertexfragment","VertexShader","volatile",
        };

        // HLSL vector types (name → component count) – used for constructor detection.
        private static readonly Dictionary<string,int> s_vecTypes =
            new Dictionary<string,int>
        {
            {"bool2",2},{"bool3",3},{"bool4",4},
            {"int2",2}, {"int3",3}, {"int4",4},
            {"uint2",2},{"uint3",3},{"uint4",4},
            {"double2",2},{"double3",3},{"double4",4},
            {"float2",2},{"float3",3},{"float4",4},
        };

        // HLSL matrix types (name → rows,cols) – used for constructor detection.
        private static readonly Dictionary<string,(int r,int c)> s_matTypes =
            new Dictionary<string,(int,int)>
        {
            {"float2x2",(2,2)},{"float2x3",(2,3)},{"float2x4",(2,4)},
            {"float3x2",(3,2)},{"float3x3",(3,3)},{"float3x4",(3,4)},
            {"float4x2",(4,2)},{"float4x3",(4,3)},{"float4x4",(4,4)},
            {"double2x2",(2,2)},{"double3x3",(3,3)},{"double4x4",(4,4)},
        };

        // Vector comparison functions: lessThan(a,b) → (a < b), etc.
        private static readonly Dictionary<string,(string op, int arity)> s_vecCmp =
            new Dictionary<string,(string,int)>
        {
            {"lessThan",        ("<",  2)},
            {"lessThanEqual",   ("<=", 2)},
            {"greaterThan",     (">",  2)},
            {"greaterThanEqual",(">=", 2)},
            {"equal",           ("==", 2)},
            {"notEqual",        ("!=", 2)},
            {"not",             ("!",  1)},
        };

        // ══════════════════════════════════════════════════════════════════════════
        //  Public entry point
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert Shadertoy GLSL to Unity ShaderLab.
        /// </summary>
        /// <param name="glsl">Raw GLSL source (the code you paste into Shadertoy).</param>
        /// <param name="extractProps">
        ///   When true, <c>#define</c> macros with numeric values and top-level
        ///   <c>const</c> variables are exposed as ShaderLab Properties.
        /// </param>
        /// <param name="raymarch">
        ///   When true, the fragment shader receives world-space ray-origin /
        ///   ray-direction from a matching vertex shader so the shader can march
        ///   against mesh geometry.
        /// </param>
        /// <param name="shaderName">Name placed in the <c>Shader "…"</c> header.</param>
        /// <returns>Complete ShaderLab (.shader) file text.</returns>
        public static string Transpile(
            string glsl,
            bool   extractProps,
            bool   raymarch,
            string shaderName = "Converted")
        {
            if (string.IsNullOrWhiteSpace(glsl))
                return "// Empty GLSL input";

            // ── Pass 1 ─ preprocessor ─────────────────────────────────────────────
            var preDefs = new Dictionary<int,string>();
            var props   = new List<ShaderProp>();
            string body = HandlePreprocessorLines(glsl, extractProps, preDefs, props);

            // ── Pass 2 ─ simple word-level substitutions ──────────────────────────
            string hlsl = ApplyToCode(body, src =>
            {
                src = ApplyTypeSubstitutions(src);
                src = ApplySimpleFuncRenames(src);
                src = ApplyBuiltinIdents(src);
                src = StripQualifiers(src);
                src = EscapeHlslKeywords(src);
                return src;
            });

            // ── Pass 3 ─ complex function-call transformations ────────────────────
            hlsl = TransformComplexFunctions(hlsl);

            // ── Pass 4 ─ logical-XOR operator ────────────────────────────────────
            // GLSL '^^ ' is logical XOR (booleans only); HLSL has no '^^'.
            // '!= ' is the correct HLSL equivalent for boolean XOR.
            hlsl = ApplyToCode(hlsl, src => src.Replace("^^", "!="));

            // ── Pass 5 ─ add 'static' to global variable declarations ─────────────
            hlsl = AddStaticToGlobals(hlsl, props);

            // ── Pass 6 ─ extract top-level const variables as properties ──────────
            if (extractProps)
                hlsl = ExtractConstProps(hlsl, props);

            // ── Pass 7 ─ transform mainImage() ───────────────────────────────────
            hlsl = TransformMainImage(hlsl, raymarch);

            // ── Pass 8 ─ restore preprocessor defs ───────────────────────────────
            hlsl = RestorePreprocessorDefs(hlsl, preDefs);

            // ── Pass 9 ─ ShaderLab wrapper ────────────────────────────────────────
            return BuildShaderLab(hlsl, props, raymarch, shaderName);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 1 – preprocessor
        // ══════════════════════════════════════════════════════════════════════════

        private static string HandlePreprocessorLines(
            string                    src,
            bool                      extractProps,
            Dictionary<int,string>    defs,
            List<ShaderProp>          props)
        {
            var sb    = new StringBuilder();
            var lines = src.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string raw     = lines[i];
                string trimmed = raw.TrimStart();

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    // Collect logical line (handle backslash continuation)
                    string logical = raw.TrimEnd();
                    int    li      = i;
                    while (logical.EndsWith("\\") && li + 1 < lines.Length)
                    {
                        logical = logical.Substring(0, logical.Length - 1) + lines[++li];
                        i = li;
                    }

                    string directive = logical.TrimStart();

                    // Strip #version / #extension
                    if (IsDirective(directive, "version") || IsDirective(directive, "extension"))
                    {
                        defs[i] = "// " + directive + "\n";
                        sb.AppendFormat("float __PREPROC{0}__;\n", i);
                        continue;
                    }

                    // Handle #define
                    if (IsDirective(directive, "define"))
                    {
                        string replacement = directive; // default: keep as-is
                        if (extractProps)
                        {
                            ShaderProp p = TryExtractDefine(directive);
                            if (p != null)
                            {
                                props.Add(p);
                                // Replace with a typed variable declaration so the name
                                // stays valid in the body but the value comes from the property.
                                replacement = p.HlslType + " " + p.Name + ";\n";
                            }
                        }
                        defs[i] = replacement + "\n";
                        sb.AppendFormat("float __PREPROC{0}__;\n", i);
                        continue;
                    }

                    // All other directives (#ifdef, #if, #else, #endif, #pragma, etc.) – keep
                    defs[i] = directive + "\n";
                    sb.AppendFormat("float __PREPROC{0}__;\n", i);
                    continue;
                }

                // Strip 'precision …;' statements (GLSL precision qualifiers, no HLSL equivalent)
                if (trimmed.StartsWith("precision ", StringComparison.Ordinal) && trimmed.EndsWith(";"))
                {
                    sb.AppendLine("// " + trimmed);
                    continue;
                }

                sb.Append(raw);
                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static bool IsDirective(string line, string name)
        {
            // line already TrimStart'd and starts with '#'
            int pos = 1;
            while (pos < line.Length && line[pos] == ' ') pos++;
            return line.Substring(pos).StartsWith(name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Try to extract a ShaderProp from a <c>#define NAME VALUE</c> directive.
        /// Returns null when the value is not a simple numeric / vector literal.
        /// </summary>
        private static ShaderProp TryExtractDefine(string directive)
        {
            // Normalise whitespace after #define
            var m = Regex.Match(directive,
                @"^#\s*define\s+([A-Za-z_]\w*)\s+(.+)$",
                RegexOptions.Singleline);
            if (!m.Success) return null;

            string name = m.Groups[1].Value;
            string val  = m.Groups[2].Value.Trim();

            // Single numeric literal?
            if (TryParseNumericLiteral(val, out float fv))
            {
                bool isBool = (fv == 0f || fv == 1f) && !val.Contains(".");
                return new ShaderProp
                {
                    Name     = name,
                    PropType = "Float",
                    HlslType = "float",
                    Default  = fv.ToString(CultureInfo.InvariantCulture),
                    Toggle   = isBool,
                };
            }

            // Vector constructor? e.g. vec3(0.1, 0.2, 0.3)  or  float4(…)
            var vm = Regex.Match(val, @"^(?:vec|float|double|ivec|int|uvec|uint|bvec|bool)([234])\s*\((.+)\)$");
            if (vm.Success)
            {
                int  n       = int.Parse(vm.Groups[1].Value);
                var  numStrs = SplitArgs(vm.Groups[2].Value);
                if (numStrs.Count == n)
                {
                    var floats = new List<float>();
                    bool ok = true;
                    foreach (var ns in numStrs)
                    {
                        if (TryParseNumericLiteral(ns.Trim(), out float nf))
                            floats.Add(nf);
                        else { ok = false; break; }
                    }
                    if (ok)
                    {
                        // Pad to at least 4 components for Unity Vector property
                        while (floats.Count < 4) floats.Add(0f);
                        string defVal = $"({floats[0].ToString(CultureInfo.InvariantCulture)}," +
                                        $"{floats[1].ToString(CultureInfo.InvariantCulture)}," +
                                        $"{floats[2].ToString(CultureInfo.InvariantCulture)}," +
                                        $"{floats[3].ToString(CultureInfo.InvariantCulture)})";
                        return new ShaderProp
                        {
                            Name     = name,
                            PropType = "Vector",
                            HlslType = $"float{n}",
                            Default  = defVal,
                            Toggle   = false,
                        };
                    }
                }
            }

            return null;
        }

        private static bool TryParseNumericLiteral(string s, out float v)
        {
            // Strip suffixes: f, F, u, U, lf, LF
            string clean = Regex.Replace(s, @"[uUfFlL]+$", "");
            // Handle unary minus
            return float.TryParse(clean,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out v);
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 2 helpers – word-level substitutions
        // ══════════════════════════════════════════════════════════════════════════

        private static string ApplyTypeSubstitutions(string src)
        {
            foreach (var (from, to) in s_typeMap)
                src = WordReplace(src, from, to);
            return src;
        }

        private static string ApplySimpleFuncRenames(string src)
        {
            // Only rename when the identifier is immediately followed by '('
            foreach (var kv in s_funcRenames)
                src = Regex.Replace(src, $@"\b{Regex.Escape(kv.Key)}\b(?=\s*\()", kv.Value);
            return src;
        }

        private static string ApplyBuiltinIdents(string src)
        {
            foreach (var kv in s_builtinIdents)
                src = WordReplace(src, kv.Key, kv.Value);
            return src;
        }

        private static string StripQualifiers(string src)
        {
            // Remove storage qualifiers that have no meaning in Unity CG/HLSL globals
            // 'uniform' – variables become shader properties / global uniforms automatically
            src = Regex.Replace(src, @"\buniform\b\s*", "");
            // Precision qualifiers
            src = Regex.Replace(src, @"\b(?:mediump|highp|lowp)\b\s*", "");
            // Old GLSL interpolation qualifiers
            src = Regex.Replace(src, @"\b(?:attribute|varying)\b\s*", "");
            return src;
        }

        private static string EscapeHlslKeywords(string src)
        {
            foreach (var kw in s_hlslKeywords)
                src = WordReplace(src, kw, kw + "_");
            return src;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 3 – complex function-call transformations
        // ══════════════════════════════════════════════════════════════════════════

        private static string TransformComplexFunctions(string src)
        {
            // Walk through the code character-by-character, passing over comments /
            // strings untouched and scanning for function-call patterns inside code.
            var    result = new StringBuilder(src.Length + 256);
            int    i      = 0;

            while (i < src.Length)
            {
                // Pass comments verbatim
                if (i + 1 < src.Length && src[i] == '/' && src[i+1] == '/')
                {
                    int end = src.IndexOf('\n', i);
                    if (end < 0) end = src.Length;
                    result.Append(src, i, end - i);
                    i = end;
                    continue;
                }
                if (i + 1 < src.Length && src[i] == '/' && src[i+1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) end = src.Length - 2; else end += 2;
                    result.Append(src, i, end - i);
                    i = end;
                    continue;
                }
                // Pass string literals verbatim
                if (src[i] == '"')
                {
                    result.Append(src[i]); i++;
                    while (i < src.Length && src[i] != '"')
                    {
                        if (src[i] == '\\') { result.Append(src[i]); i++; }
                        if (i < src.Length) { result.Append(src[i]); i++; }
                    }
                    if (i < src.Length) { result.Append(src[i]); i++; }
                    continue;
                }

                // Collect an identifier
                if (char.IsLetter(src[i]) || src[i] == '_')
                {
                    int start = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_'))
                        i++;
                    string ident = src.Substring(start, i - start);

                    // Peek past whitespace to see if '(' follows
                    int j = i;
                    while (j < src.Length && (src[j] == ' ' || src[j] == '\t')) j++;

                    if (j < src.Length && src[j] == '(')
                    {
                        // Parse arguments
                        var args = ParseCallArgs(src, j, out int argsEnd);

                        string replacement = TransformFunctionCall(ident, args, src, j);
                        if (replacement != null)
                        {
                            result.Append(replacement);
                            i = argsEnd;
                            continue;
                        }
                    }

                    result.Append(ident);
                    continue;
                }

                result.Append(src[i]); i++;
            }

            return result.ToString();
        }

        /// <summary>
        /// Given a function name and its already-converted argument list, return
        /// the HLSL equivalent string, or null to keep the original emission.
        /// </summary>
        private static string TransformFunctionCall(
            string       name,
            List<string> args,
            string       src,
            int          openParen)
        {
            // ── atan overload ────────────────────────────────────────────────────
            if (name == "atan")
            {
                if (args.Count == 2)
                    return $"atan2({args[0]}, {args[1]})";
                // 1-arg form stays as atan
                return args.Count == 1 ? $"atan({args[0]})" : null;
            }

            // ── texture / texture2D ──────────────────────────────────────────────
            if (name == "texture" || name == "texture2D")
            {
                if (args.Count >= 2)
                {
                    // 3rd arg is an optional LOD bias
                    return args.Count >= 3
                        ? $"tex2Dbias({args[0]}, float4({args[1]}, 0, {args[2]}))"
                        : $"tex2D({args[0]}, {args[1]})";
                }
                return null;
            }

            // ── textureLod ───────────────────────────────────────────────────────
            if (name == "textureLod" || name == "texture2DLod" || name == "tex2DLod")
            {
                if (args.Count == 3)
                    return $"tex2Dlod({args[0]}, float4({args[1]}, 0, {args[2]}))";
                return null;
            }

            // ── texelFetch ───────────────────────────────────────────────────────
            if (name == "texelFetch")
            {
                // texelFetch(ch, ivec2, lod) → tex2Dlod approximation
                if (args.Count >= 2)
                {
                    string lod = args.Count >= 3 ? args[2] : "0";
                    return $"tex2Dlod({args[0]}, float4(({args[1]}) * {args[0]}_TexelSize.xy + {args[0]}_TexelSize.xy * 0.5, 0, {lod}))";
                }
                return null;
            }

            // ── vector comparison functions ──────────────────────────────────────
            if (s_vecCmp.TryGetValue(name, out var cmp))
            {
                if (cmp.arity == 2 && args.Count >= 2)
                    return $"(({args[0]}) {cmp.op} ({args[1]}))";
                if (cmp.arity == 1 && args.Count >= 1)
                    return $"({cmp.op}({args[0]}))";
                return null;
            }

            // ── single-value vector constructors  e.g. float3(x) → ((float3)x) ─
            if (s_vecTypes.TryGetValue(name, out int vecN) && args.Count == 1)
                return $"(({name}){args[0]})";

            // ── matrix constructors ──────────────────────────────────────────────
            // In GLSL matrices are column-major; HLSL is row-major → need transpose()
            // when all elements are supplied.
            if (s_matTypes.TryGetValue(name, out var matDim))
            {
                int totalElems = matDim.r * matDim.c;
                if (args.Count == totalElems)
                    return $"transpose({name}({string.Join(", ", args)}))";
                // single-value broadcast
                if (args.Count == 1)
                    return $"(({name}){args[0]})";
            }

            // ── mod → glsl_mod ───────────────────────────────────────────────────
            // (already handled as simple rename, but tex2D conflicts can happen if not)
            if (name == "mod")
                return $"glsl_mod({string.Join(", ", args)})";

            return null; // no special handling needed
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 5 – add 'static' to global variable declarations
        // ══════════════════════════════════════════════════════════════════════════

        private static string AddStaticToGlobals(string src, List<ShaderProp> props)
        {
            // Build set of property names (they should not receive 'static')
            var propNames = new HashSet<string>();
            foreach (var p in props) propNames.Add(p.Name);

            var    result    = new StringBuilder(src.Length);
            int    braceDepth = 0;
            int    i         = 0;

            while (i < src.Length)
            {
                // Track brace depth (skip strings/comments first)
                char c = src[i];

                // Line comment
                if (c == '/' && i + 1 < src.Length && src[i+1] == '/')
                {
                    int end = src.IndexOf('\n', i); if (end < 0) end = src.Length;
                    result.Append(src, i, end - i); i = end; continue;
                }
                // Block comment
                if (c == '/' && i + 1 < src.Length && src[i+1] == '*')
                {
                    int end = src.IndexOf("*/", i+2, StringComparison.Ordinal);
                    if (end < 0) end = src.Length - 2; else end += 2;
                    result.Append(src, i, end - i); i = end; continue;
                }
                // String literal
                if (c == '"')
                {
                    result.Append(c); i++;
                    while (i < src.Length && src[i] != '"')
                    { if (src[i] == '\\') { result.Append(src[i]); i++; } result.Append(src[i]); i++; }
                    if (i < src.Length) { result.Append(src[i]); i++; }
                    continue;
                }

                if (c == '{') { braceDepth++; result.Append(c); i++; continue; }
                if (c == '}') { braceDepth--; result.Append(c); i++; continue; }

                // At global scope, look for a declaration start:
                // A newline (or start of string) followed by an identifier that is a
                // known HLSL type keyword, NOT preceded by 'static'.
                if (braceDepth == 0 && (c == '\n' || i == 0))
                {
                    int lineStart = i;
                    if (c == '\n') { result.Append(c); i++; lineStart = i; }

                    // Skip leading whitespace
                    int ws = i;
                    while (ws < src.Length && (src[ws] == ' ' || src[ws] == '\t')) ws++;

                    // Try to detect a variable declaration:
                    //   optional qualifiers, type keyword, identifier, ';' or '=' or '['
                    // We only insert 'static' when:
                    //  (a) not already starting with 'static '
                    //  (b) starts with a type-like word
                    //  (c) not a function definition (next non-whitespace after ident is not '(')
                    //  (d) not a struct definition
                    //  (e) not a blank / comment / preprocessor placeholder line
                    if (ws < src.Length && (char.IsLetter(src[ws]) || src[ws] == '_'))
                    {
                        // Read first word
                        int wEnd = ws;
                        while (wEnd < src.Length && (char.IsLetterOrDigit(src[wEnd]) || src[wEnd] == '_')) wEnd++;
                        string word = src.Substring(ws, wEnd - ws);

                        bool shouldAddStatic =
                            !word.Equals("static",    StringComparison.Ordinal) &&
                            !word.Equals("struct",    StringComparison.Ordinal) &&
                            !word.Equals("typedef",   StringComparison.Ordinal) &&
                            !word.Equals("void",      StringComparison.Ordinal) &&
                            !word.StartsWith("__PREPROC", StringComparison.Ordinal) &&
                            IsHlslTypeOrQualifier(word);

                        if (shouldAddStatic)
                        {
                            // Check the full declaration: skip qualifiers + type → identifier
                            // If what follows the ident is '(' it's a function definition
                            bool isFunc = DeclarationLooksLikeFunction(src, ws);
                            if (!isFunc)
                            {
                                // Don't add static to property variables
                                string declName = GetDeclaredName(src, ws);
                                if (declName == null || !propNames.Contains(declName))
                                {
                                    result.Append("static ");
                                }
                            }
                        }
                    }

                    // The characters from 'lineStart' to 'i' were already appended
                    // (whitespace is re-appended below in the general path)
                    continue;
                }

                result.Append(c); i++;
            }

            return result.ToString();
        }

        // Returns true if this declaration at `pos` (start of type word) looks like
        // a function definition / prototype rather than a variable.
        private static bool DeclarationLooksLikeFunction(string src, int pos)
        {
            // Skip the whole declaration token by token looking for '(' before ';'
            int i = pos;
            int depth = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '(') return true;
                if (c == ';' || c == '=' || c == '{' || c == '\n') return false;
                i++;
            }
            return false;
        }

        // Extract the variable name from a declaration starting at pos.
        private static string GetDeclaredName(string src, int pos)
        {
            // Skip qualifiers / type words until we find the final identifier before ';','=','['
            int i = pos;
            string lastName = null;
            while (i < src.Length)
            {
                char c = src[i];
                if (c == ';' || c == '=' || c == '[') return lastName;
                if (c == '(' || c == '{' || c == '\n') return null;
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    lastName = src.Substring(s, i - s);
                    continue;
                }
                i++;
            }
            return lastName;
        }

        private static bool IsHlslTypeOrQualifier(string w)
        {
            // Common HLSL/CG type starters + qualifiers that can begin a global declaration
            switch (w)
            {
                case "float": case "double": case "int": case "uint": case "bool":
                case "half":
                case "float2": case "float3": case "float4":
                case "float2x2": case "float3x3": case "float4x4":
                case "float2x3": case "float2x4": case "float3x2":
                case "float3x4": case "float4x2": case "float4x3":
                case "int2": case "int3": case "int4":
                case "uint2": case "uint3": case "uint4":
                case "bool2": case "bool3": case "bool4":
                case "double2": case "double3": case "double4":
                case "sampler2D": case "samplerCUBE": case "sampler3D":
                case "const": case "uniform":
                    return true;
                default:
                    return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 6 – extract const globals as properties
        // ══════════════════════════════════════════════════════════════════════════

        private static string ExtractConstProps(string src, List<ShaderProp> props)
        {
            // Pattern: (static )? const <type> <name> = <value> ;
            var re = new Regex(
                @"(?:static\s+)?const\s+" +
                @"(float|double|int|uint|bool|float[234]|int[234]|uint[234]|bool[234])\s+" +
                @"([A-Za-z_]\w*)\s*=\s*([^;]+);",
                RegexOptions.Multiline);

            return ApplyToCode(src, code => re.Replace(code, m =>
            {
                string type  = m.Groups[1].Value;
                string vname = m.Groups[2].Value;
                string val   = m.Groups[3].Value.Trim();

                if (props.Exists(p => p.Name == vname)) return m.Value; // already extracted

                ShaderProp prop = BuildPropFromConst(type, vname, val);
                if (prop != null)
                {
                    props.Add(prop);
                    // Keep the declaration without initialiser so Unity auto-binds the property
                    return type + " " + vname + ";";
                }
                return m.Value;
            }));
        }

        private static ShaderProp BuildPropFromConst(string type, string name, string val)
        {
            bool isBool = type == "bool";

            if (type == "float" || type == "double" || type == "int" || type == "uint" || isBool)
            {
                if (TryParseNumericLiteral(val, out float fv))
                    return new ShaderProp
                    {
                        Name     = name,
                        PropType = "Float",
                        HlslType = type,
                        Default  = fv.ToString(CultureInfo.InvariantCulture),
                        Toggle   = isBool,
                    };
            }

            // Vector types: float3, int2, etc.
            var vm = Regex.Match(val, @"^(?:\w+)\s*\((.+)\)$");
            if (vm.Success)
            {
                var numStrs = SplitArgs(vm.Groups[1].Value);
                var floats  = new List<float>();
                bool ok = true;
                foreach (var ns in numStrs)
                    if (TryParseNumericLiteral(ns.Trim(), out float f)) floats.Add(f);
                    else { ok = false; break; }

                if (ok && floats.Count >= 2)
                {
                    while (floats.Count < 4) floats.Add(0f);
                    string dv = $"({floats[0].ToString(CultureInfo.InvariantCulture)}," +
                                $"{floats[1].ToString(CultureInfo.InvariantCulture)}," +
                                $"{floats[2].ToString(CultureInfo.InvariantCulture)}," +
                                $"{floats[3].ToString(CultureInfo.InvariantCulture)})";
                    return new ShaderProp
                    {
                        Name = name, PropType = "Vector", HlslType = type, Default = dv, Toggle = false,
                    };
                }
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 7 – mainImage() → fragment shader
        // ══════════════════════════════════════════════════════════════════════════

        private static string TransformMainImage(string src, bool raymarch)
        {
            // Match:  void mainImage ( [out] type fragColorName , [in] type fragCoordName )
            // Types may already be substituted (float4 / float2) or still be vec4/vec2.
            var re = new Regex(
                @"void\s+mainImage\s*\(\s*" +
                @"(?:out\s+)?(?:\w+)\s+(\w+)\s*,\s*" +
                @"(?:in\s+)?(?:\w+)\s+(\w+)\s*\)",
                RegexOptions.Singleline);

            var m = re.Match(src);
            if (!m.Success) return src;   // no mainImage found

            string fragColor = m.Groups[1].Value;
            string fragCoord = m.Groups[2].Value;

            // Extract the body: everything between the outermost { } of mainImage
            int bodyStart = src.IndexOf('{', m.Index + m.Length);
            if (bodyStart < 0) return src;

            int bodyEnd = FindMatchingBrace(src, bodyStart);
            if (bodyEnd < 0) return src;

            string body = src.Substring(bodyStart + 1, bodyEnd - bodyStart - 1);

            // Build replacement frag function
            var frag = new StringBuilder();
            string indent = "            "; // 3-level indent to match ShaderLab indentation

            if (raymarch)
            {
                frag.AppendLine($"{indent}float4 frag (v2f __vertex_output, float facing : VFACE) : SV_Target");
            }
            else
            {
                frag.AppendLine($"{indent}float4 frag (v2f __vertex_output) : SV_Target");
            }
            frag.AppendLine($"{indent}{{");
            frag.AppendLine($"{indent}    vertex_output = __vertex_output;");
            frag.AppendLine($"{indent}    float4 {fragColor} = 0;");
            frag.AppendLine($"{indent}    float2 {fragCoord} = vertex_output.uv * _Resolution;");
            frag.Append(body);
            frag.AppendLine($"{indent}    if (_GammaCorrect) {fragColor}.rgb = pow({fragColor}.rgb, 2.2);");
            frag.AppendLine($"{indent}    return {fragColor};");
            frag.Append($"{indent}}}");

            // Replace the original mainImage declaration + body
            return src.Substring(0, m.Index) +
                   frag.ToString() +
                   src.Substring(bodyEnd + 1);
        }

        /// <summary>Returns the index just past the '}' that matches the '{' at <paramref name="openPos"/>.</summary>
        private static int FindMatchingBrace(string src, int openPos)
        {
            int depth = 0;
            for (int i = openPos; i < src.Length; i++)
            {
                // Skip strings/comments to avoid counting braces inside them
                if (i + 1 < src.Length && src[i] == '/' && src[i+1] == '/')
                { while (i < src.Length && src[i] != '\n') i++; continue; }
                if (i + 1 < src.Length && src[i] == '/' && src[i+1] == '*')
                { i += 2; while (i + 1 < src.Length && !(src[i] == '*' && src[i+1] == '/')) i++; i++; continue; }
                if (src[i] == '"')
                { i++; while (i < src.Length && src[i] != '"') { if (src[i] == '\\') i++; i++; } continue; }

                if (src[i] == '{') depth++;
                else if (src[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 8 – restore preprocessor placeholders
        // ══════════════════════════════════════════════════════════════════════════

        private static string RestorePreprocessorDefs(string src, Dictionary<int,string> defs)
        {
            var sb = new StringBuilder(src.Length);
            foreach (string line in src.Split('\n'))
            {
                var trimmed = line.TrimStart();
                // Match placeholder:  [static] float __PREPROCn__;
                var pm = Regex.Match(trimmed, @"(?:static\s+)?float\s+__PREPROC(\d+)__\s*;");
                if (pm.Success && int.TryParse(pm.Groups[1].Value, out int idx) && defs.TryGetValue(idx, out string rep))
                {
                    sb.Append(rep.TrimStart());
                    // rep already ends with '\n' from HandlePreprocessorLines
                }
                else
                {
                    sb.Append(line);
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Pass 9 – ShaderLab wrapper
        // ══════════════════════════════════════════════════════════════════════════

        private static string BuildShaderLab(
            string           body,
            List<ShaderProp> props,
            bool             raymarch,
            string           shaderName)
        {
            var sb = new StringBuilder(4096);

            // ── Header ──────────────────────────────────────────────────────────
            sb.AppendLine($"Shader \"Converted/{shaderName}\"");
            sb.AppendLine("{");
            sb.AppendLine("    Properties");
            sb.AppendLine("    {");

            if (raymarch)
                sb.AppendLine("        [Header(General)]");

            sb.AppendLine("        _MainTex   (\"iChannel0\", 2D) = \"white\" {}");
            sb.AppendLine("        _SecondTex (\"iChannel1\", 2D) = \"white\" {}");
            sb.AppendLine("        _ThirdTex  (\"iChannel2\", 2D) = \"white\" {}");
            sb.AppendLine("        _FourthTex (\"iChannel3\", 2D) = \"white\" {}");
            sb.AppendLine("        _Mouse (\"Mouse\", Vector) = (0.5, 0.5, 0.5, 0.5)");
            sb.AppendLine("        [ToggleUI] _GammaCorrect (\"Gamma Correction\", Float) = 1");
            sb.AppendLine("        _Resolution (\"Resolution (Change if AA is bad)\", Range(1, 1024)) = 1");

            if (raymarch)
            {
                sb.AppendLine();
                sb.AppendLine("        [Header(Raymarching)]");
                sb.AppendLine("        [ToggleUI] _WorldSpace (\"World Space Marching\", Float) = 0");
                sb.AppendLine("        _Offset (\"Offset (W=Scale)\", Vector) = (0, 0, 0, 1)");
            }

            if (props.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        [Header(Extracted)]");
                foreach (var p in props)
                {
                    sb.Append("        ");
                    if (p.Toggle) sb.Append("[ToggleUI] ");
                    sb.AppendLine($"{p.Name} (\"{p.Name}\", {p.PropType}) = {p.Default}");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            if (raymarch) sb.AppendLine("            Cull Off");
            sb.AppendLine();
            sb.AppendLine("            CGPROGRAM");
            sb.AppendLine("            #pragma vertex vert");
            sb.AppendLine("            #pragma fragment frag");
            sb.AppendLine();
            sb.AppendLine("            #include \"UnityCG.cginc\"");
            sb.AppendLine();

            if (raymarch)
            {
                sb.AppendLine(RaymarchStructs());
            }
            else
            {
                sb.AppendLine(QuadStructs());
            }

            sb.AppendLine("            // Built-in properties");
            sb.AppendLine("            sampler2D _MainTex;   float4 _MainTex_TexelSize;");
            sb.AppendLine("            sampler2D _SecondTex; float4 _SecondTex_TexelSize;");
            sb.AppendLine("            sampler2D _ThirdTex;  float4 _ThirdTex_TexelSize;");
            sb.AppendLine("            sampler2D _FourthTex; float4 _FourthTex_TexelSize;");
            sb.AppendLine("            float4 _Mouse;");
            sb.AppendLine("            float  _GammaCorrect;");
            sb.AppendLine("            float  _Resolution;");
            if (raymarch)
            {
                sb.AppendLine("            float  _WorldSpace;");
                sb.AppendLine("            float4 _Offset;");
            }
            sb.AppendLine();
            sb.AppendLine("            // GLSL compatibility macros");
            sb.AppendLine("            #define glsl_mod(x,y) (((x)-(y)*floor((x)/(y))))");
            sb.AppendLine("            #define texelFetch(ch, uv, lod) tex2Dlod(ch, float4((uv).xy * ch##_TexelSize.xy + ch##_TexelSize.xy * 0.5, 0, lod))");
            sb.AppendLine("            #define textureLod(ch, uv, lod) tex2Dlod(ch, float4(uv, 0, lod))");
            sb.AppendLine("            #define iResolution float3(_Resolution, _Resolution, _Resolution)");
            sb.AppendLine("            #define iFrame (floor(_Time.y / 60))");
            sb.AppendLine("            #define iChannelTime float4(_Time.y, _Time.y, _Time.y, _Time.y)");
            sb.AppendLine("            #define iDate float4(2020, 6, 18, 30)");
            sb.AppendLine("            #define iSampleRate (44100)");
            sb.AppendLine("            #define iChannelResolution float4x4(                      \\");
            sb.AppendLine("                _MainTex_TexelSize.z,   _MainTex_TexelSize.w,   0, 0, \\");
            sb.AppendLine("                _SecondTex_TexelSize.z, _SecondTex_TexelSize.w, 0, 0, \\");
            sb.AppendLine("                _ThirdTex_TexelSize.z,  _ThirdTex_TexelSize.w,  0, 0, \\");
            sb.AppendLine("                _FourthTex_TexelSize.z, _FourthTex_TexelSize.w, 0, 0)");
            sb.AppendLine();
            sb.AppendLine("            // Global access to interpolated data");
            sb.AppendLine("            static v2f vertex_output;");
            sb.AppendLine();

            if (raymarch)
                sb.AppendLine(RaymarchVertexShader());
            else
                sb.AppendLine(QuadVertexShader());

            sb.AppendLine();
            sb.Append(body.TrimEnd());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("            ENDCG");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.Append("}");

            return sb.ToString();
        }

        private static string QuadStructs() => @"            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv     : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };
";

        private static string QuadVertexShader() => @"            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                return o;
            }";

        private static string RaymarchStructs() => @"            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv       : TEXCOORD0;
                float4 vertex   : SV_POSITION;
                float3 ro_w     : TEXCOORD1;
                float3 hitPos_w : TEXCOORD2;
            };
";

        private static string RaymarchVertexShader() => @"            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;

                if (_WorldSpace)
                {
                    o.ro_w     = _WorldSpaceCameraPos;
                    o.hitPos_w = mul(unity_ObjectToWorld, v.vertex);
                }
                else
                {
                    o.ro_w     = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1));
                    o.hitPos_w = v.vertex;
                }

                return o;
            }";

        // ══════════════════════════════════════════════════════════════════════════
        //  Utility helpers
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply <paramref name="transform"/> to each "real code" segment of
        /// <paramref name="src"/>, leaving comments and string literals unchanged.
        /// </summary>
        private static string ApplyToCode(string src, Func<string,string> transform)
        {
            // Split on comments and string literals, transform only non-comment parts.
            var re = new Regex(
                @"//[^\n]*" +             // line comment
                @"|/\*[\s\S]*?\*/" +      // block comment
                @"|""(?:[^""\\]|\\.)*""", // string literal
                RegexOptions.Singleline);

            var result = new StringBuilder(src.Length);
            int pos = 0;
            foreach (Match m in re.Matches(src))
            {
                if (m.Index > pos)
                    result.Append(transform(src.Substring(pos, m.Index - pos)));
                result.Append(m.Value);
                pos = m.Index + m.Length;
            }
            if (pos < src.Length)
                result.Append(transform(src.Substring(pos)));
            return result.ToString();
        }

        /// <summary>Whole-word replace.</summary>
        private static string WordReplace(string src, string from, string to)
            => Regex.Replace(src, $@"\b{Regex.Escape(from)}\b", to);

        /// <summary>
        /// Parse the comma-separated arguments of a function call.
        /// <paramref name="openParen"/> must point to the '(' character.
        /// <paramref name="endPos"/> is set to one past the closing ')'.
        /// </summary>
        private static List<string> ParseCallArgs(string src, int openParen, out int endPos)
        {
            var args    = new List<string>();
            var current = new StringBuilder();
            int depth   = 0;
            int i       = openParen + 1; // skip '('

            while (i < src.Length)
            {
                char c = src[i];

                // String literals inside arguments
                if (c == '"')
                {
                    current.Append(c); i++;
                    while (i < src.Length && src[i] != '"')
                    { if (src[i] == '\\') { current.Append(src[i]); i++; } current.Append(src[i]); i++; }
                    if (i < src.Length) { current.Append(src[i]); i++; }
                    continue;
                }
                // Line comment inside argument (rare but possible with macros)
                if (c == '/' && i+1 < src.Length && src[i+1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') { current.Append(src[i]); i++; }
                    continue;
                }
                // Block comment
                if (c == '/' && i+1 < src.Length && src[i+1] == '*')
                {
                    current.Append(src[i]); i++;
                    current.Append(src[i]); i++;
                    while (i+1 < src.Length && !(src[i] == '*' && src[i+1] == '/'))
                    { current.Append(src[i]); i++; }
                    if (i+1 < src.Length) { current.Append(src[i]); i++; current.Append(src[i]); i++; }
                    continue;
                }

                if (c == '(' || c == '[' || c == '{') { depth++; current.Append(c); i++; }
                else if ((c == ')' || c == ']' || c == '}') && depth > 0) { depth--; current.Append(c); i++; }
                else if (c == ')') // closing paren at depth 0
                {
                    string a = current.ToString().Trim();
                    if (a.Length > 0 || args.Count > 0) args.Add(a);
                    i++;
                    break;
                }
                else if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    i++;
                }
                else { current.Append(c); i++; }
            }

            endPos = i;
            // f() with no args → empty list
            if (args.Count == 1 && args[0] == "") args.Clear();
            return args;
        }

        /// <summary>Split a comma-separated string respecting nested parens.</summary>
        private static List<string> SplitArgs(string s)
        {
            var parts = new List<string>();
            var cur   = new StringBuilder();
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '(' || c == '[') { depth++; cur.Append(c); }
                else if (c == ')' || c == ']') { depth--; cur.Append(c); }
                else if (c == ',' && depth == 0) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
                else cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString().Trim());
            return parts;
        }
    }
}
