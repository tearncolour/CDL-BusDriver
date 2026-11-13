using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

// Piper, the open-source offline text-to-speech engine (MIT, rhasspy/piper): installed on request into %LOCALAPPDATA%\CDLC\piper
// (the Windows build from its GitHub release and the voices from Hugging Face, with the hf-mirror.com mirror as fallback), then run
// as one resident process per voice that reads JSON lines and writes WAV files. Everything here runs on background threads.
namespace CDLC.BusDriver
{
    internal static class PiperInstall
    {
        internal const string kZipUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
        const int kDownloadTimeoutMs = 60000;

        static readonly object s_Lock = new object();
        static Thread s_Worker;

        internal static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDLC", "piper");
        internal static string Exe => Path.Combine(Root, "piper", "piper.exe");
        internal static string VoicesDir => Path.Combine(Root, "voices");
        internal static string ModelPath(string voice) => Path.Combine(VoicesDir, voice + ".onnx");
        internal static string ConfigPath(string voice) => Path.Combine(VoicesDir, voice + ".onnx.json");

        /// <summary>What the installer is doing (for the log and the notification); empty when idle.</summary>
        internal static string Progress { get; private set; } = "";
        internal static bool Busy => s_Worker != null && s_Worker.IsAlive;
        /// <summary>Set when an install finished (well or not); the game thread reads it once and shows the notice.</summary>
        internal static string Finished;

        internal static bool EngineInstalled => File.Exists(Exe);
        internal static bool VoiceInstalled(string voice) => !string.IsNullOrEmpty(voice) && File.Exists(ModelPath(voice)) && File.Exists(ConfigPath(voice));

        /// <summary>Downloads the engine (once) and the voices that are missing, in the background.</summary>
        internal static void Install(IList<string> voices, string proxySpec)
        {
            lock (s_Lock)
            {
                if (Busy)
                    return;
                var list = new List<string>(voices);
                s_Worker = new Thread(() => Work(list, proxySpec)) { IsBackground = true, Name = "CDLC Bus Driver Piper install", Priority = ThreadPriority.BelowNormal };
                s_Worker.Start();
            }
        }

        static void Work(List<string> voices, string proxySpec)
        {
            long total = 0;
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(VoicesDir);
                IWebProxy proxy = AzureTts.ResolveProxy(proxySpec);
                if (!EngineInstalled)
                {
                    Progress = "downloading the Piper engine (22 MB)";
                    string zip = Path.Combine(Root, "piper_windows_amd64.zip");
                    total += Download(kZipUrl, zip, proxy);
                    Progress = "unpacking the Piper engine";
                    string staging = Path.Combine(Root, "unpack");
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    ZipFile.ExtractToDirectory(zip, staging);
                    string inner = Path.Combine(staging, "piper");
                    string target = Path.Combine(Root, "piper");
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    Directory.Move(Directory.Exists(inner) ? inner : staging, target);
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    File.Delete(zip);
                    if (!EngineInstalled)
                        throw new FileNotFoundException("piper.exe is not in the package");
                }

                foreach (string voice in voices)
                {
                    if (VoiceInstalled(voice))
                        continue;
                    Progress = "downloading the voice " + voice + " (63 MB)";
                    foreach (string suffix in new[] { ".onnx.json", ".onnx" })
                    {
                        string file = Path.Combine(VoicesDir, voice + suffix);
                        Exception last = null;
                        foreach (string url in TtsText.PiperVoiceUrls(voice, suffix))
                        {
                            try
                            {
                                total += Download(url, file, proxy);
                                last = null;
                                break;
                            }
                            catch (Exception ex)
                            {
                                last = ex;
                            }
                        }

                        if (last != null)
                            throw new WebException("the voice " + voice + " could not be downloaded: " + last.Message);
                    }
                }

                Progress = "";
                Finished = $"Piper voice installed ({total / 1048576f:0} MB).";
                Mod.log.Info(Finished);
            }
            catch (Exception ex)
            {
                Progress = "";
                Finished = "Piper install failed: " + ex.Message;
                Mod.log.Warn(Finished);
            }
        }

        static long Download(string url, string file, IWebProxy proxy)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = kDownloadTimeoutMs;
            request.ReadWriteTimeout = kDownloadTimeoutMs;
            request.UserAgent = TtsText.kUserAgent;
            request.AllowAutoRedirect = true;
            request.Proxy = proxy;
            string tmp = file + ".part";
            long bytes = 0;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    bytes += read;
                }
            }

            if (bytes < 1000)
                throw new WebException("empty download from " + url);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            return bytes;
        }
    }

    /// <summary>
    /// One resident Piper process per voice (started on first use, ended with the game): a JSON line in, a WAV file out. The
    /// caller waits for the file; a process that dies is started again once.
    /// </summary>
    internal sealed class PiperEngine
    {
        const int kSynthTimeoutMs = 20000;

        sealed class Runner
        {
            internal Process Process;
            internal StreamWriter Input;
        }

        readonly Dictionary<string, Runner> m_Runners = new Dictionary<string, Runner>(StringComparer.Ordinal);
        readonly object m_Lock = new object();
        // Set by Shutdown: a sentence being made stops waiting at once (the worker held the lock for up to two 20 s waits, and
        // the game's exit waited for it), and no process is started again.
        volatile bool m_Stopping;

        /// <summary>Synthesises text to path with the voice; false when the engine or the voice is missing or it failed twice.</summary>
        internal bool Synthesize(string voice, string text, string path)
        {
            if (m_Stopping || !PiperInstall.EngineInstalled || !PiperInstall.VoiceInstalled(voice))
                return false;

            lock (m_Lock)
            {
                for (int attempt = 0; attempt < 2 && !m_Stopping; attempt++)
                {
                    Runner runner = Get(voice);
                    if (runner == null)
                        return false;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        if (File.Exists(path)) File.Delete(path);
                        runner.Input.Write(TtsText.PiperJsonLine(text, path));
                        runner.Input.Write('\n');
                        runner.Input.Flush();
                        if (WaitForFile(path, runner.Process))
                            return true;
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn($"Piper: {ex.GetType().Name}: {ex.Message}");
                    }

                    Stop(voice);
                }
            }

            return false;
        }

        Runner Get(string voice)
        {
            if (m_Runners.TryGetValue(voice, out Runner runner) && !runner.Process.HasExited)
                return runner;

            try
            {
                var start = new ProcessStartInfo(PiperInstall.Exe, "--model \"" + PiperInstall.ModelPath(voice) + "\" --config \"" + PiperInstall.ConfigPath(voice) + "\" --json-input --sentence_silence 0.25")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(PiperInstall.Exe),
                    StandardErrorEncoding = Encoding.UTF8,
                };
                Process process = Process.Start(start);
                if (process == null)
                    return null;
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                process.BeginErrorReadLine();      // piper logs to stderr; read and drop it so the pipe never fills
                process.ErrorDataReceived += (s, e) => { };
                var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                runner = new Runner { Process = process, Input = writer };
                m_Runners[voice] = runner;
                return runner;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Piper could not start: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        bool WaitForFile(string path, Process process)
        {
            var watch = Stopwatch.StartNew();
            long lastSize = -1;
            int stable = 0;
            while (watch.ElapsedMilliseconds < kSynthTimeoutMs)
            {
                Thread.Sleep(25);
                if (m_Stopping || process.HasExited)
                    return false;
                if (!File.Exists(path))
                    continue;
                long size;
                try { size = new FileInfo(path).Length; } catch (IOException) { continue; }
                if (size > 44 && size == lastSize)
                {
                    if (++stable >= 3)
                        return true;
                }
                else
                    stable = 0;
                lastSize = size;
            }

            return false;
        }

        void Stop(string voice)
        {
            if (!m_Runners.TryGetValue(voice, out Runner runner))
                return;
            m_Runners.Remove(voice);
            try { runner.Input.Dispose(); } catch (Exception) { }
            try { if (!runner.Process.HasExited && !runner.Process.WaitForExit(1000)) runner.Process.Kill(); } catch (Exception) { }
            try { runner.Process.Dispose(); } catch (Exception) { }
        }

        internal void Shutdown()
        {
            m_Stopping = true;
            // The worker lets go within a frame of the flag (its wait checks it every 25 ms, then ends its process); a lock
            // still held after 3 s is left to it rather than holding up the game's exit.
            if (!Monitor.TryEnter(m_Lock, 3000))
            {
                Mod.log.Warn("Piper is still busy at shutdown; its processes end with the game.");
                return;
            }

            try
            {
                foreach (string voice in new List<string>(m_Runners.Keys))
                    Stop(voice);
            }
            finally
            {
                Monitor.Exit(m_Lock);
            }
        }
    }
}
