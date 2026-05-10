using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Shadertoy2Unity
{
    /// <summary>
    /// Unity Editor window that converts Shadertoy GLSL shaders to Unity ShaderLab format
    /// using the glsl2hlsl command-line tool.
    /// Open via: Window > Shadertoy2Unity
    /// </summary>
    public class Shadertoy2UnityWindow : EditorWindow
    {
        // ── Serialisable fields preserved across domain reloads ─────────────────
        [SerializeField] private int _inputModeIndex = 0;
        [SerializeField] private string _shadertoyUrl  = "";
        [SerializeField] private string _glslCode      = "";
        [SerializeField] private string _shaderName    = "ConvertedShader";
        [SerializeField] private bool   _extractProps  = true;
        [SerializeField] private bool   _raymarch      = true;
        [SerializeField] private string _outputFolder  = "Assets/Shaders/Converted";
        [SerializeField] private string _binaryPath    = "";
        [SerializeField] private bool   _showSettings  = false;

        // ── Runtime state ───────────────────────────────────────────────────────
        private Vector2 _glslScroll;
        private Vector2 _mainScroll;
        private string  _statusMessage = "";
        private bool    _isError       = false;

        private static readonly string[] k_InputModeLabels = { "From Shadertoy URL", "From GLSL Code" };
        private const string k_BinaryPathPref  = "Shadertoy2Unity_BinaryPath";
        private const string k_OutputFolderPref = "Shadertoy2Unity_OutputFolder";
        private const string k_DefaultApiKey   = "NtHtMm";

        // ── Menu entry ──────────────────────────────────────────────────────────
        [MenuItem("Window/Shadertoy2Unity")]
        public static void ShowWindow()
        {
            var win = GetWindow<Shadertoy2UnityWindow>(false, "Shadertoy2Unity", true);
            win.minSize = new Vector2(420, 560);
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────
        private void OnEnable()
        {
            _binaryPath   = EditorPrefs.GetString(k_BinaryPathPref,   _binaryPath);
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
            EditorGUILayout.Space(4);

            DrawSettingsSection();
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
                        "Attempt to detect uniform variables and expose them as shader properties in the Unity Inspector."),
                    _extractProps);

                _raymarch = EditorGUILayout.Toggle(
                    new GUIContent("Raymarch / Raytrace Mode",
                        "Attempt to make the shader work as a 3-D world-space raymarched shader on a mesh, instead of a screen-space quad."),
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
                        // Convert absolute path to relative (Assets/…)
                        if (abs.StartsWith(Application.dataPath))
                            abs = "Assets" + abs.Substring(Application.dataPath.Length);
                        _outputFolder = abs;
                        EditorPrefs.SetString(k_OutputFolderPref, _outputFolder);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSettingsSection()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "glsl2hlsl Binary Settings", true);
            if (!_showSettings) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.BeginHorizontal();
                _binaryPath = EditorGUILayout.TextField("Binary Path", _binaryPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFilePanel(
                        "Select glsl2hlsl binary", "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _binaryPath = picked;
                        EditorPrefs.SetString(k_BinaryPathPref, _binaryPath);
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.HelpBox(
                    "Build the binary from the repository root:\n" +
                    "  cargo build --release\n\n" +
                    "Then set the path to:\n" +
                    "  target/release/glsl2hlsl  (Linux / macOS)\n" +
                    "  target\\release\\glsl2hlsl.exe  (Windows)\n\n" +
                    "On macOS/Linux you may need:\n" +
                    "  chmod +x <path>",
                    MessageType.Info);
            }
        }

        private void DrawConvertButton()
        {
            GUI.enabled = !string.IsNullOrEmpty(_binaryPath);
            if (GUILayout.Button("Convert Shader", GUILayout.Height(36)))
                RunConversion();
            GUI.enabled = true;

            if (string.IsNullOrEmpty(_binaryPath))
            {
                EditorGUILayout.HelpBox(
                    "Set the path to the glsl2hlsl binary in the settings section above.",
                    MessageType.Warning);
            }
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
            _isError = false;

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

                string apiKey = k_DefaultApiKey;
                try
                {
                    string jsonResponse = FetchUrl(
                        $"https://www.shadertoy.com/api/v1/shaders/{id}?key={apiKey}");
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

            // 2. Write GLSL to a temp file
            string tempDir  = Path.Combine(Path.GetTempPath(), "Shadertoy2Unity");
            Directory.CreateDirectory(tempDir);
            string tempGlsl = Path.Combine(tempDir, "input.glsl");
            File.WriteAllText(tempGlsl, glsl, Encoding.UTF8);

            // 3. Invoke glsl2hlsl binary
            string shaderContent;
            try
            {
                shaderContent = InvokeGlsl2Hlsl(tempGlsl);
            }
            catch (Exception ex)
            {
                SetError($"glsl2hlsl failed:\n{ex.Message}");
                return;
            }

            // 4. Save to project
            try
            {
                SaveShaderToProject(shaderContent, nameHint);
            }
            catch (Exception ex)
            {
                SetError($"Failed to save shader file:\n{ex.Message}");
                return;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>Extracts the 6-char shader ID from a Shadertoy URL or returns the input if it already looks like an ID.</summary>
        private static string ExtractShadertoyId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            // Full URL  →  /view/XXXXXX
            var match = Regex.Match(input, @"shadertoy\.com/view/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;

            // Raw ID (alphanumeric, typical length 6-8)
            if (Regex.IsMatch(input, @"^[A-Za-z0-9]{4,16}$"))
                return input;

            return null;
        }

        /// <summary>Simple blocking HTTP GET.</summary>
        private static string FetchUrl(string url)
        {
#pragma warning disable SYSLIB0014  // WebClient is obsolete in .NET 6+ but available in Unity
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
#pragma warning restore SYSLIB0014
        }

        /// <summary>
        /// Minimal JSON parsing of the Shadertoy API response.
        /// Avoids pulling in a full JSON library – Unity's JsonUtility cannot
        /// deserialise nested arrays directly from non-MonoBehaviour contexts.
        /// </summary>
        private static string ParseGlslFromApiJson(string json, out string shaderName)
        {
            shaderName = "ConvertedShader";

            // Error field?
            var errMatch = Regex.Match(json, "\"Error\"\\s*:\\s*\"([^\"]+)\"");
            if (errMatch.Success)
                throw new InvalidOperationException($"Shadertoy API error: {errMatch.Groups[1].Value}");

            // Shader name
            var nameMatch = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (nameMatch.Success)
                shaderName = SanitiseFileName(nameMatch.Groups[1].Value);

            // Extract first renderpass "code" value.
            // The value is a JSON string – handle basic escapes.
            int codeIdx = json.IndexOf("\"code\"", StringComparison.Ordinal);
            if (codeIdx < 0)
                throw new InvalidOperationException("Could not find 'code' field in Shadertoy API response.");

            int colonIdx = json.IndexOf(':', codeIdx + 6);
            if (colonIdx < 0)
                throw new InvalidOperationException("Malformed Shadertoy API response.");

            // Skip whitespace and the opening quote
            int start = colonIdx + 1;
            while (start < json.Length && json[start] != '"') start++;
            if (start >= json.Length)
                throw new InvalidOperationException("Could not find code string in Shadertoy API response.");

            start++; // skip opening "
            var sb = new StringBuilder();
            int i = start;
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
                        {
                            if (i + 5 < json.Length)
                            {
                                string hex = json.Substring(i + 2, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                    null, out int code))
                                {
                                    sb.Append((char)code);
                                    i += 6;
                                    continue;
                                }
                            }
                            break;
                        }
                    }
                }
                else if (c == '"')
                {
                    break; // end of string
                }
                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>Calls the glsl2hlsl binary and returns the converted shader source.</summary>
        private string InvokeGlsl2Hlsl(string glslFilePath)
        {
            if (!File.Exists(_binaryPath))
                throw new FileNotFoundException($"glsl2hlsl binary not found at: {_binaryPath}");

            // Build argument list
            var args = new List<string> { $"\"{glslFilePath}\"" };
            if (!_extractProps) args.Add("--no-props");
            if (!_raymarch)     args.Add("--no-raymarch");

            var psi = new ProcessStartInfo
            {
                FileName               = _binaryPath,
                Arguments              = string.Join(" ", args),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                    throw new InvalidOperationException("Failed to start glsl2hlsl process.");

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    string detail = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                    throw new InvalidOperationException(
                        $"glsl2hlsl exited with code {proc.ExitCode}.\n{detail}");
                }
            }

            // Binary writes output as  <input>.shader
            string outputPath = glslFilePath + ".shader";
            if (!File.Exists(outputPath))
                throw new FileNotFoundException(
                    $"Expected output file was not created: {outputPath}");

            return File.ReadAllText(outputPath, Encoding.UTF8);
        }

        /// <summary>Writes the shader to the Unity project and refreshes the AssetDatabase.</summary>
        private void SaveShaderToProject(string content, string shaderName)
        {
            // Resolve output folder
            string absFolder;
            if (_outputFolder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_outputFolder, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                int prefixLen = string.Equals(_outputFolder, "Assets", StringComparison.OrdinalIgnoreCase)
                    ? "Assets".Length
                    : "Assets/".Length;
                absFolder = Path.Combine(Application.dataPath, _outputFolder.Substring(prefixLen));
            }
            else
            {
                absFolder = _outputFolder;
            }

            Directory.CreateDirectory(absFolder);

            string fileName = SanitiseFileName(shaderName);
            if (!fileName.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                fileName += ".shader";

            string fullPath = Path.Combine(absFolder, fileName);

            // Avoid overwriting without confirmation
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

            // Ping the newly created asset in the Project window
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relPath);
            if (asset != null) EditorGUIUtility.PingObject(asset);
        }

        private static string SanitiseFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        private void SetError(string msg)
        {
            _statusMessage = msg;
            _isError       = true;
            Repaint();
        }

        private void SetInfo(string msg)
        {
            _statusMessage = msg;
            _isError       = false;
            Repaint();
        }
    }
}
