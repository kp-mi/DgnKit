using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DgnKit.Model
{
    /// <summary>Resolves and loads reference attachments (recursively, with cycle protection).</summary>
    internal static class ReferenceLoader
    {
        public static void LoadAll(DgnFile master, DgnReadOptions opt, int depth)
        {
            if (depth >= opt.MaxReferenceDepth || master.Path == null) return;
            var cache = new Dictionary<string, DgnFile>(StringComparer.OrdinalIgnoreCase) { [master.Path] = master };
            Load(master, opt, depth, cache);
        }

        private static void Load(DgnFile file, DgnReadOptions opt, int depth, Dictionary<string, DgnFile> cache)
        {
            if (depth >= opt.MaxReferenceDepth) return;
            foreach (var model in file.Models)
            {
                foreach (var a in model.References)
                {
                    var path = Resolve(file, a, opt);
                    if (path == null) { file.Warn($"Reference not found: {a.FileName} (model '{model.Name}')"); continue; }
                    if (!cache.TryGetValue(path, out var rf))
                    {
                        try
                        {
                            var sub = new DgnReadOptions { ReadXAttributes = opt.ReadXAttributes, KeepRawBytes = opt.KeepRawBytes, LoadReferences = false };
                            rf = DgnFile.Read(File.ReadAllBytes(path), sub);
                            rf.Path = path;
                            cache[path] = rf;
                            Load(rf, opt, depth + 1, cache);
                        }
                        catch (Exception ex) { file.Warn($"Reference load failed {path}: {ex.Message}"); continue; }
                    }
                    a.Loaded = rf;
                    a.LoadedModel = string.IsNullOrEmpty(a.ModelName) ? rf.DefaultModel : rf.FindModel(a.ModelName!) ?? rf.DefaultModel;
                }
            }
        }

        private static string? Resolve(DgnFile master, ReferenceAttachment a, DgnReadOptions opt)
        {
            // A self-reference (same file, other model) has an empty file name.
            if (string.IsNullOrEmpty(a.FileName) && string.IsNullOrEmpty(a.FullPath)) return master.Path;
            var names = new[] { a.FullPath, a.FileName }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            var dirs = new List<string>();
            if (master.Path != null) dirs.Add(Path.GetDirectoryName(master.Path)!);
            dirs.AddRange(opt.ReferenceSearchPaths);
            foreach (var n in names)
            {
                if (Path.IsPathRooted(n) && File.Exists(n)) return Path.GetFullPath(n);
                string leaf = Path.GetFileName(n.Replace('/', '\\'));
                foreach (var d in dirs)
                {
                    var p = Path.Combine(d, leaf);
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
            }
            return null;
        }
    }
}
