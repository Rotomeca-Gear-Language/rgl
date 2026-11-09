using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>MessagePack et CBOR : les exemples des spécifications, puis l'aller-retour.</summary>
public class StdBinaryFormatsTests
{
    [ZigFact]
    public void MessagePack_ecrit_et_lit_les_exemples_de_la_specification()
    {
        var output = StdHarness.Main("""
            use <Data> only {DataValue, dataNull, dataBool, dataInt, dataDouble, dataText, dataBytes, dataList, dataMap, dataToText, dataGet, dataAt};
            use <MessagePack> only {toMessagePack, fromMessagePack};
            use <Text> only {hexEncode, hexDecode};

            gear pack(DataValue value): string => hexEncode(toMessagePack(value));
            gear unpack(string hex): string {
                const DataValue? value: fromMessagePack(hexDecode(hex) ?? ('' as bytes));
                return value eq null ? 'refusé' : dataToText(value ?? dataNull());
            }

            gear Test(): void {
                const DataValue compact: dataMap(['compact', 'schema'], [dataBool(true), dataInt(0)]);
                std.writeLine(pack(compact));
                std.writeLine(unpack('82a7636f6d70616374c3a6736368656d6100'));
                std.writeLine(pack(dataInt(0)) + ' ' + pack(dataInt(127)) + ' ' + pack(dataInt(128)) + ' ' + pack(dataInt(-1)) + ' ' + pack(dataInt(-32)) + ' ' + pack(dataInt(-33)));
                std.writeLine(pack(dataInt(256)) + ' ' + pack(dataInt(65536)) + ' ' + pack(dataInt(4294967296)) + ' ' + pack(dataInt(-129)) + ' ' + pack(dataInt(-32769)));
                std.writeLine(pack(dataDouble(1.5)) + ' ' + pack(dataNull()) + ' ' + pack(dataBool(false)));
                std.writeLine(pack(dataText('abc')) + ' ' + pack(dataBytes(hexDecode('0102') ?? ('' as bytes))));
                std.writeLine(pack(dataList([dataInt(1), dataInt(2), dataInt(3)])));
                std.writeLine(unpack('93010203') + ' ' + unpack('cb3ff8000000000000') + ' ' + unpack('ca3fc00000') + ' ' + unpack('c403010203') + ' ' + unpack('d0ff') + ' ' + unpack('d1ff00') + ' ' + unpack('cd0100'));

                const DataValue nested: dataMap(['liste', 'texte', 'vide'], [dataList([dataInt(-5), dataDouble(2.25), dataNull()]), dataText('héros à 100 %'), dataMap([], [])]);
                const DataValue? again: fromMessagePack(toMessagePack(nested));
                std.writeLine(dataToText(again ?? dataNull()));
                std.writeLine(again eq nested);

                const string longText: 'x'.repeat(40);
                const DataValue? roundLong: fromMessagePack(toMessagePack(dataText(longText)));
                std.writeLine((roundLong ?? dataNull()).text.length);
                std.writeLine(pack(dataText(longText)).substring(0, 4));

                std.writeLine(unpack('c1') + ' ' + unpack('') + ' ' + unpack('92') + ' ' + unpack('a3616') + ' ' + unpack('c0c0') + ' ' + unpack('81c0c0') + ' ' + unpack('d9') + ' ' + unpack('a2ffff'));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "82a7636f6d70616374c3a6736368656d6100\n"
            + "{\"compact\":true,\"schema\":0}\n"
            + "00 7f cc80 ff e0 d0df\n"
            + "cd0100 ce00010000 cf0000000100000000 d1ff7f d2ffff7fff\n"
            + "cb3ff8000000000000 c0 c2\n"
            + "a3616263 c4020102\n"
            + "93010203\n"
            + "[1,2,3] 1.5 1.5 h'010203' -1 -256 256\n"
            + "{\"liste\":[-5,2.25,null],\"texte\":\"héros à 100 %\",\"vide\":{}}\n"
            + "true\n"
            + "40\n"
            + "d928\n"
            + "refusé refusé refusé refusé refusé refusé refusé refusé\n", output);
    }
}
