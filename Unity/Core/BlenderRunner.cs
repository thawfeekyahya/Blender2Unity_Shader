using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;

namespace B2U
{
    /// <summary>Runs Blender in background mode with the bundled export script.</summary>
    public static class BlenderRunner
    {
        public const string ScriptFileName = "b2u_export.py";
        const string ScriptSubPath = "Blender~/b2u_unity_exporter/" + ScriptFileName;

        /// <summary>Absolute path of Blender~/b2u_unity_exporter/b2u_export.py, whether installed as a package or under Assets/.</summary>
        public static string ScriptPath()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BlenderRunner).Assembly);
            if (pkg != null)
            {
                var p = Path.Combine(pkg.resolvedPath, ScriptSubPath);
                if (File.Exists(p)) return p;
            }
            foreach (var guid in AssetDatabase.FindAssets("BlenderRunner t:MonoScript"))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(AssetDatabase.GUIDToAssetPath(guid)));
                for (int k = 0; k < 5 && dir != null; k++)
                {
                    var p = Path.Combine(dir, ScriptSubPath);
                    if (File.Exists(p)) return p;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        public sealed class Result
        {
            public bool Success;
            public bool Cancelled;
            public int ExitCode;
            public string Log;
        }

        /// <param name="progress">Called with the latest log line; return true to cancel.</param>
        public static Result Run(string blendFile, string configPath, Func<string, bool> progress)
        {
            var script = ScriptPath();
            if (script == null)
                return new Result { Log = "Could not locate " + ScriptSubPath + " inside the plugin." };

            var psi = new ProcessStartInfo
            {
                FileName = B2USettings.BlenderExecutable,
                Arguments = $"-b {Quote(blendFile)} --python-exit-code 1 --python {Quote(script)} -- --config {Quote(configPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(blendFile) ?? "",
            };

            var log = new StringBuilder();
            var lastLine = "Starting Blender...";
            var gate = new object();
            void OnData(object _, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (gate)
                {
                    log.AppendLine(e.Data);
                    if (e.Data.StartsWith("[B2U]") || e.Data.Contains("Error")) lastLine = e.Data;
                }
            }

            var result = new Result();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += OnData;
                proc.ErrorDataReceived += OnData;
                try
                {
                    proc.Start();
                }
                catch (Exception e)
                {
                    return new Result { Log = "Failed to start Blender (" + psi.FileName + "): " + e.Message };
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var deadline = DateTime.Now.AddMinutes(B2USettings.TimeoutMinutes);
                while (!proc.WaitForExit(100))
                {
                    string line;
                    lock (gate) line = lastLine;
                    if (progress != null && progress(line))
                    {
                        result.Cancelled = true;
                        break;
                    }
                    if (DateTime.Now > deadline)
                    {
                        lock (gate) log.AppendLine("[B2U] Timed out.");
                        break;
                    }
                }
                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { /* already gone */ }
                    proc.WaitForExit(2000);
                }
                else
                {
                    proc.WaitForExit(); // flush async output
                    result.ExitCode = proc.ExitCode;
                    result.Success = proc.ExitCode == 0;
                }
            }
            lock (gate) result.Log = log.ToString();
            return result;
        }
    }
}
