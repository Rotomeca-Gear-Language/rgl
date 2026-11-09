using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le JSON dynamique et les dates dans les formats de données.</summary>
public class StdDataJsonTests
{
    [ZigFact]
    public void Un_json_dynamique_se_lit_et_se_reecrit()
    {
        var output = StdHarness.Body("""
            const string text: '{"nom": "Héros", "niveau": 3, "pv": 12.5, "actif": true, "rien": null, "liste": [1, 2, {"x": "a\"b\n"}], "vide": {}, "tab": [], "u": "\u00e9\ud83d\ude00"}';
            const std.DataValue? value: std.parseDataJson(text);
            std.writeLine(value eq null);
            std.writeLine(std.dataToJson(value ?? std.dataNull()));
            std.writeLine((std.dataGet(value ?? std.dataNull(), 'niveau') ?? std.dataNull()).number);
            std.writeLine(std.parseDataJson('[1, 2') eq null);
            std.writeLine(std.parseDataJson('[1, 2,]') eq null);
            std.writeLine(std.parseDataJson('{"a" 1}') eq null);
            std.writeLine(std.parseDataJson('12 13') eq null);
            std.writeLine(std.parseDataJson('"é"') eq null);
            std.writeLine(std.dataToJson(std.dataText('a"b\c' + "\n")));
            std.writeLine(std.dataToJson(std.dataDouble(1.0 / 0.0)));
            """);

        Assert.Equal("false\n{\"nom\":\"Héros\",\"niveau\":3,\"pv\":12.5,\"actif\":true,\"rien\":null,\"liste\":[1,2,{\"x\":\"a\\\"b\\n\"}],\"vide\":{},\"tab\":[],\"u\":\"é😀\"}\n3\ntrue\ntrue\ntrue\ntrue\nfalse\n\"a\\\"b\\\\c\\n\"\nnull\n", output);
    }

    [ZigFact]
    public void Les_dates_passent_par_les_formats()
    {
        var output = StdHarness.Body("""
            const datetime date: std.dateTime(2026, 10, 24, 10, 30, 0) ?? (0 as datetime);
            const std.DataValue value: std.dataMap(['quand', 'n'], [std.dataDate(date), std.dataInt(7)]);
            std.writeLine(std.dataToJson(value));
            const std.DataValue? viaMessagePack: std.fromMessagePack(std.toMessagePack(value));
            const std.DataValue? viaCbor: std.fromCbor(std.toCbor(value));
            const std.DataValue? textual: std.dataGet(viaMessagePack ?? std.dataNull(), 'quand');
            const std.DataValue? typed: std.dataGet(viaCbor ?? std.dataNull(), 'quand');
            std.writeLine((textual ?? std.dataNull()).kind eq std.DataKind.Text);
            std.writeLine((typed ?? std.dataNull()).kind eq std.DataKind.Date);
            std.writeLine(std.dataToDate(textual ?? std.dataNull()) eq date);
            std.writeLine(std.dataToDate(typed ?? std.dataNull()) eq date);
            std.writeLine(std.dataToDate(std.dataInt(3)) eq null);
            std.writeLine(std.toCbor(std.dataDate(date)).length);
            """);

        Assert.Equal("{\"quand\":\"2026-10-24T10:30:00Z\",\"n\":7}\ntrue\ntrue\ntrue\ntrue\ntrue\n22\n", output);
    }

    [ZigFact]
    public void Les_etiquettes_cbor_de_date_en_secondes_donnent_une_date()
    {
        var output = StdHarness.Body("""
            const bytes seconds: [0xc1, 0x1a, 0x65, 0x4f, 0x5f, 0x00];
            const std.DataValue? value: std.fromCbor(seconds);
            std.writeLine((value ?? std.dataNull()).kind eq std.DataKind.Date);
            std.writeLine((value ?? std.dataNull()).number);
            const bytes ignored: [0xd8, 0x20, 0x61, 0x61];
            std.writeLine((std.fromCbor(ignored) ?? std.dataNull()).text);
            """);

        Assert.Equal("true\n1699700480000\na\n", output);
    }
}
