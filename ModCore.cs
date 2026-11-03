using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VbIO = Microsoft.VisualBasic.FileIO;

namespace CotLModManager;

class PluginInfo
{
    public string Guid, Name, Version;
    public List<(string Guid, string MinVersion)> HardDeps = new();
}

class ModEntry
{
    public string Path;
    public bool Enabled;
    public bool IsFolder;
    public List<PluginInfo> Plugins = new();
    public string Status = "";
    public bool Problem;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string DisplayName => Plugins.Count > 0
        ? string.Join(" + ", Plugins.Select(p => p.Name).Distinct())
        : (IsFolder ? FileName : System.IO.Path.GetFileNameWithoutExtension(Path));
    public string VersionText => string.Join(", ", Plugins.Select(p => p.Version).Distinct());
}

class InstallException : Exception { public InstallException(string m) : base(m) { } }

/// <summary>Reads [BepInPlugin]/[BepInDependency] attributes from plugin DLLs without loading them.</summary>
static class PluginScanner
{
    public static List<PluginInfo> ScanPath(string path)
    {
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).SelectMany(ScanDll).ToList();
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return ScanDll(path);
        return new List<PluginInfo>();
    }

    public static List<PluginInfo> ScanDll(string path)
    {
        var list = new List<PluginInfo>();
        try
        {
            // Read into memory so the file is never locked (we need to move it later).
            using var pe = new PEReader(new MemoryStream(File.ReadAllBytes(path)));
            if (!pe.HasMetadata) return list;
            var md = pe.GetMetadataReader();
            var prov = new AttrTypeProvider();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                PluginInfo pi = null;
                var deps = new List<(string, string)>();
                foreach (var ah in td.GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(ah);
                    var name = AttrName(md, ca);
                    if (name != "BepInPlugin" && name != "BepInDependency") continue;
                    CustomAttributeValue<string> v;
                    try { v = ca.DecodeValue(prov); } catch { continue; }
                    var a = v.FixedArguments;
                    if (name == "BepInPlugin" && a.Length >= 3)
                        pi = new PluginInfo { Guid = a[0].Value as string, Name = a[1].Value as string, Version = a[2].Value as string };
                    else if (name == "BepInDependency" && a.Length >= 1 && a[0].Value is string g)
                    {
                        string minVer = null; bool hard = true;
                        if (a.Length >= 2)
                        {
                            if (a[1].Value is string s) minVer = s;
                            else if (a[1].Value is int f) hard = (f & 1) != 0; // DependencyFlags.HardDependency = 1
                        }
                        if (hard) deps.Add((g, minVer));
                    }
                }
                if (pi != null && pi.Guid != null) { pi.HardDeps = deps; list.Add(pi); }
            }
        }
        catch { /* not a .NET assembly or unreadable */ }
        return list;
    }

    static string AttrName(MetadataReader md, CustomAttribute ca)
    {
        EntityHandle t;
        if (ca.Constructor.Kind == HandleKind.MemberReference) t = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition) t = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();
        else return null;
        if (t.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)t).Name);
        if (t.Kind == HandleKind.TypeDefinition) return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)t).Name);
        return null;
    }

    class AttrTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
        { var d = r.GetTypeDefinition(h); return r.GetString(d.Namespace) + "." + r.GetString(d.Name); }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        { var d = r.GetTypeReference(h); return r.GetString(d.Namespace) + "." + r.GetString(d.Name); }
        public string GetTypeFromSerializedName(string n) => n;
        public PrimitiveTypeCode GetUnderlyingEnumType(string t) => PrimitiveTypeCode.Int32; // BepInEx enums are int
        public bool IsSystemType(string t) => t == "System.Type";
    }
}

class ModStore
{
    public const string GameExe = "Cult Of The Lamb.exe";
    public const string SteamAppId = "1313140";

    public string Game;
    public ModStore(string game) { Game = game; }

    public string BepInExDir => Path.Combine(Game, "BepInEx");
    public string PluginsDir => Path.Combine(BepInExDir, "plugins");
    public string DisabledDir => Path.Combine(BepInExDir, "plugins_disabled");

    public static bool IsGameDir(string p) => !string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, GameExe));
    public static bool GameRunning() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).Length > 0;

    public string BepInExVersion()
    {
        var dll = Path.Combine(BepInExDir, "core", "BepInEx.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(Game, "winhttp.dll"))) return null;
        return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "?";
    }

    public static string DetectGame()
    {
        var libs = new List<string>();
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam != null)
        {
            libs.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        foreach (var lib in libs)
        {
            var p = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", "Cult of the Lamb"));
            if (IsGameDir(p)) return p;
        }
        return null;
    }

    // ---------- listing ----------

    public List<ModEntry> Load()
    {
        var mods = new List<ModEntry>();
        foreach (var (dir, enabled) in new[] { (PluginsDir, true), (DisabledDir, false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var p in Directory.EnumerateFileSystemEntries(dir))
            {
                bool isDir = Directory.Exists(p);
                if (!isDir && !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                mods.Add(new ModEntry { Path = p, Enabled = enabled, IsFolder = isDir, Plugins = PluginScanner.ScanPath(p) });
            }
        }
        Analyze(mods);
        return mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Analyze(List<ModEntry> mods)
    {
        var enabled = new Dictionary<string, (ModEntry Mod, PluginInfo P)>(StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<string>();
        foreach (var m in mods)
            foreach (var p in m.Plugins)
            {
                all[p.Guid] = p;
                if (!m.Enabled) continue;
                if (enabled.TryGetValue(p.Guid, out var other) && other.Mod != m)
                    dupes.Add(p.Guid);
                else enabled[p.Guid] = (m, p);
            }

        foreach (var m in mods)
        {
            var notes = new List<string>();
            if (m.Plugins.Count == 0) notes.Add("No BepInEx plugin found (library or not a mod?)");
            if (m.Enabled)
            {
                foreach (var p in m.Plugins)
                {
                    if (dupes.Contains(p.Guid, StringComparer.OrdinalIgnoreCase))
                    { notes.Add($"Duplicate of another installed copy of {p.Name}"); m.Problem = true; }
                    foreach (var (g, minVer) in p.HardDeps)
                    {
                        if (enabled.TryGetValue(g, out var dep))
                        {
                            if (minVer != null && Version.TryParse(minVer, out var need) && Version.TryParse(dep.P.Version, out var have) && have < need)
                            { notes.Add($"Needs {dep.P.Name} ≥ {minVer} (have {dep.P.Version})"); m.Problem = true; }
                        }
                        else if (all.TryGetValue(g, out var dis))
                        { notes.Add($"Needs {dis.Name} — it is disabled"); m.Problem = true; }
                        else
                        { notes.Add($"Missing dependency: {g}" + (minVer != null ? $" ≥ {minVer}" : "")); m.Problem = true; }
                    }
                }
            }
            m.Status = notes.Count > 0 ? string.Join("; ", notes) : (m.Enabled ? "OK" : "Disabled");
        }
    }

    /// <summary>Enabled mods that hard-depend on any plugin in <paramref name="m"/>.</summary>
    public static List<ModEntry> Dependents(List<ModEntry> mods, ModEntry m)
    {
        var guids = new HashSet<string>(m.Plugins.Select(p => p.Guid), StringComparer.OrdinalIgnoreCase);
        return mods.Where(o => o != m && o.Enabled && o.Plugins.Any(p => p.HardDeps.Any(d => guids.Contains(d.Guid)))).ToList();
    }

    // ---------- enable / disable / remove ----------

    public void SetEnabled(ModEntry m, bool enable)
    {
        if (m.Enabled == enable) return;
        var dstDir = enable ? PluginsDir : DisabledDir;
        Directory.CreateDirectory(dstDir);
        var dst = Path.Combine(dstDir, m.FileName);
        if (File.Exists(dst) || Directory.Exists(dst))
            throw new IOException($"'{m.FileName}' already exists in {Path.GetFileName(dstDir)}. Remove one copy first.");
        if (m.IsFolder) Directory.Move(m.Path, dst); else File.Move(m.Path, dst);
        m.Path = dst; m.Enabled = enable;
    }

    public static void Recycle(string path)
    {
        if (Directory.Exists(path))
            VbIO.FileSystem.DeleteDirectory(path, VbIO.UIOption.OnlyErrorDialogs, VbIO.RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
            VbIO.FileSystem.DeleteFile(path, VbIO.UIOption.OnlyErrorDialogs, VbIO.RecycleOption.SendToRecycleBin);
    }

    // ---------- install ----------

    static readonly string[] ArchiveExts = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab" };
    public static bool IsArchive(string p) => ArchiveExts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    public static bool IsInstallable(string p) =>
        Directory.Exists(p) || p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || IsArchive(p);

    static string ArchiveBaseName(string p)
    {
        var n = Path.GetFileName(p);
        foreach (var e in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" }.Concat(ArchiveExts))
            if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return n[..^e.Length];
        return Path.GetFileNameWithoutExtension(n);
    }

    /// <summary>
    /// Installs a mod from an archive (zip/7z/rar/tar...), a folder or a single DLL, working out the layout automatically.
    /// Handles BepInEx\plugins\... packages, Thunderstore packages, Nexus zips with a loose DLL, nested folders,
    /// archives inside archives, and the BepInEx loader itself (installed into the game root).
    /// Returns the names of what was installed.
    /// </summary>
    /// <param name="confirm">Asked before replacing existing mods / updating the loader; return false to abort.</param>
    public List<string> Install(string source, Func<string, bool> confirm, int depth = 0)
    {
        string temp = null;
        try
        {
            string work, baseName;
            if (Directory.Exists(source))
            {
                work = source;
                baseName = CleanName(Path.GetFileName(source.TrimEnd('\\', '/')));
            }
            else if (source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                RequireBepInEx();
                return CopyIntoPlugins(new() { (source, Path.GetFileName(source)) }, null, confirm);
            }
            else if (IsArchive(source))
            {
                temp = Path.Combine(Path.GetTempPath(), "CotLModManager", System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                Extract(source, temp);
                work = temp;
                baseName = CleanName(ArchiveBaseName(source));
            }
            else throw new InstallException("Unsupported file type. Use .zip, .7z, .rar, .tar(.gz), .dll or a folder.");

            // The BepInEx loader pack goes into the game root, not into plugins.
            var loaderRoot = FindLoaderRoot(work);
            if (loaderRoot != null) return InstallLoader(loaderRoot, confirm);

            // No DLLs but archives inside (e.g. a mod pack of zips): install each inner archive.
            if (!Directory.EnumerateFiles(work, "*.dll", SearchOption.AllDirectories).Any())
            {
                var inner = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Where(IsArchive).ToList();
                if (inner.Count > 0 && depth < 3)
                    return inner.SelectMany(a => Install(a, confirm, depth + 1)).ToList();
            }

            RequireBepInEx();
            return CopyIntoPlugins(Plan(work, baseName), work, confirm);
        }
        finally
        {
            if (temp != null) try { Directory.Delete(temp, true); } catch { }
        }
    }

    void RequireBepInEx()
    {
        if (BepInExVersion() == null)
            throw new InstallException("BepInEx is not installed in the game folder.\nInstall the BepInEx 5 archive first (you can add it here too).");
        Directory.CreateDirectory(PluginsDir);
    }

    List<string> CopyIntoPlugins(List<(string Src, string Dest)> items, string work, Func<string, bool> confirm)
    {
        // Existing mods that would be replaced: same file/folder name, or same plugin GUID (= an update).
        var current = Load();
        var newGuids = new HashSet<string>(items.SelectMany(i => PluginScanner.ScanPath(i.Src)).Select(p => p.Guid), StringComparer.OrdinalIgnoreCase);
        var destNames = new HashSet<string>(items.Select(i => i.Dest), StringComparer.OrdinalIgnoreCase);
        var replace = current.Where(m => destNames.Contains(m.FileName) || m.Plugins.Any(p => newGuids.Contains(p.Guid))).ToList();
        if (replace.Count > 0)
        {
            var msg = "This will replace (old copy goes to the Recycle Bin):\n\n" +
                      string.Join("\n", replace.Select(m => $"  • {m.DisplayName} {m.VersionText}  [{m.FileName}]")) + "\n\nContinue?";
            if (!confirm(msg)) return new List<string>();
            foreach (var m in replace) Recycle(m.Path);
        }

        foreach (var (src, dest) in items)
        {
            var dst = Path.Combine(PluginsDir, dest);
            if (Directory.Exists(src)) CopyDir(src, dst); else File.Copy(src, dst, true);
        }

        // Packages laid out as BepInEx\... may also ship patchers/config/translations.
        if (work != null)
        {
            var bep = FindBepInExDir(work);
            if (bep != null)
                foreach (var sub in Directory.GetDirectories(bep))
                {
                    var n = Path.GetFileName(sub).ToLowerInvariant();
                    if (n == "plugins" || n == "core") continue;      // plugins handled above; never touch the loader
                    CopyDir(sub, Path.Combine(BepInExDir, Path.GetFileName(sub)), overwrite: n != "config"); // keep user configs
                }
        }
        return items.Select(i => i.Dest).ToList();
    }

    /// <summary>Folder that contains BepInEx\core\BepInEx.Preloader.dll (= a BepInEx 5 loader package), or null.</summary>
    static string FindLoaderRoot(string work)
    {
        var pre = Directory.EnumerateFiles(work, "BepInEx.Preloader.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (pre == null)
        {
            if (Directory.EnumerateFiles(work, "BepInEx.Unity.IL2CPP.dll", SearchOption.AllDirectories).Any() ||
                Directory.EnumerateFiles(work, "BepInEx.Unity.Mono.Preloader.dll", SearchOption.AllDirectories).Any())
                throw new InstallException("This is BepInEx 6. Cult of the Lamb mods need BepInEx 5 (x64, Mono).");
            return null;
        }
        var core = Path.GetDirectoryName(pre);                  // ...\BepInEx\core
        return Path.GetDirectoryName(Path.GetDirectoryName(core));
    }

    List<string> InstallLoader(string root, Func<string, bool> confirm)
    {
        var ver = FileVersionInfo.GetVersionInfo(Path.Combine(root, "BepInEx", "core", "BepInEx.dll")).FileVersion ?? "?";
        if (!File.Exists(Path.Combine(root, "winhttp.dll")))
            throw new InstallException("This BepInEx package has no winhttp.dll, so it isn't the Windows build.");
        var cur = BepInExVersion();
        var msg = cur == null
            ? $"This is the BepInEx {ver} mod loader.\nInstall it into the game folder?"
            : $"This is the BepInEx {ver} mod loader (installed: {cur}).\nReplace the loader files? Your plugins and configs are kept.";
        if (!confirm(msg)) return new List<string>();

        var cfg = Path.Combine("BepInEx", "config");
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            var dst = Path.Combine(Game, rel);
            if (rel.StartsWith(cfg, StringComparison.OrdinalIgnoreCase) && File.Exists(dst)) continue; // keep the user's BepInEx.cfg
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(f, dst, true);
        }
        Directory.CreateDirectory(PluginsDir);
        return new() { $"BepInEx loader {ver}" };
    }

    static void Extract(string archive, string dest)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-xf"); psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dest);
        using var p = Process.Start(psi);
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InstallException("Could not extract archive:\n" + err.Trim());
    }

    static string FindBepInExDir(string root)
    {
        if (Path.GetFileName(root).Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) return root;
        return Directory.EnumerateDirectories(root, "BepInEx", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>Works out what to copy into BepInEx\plugins from an extracted/selected mod folder.</summary>
    static List<(string, string)> Plan(string work, string name)
    {
        var allDlls = Directory.GetFiles(work, "*.dll", SearchOption.AllDirectories);
        if (allDlls.Length == 0) throw new InstallException("No .dll found — this doesn't look like a BepInEx mod.");

        // 1) Explicit BepInEx\plugins\... layout: install its children as-is.
        var bep = FindBepInExDir(work);
        var bepPlugins = bep == null ? null : Path.Combine(bep, "plugins");
        if (bepPlugins != null && Directory.Exists(bepPlugins))
        {
            var children = Directory.GetDirectories(bepPlugins).Concat(Directory.GetFiles(bepPlugins, "*.dll")).ToList();
            if (children.Count == 0) throw new InstallException("BepInEx\\plugins in this package is empty.");
            return children.Select(c => (c, Path.GetFileName(c))).ToList();
        }

        // 2) Thunderstore layout: manifest.json + plugins\
        var manifest = Path.Combine(work, "manifest.json");
        var tsPlugins = Path.Combine(work, "plugins");
        if (File.Exists(manifest) && Directory.Exists(tsPlugins))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is string s && s.Length > 0) name = CleanName(s);
            }
            catch { }
            var dirs = Directory.GetDirectories(tsPlugins);
            if (dirs.Length == 1 && Directory.GetFiles(tsPlugins).Length == 0)
                return new() { (dirs[0], Path.GetFileName(dirs[0])) };
            return new() { (tsPlugins, name) };
        }

        // 3) Generic: unwrap single-folder nesting, then a lone DLL goes in loose, anything else as a folder.
        var root = work;
        while (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root).Length == 1)
        {
            root = Directory.GetDirectories(root)[0];
            name = Path.GetFileName(root);
        }
        var files = Directory.GetFiles(root);
        var rootDlls = files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        bool onlyDocsBesides = files.Except(rootDlls).All(f => Regex.IsMatch(Path.GetFileName(f), @"(?i)^(readme|license|changelog|icon|manifest).*|\.(md|txt|png|jpg|json)$"));
        if (Directory.GetDirectories(root).Length == 0 && rootDlls.Count == 1 && onlyDocsBesides)
            return new() { (rootDlls[0], Path.GetFileName(rootDlls[0])) };
        return new() { (root, name) };
    }

    /// <summary>"Inspire All-69-1-1-0-1772044929" → "Inspire All", "Namify 15 0.2.4 2026-09-13T07-04Z X" → "Namify".</summary>
    public static string CleanName(string s)
    {
        s = Regex.Replace(s, @"(?i)\.zip(?=-|$)", "");
        var m = Regex.Match(s, @"^(.+?)-\d+(-\d+){2,}$");                                   // Nexus
        if (m.Success) s = m.Groups[1].Value;
        m = Regex.Match(s, @"^(.+?) \d+ \d+(\.\d+)+ \d{4}-\d{2}-\d{2}T\S+ \S+$");           // Nexus/r2 export
        if (m.Success) s = m.Groups[1].Value;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length > 0 ? s : "Mod";
    }

    static void CopyDir(string src, string dst, bool overwrite = true)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            if (overwrite || !File.Exists(t)) File.Copy(f, t, true);
        }
    }
}
