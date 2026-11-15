using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// Runtime FBX 7.4 writer. The data is captured from the current outfit, including
// its evaluated bind pose, rather than exporting the preview as rigid geometry.
internal static class BinaryFbxExporter
{
    internal const string ArmatureName = "CharMakerUniformExport";

    internal sealed class Scene
    {
        internal string[] BoneNames;
        internal int[] Parents;
        internal Matrix4x4[] Bind;
        internal string Armature;
        internal readonly List<Geometry> Meshes = new();
        internal readonly List<Surface> Materials = new();
    }

    internal sealed class Geometry
    {
        internal string Name;
        internal Vector3[] Positions, Normals;
        internal Vector2[] Uv;
        internal Color32[] Colors;
        internal int[] Triangles;
        internal int[,] Bones;
        internal float[,] Weights;
        internal int Material;
    }

    internal sealed class Surface
    {
        internal string Name, Diffuse, Oc, Mask, Spm;
        internal Vector2 UvScale = Vector2.one, UvOffset;
        internal Color Color = Color.white;
    }

    sealed class Node
    {
        internal readonly string Name;
        internal readonly object[] Properties;
        internal readonly List<Node> Children = new();
        internal Node(string name, params object[] properties) { Name = name; Properties = properties; }
        internal Node Add(string name, params object[] properties)
        {
            var child = new Node(name, properties);
            Children.Add(child);
            return child;
        }
    }

    static readonly Matrix4x4 Mirror = Matrix4x4.Scale(new Vector3(1, 1, -1));

    internal static byte[] Encode(Scene scene)
    {
        if (scene.Meshes.Count == 0) throw new InvalidDataException("No visible outfit meshes to export.");
        var header = new Node("FBXHeaderExtension");
        header.Add("FBXHeaderVersion", 1003);
        header.Add("FBXVersion", 7400);
        header.Add("EncryptionType", 0);
        header.Add("Creator", ArmatureName);
        var settings = new Node("GlobalSettings");
        settings.Add("Version", 1000);
        var global = settings.Add("Properties70");
        Property(global, "UpAxis", "int", 1);
        Property(global, "UpAxisSign", "int", 1);
        Property(global, "FrontAxis", "int", 2);
        Property(global, "FrontAxisSign", "int", 1);
        Property(global, "CoordAxis", "int", 0);
        Property(global, "CoordAxisSign", "int", 1);
        Property(global, "UnitScaleFactor", "double", 1d);
        Property(global, "OriginalUnitScaleFactor", "double", 1d);
        var objects = new Node("Objects");
        var connections = new Node("Connections");
        long nextId = 1000;
        long root = nextId++;
        var armature = Model(objects, root, scene.Armature ?? ArmatureName, "Null", Matrix4x4.identity);
        armature.Children.Single(n => n.Name == "Properties70").Add("P", "CharMakerSourcePose", "KString", "", "U", scene.Armature ?? ArmatureName);
        Connect(connections, root, 0);
        var boneIds = new long[scene.BoneNames.Length];
        // FBX/Blender armatures normalize rest-bone scales. Keep the evaluated
        // shape in the mesh and store a scale-free skeleton with the same anchors.
        var bind = scene.Bind.Select(m => ToFbx(Matrix4x4.TRS(m.GetColumn(3), m.rotation, Vector3.one))).ToArray();
        for (int i = 0; i < boneIds.Length; i++) boneIds[i] = nextId++;
        for (int i = 0; i < boneIds.Length; i++) {
            int parent = scene.Parents[i];
            bool isRoot = parent < 0 || parent >= boneIds.Length || parent == i;
            Matrix4x4 local = isRoot ? bind[i] : bind[parent].inverse * bind[i];
            Model(objects, boneIds[i], scene.BoneNames[i], "LimbNode", local);
            long attribute = nextId++;
            var nodeAttribute = objects.Add("NodeAttribute", attribute, scene.BoneNames[i] + "\0\u0001NodeAttribute", "LimbNode");
            nodeAttribute.Add("TypeFlags", "Skeleton");
            Property(nodeAttribute.Add("Properties70"), "Size", "double", 1d);
            Connect(connections, attribute, boneIds[i]);
            Connect(connections, boneIds[i], isRoot ? root : boneIds[parent]);
        }
        var materialIds = new long[scene.Materials.Count];
        for (int i = 0; i < materialIds.Length; i++) {
            var material = scene.Materials[i];
            long id = materialIds[i] = nextId++;
            var node = objects.Add("Material", id, material.Name + "\0\u0001Material", "");
            node.Add("Version", 102);
            node.Add("ShadingModel", "lambert");
            node.Add("MultiLayer", 0);
            var properties = node.Add("Properties70");
            Property(properties, "DiffuseColor", "Color", (double)material.Color.r, (double)material.Color.g, (double)material.Color.b);
            Property(properties, "DiffuseFactor", "double", 1d);
            AddTexture(material.Diffuse, "DiffuseColor");
            // Keep the game channels available as separate texture references.
            AddTexture(material.Oc, "AmbientColor");
            AddTexture(material.Spm, "SpecularColor");
            AddTexture(material.Mask, "CharMakerSkinMask");
            void AddTexture(string path, string property)
            {
                if (string.IsNullOrEmpty(path)) return;
                long videoId = nextId++, textureId = nextId++;
                string name = Path.GetFileNameWithoutExtension(path);
                var video = objects.Add("Video", videoId, name + "\0\u0001Video", "Clip");
                video.Add("Type", "Clip");
                video.Add("Filename", path);
                video.Add("RelativeFilename", path);
                var texture = objects.Add("Texture", textureId, name + "\0\u0001Texture", "");
                texture.Add("Type", "TextureVideoClip");
                texture.Add("Version", 202);
                texture.Add("TextureName", name + "\0\u0001Texture");
                texture.Add("Media", name + "\0\u0001Video");
                texture.Add("FileName", path);
                texture.Add("RelativeFilename", path);
                double offsetY = 1d - material.UvScale.y - material.UvOffset.y;
                texture.Add("ModelUVTranslation", (double)material.UvOffset.x, offsetY);
                texture.Add("ModelUVScaling", (double)material.UvScale.x, (double)material.UvScale.y);
                var properties = texture.Add("Properties70");
                Property(properties, "Translation", "Vector3D", (double)material.UvOffset.x, offsetY, 0d);
                Property(properties, "Scaling", "Vector3D", 1d / material.UvScale.x, 1d / material.UvScale.y, 1d);
                texture.Add("Texture_Alpha_Source", "None");
                Connect(connections, videoId, textureId);
                connections.Add("C", "OP", textureId, id, property);
            }
        }
        foreach (var mesh in scene.Meshes) {
            long modelId = nextId++, geometryId = nextId++, skinId = nextId++;
            Model(objects, modelId, mesh.Name, "Mesh", Matrix4x4.identity);
            Connect(connections, modelId, root);
            Connect(connections, materialIds[mesh.Material], modelId);
            var geometry = objects.Add("Geometry", geometryId, mesh.Name + "\0\u0001Geometry", "Mesh");
            geometry.Add("GeometryVersion", 124);
            geometry.Add("Vertices", mesh.Positions.SelectMany(p => new[] { (double)p.x * 100, (double)p.y * 100, -(double)p.z * 100 }).ToArray());
            var indices = (int[])mesh.Triangles.Clone();
            // Unity uses the opposite handedness and front-face winding.
            for (int i = 0; i < indices.Length; i += 3) {
                int middle = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = -middle - 1;
            }
            geometry.Add("PolygonVertexIndex", indices);
            var normals = Layer(geometry, "LayerElementNormal", "ByVertice", "Direct");
            normals.Add("Normals", mesh.Normals.SelectMany(n => new[] { (double)n.x, (double)n.y, -(double)n.z }).ToArray());
            var uv = Layer(geometry, "LayerElementUV", "ByVertice", "Direct");
            uv.Add("UV", mesh.Uv.SelectMany(p => new[] { (double)p.x, 1d - p.y }).ToArray());
            var materials = Layer(geometry, "LayerElementMaterial", "AllSame", "IndexToDirect");
            materials.Add("Materials", new[] { 0 });
            var layer = geometry.Add("Layer", 0);
            layer.Add("Version", 100);
            foreach (string type in new[] { "LayerElementNormal", "LayerElementUV", "LayerElementMaterial" }) {
                var element = layer.Add("LayerElement"); element.Add("Type", type); element.Add("TypedIndex", 0);
            }
            if (mesh.Colors != null && mesh.Colors.Length == mesh.Positions.Length) {
                var colors = Layer(geometry, "LayerElementColor", "ByVertice", "Direct");
                colors.Add("Colors", mesh.Colors.SelectMany(c => new[] { c.r / 255d, c.g / 255d, c.b / 255d, c.a / 255d }).ToArray());
                var element = layer.Add("LayerElement"); element.Add("Type", "LayerElementColor"); element.Add("TypedIndex", 0);
            }
            Connect(connections, geometryId, modelId);
            var skin = objects.Add("Deformer", skinId, mesh.Name + "\0\u0001Deformer", "Skin");
            skin.Add("Version", 101); skin.Add("Link_DeformAcuracy", 50d); skin.Add("SkinningType", "Linear");
            Connect(connections, skinId, geometryId);
            var clusters = new Dictionary<int, List<(int Vertex, double Weight)>>();
            var vertexWeights = new Dictionary<int, double>();
            for (int v = 0; v < mesh.Positions.Length; v++) {
                vertexWeights.Clear();
                for (int w = 0; w < 8; w++) {
                    double weight = mesh.Weights[v, w];
                    if (weight <= 0) continue;
                    int bone = mesh.Bones[v, w];
                    vertexWeights.TryGetValue(bone, out double previous);
                    vertexWeights[bone] = previous + weight;
                }
                double total = vertexWeights.Values.Sum();
                if (total <= 0) throw new InvalidDataException("Cannot export an unweighted vertex.");
                foreach (var weight in vertexWeights) {
                    if (!clusters.TryGetValue(weight.Key, out var vertices)) clusters.Add(weight.Key, vertices = new());
                    vertices.Add((v, weight.Value / total));
                }
            }
            foreach (var pair in clusters) {
                long clusterId = nextId++;
                var cluster = objects.Add("Deformer", clusterId, scene.BoneNames[pair.Key] + "\0\u0001SubDeformer", "Cluster");
                cluster.Add("Version", 100); cluster.Add("UserData", "", "");
                cluster.Add("Indexes", pair.Value.Select(v => v.Vertex).ToArray());
                cluster.Add("Weights", pair.Value.Select(v => v.Weight).ToArray());
                cluster.Add("Transform", MatrixValues(bind[pair.Key].inverse));
                cluster.Add("TransformLink", MatrixValues(bind[pair.Key]));
                Connect(connections, clusterId, skinId); Connect(connections, boneIds[pair.Key], clusterId);
            }
            var pose = objects.Add("Pose", nextId++, mesh.Name + "\0\u0001Pose", "BindPose");
            pose.Add("Type", "BindPose"); pose.Add("Version", 100); pose.Add("NbPoseNodes", boneIds.Length + 2);
            PoseNode(pose, root, Matrix4x4.identity); PoseNode(pose, modelId, Matrix4x4.identity);
            for (int i = 0; i < boneIds.Length; i++) PoseNode(pose, boneIds[i], bind[i]);
        }
        var documents = new Node("Documents"); documents.Add("Count", 1);
        var document = documents.Add("Document", nextId++, "Scene", "Scene"); document.Add("RootNode", 0L);
        var definitions = new Node("Definitions"); definitions.Add("Version", 100);
        definitions.Add("Count", objects.Children.Count);
        foreach (var group in objects.Children.GroupBy(n => n.Name)) definitions.Add("ObjectType", group.Key).Add("Count", group.Count());
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); writer.Write(7400);
        foreach (var node in new[] { header, settings, documents, new Node("References"), definitions, objects, connections, new Node("Takes") }) WriteNode(writer, node);
        writer.Write(new byte[13]);
        writer.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
        writer.Write(0); writer.Write(new byte[16 - stream.Position % 16]); writer.Write(7400); writer.Write(new byte[120]);
        writer.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("FBX exceeds the supported 64 MB import limit.");
        return stream.ToArray();
    }

    static Matrix4x4 ToFbx(Matrix4x4 matrix)
    {
        matrix = Mirror * matrix * Mirror;
        for (int i = 0; i < 3; i++) matrix[i, 3] *= 100;
        return matrix;
    }

    static double[] MatrixValues(Matrix4x4 matrix) => Enumerable.Range(0, 16).Select(i => (double)matrix[i % 4, i / 4]).ToArray();
    static void Connect(Node connections, long child, long parent) => connections.Add("C", "OO", child, parent);
    static void PoseNode(Node pose, long id, Matrix4x4 matrix)
    {
        var node = pose.Add("PoseNode"); node.Add("Node", id); node.Add("Matrix", MatrixValues(matrix));
    }

    static Node Layer(Node geometry, string name, string mapping, string reference)
    {
        var layer = geometry.Add(name, 0); layer.Add("Version", 101); layer.Add("Name", "");
        layer.Add("MappingInformationType", mapping); layer.Add("ReferenceInformationType", reference);
        return layer;
    }

    static Node Model(Node objects, long id, string name, string type, Matrix4x4 transform)
    {
        var model = objects.Add("Model", id, name + "\0\u0001Model", type); model.Add("Version", 232);
        var properties = model.Add("Properties70");
        Vector3 position = transform.GetColumn(3), rotation = transform.rotation.eulerAngles, scale = transform.lossyScale;
        Property(properties, "Lcl Translation", "Lcl Translation", (double)position.x, (double)position.y, (double)position.z);
        Property(properties, "Lcl Rotation", "Lcl Rotation", (double)rotation.x, (double)rotation.y, (double)rotation.z);
        Property(properties, "Lcl Scaling", "Lcl Scaling", (double)scale.x, (double)scale.y, (double)scale.z);
        Property(properties, "RotationOrder", "enum", 4); // Unity's Quaternion.Euler uses ZXY.
        Property(properties, "RotationActive", "bool", 1);
        model.Add("Shading", true); model.Add("Culling", "CullingOff");
        return model;
    }

    static void Property(Node parent, string name, string type, params object[] values)
    {
        string label = type == "double" ? "Number" : type == "int" ? "Integer" : "";
        string flags = type.StartsWith("Lcl ", StringComparison.Ordinal) ? "A" : "";
        parent.Children.Add(new Node("P", new object[] { name, type, label, flags }.Concat(values).ToArray()));
    }

    static void WriteNode(BinaryWriter writer, Node node)
    {
        long start = writer.BaseStream.Position;
        writer.Write(0u); writer.Write((uint)node.Properties.Length); writer.Write(0u);
        byte[] name = Encoding.UTF8.GetBytes(node.Name); writer.Write(checked((byte)name.Length)); writer.Write(name);
        long propertiesStart = writer.BaseStream.Position;
        foreach (object property in node.Properties) WriteProperty(writer, property);
        uint propertyLength = checked((uint)(writer.BaseStream.Position - propertiesStart));
        foreach (var child in node.Children) WriteNode(writer, child);
        if (node.Children.Count > 0 || node.Properties.Length == 0) writer.Write(new byte[13]);
        long end = writer.BaseStream.Position;
        writer.BaseStream.Position = start; writer.Write(checked((uint)end)); writer.BaseStream.Position = start + 8; writer.Write(propertyLength);
        writer.BaseStream.Position = end;
    }

    static void WriteProperty(BinaryWriter writer, object value)
    {
        switch (value) {
            case int number: writer.Write((byte)'I'); writer.Write(number); break;
            case long number: writer.Write((byte)'L'); writer.Write(number); break;
            case double number: writer.Write((byte)'D'); writer.Write(number); break;
            case bool boolean: writer.Write((byte)'C'); writer.Write(boolean); break;
            case string text:
                writer.Write((byte)'S'); byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); break;
            case int[] numbers:
                writer.Write((byte)'i'); writer.Write(numbers.Length); writer.Write(0); writer.Write(checked(numbers.Length * 4));
                foreach (int number in numbers) writer.Write(number); break;
            case double[] numbers:
                writer.Write((byte)'d'); writer.Write(numbers.Length); writer.Write(0); writer.Write(checked(numbers.Length * 8));
                foreach (double number in numbers) writer.Write(number); break;
            default: throw new InvalidDataException("Unsupported FBX property type.");
        }
    }
}
