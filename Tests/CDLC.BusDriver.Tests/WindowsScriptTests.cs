using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CDLC.BusDriver;

// The script of the Windows voices (TtsText.WindowsSpeechScript): it holds none of the values it is given, and, where Windows PowerShell exists,
// its own parser is asked what it makes of the script of texts that broke the old one. The old script put a text between single quotes and
// doubled only the ASCII apostrophe: PowerShell's single-quote characters include U+2018 to U+201B, so a station's name with one made the script
// fail to parse (the sentence was silent), and a name that closed the string had what followed parsed, and run, as a command.
static class WindowsScriptTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string text = "it's 下一站";
        const string file = "c:/tmp/a.wav";
        string script = TtsText.WindowsSpeechScript("zh-CN", text, file);
        check(script.Contains("System.Speech"), "Windows speech script: it uses System.Speech");
        check(!script.Contains("it's") && !script.Contains("下一站") && !script.Contains("a.wav") && !script.Contains("zh-CN"), "Windows speech script: it holds none of its values");
        check(script.Contains(Base64(text)) && script.Contains(Base64(file)) && script.Contains(Base64("zh-CN")) && script.Contains(Base64("zh")), "Windows speech script: its values are in it as Base64 of their UTF-8 bytes");

        string hostile = HostileText("c:/x/injected.txt");
        string hostileScript = TtsText.WindowsSpeechScript("zh-CN", hostile, "c:/a b/c.wav");
        check(!hostileScript.Contains("injected") && !hostileScript.Contains("Set-Content") && !hostileScript.Contains("$(Get-Date)"), "Windows speech script: a text with every kind of quote, a command and a subexpression reaches it only as Base64");
        bool ascii = true;
        foreach (char c in hostileScript)
            ascii &= c < 128;
        check(ascii, "Windows speech script: the script of any text is plain ASCII");
        check(TtsText.WindowsSpeechScript(null, null, null).Contains("System.Speech"), "Windows speech script: no values at all still make a script");
        check(TtsText.WindowsSpeechScript("zh-CN", "a", "b") == TtsText.WindowsSpeechScript("zh-CN", "a", "b") &&
              TtsText.WindowsSpeechScript("zh-CN", "a", "b") != TtsText.WindowsSpeechScript("zh-CN", "a2", "b"), "Windows speech script: it is a function of its values");
        TheParser(check);
    }

    static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // every kind of quote of PowerShell's, a command, a subexpression and a line break, in one text (the file is where a command would write)
    static string HostileText(string marker) =>
        "x\u2019; Set-Content -Path '" + marker + "' -Value injected; \u2018y \u201A\u201B\"`\n$(Get-Date)'";

    // What PowerShell's parser (it runs nothing) makes of a script, as the probe script reports it.
    sealed class Parsed
    {
        public int errors;
        public string[] commands;
        public string[] strings;
        public int expandables;
    }

    static Parsed Parse(string exe, string probe, string script)
    {
        string scriptFile = Path.Combine(Path.GetTempPath(), "cdlc-tts-script-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptFile, script, new UTF8Encoding(true));
        try
        {
            var info = new ProcessStartInfo(exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + probe + "\" -ScriptFile \"" + scriptFile + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? ParseJson(output) : null;
            }
        }
        finally { try { File.Delete(scriptFile); } catch (Exception) { } }
    }

    // the probe's one line of JSON: {"errors":0,"commands":["a"],"strings":["b"],"expandables":0} (PowerShell writes a one-element array as a bare value)
    static Parsed ParseJson(string json)
    {
        var parsed = new Parsed();
        parsed.errors = (int)Number(json, "errors");
        parsed.expandables = (int)Number(json, "expandables");
        parsed.commands = List(json, "commands");
        parsed.strings = List(json, "strings");
        return parsed;
    }

    static double Number(string json, string name)
    {
        int at = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal) + name.Length + 3;
        int end = at;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.')) end++;
        return double.Parse(json.Substring(at, end - at), System.Globalization.CultureInfo.InvariantCulture);
    }

    static string[] List(string json, string name)
    {
        int at = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal) + name.Length + 3;
        var items = new System.Collections.Generic.List<string>();
        if (json[at] == '[')
        {
            at++;
            while (json[at] != ']')
            {
                if (json[at] == ',') at++;
                items.Add(Quoted(json, ref at));
            }
        }
        else if (json[at] == '"')
            items.Add(Quoted(json, ref at));
        return items.ToArray();
    }

    static string Quoted(string json, ref int at)
    {
        var text = new StringBuilder();
        at++;
        while (json[at] != '"')
        {
            if (json[at] == '\\')
            {
                at++;
                char e = json[at];
                if (e == 'u') { text.Append((char)Convert.ToInt32(json.Substring(at + 1, 4), 16)); at += 4; }
                else text.Append(e == 'n' ? '\n' : e == 'r' ? '\r' : e == 't' ? '\t' : e);
            }
            else text.Append(json[at]);
            at++;
        }

        at++;
        return text.ToString();
    }

    // Windows PowerShell (where there is one) parses the script of each text: no syntax error, no command called except those the script itself
    // uses, no expandable string or subexpression, and the text is in the script only as the one constant of Base64. Nothing is run.
    static void TheParser(Action<bool, string> check)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string probe = FindUp("probe-ast.ps1");
        if (!File.Exists(exe) || probe == null)
        {
            Console.WriteLine("  (no Windows PowerShell or no probe script here: the script was not parsed)");
            return;
        }

        string marker = "c:/x/injected-" + Guid.NewGuid().ToString("N") + ".txt";
        string[] texts =
        {
            "Zhongshan Road",
            "Zhang's Road",
            "Zhang\u2019s Road",
            "\u2018Pier\u2019 \u201AEast\u201B",
            "x\u2019; Set-Content -LiteralPath '" + marker + "' -Value injected; \u2018y",
            "x'; Set-Content -LiteralPath '" + marker + "' -Value injected; '",
            "x\"; Set-Content -LiteralPath '" + marker + "' -Value injected; \"y",
            "$(Set-Content -LiteralPath '" + marker + "' -Value injected)",
            "`\"; & { Set-Content -LiteralPath '" + marker + "' -Value injected }",
            HostileText(marker),
            "下一站，人民广场。",
        };

        // the commands a script of this generator calls on its own, whatever the text (the baseline: a plain text)
        Parsed baseline = Parse(exe, probe, TtsText.WindowsSpeechScript("xx-YY", "plain", "c:/a.wav"));
        check(baseline != null && baseline.errors == 0, "the script of a plain text parses (the baseline of the others)");
        if (baseline == null) return;
        foreach (string text in texts)
        {
            Parsed p = Parse(exe, probe, TtsText.WindowsSpeechScript("xx-YY", text, "c:/a.wav"));
            string shown = (text.Length > 30 ? text.Substring(0, 30) : text).Replace('\n', ' ');
            check(p != null && p.errors == 0, "PowerShell parses the script of \"" + shown + "\" without a syntax error (" + (p == null ? "no answer" : p.errors + " error(s)") + ")");
            if (p == null) continue;
            check(string.Join(",", p.commands) == string.Join(",", baseline.commands), "the script of \"" + shown + "\" calls only the commands of any script (" + string.Join(",", p.commands) + ")");
            check(p.expandables == baseline.expandables, "the script of \"" + shown + "\" has no expandable string or subexpression more than any script");
            check(Array.IndexOf(p.strings, Base64(text)) >= 0, "the text of \"" + shown + "\" is in its script as one constant of Base64");
        }
    }

    static string FindUp(string name)
    {
        for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            foreach (string candidate in new[] { Path.Combine(dir, name), Path.Combine(dir, "Tests", name), Path.Combine(dir, "Tests", "WindowsScript", name) })
                if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
