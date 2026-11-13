using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The announcements' pure side: voices per language, the sentences, cache names, the Azure Speech SSML and endpoint, proxy
// strings and the Windows speech script.
static class TtsTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(TtsText.NeuralVoice("zh-HANS") == "zh-CN-XiaoxiaoNeural" && TtsText.NeuralVoice("zh-HANT") == "zh-TW-HsiaoChenNeural" && TtsText.NeuralVoice("en-US") == "en-US-JennyNeural" && TtsText.NeuralVoice("xx") == "en-US-JennyNeural",
            "neural voices: Xiaoxiao for simplified Chinese, HsiaoChen for traditional, Jenny for English and anything else");
        check(TtsText.VoiceLanguage("zh-CN-XiaoxiaoNeural") == "zh-CN" && TtsText.VoiceLanguage("en-US-JennyNeural") == "en-US", "a voice's language tag");
        check(TtsText.WindowsCulture("zh-HANS") == "zh-CN" && TtsText.WindowsCulture("de-DE") == "en-US", "Windows voice cultures");

        // the texts of the language files themselves, as BusLocale.Text gives them (the code holds none)
        check(System.IO.File.Exists(System.IO.Path.Combine(LangPack.Dir(), "zh-HANS.json")) && System.IO.File.Exists(System.IO.Path.Combine(LangPack.Dir(), "en-US.json")),
            "lang/zh-HANS.json and lang/en-US.json found");
        Func<string, string> Zh = LangPack.Text("zh-HANS"), En = LangPack.Text("en-US");
        void Same(string got, string want, string what) => check(got == want, $"{what}: '{got}' (expected '{want}')");
        Same(TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", true, Zh, "K3"), "车辆起步，请站稳扶好。 K3路，开往码头路方向。 下一站：人民广场，请下车的乘客提前做好准备。", "Chinese leaving sentence with the line");
        Same(TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", true, Zh), "车辆起步，请站稳扶好。 本车开往码头路方向。 下一站：人民广场，请下车的乘客提前做好准备。", "without a line label");
        Same(TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", true, Zh, "高港2号专线", true), "车辆起步，请站稳扶好。 高港2号专线，开往码头路方向。 下一站：人民广场，请下车的乘客提前做好准备。", "a line's name: no 路 after it");
        Same(TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", true, Zh, "快3线", true), "车辆起步，请站稳扶好。 快3线，开往码头路方向。 下一站：人民广场，请下车的乘客提前做好准备。", "a number with its own word: no 路 either");
        Same(TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", false, Zh, "K3"), "车辆起步，请站稳扶好。 下一站：人民广场，请下车的乘客提前做好准备。", "terminus left out when not wanted");
        Same(TtsText.Sentence(AnnounceKind.Arriving, "人民广场", "码头路", true, Zh), "前方到站：人民广场，请下车的乘客从后门下车。", "approaching: not 'arrived'");
        Same(TtsText.Sentence(AnnounceKind.AtStop, "人民广场", "码头路", true, Zh), "人民广场站到了，请从后门下车，下车请注意安全。", "doors open: the station");
        Same(TtsText.Sentence(AnnounceKind.AtStop, "火车站", "码头路", true, Zh), "火车站到了，请从后门下车，下车请注意安全。", "a name ending in 站 is not doubled");
        Same(TtsText.Sentence(AnnounceKind.Skip, "北门", "码头路", true, Zh), "前方北门站无人上下车，本车不停靠。", "skipped stop");
        Same(TtsText.Sentence(AnnounceKind.Next, "码头路", "码头路", true, Zh, "K3"), "车辆起步，请站稳扶好。 下一站：终点站码头路，请带好随身物品，准备下车。", "next stop is the final stop");
        Same(TtsText.Sentence(AnnounceKind.AtStop, "码头路", "码头路", true, Zh), "终点站码头路站到了，请全体乘客带好随身物品下车，欢迎再次乘坐。", "at the final stop");
        Same(TtsText.Sentence(AnnounceKind.Next, "Pier Road", "Central", true, En, "12"), "The bus is moving off, please hold on. Route 12 to Central. The next stop is Pier Road.", "English leaving sentence");
        check(TtsText.Sentence(AnnounceKind.AtStop, "", "Central", true, En) == "", "no stop name: nothing to say");

        check(TtsText.CacheName("zh-CN-XiaoxiaoNeural", "下一站，人民广场。") == "61e582437acc4d6baf0ab9138b34078f9b9e91ef", "cache name is the SHA-1 of voice + text");
        check(TtsText.CacheName("a", "b") != TtsText.CacheName("a", "c"), "different texts, different files");

        string ssml = TtsText.Ssml("zh-CN-XiaoxiaoNeural", "A & B <c>");
        check(ssml.StartsWith("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>") && ssml.Contains("<voice name='zh-CN-XiaoxiaoNeural'>") && ssml.Contains("A &amp; B &lt;c&gt;") && ssml.EndsWith("</prosody></voice></speak>"), "SSML with the voice's language and escaped text");
        check(TtsText.AzureEndpoint("EastAsia ") == "https://eastasia.tts.speech.microsoft.com/cognitiveservices/v1", "Azure endpoint from the region");
        check(TtsText.AzureEndpoint("") == null && TtsText.AzureEndpoint("east asia") == null && TtsText.AzureEndpoint("x") == null, "bad regions are refused");
        check(TtsText.PlausibleAzureKey("0123456789abcdef0123456789abcdef") && !TtsText.PlausibleAzureKey("short") && !TtsText.PlausibleAzureKey("0123456789abcdef0123456789abcde!"), "key plausibility");

        check(TtsText.ParseProxy("127.0.0.1:7897", out string host, out int port) && host == "127.0.0.1" && port == 7897, "proxy host:port");
        check(TtsText.ParseProxy("http://user:pw@proxy.local:8080/", out host, out port) && host == "proxy.local" && port == 8080, "proxy URL with credentials");
        check(!TtsText.ParseProxy("", out _, out _) && !TtsText.ParseProxy("none", out _, out _) && !TtsText.ParseProxy("host", out _, out _) && !TtsText.ParseProxy("host:99999", out _, out _), "no proxy for empty, none, no port, bad port");
        check(TtsText.WindowsProxyServer("http=127.0.0.1:1;https=127.0.0.1:2;ftp=x") == "127.0.0.1:2" && TtsText.WindowsProxyServer("127.0.0.1:7897") == "127.0.0.1:7897" && TtsText.WindowsProxyServer("") == null, "Windows ProxyServer values");

        check(TtsText.PiperVoice("zh-HANS") == "zh_CN-huayan-medium" && TtsText.PiperVoice("en-US") == "en_US-lessac-medium" && TtsText.PiperVoice("ja-JP") == "en_US-lessac-medium", "Piper voices per locale");
        string[] urls = TtsText.PiperVoiceUrls("zh_CN-huayan-medium", ".onnx");
        check(urls.Length == 2 && urls[0] == "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx" && urls[1].StartsWith("https://hf-mirror.com/"), "Piper voice URLs with the mirror");
        check(TtsText.PiperVoiceUrls("bad", ".onnx").Length == 0, "a malformed voice id has no URLs");
        check(TtsText.PiperJsonLine("say \"hi\"\n", @"C:/a\b.wav") == "{\"text\":\"say \\\"hi\\\" \",\"output_file\":\"C:/a\\\\b.wav\"}", "Piper JSON line escapes quotes, backslashes and control characters");

        WindowsScriptTests.Run(check);
        WindowsVoiceGateTests.Run(check);
        string encoded = TtsText.EncodedCommand("exit 0");
        check(System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)) == "exit 0", "encoded command is UTF-16LE Base64");
    }
}
