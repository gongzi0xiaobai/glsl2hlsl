using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Shadertoy2Unity
{
    /// <summary>
    /// Unity Editor window that converts Shadertoy GLSL shaders to Unity ShaderLab format.
    /// Conversion is performed entirely in C# — no external binary required.
    /// Open via: Window > Shadertoy2Unity
    /// </summary>
    public class Shadertoy2UnityWindow : EditorWindow
    {
        // ── Serialisable fields preserved across domain reloads ─────────────────
        [SerializeField] private int    _inputModeIndex = 0;
        [SerializeField] private string _shadertoyUrl   = "";
        [SerializeField] private string _glslCode       = "";
        [SerializeField] private string _shaderName     = "ConvertedShader";
        [SerializeField] private bool   _extractProps   = true;
        [SerializeField] private bool   _raymarch       = true;
        [SerializeField] private string _outputFolder   = "Assets/Shaders/Converted";

        // ── Runtime state ───────────────────────────────────────────────────────
        private Vector2 _glslScroll;
        private Vector2 _mainScroll;
        private string  _statusMessage = "";
        private bool    _isError       = false;

        private static readonly string[] k_InputModeLabels = { "From Shadertoy URL", "From GLSL Code" };
        private const string k_OutputFolderPref = "Shadertoy2Unity_OutputFolder";
        private const string k_DefaultApiKey    = "NtHtMm";

        // ── Menu entry ──────────────────────────────────────────────────────────
        [MenuItem("Window/Shadertoy2Unity")]
        public static void ShowWindow()
        {
            var win = GetWindow<Shadertoy2UnityWindow>(false, "Shadertoy2Unity", true);
            win.minSize = new Vector2(420, 520);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────
        private void OnEnable()
        {
            _outputFolder = EditorPrefs.GetString(k_OutputFolderPref, _outputFolder);
        }

        // ── GUI ─────────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            DrawHeader();
            EditorGUILayout.Space(6);

            DrawInputSection();
            EditorGUILayout.Space(6);

            DrawShaderNameField();
            EditorGUILayout.Space(4);

            DrawOptionsSection();
            EditorGUILayout.Space(4);

            DrawOutputSection();
            EditorGUILayout.Space(8);

            DrawConvertButton();

            DrawStatus();

            EditorGUILayout.EndScrollView();
        }

        // ── Section renderers ────────────────────────────────────────────────────
        private void DrawHeader()
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 14,
                alignment = TextAnchor.MiddleCenter,
            };
            GUILayout.Label("Shadertoy  →  Unity Shader", style);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        private void DrawInputSection()
        {
            _inputModeIndex = GUILayout.Toolbar(_inputModeIndex, k_InputModeLabels);
            EditorGUILayout.Space(4);

            if (_inputModeIndex == 0)   // URL mode
            {
                EditorGUILayout.LabelField("Shadertoy URL or Shader ID", EditorStyles.boldLabel);
                _shadertoyUrl = EditorGUILayout.TextField(_shadertoyUrl).Trim();
                EditorGUILayout.HelpBox(
                    "Examples:\n  https://www.shadertoy.com/view/XsBXWt\n  XsBXWt",
                    MessageType.None);
            }
            else                        // GLSL code mode
            {
                EditorGUILayout.LabelField("GLSL Source (mainImage)", EditorStyles.boldLabel);
                _glslScroll = EditorGUILayout.BeginScrollView(_glslScroll,
                    GUILayout.Height(180));
                _glslCode = EditorGUILayout.TextArea(_glslCode,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawShaderNameField()
        {
            _shaderName = EditorGUILayout.TextField("Shader Name", _shaderName);
        }

        private void DrawOptionsSection()
        {
            EditorGUILayout.LabelField("Conversion Options", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                _extractProps = EditorGUILayout.Toggle(
                    new GUIContent("Extract Properties",
                        "Detect #define macros and top-level const variables and expose " +
                        "them as shader properties in the Unity Inspector."),
                    _extractProps);

                _raymarch = EditorGUILayout.Toggle(
                    new GUIContent("Raymarch / Raytrace Mode",
                        "Make the shader work as a 3-D world-space raymarched shader on a mesh " +
                        "instead of a screen-space fullscreen quad."),
                    _raymarch);
            }
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.BeginHorizontal();
                _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string abs = EditorUtility.SaveFolderPanel(
                        "Select Output Folder",
                        Application.dataPath, "Converted");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        if (abs.StartsWith(Application.dataPath))
                            abs = "Assets" + abs.Substring(Application.dataPath.Length);
                        _outputFolder = abs;
                        EditorPrefs.SetString(k_OutputFolderPref, _outputFolder);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawConvertButton()
        {
            if (GUILayout.Button("Convert Shader", GUILayout.Height(36)))
                RunConversion();
        }

        private void DrawStatus()
        {
            if (string.IsNullOrEmpty(_statusMessage)) return;
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(_statusMessage,
                _isError ? MessageType.Error : MessageType.Info);
        }

        // ── Conversion pipeline ──────────────────────────────────────────────────
        private void RunConversion()
        {
            _statusMessage = "";
            _isError       = false;

            // 1. Obtain GLSL source
            string glsl;
            string nameHint = _shaderName;

            if (_inputModeIndex == 0)
            {
                string id = ExtractShadertoyId(_shadertoyUrl);
                if (string.IsNullOrEmpty(id))
                {
                    SetError("Could not extract a shader ID from the provided URL.\n" +
                             "Expected format: https://www.shadertoy.com/view/XXXXXXX  or just  XXXXXXX");
                    return;
                }

                try
                {
                    string jsonResponse = FetchUrl(
                        $"https://www.shadertoy.com/api/v1/shaders/{id}?key={k_DefaultApiKey}");
                    glsl     = ParseGlslFromApiJson(jsonResponse, out nameHint);
                    nameHint = string.IsNullOrEmpty(_shaderName) ? nameHint : _shaderName;
                }
                catch (Exception ex)
                {
                    SetError($"Failed to download shader from Shadertoy:\n{ex.Message}");
                    return;
                }
            }
            else
            {
                glsl = _glslCode;
                if (string.IsNullOrEmpty(glsl))
                {
                    SetError("GLSL code is empty. Paste your Shadertoy mainImage code.");
                    return;
                }
            }

            // 2. Convert using the built-in GlslConverter (no external binary needed)
            string shaderContent;
            try
            {
                shaderContent = GlslConverter.Transpile(
                    glsl, _extractProps, _raymarch,
                    string.IsNullOrWhiteSpace(nameHint) ? "Converted" : nameHint);
            }
            catch (Exception ex)
            {
                SetError($"Conversion failed:\n{ex.Message}");
                return;
            }

            // 3. Save to project
            try
            {
                SaveShaderToProject(shaderContent, nameHint);
            }
            catch (Exception ex)
            {
                SetError($"Failed to save shader file:\n{ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string ExtractShadertoyId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            var match = Regex.Match(input, @"shadertoy\.com/view/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;

            if (Regex.IsMatch(input, @"^[A-Za-z0-9]{4,16}$"))
                return input;

            return null;
        }

        private static string FetchUrl(string url)
        {
#pragma warning disable SYSLIB0014
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
#pragma warning restore SYSLIB0014
        }

        private static string ParseGlslFromApiJson(string json, out string shaderName)
        {
            shaderName = "ConvertedShader";

            var errMatch = Regex.Match(json, "\"Error\"\\s*:\\s*\"([^\"]+)\"");
            if (errMatch.Success)
                throw new InvalidOperationException($"Shadertoy API error: {errMatch.Groups[1].Value}");

            var nameMatch = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (nameMatch.Success)
                shaderName = SanitiseFileName(nameMatch.Groups[1].Value);

            int codeIdx = json.IndexOf("\"code\"", StringComparison.Ordinal);
            if (codeIdx < 0)
                throw new InvalidOperationException("Could not find 'code' field in Shadertoy API response.");

            int colonIdx = json.IndexOf(':', codeIdx + 6);
            if (colonIdx < 0)
                throw new InvalidOperationException("Malformed Shadertoy API response.");

            int start = colonIdx + 1;
            while (start < json.Length && json[start] != '"') start++;
            if (start >= json.Length)
                throw new InvalidOperationException("Could not find code string in Shadertoy API response.");

            start++;
            var sb = new StringBuilder();
            int i  = start;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"':  sb.Append('"');  i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/':  sb.Append('/');  i += 2; continue;
                        case 'n':  sb.Append('\n'); i += 2; continue;
                        case 'r':  sb.Append('\r'); i += 2; continue;
                        case 't':  sb.Append('\t'); i += 2; continue;
                        case 'u':
                            if (i + 5 < json.Length)
                            {
                                string hex = json.Substring(i + 2, 4);
                                if (int.TryParse(hex,
                                        System.Globalization.NumberStyles.HexNumber,
                                        null, out int code))
                                { sb.Append((char)code); i += 6; continue; }
                            }
                            break;
                    }
                }
                else if (c == '"') break;
                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private void SaveShaderToProject(string content, string shaderName)
        {
            string absFolder;
            string folder = _outputFolder;

            if (folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                absFolder = Path.Combine(Application.dataPath, folder.Substring("Assets/".Length));
            else if (string.Equals(folder, "Assets", StringComparison.OrdinalIgnoreCase))
                absFolder = Application.dataPath;
            else
                absFolder = folder;

            Directory.CreateDirectory(absFolder);

            string fileName = SanitiseFileName(shaderName);
            if (!fileName.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                fileName += ".shader";

            string fullPath = Path.Combine(absFolder, fileName);

            if (File.Exists(fullPath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "File Exists",
                    $"A shader file already exists at:\n{fullPath}\n\nOverwrite it?",
                    "Overwrite", "Cancel");
                if (!overwrite) return;
            }

            File.WriteAllText(fullPath, content, Encoding.UTF8);
            AssetDatabase.Refresh();

            string relPath = "Assets" + fullPath.Substring(Application.dataPath.Length);
            SetInfo($"Shader saved to:\n{relPath}");

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relPath);
            if (asset != null) EditorGUIUtility.PingObject(asset);
        }

        private static string SanitiseFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        private void SetError(string msg) { _statusMessage = msg; _isError = true;  Repaint(); }
        private void SetInfo (string msg) { _statusMessage = msg; _isError = false; Repaint(); }
    }
}
