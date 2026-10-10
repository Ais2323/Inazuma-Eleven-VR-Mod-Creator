#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using UnityEditor;
using UnityEngine;

public static class G4CompositionValidation
{
    public static async void MixedPalette()
    {
        string output = Environment.GetEnvironmentVariable("G4_COMPOSITION_OUTPUT");
        try
        {
            Directory.CreateDirectory(output);
            string common = @"D:\備份\data\data\common", dx11 = @"D:\備份\data\data\dx11";
            string target = @"01_IE1\c01000010\c01000010.g4md", source = @"11_VICTORY\c11010110\c11010110.g4md";
            var targetAsset = RawHeadAsset.Read(common, dx11, target, CancellationToken.None);
            var sourceAsset = RawHeadAsset.Read(common, dx11, source, CancellationToken.None);
            var doc = new CompositionDocument();
            foreach (var item in new[] { Tuple.Create(targetAsset, 0), Tuple.Create(sourceAsset, 1), Tuple.Create(sourceAsset, 2) })
            {
                var mesh = item.Item1.Meshes[item.Item2];
                doc.Parts.Add(new CompositionPart { Name = item.Item1.Preview.Parts.First(p => p.SourceMeshIndex == item.Item2).Name, SourceRelativePath = item.Item1.RelativePath, MeshIndex = item.Item2, Triangles = Enumerable.Range(0, mesh.Indices.Length / 3).ToArray() });
            }
            string result = await new CompositionExporter(common, dx11).ExportAsync(new ExportRequest { Document = doc, DestinationFolder = output, TargetRelativePath = target }, CancellationToken.None);
            File.WriteAllText(Path.Combine(output, "mixed-palette.txt"), result); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); if (output != null) File.WriteAllText(Path.Combine(output, "mixed-palette-failure.txt"), e.ToString()); EditorApplication.Exit(1); }
    }
    public static async void Run()
    {
        string output = Environment.GetEnvironmentVariable("G4_COMPOSITION_OUTPUT");
        try
        {
            Directory.CreateDirectory(output);
            string common = @"D:\備份\data\data\common", dx11 = @"D:\備份\data\data\dx11", face = Path.Combine(common, "chr", "_face");
            foreach (string cfg in Directory.GetFiles(Path.Combine(common, "gamedata", "character"), "chara_*.cfg.bin"))
            {
                if (!Path.GetFileName(cfg).StartsWith("chara_model_") && !Path.GetFileName(cfg).StartsWith("chara_base_") && !Path.GetFileName(cfg).StartsWith("chara_param_1")) continue;
                var table = CfgTable.Read(File.ReadAllBytes(cfg), cfg); var roundtrip = CfgTable.Read(table.Write(), cfg);
                if (table.Rows.Count != roundtrip.Rows.Count) throw new Exception("CFGBIN roundtrip count");
                for (int i = 0; i < table.Rows.Count; i++)
                    for (int j = 0; j < table.Rows[i].Values.Length; j++)
                        if (!Equals(table.Rows[i].Values[j], roundtrip.Rows[i].Values[j])) throw new Exception("CFGBIN roundtrip " + i + "/" + j);
                Debug.Log("CFGBIN pass " + cfg + " (" + table.Rows.Count + ")");
            }
            var groups = ConnectedComponents.Split(6, new[] { 0, 1, 2, 3, 4, 5 });
            if (groups.Count != 2) throw new Exception("Connected components welded disconnected indices.");
            var doc = new CompositionDocument();
            string target = null;
            foreach (string stem in new[] { "c01000010", "c11010010" })
            {
                string file = Directory.GetFiles(face, stem + ".g4md", SearchOption.AllDirectories).Single();
                string relative = file.Substring(face.Length + 1); target = target ?? relative;
                var a = RawHeadAsset.Read(common, dx11, relative, CancellationToken.None);
                a.Skeleton = new SkeletonResolver(common).Resolve(a, CancellationToken.None);
                Debug.Log("Resolved " + relative + " => " + a.Skeleton.Path + " : " + a.Skeleton.Evidence);
                var part = a.Preview.Parts[0];
                var components = ConnectedComponents.Split(part.Positions.Length, part.Indices);
                if (stem == "c01000010")
                    foreach (var mesh in a.Preview.Parts) doc.Parts.Add(new CompositionPart { Name = mesh.Name, SourceRelativePath = relative, MeshIndex = mesh.SourceMeshIndex, Triangles = Enumerable.Range(0, mesh.Indices.Length / 3).ToArray() });
                else doc.Parts.Add(new CompositionPart { Name = stem, SourceRelativePath = relative, MeshIndex = part.SourceMeshIndex, Triangles = components.OrderByDescending(c => c.Length).First(), Position = new Vector3(.02f, 0, 0) });
            }
            string result = await new CompositionExporter(common, dx11).ExportAsync(new ExportRequest { Document = doc, DestinationFolder = output, TargetRelativePath = target }, CancellationToken.None);
            var catalog = CharacterCatalog.Read(common, CancellationToken.None);
            var ordinary = catalog.Templates.First(r => { try { return catalog.Face(r) == target; } catch { return false; } });
            var variant = catalog.Parameters.Rows.First(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == ordinary.UInt(0));
            var settings = new NewCharacterSettings { TemplateBaseId = ordinary.UInt(0), TemplateParameterId = variant.UInt(0), DisplayName = "拼貼測試角色", Affinity = 1, MainPosition = 1, SubPosition = 2,
                StatModifiers = Enumerable.Range(1, 7).ToArray(), Skills = catalog.Skills.Keys.Take(9).ToArray(), UnlockLevels = Enumerable.Range(1, 9).ToArray() };
            string newCharacter = await new CompositionExporter(common, dx11).ExportAsync(new ExportRequest { Document = doc, DestinationFolder = output, TargetRelativePath = target, Mode = ExportMode.NewCharacter, Character = settings }, CancellationToken.None);
            File.WriteAllText(Path.Combine(output, "new-character.txt"), newCharacter);
            File.WriteAllText(Path.Combine(output, "success.txt"), result);
            if (File.Exists(Path.Combine(output, "failure.txt"))) File.Delete(Path.Combine(output, "failure.txt"));
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); if (output != null) File.WriteAllText(Path.Combine(output, "failure.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    public static async void Edges()
    {
        string output = Environment.GetEnvironmentVariable("G4_COMPOSITION_OUTPUT");
        try
        {
            string common = @"D:\備份\data\data\common", dx11 = @"D:\備份\data\data\dx11";
            Reject(() => ExportPathGuard.Validate(common, common, dx11), "Dump destination");
            Reject(() => ExportPathGuard.Validate(Directory.GetParent(common).FullName, common, dx11), "Dump root");
            Reject(() => ExportPathGuard.Validate(Path.Combine(output, "DumpLink"), common, dx11), "junction to Dump");
            var asset = RawHeadAsset.Read(common, dx11, @"01_IE1\c01000010\c01000010.g4md", CancellationToken.None);
            asset.Skeleton = new SkeletonResolver(common).Resolve(asset, CancellationToken.None);
            var part = new CompositionPart { Name = "test", SourceRelativePath = asset.RelativePath, MeshIndex = 0, Triangles = Enumerable.Range(0, asset.Meshes[0].Indices.Length / 3).ToArray() };
            var doc = new CompositionDocument(); doc.Parts.Add(part);
            var sources = new System.Collections.Generic.Dictionary<string, RawHeadAsset> { { asset.RelativePath, asset } };
            var writer = new G4CompositionWriter();
            Reject(() => writer.Build(new CompositionDocument(), sources, asset, CancellationToken.None), "empty composition");
            part.Scale = new Vector3(-1, 1, 1); Reject(() => writer.Build(doc, sources, asset, CancellationToken.None), "negative scale"); part.Scale = Vector3.one;
            var textures = asset.Textures.ToArray(); asset.Textures.Clear(); Reject(() => writer.Build(doc, sources, asset, CancellationToken.None), "missing required texture"); asset.Textures.AddRange(textures);
            part.Bones.Add(new BoneMapping { SourceHash = asset.Meshes[0].Palette[0], TargetHash = 0xdeadbeef, Manual = true });
            Reject(() => writer.Build(doc, sources, asset, CancellationToken.None), "unmapped bone"); part.Bones.Clear();
            uint head = asset.Skeleton.Hashes[Array.IndexOf(asset.Skeleton.Names, "c_head_1_0")];
            foreach (uint hash in asset.Meshes[0].Palette) part.Bones.Add(new BoneMapping { SourceHash = hash, TargetHash = head, Manual = true });
            var merged = writer.Build(doc, sources, asset, CancellationToken.None);
            if (merged.Metadata[0xa0 + 0x3c] != 1) throw new Exception("Manual bone mapping did not merge the palette.");
            part.Bones.Clear();
            int before = Directory.GetDirectories(output).Length;
            try { await new CompositionExporter(common, dx11).ExportAsync(new ExportRequest { Document = doc, DestinationFolder = output, TargetRelativePath = asset.RelativePath }, new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            if (Directory.GetDirectories(output).Length != before) throw new Exception("Cancelled export created files.");
            if (Directory.GetDirectories(output, ".g4-staging-*").Length != 0) throw new Exception("Leaked staging directory.");
            File.WriteAllText(Path.Combine(output, "edge-validation.txt"), "PASS: Dump root/subdirectory/junction rejection, empty composition, negative scale, missing textures, missing bones, manual bone merge, cancellation without output, staging cleanup.");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.WriteAllText(Path.Combine(output, "edge-validation.txt"), "FAIL: " + e); EditorApplication.Exit(1); }
    }
    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception e) when (e is System.IO.IOException || e is System.IO.InvalidDataException) { return; }
        throw new Exception("Expected rejection: " + name);
    }

    public static void Series()
    {
        string output = Environment.GetEnvironmentVariable("G4_COMPOSITION_OUTPUT");
        string common = @"D:\備份\data\data\common", dx11 = @"D:\備份\data\data\dx11", face = Path.Combine(common, "chr", "_face");
        var report = new System.Text.StringBuilder(); var resolver = new SkeletonResolver(common);
        try
        {
            foreach (string directory in Directory.GetDirectories(face).OrderBy(p => p))
            {
                string file = Directory.GetFiles(directory, "*.g4md", SearchOption.AllDirectories).Where(p => File.Exists(Path.ChangeExtension(p, ".g4mg"))).OrderBy(p => p).FirstOrDefault();
                if (file == null) continue;
                string relative = file.Substring(face.Length + 1);
                try
                {
                    var a = RawHeadAsset.Read(common, dx11, relative, CancellationToken.None); a.Skeleton = resolver.Resolve(a, CancellationToken.None);
                    var doc = new CompositionDocument(); foreach (var mesh in a.Preview.Parts) doc.Parts.Add(new CompositionPart { Name = mesh.Name, SourceRelativePath = relative, MeshIndex = mesh.SourceMeshIndex, Triangles = Enumerable.Range(0, mesh.Indices.Length / 3).ToArray() });
                    var result = new G4CompositionWriter().Build(doc, new System.Collections.Generic.Dictionary<string, RawHeadAsset> { { relative, a } }, a, CancellationToken.None);
                    string destination = Path.Combine(output, "Series", Path.GetFileName(directory)); Directory.CreateDirectory(destination);
                    File.WriteAllBytes(Path.Combine(destination, "head.g4md"), result.Metadata); File.WriteAllBytes(Path.Combine(destination, "head.g4mg"), result.Geometry); File.WriteAllBytes(Path.Combine(destination, "head.g4tx"), result.Textures);
                    report.AppendLine("PASS " + relative + " meshes=" + doc.Parts.Count);
                }
                catch (Exception e) { report.AppendLine("BLOCKED " + relative + ": " + e.Message); }
            }
            File.WriteAllText(Path.Combine(output, "series-validation.txt"), report.ToString()); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.WriteAllText(Path.Combine(output, "series-validation.txt"), report + "\nFAIL: " + e); EditorApplication.Exit(1); }
    }
}
#endif
