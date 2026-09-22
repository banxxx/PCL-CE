using System.IO;
using System.Text;
using PCL.Core.App.Localization;
using PCL.Core.Utils.Hash;
using CompType = PCL.ModComp.CompType;
using LocalCompFile = PCL.ModLocalComp.LocalCompFile;

namespace PCL;

/// <summary>
///     下载前的本地查重：按内容哈希（SHA1/MD5）识别目标目录中已存在的完全相同文件，
///     哈希缺失时退化到归一化文件名（忽略大小写与 .disabled/.old 后缀）匹配。
/// </summary>
public static class ModCompLocalDup
{
    public enum DupKind
    {
        /// <summary>本地不存在。</summary>
        None,

        /// <summary>存在归一化同名文件，但无法确认内容相同。</summary>
        SameName,

        /// <summary>存在内容完全相同（哈希一致）的启用文件。</summary>
        Identical,

        /// <summary>存在内容完全相同但当前处于禁用状态（.disabled/.old）的文件。</summary>
        IdenticalDisabled
    }

    public sealed class DupResult
    {
        public DupKind Kind = DupKind.None;
        public string LocalPath = "";
    }

    private sealed class DirIndex
    {
        public string Fingerprint = "";

        // 哈希 -> 本地文件路径（仅启用文件）
        public readonly Dictionary<string, string> HashToPath = new(StringComparer.OrdinalIgnoreCase);

        // 哈希 -> 本地文件路径（首个命中的禁用文件）
        public readonly Dictionary<string, string> HashToPathDisabled = new(StringComparer.OrdinalIgnoreCase);

        // 归一化文件名 -> 本地文件路径（启用文件优先）
        public readonly Dictionary<string, (string Path, bool Disabled)> NameToPath =
            new(StringComparer.OrdinalIgnoreCase);
    }

    // 与模组更新检查共用同一份持久化哈希库，二次检测几乎零成本
    private static readonly Lazy<HashCache> SharedHashCache = new(() =>
        new HashCache(ModBase.pathTemp + @"Cache\HashCache.db"));

    private static readonly object ScanLock = new();

    // 键：目录完整路径|哈希算法名
    private static readonly Dictionary<string, DirIndex> Cache = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxScanFiles = 4000;
    private const int MaxScanDirectories = 3000;

    /// <summary>
    ///     检查 targetDir 中是否已存在与待下载文件相同的本地文件。
    ///     位于 targetPath 自身的命中不计入（保存对话框与文件校验器会静默处理）。
    /// </summary>
    public static DupResult Check(string targetDir, string targetPath, ModComp.CompFile file)
    {
        var result = new DupResult();
        if (string.IsNullOrEmpty(targetDir) || file.Type is CompType.ModPack or CompType.World)
            return result;

        try
        {
            if (!Directory.Exists(targetDir))
                return result;

            targetDir = Path.GetFullPath(targetDir);
            var targetFullPath = Path.GetFullPath(targetPath);
            var hash = file.Hash?.Trim() ?? "";
            var algo = hash.Length == 32 ? "MD5" : "SHA1";

            List<string> candidates;
            string fingerprint;
            lock (ScanLock)
            {
                (candidates, fingerprint) = CollectCandidates(targetDir, file.Type);
                var cacheKey = targetDir + "|" + algo;
                if (!Cache.TryGetValue(cacheKey, out var index) || index.Fingerprint != fingerprint)
                {
                    index = BuildIndex(candidates, fingerprint, algo);
                    Cache[cacheKey] = index;
                }

                // 哈希精确匹配
                if (hash.Length > 0)
                {
                    if (index.HashToPath.TryGetValue(hash, out var hit) &&
                        !StringEqualsPath(hit, targetFullPath))
                    {
                        result.Kind = DupKind.Identical;
                        result.LocalPath = hit;
                        return result;
                    }

                    if (index.HashToPathDisabled.TryGetValue(hash, out var hitDisabled) &&
                        !StringEqualsPath(hitDisabled, targetFullPath))
                    {
                        result.Kind = DupKind.IdenticalDisabled;
                        result.LocalPath = hitDisabled;
                        return result;
                    }
                }

                // 文件名归一化匹配（哈希不可用或不同名文件的兜底）
                var nameKey = NormalizeName(file.FileName);
                if (index.NameToPath.TryGetValue(nameKey, out var nameHit) &&
                    !StringEqualsPath(nameHit.Path, targetFullPath))
                {
                    result.Kind = DupKind.SameName;
                    result.LocalPath = nameHit.Path;
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[CompDup] 本地查重失败，按无重复处理", ModBase.LogLevel.Debug);
            result.Kind = DupKind.None;
        }

        return result;
    }

    /// <summary>
    ///     按查重结果向用户确认是否继续下载。返回 true 表示继续下载。必须在 UI 线程调用。
    /// </summary>
    public static bool ConfirmDup(DupResult dup)
    {
        switch (dup.Kind)
        {
            case DupKind.Identical:
            {
                var choice = ModMain.MyMsgBox(
                    Lang.Text("Download.Comp.Dup.Exists.Message", dup.LocalPath),
                    Lang.Text("Download.Comp.Dup.Exists.Title"),
                    Lang.Text("Download.Comp.Dup.SkipDownload"),
                    Lang.Text("Download.Comp.Dup.DownloadAnyway"));
                return choice == 2;
            }
            case DupKind.IdenticalDisabled:
            {
                var choice = ModMain.MyMsgBox(
                    Lang.Text("Download.Comp.Dup.Disabled.Message", dup.LocalPath),
                    Lang.Text("Download.Comp.Dup.Exists.Title"),
                    Lang.Text("Download.Comp.Dup.SkipDownload"),
                    Lang.Text("Download.Comp.Dup.DownloadAnyway"));
                return choice == 2;
            }
            case DupKind.SameName:
            {
                var choice = ModMain.MyMsgBox(
                    Lang.Text("Download.Comp.Dup.SameName.Message", dup.LocalPath),
                    Lang.Text("Download.Comp.Dup.SameName.Title"),
                    Lang.Text("Download.Comp.Dup.DownloadAnyway"),
                    Lang.Text("Common.Action.Cancel"),
                    isWarn: true);
                return choice == 1;
            }
            default:
                return true;
        }
    }

    private static (List<string>, string) CollectCandidates(string targetDir, CompType compType)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(targetDir);
        var visitedDirs = 0;
        while (stack.Count > 0 && visitedDirs < MaxScanDirectories && files.Count <= MaxScanFiles)
        {
            var current = stack.Pop();
            visitedDirs++;
            try
            {
                foreach (var sub in Directory.GetDirectories(current))
                    stack.Push(sub);
            }
            catch
            {
                // 忽略无权限的子目录
            }

            try
            {
                foreach (var file in Directory.GetFiles(current))
                {
                    if (LocalCompFile.IsCompFile(file, compType))
                        files.Add(file);
                }
            }
            catch
            {
                // 忽略无权限的目录
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        var fp = new StringBuilder();
        foreach (var file in files)
            try
            {
                var info = new FileInfo(file);
                fp.Append(file).Append('|').Append(info.Length).Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            catch
            {
                fp.Append(file).Append("|0|0\n");
            }

        return (files, fp.ToString());
    }

    private static DirIndex BuildIndex(List<string> candidates, string fingerprint, string algo)
    {
        var index = new DirIndex { Fingerprint = fingerprint };
        if (candidates.Count > MaxScanFiles)
        {
            ModBase.Log($"[CompDup] 本地文件过多（{candidates.Count}），跳过哈希扫描", ModBase.LogLevel.Debug);
            return index;
        }

        foreach (var path in candidates)
        {
            var disabled = IsDisabled(path);
            var nameKey = NormalizeName(Path.GetFileName(path));
            if (!index.NameToPath.TryGetValue(nameKey, out var existing) ||
                (existing.Disabled && !disabled))
                index.NameToPath[nameKey] = (path, disabled);

            string hash;
            try
            {
                hash = algo == "MD5"
                    ? SharedHashCache.Value.GetMD5Async(path).GetAwaiter().GetResult()
                    : SharedHashCache.Value.GetSHA1Async(path).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[CompDup] 计算本地文件哈希失败：{path}", ModBase.LogLevel.Debug);
                continue;
            }

            if (string.IsNullOrEmpty(hash))
                continue;
            if (!disabled)
                index.HashToPath[hash] = path;
            else if (!index.HashToPath.ContainsKey(hash) && !index.HashToPathDisabled.ContainsKey(hash))
                index.HashToPathDisabled[hash] = path;
        }

        return index;
    }

    private static bool IsDisabled(string path)
    {
        return path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string nameOrPath)
    {
        var name = Path.GetFileName(nameOrPath);
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];
        else if (name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            name = name[..^".old".Length];
        return name.ToLowerInvariant();
    }

    private static bool StringEqualsPath(string left, string right)
    {
        return left is not null && string.Equals(
            Path.GetFullPath(left), right, StringComparison.OrdinalIgnoreCase);
    }
}
