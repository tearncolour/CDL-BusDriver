using System;
using System.Security.Cryptography;
using System.Text;

// The pure side of the stop announcements: which voice speaks which language, the sentence for each event, the cache file name
// of a spoken text, the SSML and endpoint of the Azure Speech service, proxy strings and the Windows speech script. Tested offline.
namespace CDLC.BusDriver
{
    /// <summary>What the announcement says.</summary>
    internal enum AnnounceKind : byte
    {
        Next,       // pulling away: "The bus is moving off, please hold on. Route 3 to Y. The next stop is X."
        Arriving,   // 150 m before a stop: "Approaching X. Please get ready to leave by the rear door."
        Skip,       // 150 m before a stop nobody needs: "No one is getting on or off at X; this bus will not stop there."
        AtStop,     // doors open: "This is X. Please mind your step."
    }

    /// <summary>Where the voice comes from.</summary>
    public enum AnnounceVoice
    {
        Auto,       // the best one available: Azure with a key, else Piper when installed, else the Windows voice
        Piper,      // Piper, the open-source offline engine, installed on request (PiperTts.cs)
        Azure,      // Microsoft's neural voices (Xiaoxiao / Jenny) from the Azure Speech service with the player's own key
        Windows,    // the Windows voices on this PC (Huihui / Zira), offline
        ChimeOnly,  // the chime and the screen text only
    }

    internal static class TtsText
    {
        internal const string kAzureFormat = "riff-24khz-16bit-mono-pcm";
        internal const string kUserAgent = "CSL-Driving-Life-Bus-Driver";

        /// <summary>The neural voice for a game locale: Chinese and the other languages the game ships, English otherwise.</summary>
        internal static string NeuralVoice(string localeId)
        {
            string id = (localeId ?? "").ToLowerInvariant();
            if (id.StartsWith("zh-hant") || id.StartsWith("zh-tw") || id.StartsWith("zh-hk")) return "zh-TW-HsiaoChenNeural";
            if (id.StartsWith("zh")) return "zh-CN-XiaoxiaoNeural";
            if (id.StartsWith("ja")) return "ja-JP-NanamiNeural";
            if (id.StartsWith("ko")) return "ko-KR-SunHiNeural";
            if (id.StartsWith("de")) return "de-DE-KatjaNeural";
            if (id.StartsWith("fr")) return "fr-FR-DeniseNeural";
            if (id.StartsWith("es")) return "es-ES-ElviraNeural";
            if (id.StartsWith("it")) return "it-IT-ElsaNeural";
            if (id.StartsWith("pt")) return "pt-BR-FranciscaNeural";
            if (id.StartsWith("ru")) return "ru-RU-SvetlanaNeural";
            if (id.StartsWith("pl")) return "pl-PL-ZofiaNeural";
            return "en-US-JennyNeural";
        }

        /// <summary>The Piper voice for a game locale (rhasspy/piper-voices v1.0.0): Chinese huayan, English lessac, a few others.</summary>
        internal static string PiperVoice(string localeId)
        {
            string id = (localeId ?? "").ToLowerInvariant();
            if (id.StartsWith("zh")) return "zh_CN-huayan-medium";
            if (id.StartsWith("de")) return "de_DE-thorsten-medium";
            if (id.StartsWith("fr")) return "fr_FR-siwis-medium";
            if (id.StartsWith("es")) return "es_ES-davefx-medium";
            if (id.StartsWith("ru")) return "ru_RU-irina-medium";
            if (id.StartsWith("pl")) return "pl_PL-darkman-medium";
            if (id.StartsWith("pt")) return "pt_BR-faber-medium";
            return "en_US-lessac-medium";
        }

        /// <summary>Where a Piper voice file lives: Hugging Face first, then the hf-mirror.com mirror (reachable from China).</summary>
        internal static string[] PiperVoiceUrls(string voice, string suffix)
        {
            // "zh_CN-huayan-medium" -> zh/zh_CN/huayan/medium/zh_CN-huayan-medium<suffix>
            string[] parts = (voice ?? "").Split('-');
            if (parts.Length != 3 || parts[0].IndexOf('_') < 0)
                return new string[0];
            string path = parts[0].Split('_')[0] + "/" + parts[0] + "/" + parts[1] + "/" + parts[2] + "/" + voice + suffix;
            return new[]
            {
                "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/" + path,
                "https://hf-mirror.com/rhasspy/piper-voices/resolve/v1.0.0/" + path,
            };
        }

        /// <summary>One request for Piper's --json-input mode: the text and the file to write.</summary>
        internal static string PiperJsonLine(string text, string path)
        {
            string esc(string s)
            {
                var b = new StringBuilder(s.Length + 8);
                foreach (char c in s ?? "")
                {
                    if (c == '"' || c == '\\') b.Append('\\').Append(c);
                    else if (c < ' ') b.Append(' ');
                    else b.Append(c);
                }
                return b.ToString();
            }
            return "{\"text\":\"" + esc(text) + "\",\"output_file\":\"" + esc(path) + "\"}";
        }

        /// <summary>The language tag of a voice name ("zh-CN-XiaoxiaoNeural" = zh-CN).</summary>
        internal static string VoiceLanguage(string voice)
        {
            string[] parts = (voice ?? "").Split('-');
            return parts.Length >= 2 ? parts[0] + "-" + parts[1] : "en-US";
        }

        /// <summary>The culture the Windows voice is picked by (System.Speech); only Chinese and English have texts anyway.</summary>
        internal static string WindowsCulture(string localeId)
        {
            string id = (localeId ?? "").ToLowerInvariant();
            if (id.StartsWith("zh-hant") || id.StartsWith("zh-tw") || id.StartsWith("zh-hk")) return "zh-TW";
            if (id.StartsWith("zh")) return "zh-CN";
            return "en-US";
        }

        /// <summary>
        /// The sentence for an event from the language file's templates: {0} the stop's name, {1} the stop as a station (Chinese:
        /// the name with "站", not doubled when the name already ends with it), {2} the line's label. Leaving: the safety reminder,
        /// the line and its terminus (when known and wanted), then the next stop. The final stop (the stop is the terminus) has
        /// its own sentences.
        /// </summary>
        /// lineNamed: the line's label is its own name or words ("高港专线", "快3线"), said without the locale's word for a route.
        internal static string Sentence(AnnounceKind kind, string stop, string terminus, bool sayTerminus, Func<string, string> text, string line = "",
            bool lineNamed = false)
        {
            if (string.IsNullOrEmpty(stop))
                return "";

            bool final = IsTerminus(stop, terminus);
            string suffix = text("Ann.Station");
            string station = suffix.Length > 0 && !stop.EndsWith(suffix, StringComparison.Ordinal) ? stop + suffix : stop;
            line = line ?? "";
            string F(string key) => string.Format(text(key), stop, station, line);
            switch (kind)
            {
                case AnnounceKind.Next:
                {
                    var s = new StringBuilder(F("Ann.Depart"));
                    if (sayTerminus && !string.IsNullOrEmpty(terminus) && !final)
                    {
                        string towards = line.Length == 0 ? text("Ann.Towards")
                            : lineNamed ? text("Ann.NamedLine") : text("Ann.Line");
                        s.Append(' ').Append(string.Format(towards, terminus, terminus, line));
                    }
                    s.Append(' ').Append(final ? F("Ann.NextFinal") : F("Ann.Next"));
                    return s.ToString();
                }
                case AnnounceKind.Arriving:
                    return final ? F("Ann.ArrivingFinal")
                                 : F("Ann.Arriving");
                case AnnounceKind.Skip:
                    return F("Ann.Skip");
                default:
                    return final ? F("Ann.AtStopFinal")
                                 : F("Ann.AtStop");
            }
        }

        /// <summary>The stop is the terminus (by name, as the sentences tell the final stop).</summary>
        internal static bool IsTerminus(string stop, string terminus)
        {
            return !string.IsNullOrEmpty(stop) && !string.IsNullOrEmpty(terminus) && string.Equals(stop.Trim(), terminus.Trim(), StringComparison.Ordinal);
        }

        /// <summary>The cache file's name for a voice and a text: a SHA-1 of both (stable across sessions and machines).</summary>
        internal static string CacheName(string voice, string text)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(voice + "\n" + text));
                var b = new StringBuilder(40);
                foreach (byte x in hash)
                    b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        /// <summary>Text inside SSML: the five XML characters escaped.</summary>
        internal static string EscapeXml(string text)
        {
            return (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>The SSML the Azure Speech service synthesises: one voice, the text at a slightly unhurried pace.</summary>
        internal static string Ssml(string voice, string text)
        {
            string lang = VoiceLanguage(voice);
            return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='" + lang + "'>" +
                "<voice name='" + voice + "'><prosody rate='-5%'>" + EscapeXml(text) + "</prosody></voice></speak>";
        }

        /// <summary>The text-to-speech endpoint of an Azure Speech resource's region ("eastasia", "westeurope", ...); null for a bad region.</summary>
        internal static string AzureEndpoint(string region)
        {
            string r = (region ?? "").Trim().ToLowerInvariant();
            if (r.Length < 3 || r.Length > 32)
                return null;
            foreach (char c in r)
            {
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9'))
                    return null;
            }

            return "https://" + r + ".tts.speech.microsoft.com/cognitiveservices/v1";
        }

        /// <summary>Does the key look like an Azure resource key (32 hex digits or the newer longer keys)?</summary>
        internal static bool PlausibleAzureKey(string key)
        {
            string k = (key ?? "").Trim();
            if (k.Length < 32 || k.Length > 128)
                return false;
            foreach (char c in k)
            {
                if (!char.IsLetterOrDigit(c))
                    return false;
            }

            return true;
        }

        /// <summary>"host:port", "http://host:port" or "http://user:pass@host:port" to a host and a port; false for none/"direct"/empty.</summary>
        internal static bool ParseProxy(string spec, out string host, out int port)
        {
            host = null;
            port = 0;
            string s = (spec ?? "").Trim();
            if (s.Length == 0 || string.Equals(s, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "direct", StringComparison.OrdinalIgnoreCase))
                return false;
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int at = s.LastIndexOf('@');
            if (at >= 0) s = s.Substring(at + 1);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            int colon = s.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(s.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                return false;
            host = s.Substring(0, colon);
            return host.Length > 0;
        }

        /// <summary>The first proxy of a Windows "Internet Settings" ProxyServer value ("host:port" or "http=...;https=...;...").</summary>
        internal static string WindowsProxyServer(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string https = null, http = null, plain = null;
            foreach (string part in value.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                if (eq < 0) { plain = plain ?? p; continue; }
                string scheme = p.Substring(0, eq).Trim().ToLowerInvariant();
                string address = p.Substring(eq + 1).Trim();
                if (scheme == "https") https = address;
                else if (scheme == "http") http = address;
            }

            return https ?? http ?? plain;
        }

        /// <summary>
        /// PowerShell for the Windows voices: System.Speech synthesises the text to a WAV file (run out of process). The script holds none of
        /// the three values it is given (the culture, the text, the file): each is the Base64 of its UTF-8 bytes, decoded into a variable by the
        /// script. A text put between quotes ends at any of PowerShell's single-quote characters (the apostrophe and the curly ones, U+2018 to
        /// U+201B, which a station's name can hold), and what follows is parsed as a command.
        /// </summary>
        internal static string WindowsSpeechScript(string culture, string text, string path)
        {
            // each value is a variable the script fills from Base64 (letters, digits, + / = only): no text of the caller's is ever part of the syntax
            string value(string name, string s) =>
                "$" + name + " = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")) + "'))\n";
            return "Add-Type -AssemblyName System.Speech\n" +
                value("culture", culture) + value("language", (culture ?? "").Split('-')[0]) + value("text", text) + value("path", path) +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\n" +
                "$v = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq $culture } | Select-Object -First 1\n" +
                "if ($null -eq $v) { $v = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name.StartsWith($language) } | Select-Object -First 1 }\n" +
                "if ($null -eq $v) { exit 3 }\n" +
                "$s.SelectVoice($v.VoiceInfo.Name)\n" +
                "$s.Rate = -1\n" +
                "$s.SetOutputToWaveFile($path)\n" +
                "$s.Speak($text)\n" +
                "$s.SetOutputToNull()\n" +
                "$s.Dispose()\n" +
                "exit 0\n";
        }

        /// <summary>PowerShell's -EncodedCommand argument: the script as UTF-16LE in Base64 (no execution policy involved).</summary>
        internal static string EncodedCommand(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
}
