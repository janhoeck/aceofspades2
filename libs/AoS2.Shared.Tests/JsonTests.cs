using AoS2.Shared.Json;

namespace AoS2.Shared.Tests;

public class JsonTests
{
    [Fact]
    public void Parse_ReadsAllKinds()
    {
        var v = JsonParser.Parse("""{ "a": 1.5, "b": "x\"yä", "c": [true, false, null], "d": { "e": -2e3 } }""");

        Assert.Equal(1.5, v["a"]!.NumberValue);
        Assert.Equal("x\"yä", v["b"]!.StringValue);
        Assert.Equal(3, v["c"]!.Items.Count);
        Assert.True(v["c"]!.Items[0].BoolValue);
        Assert.True(v["c"]!.Items[2].IsNull);
        Assert.Equal(-2000, v["d"]!["e"]!.NumberValue);
        Assert.Null(v["missing"]);
    }

    [Theory]
    [InlineData("{ \"a\": }")]
    [InlineData("[1, 2")]
    [InlineData("{ \"a\": 1 } x")]
    [InlineData("tru")]
    [InlineData("\"offen")]
    public void Parse_InvalidJson_Throws(string json)
    {
        Assert.Throws<FormatException>(() => JsonParser.Parse(json));
    }

    [Fact]
    public void Parse_Error_NamesLine()
    {
        var e = Assert.Throws<FormatException>(() => JsonParser.Parse("{\n  \"a\": 1,\n  \"b\" 2\n}"));
        Assert.Contains("Zeile 3", e.Message);
    }

    [Fact]
    public void ToJson_ThenParse_RoundTrips()
    {
        var original = JsonValue.NewObject()
            .Set("name", "Map \"1\"\n")
            .Set("n", 0.1)
            .Set("i", 42)
            .Set("ok", true)
            .Set("list", JsonValue.NewArray().Add(JsonValue.Number(1)).Add(JsonValue.NewObject().Set("x", -3)));

        foreach (var indented in new[] { false, true })
        {
            var copy = JsonParser.Parse(original.ToJson(indented));
            Assert.Equal("Map \"1\"\n", copy.GetString("name"));
            Assert.Equal(0.1, copy.GetNumber("n", 0));
            Assert.Equal(42, copy.GetInt("i", 0));
            Assert.True(copy.GetBool("ok", false));
            Assert.Equal(-3, copy["list"]!.Items[1].GetInt("x", 0));
        }
    }

    [Fact]
    public void Getters_WrongType_Throw()
    {
        var v = JsonParser.Parse("""{ "a": "text", "b": 1.5 }""");
        Assert.Throws<FormatException>(() => v.GetInt("a", 0));
        Assert.Throws<FormatException>(() => v.GetInt("b", 0));
        Assert.Throws<FormatException>(() => v.RequireString("missing"));
        Assert.Equal(7, v.GetInt("missing", 7));
    }
}
