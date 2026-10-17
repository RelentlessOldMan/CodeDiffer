namespace CodeDiffer.Core.Model;

/// <summary>
/// What kind of file a path is, for filtering a compare's list (a view, not a verdict: it never changes what a file's
/// status or reason is, and isn't part of the delta contract). The tokens are the lowercase names.
/// </summary>
public enum FileType
{
    /// <summary>Source code: a language the compare reads (C, C++, C#, scripts, shaders…).</summary>
    Code,
    /// <summary>Project, build and configuration files: makefiles, solutions and projects, JSON/YAML/XML/INI…</summary>
    Build,
    /// <summary>Text that is neither: notes, logs, tables, and text files of a kind not known.</summary>
    Docs,
    /// <summary>Images, libraries, archives and any other binary file.</summary>
    Binary,
}

/// <summary>
/// The <see cref="FileType"/> of a path, from a built-in table: a known file name first (CMakeLists.txt is build, not
/// docs), then the extension, then whether its bytes are binary. A file that lands in the wrong bucket is fixed here.
/// </summary>
public static class FileTypes
{
    public static string Token(FileType t) => t switch
    {
        FileType.Code => "code",
        FileType.Build => "build",
        FileType.Docs => "docs",
        FileType.Binary => "binary",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };

    public static bool TryParse(string token, out FileType type)
    {
        foreach (var t in Enum.GetValues<FileType>())
            if (string.Equals(Token(t), token.Trim(), StringComparison.OrdinalIgnoreCase)) { type = t; return true; }
        type = default;
        return false;
    }

    /// <summary>A path's type. <paramref name="binary"/>: its bytes were found binary (a modified file's reason).</summary>
    public static FileType Of(string path, bool binary = false)
    {
        if (binary) return FileType.Binary;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (Names.TryGetValue(name, out var known)) return known;
        int dot = name.LastIndexOf('.');
        // ".gitignore" is a name with no extension; "a.vcxproj.filters" is looked up as ".filters".
        if (dot <= 0) return name.StartsWith('.') && Extensions.TryGetValue(name, out var dotted) ? dotted : FileType.Docs;
        return Extensions.TryGetValue(name[dot..], out var t) ? t : FileType.Docs;
    }

    private static readonly Dictionary<string, FileType> Names = Table(
        (FileType.Build, ["CMakeLists.txt", "Makefile", "GNUmakefile", "Kbuild", "Kconfig", "Dockerfile", "Containerfile",
            "Jenkinsfile", "Vagrantfile", "Rakefile", "Gemfile", "Gemfile.lock", "Pipfile", "Pipfile.lock", "SConstruct", "SConscript",
            "BUILD", "BUILD.bazel", "WORKSPACE", "WORKSPACE.bazel", "MODULE.bazel", "meson.build", "meson_options.txt",
            "requirements.txt", "constraints.txt", "go.mod", "go.sum", "Cargo.lock", "configure.ac", "configure.in", "Makefile.am",
            "Makefile.in", "Doxyfile", "premake5.lua", "build.gradle", "settings.gradle", "gradlew", "gradlew.bat"]),
        (FileType.Docs, ["README", "LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "CONTRIBUTORS", "CHANGELOG", "CHANGES",
            "HISTORY", "NEWS", "TODO", "INSTALL"]));

    private static readonly Dictionary<string, FileType> Extensions = Table(
        (FileType.Code, [".c", ".h", ".cc", ".cpp", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".tcc",
            ".m", ".mm", ".cs", ".csx", ".fs", ".fsi", ".fsx", ".vb", ".java", ".kt", ".kts", ".scala", ".groovy", ".clj", ".js", ".jsx",
            ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte", ".py", ".pyi", ".pyw", ".pyx", ".rb", ".go", ".rs", ".swift", ".php",
            ".pl", ".pm", ".lua", ".dart", ".r", ".jl", ".hs", ".ml", ".mli", ".ex", ".exs", ".erl", ".el", ".lisp", ".scm", ".zig",
            ".nim", ".d", ".pas", ".f", ".f90", ".f95", ".for", ".ada", ".adb", ".ads", ".asm", ".s", ".inc", ".cu", ".cuh", ".hlsl",
            ".hlsli", ".fx", ".fxh", ".glsl", ".vert", ".frag", ".geom", ".comp", ".tesc", ".tese", ".metal", ".usf", ".ush", ".shader",
            ".cginc", ".compute", ".wgsl", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".ksh", ".fish", ".vbs",
            ".awk", ".sed", ".tcl", ".sql", ".proto", ".idl", ".thrift", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".xaml"]),
        (FileType.Build, [".sln", ".slnx", ".slnf", ".vcxproj", ".vcproj", ".filters", ".csproj", ".vbproj", ".fsproj", ".props",
            ".targets", ".proj", ".nuspec", ".pbxproj", ".xcconfig", ".xcscheme", ".cmake", ".mk", ".mak", ".make", ".gradle", ".bazel",
            ".bzl", ".gn", ".gni", ".ninja", ".pri", ".pro", ".qbs", ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml", ".ini",
            ".cfg", ".conf", ".config", ".xml", ".xsd", ".xsl", ".xslt", ".plist", ".manifest", ".rc", ".def", ".ld", ".lds",
            ".properties", ".env", ".editorconfig", ".gitignore", ".gitattributes", ".gitmodules", ".clang-format", ".clang-tidy",
            ".dockerignore", ".npmrc", ".lock", ".spec", ".vsconfig", ".runsettings", ".ruleset", ".resx", ".pdl"]),
        (FileType.Docs, [".txt", ".text", ".md", ".markdown", ".rst", ".adoc", ".asciidoc", ".tex", ".org", ".log", ".csv", ".tsv",
            ".rtf", ".svg", ".diff", ".patch", ".1", ".man"]),
        (FileType.Binary, [".exe", ".dll", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".pdb", ".ilk", ".exp", ".pch", ".ipch",
            ".idb", ".map", ".elf", ".hex", ".bin", ".img", ".iso", ".rom", ".dat", ".db", ".sqlite", ".mdb", ".zip", ".7z", ".rar",
            ".gz", ".tgz", ".bz2", ".xz", ".zst", ".tar", ".cab", ".msi", ".nupkg", ".snupkg", ".jar", ".war", ".class", ".pyc", ".pyo",
            ".wasm", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".cur", ".tga", ".dds", ".psd", ".tif", ".tiff", ".webp", ".exr",
            ".hdr", ".ktx", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".mp3", ".wav", ".ogg", ".flac",
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".ttf", ".otf", ".woff", ".woff2", ".eot", ".fbx", ".blend", ".uasset", ".umap",
            ".pak", ".res", ".snk", ".pfx", ".cer", ".keystore"]));

    private static Dictionary<string, FileType> Table(params (FileType Type, string[] Keys)[] groups)
    {
        var d = new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, keys) in groups)
            foreach (var k in keys) d.Add(k, type); // Add: a key in two groups is a mistake in the table
        return d;
    }
}
