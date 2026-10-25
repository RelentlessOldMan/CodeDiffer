using System.Buffers;
using System.Text;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// How a language writes comments, for <see cref="ChangeNoise.Comments"/>: line markers, block pairs, and the quotes whose
/// strings a marker inside isn't one in. A string runs to its closing quote or the end of its line (a backslash escapes,
/// where <see cref="Escapes"/>), so a quote read wrongly misleads one line, not the rest of the file. Taken out of both
/// sides alike: a misread comment can only make a change look like noise when both sides misread it the same way.
/// </summary>
public sealed record CommentSyntax(string[] Line, (string Open, string Close)[] Block, string Quotes, bool Escapes)
{
    /// <summary>A <c>#</c> line marker opens a comment only at a line's start or after whitespace (<c>$#</c>, <c>${#x}</c>
    /// in a shell aren't comments); <c>;</c> in an INI file, and <c>REM</c> / <c>::</c> in batch, only first on a line.</summary>
    public bool HashAfterSpace { get; init; } = true;
    public string[] LineStartOnly { get; init; } = [];

    private static readonly CommentSyntax C = new(["//"], [("/*", "*/")], "\"'", true);
    private static readonly CommentSyntax Hash = new(["#"], [], "\"'", true);
    private static readonly CommentSyntax Markup = new([], [("<!--", "-->")], "", false);

    private static readonly Dictionary<string, CommentSyntax> ByExtension = Build();

    private static Dictionary<string, CommentSyntax> Build()
    {
        var d = new Dictionary<string, CommentSyntax>(StringComparer.OrdinalIgnoreCase);
        void Add(CommentSyntax s, params string[] exts) { foreach (var e in exts) d.Add(e, s); }
        Add(C, ".c", ".h", ".cc", ".cpp", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".tcc", ".m", ".mm",
            ".cs", ".csx", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".go",
            ".rs", ".swift", ".dart", ".cu", ".cuh", ".hlsl", ".hlsli", ".fx", ".fxh", ".glsl", ".vert", ".frag", ".geom", ".comp",
            ".tesc", ".tese", ".metal", ".usf", ".ush", ".shader", ".cginc", ".compute", ".wgsl", ".proto", ".idl", ".thrift",
            ".jsonc", ".json5", ".rc", ".zig", ".d");
        Add(C with { Line = ["//", "#"] }, ".php");
        Add(C with { Block = [("/*", "*/"), ("(*", "*)")] }, ".fs", ".fsi", ".fsx");
        Add(new([], [("/*", "*/")], "\"'", true), ".css");
        Add(C, ".scss", ".sass", ".less");
        Add(Hash, ".py", ".pyi", ".pyw", ".pyx", ".rb", ".sh", ".bash", ".zsh", ".ksh", ".fish", ".pl", ".pm", ".r", ".jl", ".ex", ".exs",
            ".nim", ".tcl", ".awk", ".sed", ".yaml", ".yml", ".toml", ".cfg", ".conf", ".properties", ".env", ".editorconfig",
            ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".npmrc", ".cmake", ".mk", ".mak", ".make", ".bzl",
            ".bazel", ".gn", ".gni", ".pri", ".pro", ".spec", ".clang-format", ".clang-tidy");
        Add(Hash with { Block = [("<#", "#>")], Escapes = false }, ".ps1", ".psm1", ".psd1"); // a backtick escapes there, not read
        Add(Hash with { Line = ["#", "//"], Block = [("/*", "*/")] }, ".s");
        Add(new([";"], [], "\"'", false), ".asm", ".el", ".lisp", ".scm", ".clj", ".def");
        Add(new(["#"], [], "\"", false) { LineStartOnly = [";"] }, ".ini", ".inf", ".reg");
        Add(new(["--"], [("/*", "*/")], "'\"", false), ".sql");
        Add(new(["--"], [("--[[", "]]")], "\"'", true), ".lua");
        Add(new(["--"], [("{-", "-}")], "\"", true), ".hs");
        Add(new(["--"], [], "\"", false), ".ada", ".adb", ".ads");
        Add(new(["%"], [], "\"", false), ".tex", ".erl");
        Add(new(["'"], [], "\"", false) { LineStartOnly = ["REM ", "Rem ", "rem "] }, ".vb", ".vbs");
        Add(new([], [], "\"", false) { LineStartOnly = ["REM ", "Rem ", "rem ", "@REM ", "@rem ", "::"] }, ".bat", ".cmd");
        Add(Markup, ".html", ".htm", ".xml", ".xaml", ".xsd", ".xsl", ".xslt", ".svg", ".resx", ".manifest", ".nuspec", ".plist",
            ".vcxproj", ".vcproj", ".filters", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".proj", ".runsettings", ".ruleset");
        return d;
    }

    private static readonly Dictionary<string, CommentSyntax> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CMakeLists.txt"] = Hash, ["Makefile"] = Hash, ["GNUmakefile"] = Hash, ["Makefile.am"] = Hash, ["Makefile.in"] = Hash,
        ["Dockerfile"] = Hash, ["Containerfile"] = Hash, ["requirements.txt"] = Hash, ["constraints.txt"] = Hash,
        ["SConstruct"] = Hash, ["SConscript"] = Hash, ["BUILD"] = Hash, ["BUILD.bazel"] = Hash, ["WORKSPACE"] = Hash,
        ["meson.build"] = Hash, ["Kconfig"] = Hash, ["Kbuild"] = Hash, ["Rakefile"] = Hash, ["Gemfile"] = Hash, ["Pipfile"] = Hash,
        ["Doxyfile"] = Hash, ["Jenkinsfile"] = C,
    };

    /// <summary>Every name and extension the tables know (for the classifier pin).</summary>
    internal static IEnumerable<string> Known => ByName.Keys.Concat(ByExtension.Keys);

    /// <summary>A file's comment syntax by its name, then its extension; null when not known (prose, JSON, data).</summary>
    public static CommentSyntax? For(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (ByName.TryGetValue(name, out var s)) return s;
        int dot = name.LastIndexOf('.');
        var ext = dot > 0 ? name[dot..] : name.StartsWith('.') ? name : null;
        return ext is not null && ByExtension.TryGetValue(ext, out s) ? s : null;
    }

    /// <summary>The text with its comments taken out: a comment's characters dropped, its line breaks kept, and a space
    /// where it was (so <c>a/*x*/b</c> stays two tokens).</summary>
    public string Strip(string text)
    {
        // Only a line break, a quote or a marker's first character can start anything: the runs between are copied whole.
        var firsts = new HashSet<char> { '\n' };
        foreach (var m in Line.Concat(LineStartOnly).Concat(Block.Select(b => b.Open))) firsts.Add(m[0]);
        foreach (var q in Quotes) firsts.Add(q);
        var special = SearchValues.Create([.. firsts]);

        var o = new StringBuilder(text.Length);
        bool lineStart = true; // only whitespace so far on this line
        int i = 0;
        while (i < text.Length)
        {
            int next = text.AsSpan(i).IndexOfAny(special);
            int run = next < 0 ? text.Length - i : next;
            if (run > 0)
            {
                var span = text.AsSpan(i, run);
                o.Append(span);
                if (lineStart && !span.IsWhiteSpace()) lineStart = false;
                i += run;
                if (i >= text.Length) break;
            }
            char c = text[i];
            if (c == '\n') { o.Append(c); i++; lineStart = true; continue; }
            if (lineStart && Starts(text, i, LineStartOnly))
            {
                i = LineEnd(text, i);
                o.Append(' ');
                continue;
            }
            if (BlockAt(text, i) is var (open, close))
            {
                int end = text.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                o.Append(' ');
                for (int k = i; k < end; k++) if (text[k] == '\n') o.Append('\n');
                i = end;
                lineStart = false;
                continue;
            }
            if (LineMarkerAt(text, i))
            {
                i = LineEnd(text, i);
                o.Append(' ');
                continue;
            }
            if (Quotes.Contains(c))
            {
                int end = i + 1;
                while (end < text.Length && text[end] != '\n' && text[end] != c)
                    end += Escapes && text[end] == '\\' && end + 1 < text.Length && text[end + 1] != '\n' ? 2 : 1;
                if (end < text.Length && text[end] == c) end++;
                o.Append(text, i, end - i);
                i = end;
                lineStart = false;
                continue;
            }
            o.Append(c); // a marker's first character, not a marker here
            if (!char.IsWhiteSpace(c)) lineStart = false;
            i++;
        }
        return o.ToString();
    }

    private (string, string)? BlockAt(string s, int i)
    {
        foreach (var b in Block)
            if (At(s, i, b.Open)) return b;
        return null;
    }

    private bool LineMarkerAt(string s, int i)
    {
        foreach (var m in Line)
            if (At(s, i, m) && (m != "#" || !HashAfterSpace || i == 0 || char.IsWhiteSpace(s[i - 1]))) return true;
        return false;
    }

    private static bool Starts(string s, int i, string[] markers)
    {
        foreach (var m in markers)
            if (At(s, i, m)) return true;
        return false;
    }

    private static bool At(string s, int i, string m) => string.CompareOrdinal(s, i, m, 0, m.Length) == 0;

    private static int LineEnd(string s, int i)
    {
        int n = s.IndexOf('\n', i);
        return n < 0 ? s.Length : n;
    }
}
