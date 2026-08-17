using System.Text.Json;
using Rc.Contracts;

namespace Rc.UiAgent;

internal static class ChromiumDevToolsDocument
{
    // 惰性节点（样式/脚本/头部元信息/内联图形）会占满元素配额并淹没正文文本节点；
    // 遍历时直接跳过、不消耗配额，让 --limit 留给可见内容。
    private static readonly HashSet<string> InertLocalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "head", "meta", "link", "noscript", "template", "title", "base",
        "svg", "path", "circle", "rect", "g", "defs", "clippath", "use", "symbol", "line", "polygon", "polyline", "ellipse",
    };

    public static UiAutomationElementSnapshot CreateSnapshot(JsonElement root, long windowHandle, int maximumDepth, int maximumElements)
    {
        var remaining = maximumElements;
        return CreateSnapshot(root, windowHandle, maximumDepth, ref remaining);
    }

    private static UiAutomationElementSnapshot CreateSnapshot(JsonElement node, long windowHandle, int depth, ref int remaining)
    {
        if (remaining-- <= 0)
        {
            throw new InvalidOperationException("The browser DOM exceeded its configured element limit.");
        }

        var attributes = ReadAttributes(node);
        var nodeName = GetString(node, "nodeName");
        var localName = GetString(node, "localName");
        var nodeValue = GetString(node, "nodeValue");
        var children = new List<UiAutomationElementSnapshot>();
        if (depth > 0 && node.TryGetProperty("children", out var childNodes) && childNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childNodes.EnumerateArray())
            {
                if (remaining == 0)
                {
                    break;
                }
                if (IsInertNode(child))
                {
                    continue;
                }
                children.Add(CreateSnapshot(child, windowHandle, depth - 1, ref remaining));
            }
        }

        var nodeId = node.TryGetProperty("nodeId", out var id) && id.TryGetInt32(out var parsedId) ? parsedId : 0;
        if (nodeId == 0)
        {
            throw new InvalidOperationException("The Chromium DOM node has no node ID.");
        }

        attributes.TryGetValue("aria-label", out var ariaLabel);
        attributes.TryGetValue("title", out var title);
        attributes.TryGetValue("id", out var automationId);
        attributes.TryGetValue("class", out var className);
        var name = !string.IsNullOrWhiteSpace(ariaLabel) ? ariaLabel :
            !string.IsNullOrWhiteSpace(title) ? title :
            !string.IsNullOrWhiteSpace(nodeValue) ? nodeValue : nodeName;
        var controlType = string.IsNullOrWhiteSpace(localName) ? "DOM.Document" : $"DOM.{localName}";
        return new UiAutomationElementSnapshot(
            [unchecked((int)windowHandle), nodeId],
            name,
            automationId ?? string.Empty,
            controlType,
            className ?? string.Empty,
            unchecked((int)windowHandle),
            0,
            0,
            0,
            0,
            true,
            false,
            children);
    }

    private static Dictionary<string, string> ReadAttributes(JsonElement node)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!node.TryGetProperty("attributes", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return attributes;
        }

        var attributeValues = values.EnumerateArray().ToArray();
        for (var index = 0; index + 1 < attributeValues.Length; index += 2)
        {
            attributes[attributeValues[index].GetString() ?? string.Empty] = attributeValues[index + 1].GetString() ?? string.Empty;
        }
        return attributes;
    }

    private static string GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;

    private static bool IsInertNode(JsonElement node)
    {
        if (!node.TryGetProperty("localName", out var localName) || localName.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return InertLocalNames.Contains(localName.GetString() ?? string.Empty);
    }
}
