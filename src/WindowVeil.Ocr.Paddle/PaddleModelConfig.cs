using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace WindowVeil.Ocr.Paddle;

/// <summary>Settings read from a detection model's <c>inference.yml</c>.</summary>
/// <param name="ModelName">Global.model_name, e.g. <c>PP-OCRv6_small_det</c>.</param>
/// <param name="Threshold">PostProcess.thresh.</param>
/// <param name="BoxThreshold">PostProcess.box_thresh.</param>
/// <param name="UnclipRatio">PostProcess.unclip_ratio.</param>
/// <param name="MaxCandidates">PostProcess.max_candidates.</param>
/// <param name="Normalization">PreProcess NormalizeImage constants.</param>
public sealed record DetectionModelConfig(string ModelName, float Threshold, float BoxThreshold, double UnclipRatio, int MaxCandidates, DetectionNormalization Normalization);

/// <summary>Settings read from a recognition model's <c>inference.yml</c>.</summary>
/// <param name="ModelName">Global.model_name, e.g. <c>PP-OCRv6_small_rec</c>.</param>
/// <param name="ImageChannels">RecResizeImg.image_shape[0].</param>
/// <param name="ImageHeight">RecResizeImg.image_shape[1] (48).</param>
/// <param name="ImageWidth">RecResizeImg.image_shape[2] (320).</param>
/// <param name="Characters">PostProcess.character_dict entries in order.</param>
public sealed record RecognitionModelConfig(string ModelName, int ImageChannels, int ImageHeight, int ImageWidth, IReadOnlyList<string> Characters);

/// <summary>Parses PaddleX <c>inference.yml</c> files with YamlDotNet.</summary>
public static class PaddleModelConfig
{
    /// <summary>Reads a detection config. Throws <see cref="PaddleOcrModelException"/> on missing or unexpected content.</summary>
    public static DetectionModelConfig LoadDetection(string path)
    {
        var root = LoadRoot(path);
        string name = Scalar(root, path, "Global", "model_name");
        var post = Mapping(root, path, "PostProcess");
        string postName = Scalar(post, path, "name");
        if (postName != "DBPostProcess")
            throw new PaddleOcrModelException($"{path}: PostProcess.name is '{postName}', expected 'DBPostProcess' for a text detection model.");
        var norm = DetectionNormalization.ImageNet;
        if (FindTransform(root, "NormalizeImage") is YamlMappingNode n)
        {
            var mean = Numbers(n, path, "mean");
            var std = Numbers(n, path, "std");
            double scale = n.Children.TryGetValue(new YamlScalarNode("scale"), out var s) && s is YamlScalarNode { Value: { } scaleText } ? ParseExpression(scaleText, path) : 1.0 / 255.0;
            if (mean.Length != 3 || std.Length != 3 || std.Any(v => v == 0))
                throw new PaddleOcrModelException($"{path}: NormalizeImage needs three means and three non-zero standard deviations.");
            if (n.Children.TryGetValue(new YamlScalarNode("order"), out var order) && ((YamlScalarNode)order).Value is { } o && o != "hwc" && o != "")
                throw new PaddleOcrModelException($"{path}: NormalizeImage order '{o}' is not supported (expected 'hwc').");
            norm = new DetectionNormalization(mean, std, scale);
        }
        if (FindTransform(root, "DecodeImage") is YamlMappingNode dec && dec.Children.TryGetValue(new YamlScalarNode("img_mode"), out var mode)
            && ((YamlScalarNode)mode).Value is { } m && m != "BGR")
            throw new PaddleOcrModelException($"{path}: DecodeImage img_mode '{m}' is not supported (expected BGR).");
        return new DetectionModelConfig(
            name,
            (float)Number(post, path, "thresh", 0.3),
            (float)Number(post, path, "box_thresh", 0.6),
            Number(post, path, "unclip_ratio", 2.0),
            (int)Number(post, path, "max_candidates", 1000),
            norm);
    }

    /// <summary>Reads a recognition config including its character dictionary.</summary>
    public static RecognitionModelConfig LoadRecognition(string path)
    {
        var root = LoadRoot(path);
        string name = Scalar(root, path, "Global", "model_name");
        var post = Mapping(root, path, "PostProcess");
        string postName = Scalar(post, path, "name");
        if (postName != "CTCLabelDecode")
            throw new PaddleOcrModelException($"{path}: PostProcess.name is '{postName}', expected 'CTCLabelDecode' for a CTC recognition model.");
        if (!post.Children.TryGetValue(new YamlScalarNode("character_dict"), out var dictNode) || dictNode is not YamlSequenceNode seq)
            throw new PaddleOcrModelException($"{path}: PostProcess.character_dict is missing or not a list.");
        var chars = new List<string>(seq.Children.Count);
        foreach (var item in seq.Children)
        {
            if (item is not YamlScalarNode sc || sc.Value is null)
                throw new PaddleOcrModelException($"{path}: character_dict entry {chars.Count} is not a scalar.");
            chars.Add(sc.Value);
        }
        if (chars.Count == 0) throw new PaddleOcrModelException($"{path}: character_dict is empty.");
        var resize = FindTransform(root, "RecResizeImg") as YamlMappingNode
            ?? throw new PaddleOcrModelException($"{path}: PreProcess has no RecResizeImg transform.");
        var shape = Numbers(resize, path, "image_shape");
        if (shape.Length != 3 || shape[0] != 3 || shape[1] <= 0 || shape[2] <= 0)
            throw new PaddleOcrModelException($"{path}: RecResizeImg.image_shape must be [3, H, W].");
        return new RecognitionModelConfig(name, (int)shape[0], (int)shape[1], (int)shape[2], chars);
    }

    private static YamlMappingNode LoadRoot(string path)
    {
        if (!File.Exists(path)) throw new PaddleOcrModelException($"Model config not found: {path}");
        try
        {
            using var reader = new StreamReader(path, System.Text.Encoding.UTF8);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new PaddleOcrModelException($"{path}: not a YAML mapping.");
            return root;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PaddleOcrModelException($"{path}: invalid YAML ({ex.Message}).", ex);
        }
        catch (IOException ex)
        {
            throw new PaddleOcrModelException($"{path}: cannot read ({ex.Message}).", ex);
        }
    }

    private static YamlNode? FindTransform(YamlMappingNode root, string name)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("PreProcess"), out var pre) || pre is not YamlMappingNode preMap) return null;
        if (!preMap.Children.TryGetValue(new YamlScalarNode("transform_ops"), out var ops) || ops is not YamlSequenceNode list) return null;
        foreach (var op in list.Children)
            if (op is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(name), out var v)) return v;
        return null;
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string path, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode m
            ? m
            : throw new PaddleOcrModelException($"{path}: missing '{key}' section.");

    private static string Scalar(YamlMappingNode node, string path, params string[] keys)
    {
        YamlNode cur = node;
        foreach (var k in keys)
        {
            if (cur is not YamlMappingNode m || !m.Children.TryGetValue(new YamlScalarNode(k), out var next))
                throw new PaddleOcrModelException($"{path}: missing '{string.Join(".", keys)}'.");
            cur = next;
        }
        return cur is YamlScalarNode s && !string.IsNullOrEmpty(s.Value) ? s.Value : throw new PaddleOcrModelException($"{path}: '{string.Join(".", keys)}' is empty.");
    }

    private static double Number(YamlMappingNode node, string path, string key, double fallback)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v)) return fallback;
        return v is YamlScalarNode s && s.Value is { } text ? ParseExpression(text, path) : throw new PaddleOcrModelException($"{path}: '{key}' is not a number.");
    }

    private static double[] Numbers(YamlMappingNode node, string path, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq)
            throw new PaddleOcrModelException($"{path}: '{key}' is missing or not a list.");
        return seq.Children.Select(c => c is YamlScalarNode s && s.Value is { } t ? ParseExpression(t, path) : throw new PaddleOcrModelException($"{path}: '{key}' contains a non-number.")).ToArray();
    }

    /// <summary>Parses a number or a simple quotient such as <c>1./255.</c> (the official config evaluates it with Python).</summary>
    internal static double ParseExpression(string text, string path)
    {
        var parts = text.Split('/');
        if (parts.Length is 1 or 2 && parts.All(p => double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            double v = double.Parse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (parts.Length == 2) v /= double.Parse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            return v;
        }
        throw new PaddleOcrModelException($"{path}: cannot parse number '{text}'.");
    }
}
