using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// The spoken announcements' files: synthesised once per sentence and voice on one low-priority background thread, kept under
// %LOCALAPPDATA%\CDLC\tts as WAV, trimmed to a size limit. The game thread only asks whether a file is ready. Three sources, in
// the order the voice option allows: Microsoft's neural voices from the Azure Speech service with the player's own key (Xiaoxiao /
// Jenny), Piper, the open-source offline engine (PiperTts.cs), and the Windows voices on this PC through PowerShell +
// System.Speech (offline; the game's Mono has no System.Speech, so it runs out of process).
namespace CDLC.BusDriver
{
    internal sealed class TtsCache
    {
        internal enum Status : byte { Unknown, Pending, Ready, Failed }

        sealed class Entry
        {
            internal Status State;
            internal string Path;
            internal double RetryAt;     // when a failed sentence may be asked for again (Seconds())
        }

        const int kNeuralTimeoutMs = 15000;
        const int kWindowsTimeoutMs = 25000;
        const int kAzureFailuresBeforeFallback = 3;
        const double kRetryAfterSeconds = 30.0;

        readonly object m_Lock = new object();
        readonly Dictionary<string, Entry> m_Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly Queue<string> m_Urgent = new Queue<string>();
        readonly Queue<string> m_Background = new Queue<string>();
        readonly PiperEngine m_Piper = new PiperEngine();
        Thread m_Worker;
        bool m_Stop;
        int m_AzureFailures;
        // whether the Windows voice is tried: a timeout is held and tried again, a culture without a voice stays out until Forget (WindowsVoiceGate.cs)
        readonly WindowsVoiceGate m_WindowsGate = new WindowsVoiceGate();
        bool m_AzureLogged, m_PiperLogged, m_WindowsLogged;

        internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDLC", "tts");
        internal AnnounceVoice Mode = AnnounceVoice.Auto;
        internal string Locale = "en-US";
        internal string AzureKey = "";
        internal string AzureRegion = "";
        internal string Proxy = "";

        /// <summary>Why the Azure voice is not used right now, or null when it is.</summary>
        internal string AzureProblem
        {
            get
            {
                if (Mode != AnnounceVoice.Azure && Mode != AnnounceVoice.Auto) return "not selected";
                if (!TtsText.PlausibleAzureKey(AzureKey)) return "no Azure Speech key";
                if (TtsText.AzureEndpoint(AzureRegion) == null) return "no Azure Speech region";
                if (m_AzureFailures >= kAzureFailuresBeforeFallback) return "the service refused repeatedly";
                return null;
            }
        }

        /// <summary>Why Piper is not used right now, or null when it is.</summary>
        internal string PiperProblem
        {
            get
            {
                if (Mode != AnnounceVoice.Piper && Mode != AnnounceVoice.Auto) return "not selected";
                if (!PiperInstall.EngineInstalled) return "Piper is not installed (Options -> Bus Driver -> Install the open-source voice)";
                if (!PiperInstall.VoiceInstalled(TtsText.PiperVoice(Locale))) return "the Piper voice " + TtsText.PiperVoice(Locale) + " is not installed";
                return null;
            }
        }

        /// <summary>Starts synthesising a sentence (now if urgent, else after the urgent ones); nothing when it is known already, or failed less than 30 s ago.</summary>
        internal void Request(string text, bool urgent)
        {
            if (string.IsNullOrEmpty(text) || Mode == AnnounceVoice.ChimeOnly)
                return;

            lock (m_Lock)
            {
                if (m_Entries.TryGetValue(text, out Entry known))
                {
                    if (known.State != Status.Failed || Seconds() < known.RetryAt)
                        return;
                    known.State = Status.Pending;
                }
                else
                    m_Entries[text] = new Entry { State = Status.Pending };

                (urgent ? m_Urgent : m_Background).Enqueue(text);
                if (m_Worker == null || !m_Worker.IsAlive)
                {
                    m_Stop = false;
                    m_Worker = new Thread(Work) { IsBackground = true, Name = "CDLC Bus Driver TTS", Priority = ThreadPriority.BelowNormal };
                    m_Worker.Start();
                }

                Monitor.Pulse(m_Lock);
            }
        }

        internal Status StateOf(string text, out string path)
        {
            lock (m_Lock)
            {
                if (m_Entries.TryGetValue(text, out Entry entry))
                {
                    path = entry.Path;
                    return entry.State;
                }
            }

            path = null;
            return Status.Unknown;
        }

        /// <summary>A ready file the game could not play: thrown away and marked failed.</summary>
        internal void Reject(string text)
        {
            string path;
            lock (m_Lock)
            {
                if (!m_Entries.TryGetValue(text, out Entry entry))
                    return;

                path = entry.Path;
                entry.State = Status.Failed;
                entry.RetryAt = Seconds() + kRetryAfterSeconds;
            }

            try { if (path != null && File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        /// <summary>Forgets what was asked (a changed voice, key or language): the files stay, they are found again by name.</summary>
        internal void Forget()
        {
            lock (m_Lock)
            {
                m_Entries.Clear();
                m_Urgent.Clear();
                m_Background.Clear();
                m_AzureFailures = 0;
                m_AzureLogged = false;
                m_PiperLogged = false;
                m_WindowsGate.Reset();
                m_WindowsLogged = false;
            }
        }

        internal void Shutdown()
        {
            lock (m_Lock)
            {
                m_Stop = true;
                m_Urgent.Clear();
                m_Background.Clear();
                Monitor.Pulse(m_Lock);
            }

            // A Windows voice being made (PowerShell, up to 25 s) is ended with the game, not left running after it.
            Process windows = m_WindowsProcess;
            if (windows != null)
            {
                try { if (!windows.HasExited) windows.Kill(); } catch (Exception) { }
            }

            m_Piper.Shutdown();
        }

        volatile Process m_WindowsProcess;

        /// <summary>Deletes the oldest files (by last access) until the folder is under the limit; in the background.</summary>
        internal void Trim(long limitBytes)
        {
            string root = Root;
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(root)) return;
                    var files = new List<FileInfo>();
                    foreach (string f in Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories))
                        files.Add(new FileInfo(f));
                    long total = 0;
                    foreach (FileInfo f in files) total += f.Length;
                    if (total <= limitBytes) return;
                    files.Sort((a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                    int removed = 0;
                    foreach (FileInfo f in files)
                    {
                        if (total <= limitBytes * 0.9) break;
                        total -= f.Length;
                        f.Delete();
                        removed++;
                    }

                    Mod.log.Info($"Announcement cache trimmed: {removed} file(s) removed, {total / 1048576f:0.0} MB left.");
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Announcement cache trim failed: {ex.Message}");
                }
            });
        }

        /// <summary>Deletes every cached file (the options page button); in the background.</summary>
        internal void ClearAll()
        {
            Forget();
            string root = Root;
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    Mod.log.Info("Announcement cache cleared.");
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Announcement cache clear failed: {ex.Message}");
                }
            });
        }

        // ------------------------------------------------------------------ the worker

        void Work()
        {
            while (true)
            {
                string text;
                lock (m_Lock)
                {
                    while (!m_Stop && m_Urgent.Count == 0 && m_Background.Count == 0)
                        Monitor.Wait(m_Lock);
                    if (m_Stop) return;
                    text = m_Urgent.Count > 0 ? m_Urgent.Dequeue() : m_Background.Dequeue();
                }

                string path = null;
                bool ok = false;
                try
                {
                    ok = Synthesize(text, out path);
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Announcement synthesis failed: {ex.GetType().Name}: {ex.Message}");
                }

                lock (m_Lock)
                {
                    if (m_Entries.TryGetValue(text, out Entry entry))
                    {
                        entry.State = ok ? Status.Ready : Status.Failed;
                        entry.Path = path;
                        if (!ok) entry.RetryAt = Seconds() + kRetryAfterSeconds;
                    }
                }
            }
        }

        bool Synthesize(string text, out string path)
        {
            path = null;

            // 1. Azure neural voice (the player's key).
            string azureProblem = AzureProblem;
            if (azureProblem == null)
            {
                string voice = TtsText.NeuralVoice(Locale);
                path = Path.Combine(Root, voice, TtsText.CacheName(voice, text) + ".wav");
                if (File.Exists(path))
                    return true;

                try
                {
                    byte[] wav = AzureTts.Synthesize(text, voice, AzureKey, AzureRegion, Proxy, kNeuralTimeoutMs, out string problem);
                    if (wav != null && wav.Length > 1000)
                    {
                        Write(path, wav);
                        m_AzureFailures = 0;
                        return true;
                    }

                    m_AzureFailures++;
                    if (!m_AzureLogged) { m_AzureLogged = true; Mod.log.Warn($"Azure voice: {problem ?? "no audio"}; the next voice is used while it fails."); }
                }
                catch (Exception ex)
                {
                    m_AzureFailures++;
                    if (!m_AzureLogged) { m_AzureLogged = true; Mod.log.Warn($"Azure voice unavailable ({ex.GetType().Name}: {ex.Message}); the next voice is used while it fails."); }
                }
            }
            else if (Mode == AnnounceVoice.Azure && !m_AzureLogged)
            {
                m_AzureLogged = true;
                Mod.log.Info($"Azure voice not used ({azureProblem}).");
            }

            // 2. Piper (open source, offline).
            string piperProblem = PiperProblem;
            if (piperProblem == null)
            {
                string voice = TtsText.PiperVoice(Locale);
                path = Path.Combine(Root, "piper-" + voice, TtsText.CacheName(voice, text) + ".wav");
                if (File.Exists(path) || m_Piper.Synthesize(voice, text, path))
                    return true;
                if (!m_PiperLogged) { m_PiperLogged = true; Mod.log.Warn("Piper did not produce a file; the Windows voice is used while it fails."); }
            }
            else if ((Mode == AnnounceVoice.Piper || Mode == AnnounceVoice.Auto) && !m_PiperLogged)
            {
                m_PiperLogged = true;
                Mod.log.Info($"Piper not used: {piperProblem}.");
            }

            // 3. The Windows voices.
            string culture = TtsText.WindowsCulture(Locale);
            if (!m_WindowsGate.May(culture, Seconds()))
                return false;

            path = Path.Combine(Root, "windows-" + culture, TtsText.CacheName(culture, text) + ".wav");
            if (File.Exists(path))
                return true;

            return WindowsSpeech(culture, text, path);
        }

        static void Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // seconds on a clock that does not go back (the gate's holds are measured on it)
        static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        bool WindowsSpeech(string culture, string text, string path)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(exe))
            {
                m_WindowsGate.Failed(Seconds());
                if (!m_WindowsLogged) { m_WindowsLogged = true; Mod.log.Warn("Windows voice unavailable: PowerShell not found; announcements are chime only, tried again in " + (int)m_WindowsGate.WaitLeft(Seconds()) + " s."); }
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp.wav";
            var start = new ProcessStartInfo(exe, "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + TtsText.EncodedCommand(TtsText.WindowsSpeechScript(culture, text, tmp)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            using (Process process = Process.Start(start))
            {
                if (process == null)
                {
                    m_WindowsGate.Failed(Seconds());
                    return false;
                }

                m_WindowsProcess = process;
                bool finished;
                try { finished = process.WaitForExit(kWindowsTimeoutMs); }
                finally { m_WindowsProcess = null; }
                if (m_Stop) return false;
                if (!finished)
                {
                    try { process.Kill(); } catch (Exception) { }
                    m_WindowsGate.Failed(Seconds());
                    if (!m_WindowsLogged) { m_WindowsLogged = true; Mod.log.Warn("Windows voice: PowerShell did not finish in time; announcements are chime only, tried again in " + (int)m_WindowsGate.WaitLeft(Seconds()) + " s."); }
                    return false;
                }

                if (process.ExitCode == 3)
                {
                    m_WindowsGate.NoVoiceFor(culture);
                    if (!m_WindowsLogged) { m_WindowsLogged = true; Mod.log.Warn($"Windows voice: no voice installed for {culture}; announcements are chime only while the language is this one."); }
                    return false;
                }
            }

            if (!File.Exists(tmp) || new FileInfo(tmp).Length < 1000)
                return false;
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            m_WindowsGate.Succeeded();
            m_WindowsLogged = false;
            return true;
        }
    }

    /// <summary>
    /// Microsoft's neural voices from the Azure Speech service (the documented text-to-speech REST API) with the player's own
    /// resource key: one HTTPS request per sentence, SSML in, a 24 kHz 16-bit mono WAV out. Through the system proxy, or the
    /// proxy named in the options. Blocking; call from a background thread only.
    /// </summary>
    internal static class AzureTts
    {
        internal static byte[] Synthesize(string text, string voice, string key, string region, string proxySpec, int timeoutMs, out string problem)
        {
            problem = null;
            string endpoint = TtsText.AzureEndpoint(region);
            if (endpoint == null || !TtsText.PlausibleAzureKey(key))
            {
                problem = "key or region missing";
                return null;
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.ContentType = "application/ssml+xml";
            request.UserAgent = TtsText.kUserAgent;
            request.Headers["Ocp-Apim-Subscription-Key"] = key.Trim();
            request.Headers["X-Microsoft-OutputFormat"] = TtsText.kAzureFormat;
            request.Proxy = ResolveProxy(proxySpec);
            byte[] body = Encoding.UTF8.GetBytes(TtsText.Ssml(voice, text));
            request.ContentLength = body.Length;
            using (Stream stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    byte[] bytes = memory.ToArray();
                    if (bytes.Length < 44 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
                    {
                        problem = "the service did not send a WAV";
                        return null;
                    }

                    return bytes;
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse http)
            {
                int code = (int)http.StatusCode;
                problem = code == 401 || code == 403 ? "the key was refused (401/403): check the key and the region"
                    : code == 429 ? "the service is throttling (429)"
                    : code == 400 ? "bad request (400): the voice or the SSML was refused"
                    : "HTTP " + code;
                http.Close();
                return null;
            }
        }

        /// <summary>The proxy from the options, else the system's (Windows Internet Settings, HTTPS_PROXY), else a direct connection.</summary>
        internal static IWebProxy ResolveProxy(string proxySpec)
        {
            string spec = (proxySpec ?? "").Trim();
            if (string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(spec, "direct", StringComparison.OrdinalIgnoreCase))
                return null;
            if (TtsText.ParseProxy(spec, out string host, out int port))
                return new WebProxy(host, port);

            string env = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy") ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            if (TtsText.ParseProxy(env, out host, out port))
                return new WebProxy(host, port);

            try
            {
                using (Microsoft.Win32.RegistryKey settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (settings != null && settings.GetValue("ProxyEnable") is int enabled && enabled != 0 &&
                        TtsText.ParseProxy(TtsText.WindowsProxyServer(settings.GetValue("ProxyServer") as string), out host, out port))
                        return new WebProxy(host, port);
                }
            }
            catch (Exception)
            {
            }

            return WebRequest.DefaultWebProxy;
        }
    }
}
