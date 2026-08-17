using System.Text.Json;
using Rc.UiAgent;
using Xunit;

namespace Rc.Agent.Tests.Ui;

public sealed class ChromiumDevToolsDocumentTests
{
    [Fact]
    public void CreatesBoundedSemanticSnapshotFromChromiumDom()
    {
        using var document = JsonDocument.Parse("""
            {
              "nodeId": 1, "nodeName": "#document", "localName": "", "nodeValue": "", "attributes": [],
              "children": [{
                "nodeId": 2, "nodeName": "BODY", "localName": "body", "nodeValue": "", "attributes": ["id", "main", "class", "article"],
                "children": [{ "nodeId": 3, "nodeName": "#text", "localName": "", "nodeValue": "Forecast for Hangzhou", "attributes": [] }]
              }]
            }
            """);

        var snapshot = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 42, maximumDepth: 2, maximumElements: 10);

        var body = Assert.Single(snapshot.Children);
        Assert.Equal("DOM.body", body.ControlType);
        Assert.Equal("main", body.AutomationId);
        Assert.Equal("article", body.ClassName);
        Assert.Equal([42, 3], Assert.Single(body.Children).RuntimeId);
        Assert.Equal("Forecast for Hangzhou", body.Children[0].Name);
    }

    [Fact]
    public void SkipsInertNodesWithoutConsumingElementLimit()
    {
        using var document = JsonDocument.Parse("""
            {
              "nodeId": 1, "nodeName": "#document", "localName": "", "nodeValue": "", "attributes": [],
              "children": [
                { "nodeId": 2, "nodeName": "HEAD", "localName": "head", "nodeValue": "", "attributes": [],
                  "children": [{ "nodeId": 3, "nodeName": "STYLE", "localName": "style", "nodeValue": "css-noise", "attributes": [] },
                               { "nodeId": 4, "nodeName": "SCRIPT", "localName": "script", "nodeValue": "js-noise", "attributes": [] }] },
                { "nodeId": 5, "nodeName": "DIV", "localName": "div", "nodeValue": "", "attributes": [],
                  "children": [{ "nodeId": 6, "nodeName": "SVG", "localName": "svg", "nodeValue": "", "attributes": [],
                                 "children": [{ "nodeId": 7, "nodeName": "PATH", "localName": "path", "nodeValue": "", "attributes": [] },
                                              { "nodeId": 8, "nodeName": "RECT", "localName": "rect", "nodeValue": "", "attributes": [] }] },
                               { "nodeId": 9, "nodeName": "#text", "localName": "", "nodeValue": "开奖号码 8 8 2", "attributes": [] }] }
              ]
            }
            """);

        // 配额小到装不下 head 下的 style/script 与 svg 图形时，正文文本仍必须出现。
        var snapshot = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 1, maximumDepth: 4, maximumElements: 4);

        var div = Assert.Single(snapshot.Children);
        Assert.Equal("DOM.div", div.ControlType);
        Assert.Equal("开奖号码 8 8 2", Assert.Single(div.Children).Name);
    }

    [Fact]
    public void StopsAtConfiguredDepthAndElementLimit()
    {
        using var document = JsonDocument.Parse("""
            { "nodeId": 1, "nodeName": "#document", "localName": "", "nodeValue": "", "attributes": [],
              "children": [{ "nodeId": 2, "nodeName": "DIV", "localName": "div", "nodeValue": "", "attributes": [],
              "children": [{ "nodeId": 3, "nodeName": "#text", "localName": "", "nodeValue": "hidden", "attributes": [] }] }] }
            """);

        var depthLimited = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 1, maximumDepth: 1, maximumElements: 10);
        var countLimited = ChromiumDevToolsDocument.CreateSnapshot(document.RootElement, 1, maximumDepth: 2, maximumElements: 2);

        Assert.Empty(Assert.Single(depthLimited.Children).Children);
        Assert.Empty(Assert.Single(countLimited.Children).Children);
    }
}
