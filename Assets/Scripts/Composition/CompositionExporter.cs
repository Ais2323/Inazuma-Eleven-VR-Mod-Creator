using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using UnityEngine;
using G4Runtime;

namespace G4Composition
{
    public static class ExportPathGuard
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        public static string ResolveExistingDirectory(string path)
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Export path validation currently requires Windows.");
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
                var buffer = new StringBuilder(32768); uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
                return buffer.ToString().TrimEnd('\\');
            }
        }
        static bool Inside(string child, string root) => string.Equals(child, root, StringComparison.OrdinalIgnoreCase) || child.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        public static string Validate(string destination, string common, string dx11)
        {
            string physical = ResolveExistingDirectory(destination);
            foreach (string root in new[] { Directory.GetParent(common).FullName, common, dx11 })
                if (Inside(physical, ResolveExistingDirectory(root))) throw new IOException("輸出位置指向來源 Dump（包含 junction／symbolic link），請選擇其他資料夾。");
            return physical;
        }
        public static string Child(string root, string relative)
        {
            if (Path.IsPathRooted(relative)) throw new InvalidDataException("Absolute export relative path.");
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!Inside(full.TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'))) throw new InvalidDataException("Export path escapes output directory.");
            return full;
        }
    }
    public sealed class CompositionExporter
    {
        readonly string common, dx11;
        public CompositionExporter(string commonPath, string dx11Path) { common = commonPath; dx11 = dx11Path; }
        public async Task<string> ExportAsync(ExportRequest request, CancellationToken token)
        {
            // Snapshot edits before yielding, so later UI changes cannot race with export.
            var copy = JsonUtility.FromJson<ExportRequest>(JsonUtility.ToJson(request));
            var assets = await Task.Run(() =>
            {
                ExportPathGuard.Validate(copy.DestinationFolder, common, dx11);
                var loaded = new Dictionary<string, RawHeadAsset>(StringComparer.OrdinalIgnoreCase);
                foreach (string relative in copy.Document.Parts.Select(p => p.SourceRelativePath).Concat(new[] { copy.TargetRelativePath }).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    loaded.Add(relative, RawHeadAsset.Read(common, dx11, relative, token));
                }
                return loaded;
            }, token);
            token.ThrowIfCancellationRequested();
            // Matrix inverse and Unity structs are evaluated on Unity's main thread.
            var target = assets[copy.TargetRelativePath];
            var model = new G4CompositionWriter().Build(copy.Document, assets, target, token);
            if (copy.Mode != ExportMode.ReplaceCharacter)
                throw new InvalidDataException("目前只支援替換既有角色。");
            string outputRelative = copy.TargetRelativePath;
            string relativeModel = Path.Combine("common", "chr", "_face", outputRelative);
            string relativeTexture = Path.ChangeExtension(Path.Combine("dx11", "chr", "_face", outputRelative), ".g4tx");
            if (target.TexturePath != null)
                relativeTexture = Path.Combine("dx11", target.TexturePath.Substring(dx11.Length + 1));
            return await Task.Run(() =>
            {
                string physical = ExportPathGuard.Validate(copy.DestinationFolder, common, dx11);
                string staging = Path.Combine(copy.DestinationFolder, ".g4-staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                string resolvedStage = ExportPathGuard.ResolveExistingDirectory(staging);
                string parentPhysical = ExportPathGuard.ResolveExistingDirectory(copy.DestinationFolder);
                if (physical != parentPhysical || !resolvedStage.StartsWith(physical + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("輸出路徑在驗證期間改變。");
                bool published = false;
                try
                {
                    Action<string, byte[]> write = (relative, bytes) =>
                    {
                        token.ThrowIfCancellationRequested();
                        string path = ExportPathGuard.Child(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
                        if (!ExportPathGuard.ResolveExistingDirectory(Path.GetDirectoryName(path)).StartsWith(resolvedStage + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe output directory.");
                        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
                    };
                    write(relativeModel, model.Metadata); write(Path.ChangeExtension(relativeModel, ".g4mg"), model.Geometry); write(relativeTexture, model.Textures);
                    G4ModelParser.Parse(File.ReadAllBytes(ExportPathGuard.Child(staging, relativeModel)), File.ReadAllBytes(ExportPathGuard.Child(staging, Path.ChangeExtension(relativeModel, ".g4mg"))), "export readback", token);
                    RawTexture.Read(File.ReadAllBytes(ExportPathGuard.Child(staging, relativeTexture)), "export textures readback");
                    var report = new StringBuilder("# G4 composition export\n\nValidation: native format readback only; original-game validation has not been performed.\n\n");
                    foreach (var a in assets.Values)
                    {
                        VerifySource(a.ModelPath, a.Metadata); VerifySource(Path.ChangeExtension(a.ModelPath, ".g4mg"), a.Geometry);
                        if (a.TexturePath != null) VerifySource(a.TexturePath, a.TextureContainer);
                        report.AppendLine("## " + a.RelativePath).AppendLine("G4MD SHA256: " + RawHeadAsset.Digest(a.Metadata)).AppendLine("G4MG SHA256: " + RawHeadAsset.Digest(a.Geometry));
                        if (a.TextureContainer != null) report.AppendLine("G4TX: " + a.TexturePath).AppendLine("G4TX SHA256: " + RawHeadAsset.Digest(a.TextureContainer));
                    }
                    report.AppendLine("## Joint palettes");
                    var targetJoints = Enumerable.Range(0, target.Reader.U16(0x24)).Select(i => target.Reader.U32(target.Section(0x74) + i * 4)).ToArray();
                    uint targetHead = G4CompositionWriter.TargetHeadJoint(target);
                    foreach (var part in copy.Document.Parts)
                    {
                        var asset = assets[part.SourceRelativePath];
                        report.AppendLine("### " + part.Name + " / " + part.Id);
                        foreach (uint hash in asset.Meshes[part.MeshIndex].Palette)
                        {
                            report.AppendLine(hash.ToString("X8") + (targetJoints.Contains(hash) ? " (same target joint index)" : " -> " + targetHead.ToString("X8") + " (target head joint)"));
                        }
                    }
                    File.WriteAllText(Path.Combine(staging, "report.md"), report.ToString(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(staging, "composition.json"), JsonUtility.ToJson(copy, true), new UTF8Encoding(false));
                    token.ThrowIfCancellationRequested(); ExportPathGuard.Validate(copy.DestinationFolder, common, dx11);
                    string stem = "G4_" + Path.GetFileNameWithoutExtension(copy.TargetRelativePath) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string final = Path.Combine(copy.DestinationFolder, stem); int serial = 1;
                    while (Directory.Exists(final) || File.Exists(final)) final = Path.Combine(copy.DestinationFolder, stem + "_" + serial++);
                    if (ExportPathGuard.ResolveExistingDirectory(staging) != resolvedStage) throw new IOException("Staging path changed.");
                    Directory.Move(staging, final); published = true; return final;
                }
                finally
                {
                    if (!published && Directory.Exists(staging) && ExportPathGuard.ResolveExistingDirectory(staging) == resolvedStage)
                    {
                        // Delete only this unique staging tree. Never follow a reparse point.
                        DeleteOwned(staging, resolvedStage);
                    }
                }
            }, token);
        }
        static void VerifySource(string path, byte[] bytes)
        {
            if (RawHeadAsset.Digest(File.ReadAllBytes(path)) != RawHeadAsset.Digest(bytes)) throw new IOException("來源在匯出期間改變：" + path);
        }
        static void DeleteOwned(string path, string root)
        {
            string physical = ExportPathGuard.ResolveExistingDirectory(path);
            if (physical != root && !physical.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Refusing cleanup outside staging.");
            foreach (string file in Directory.GetFiles(path)) File.Delete(file);
            foreach (string child in Directory.GetDirectories(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
                else DeleteOwned(child, root);
            }
            Directory.Delete(path);
        }
    }
}
