using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>CBOR : les exemples de l'annexe A de la spécification, puis l'aller-retour.</summary>
public class StdCborTests
{
    [ZigFact]
    public void CBOR_ecrit_et_lit_les_exemples_de_la_specification()
    {
        var output = StdHarness.Main("""
            use <Data> only {DataValue, dataNull, dataBool, dataInt, dataDouble, dataText, dataBytes, dataList, dataMap, dataToText};
            use <Cbor> only {toCbor, fromCbor};
            use <Text> only {hexEncode, hexDecode};

            gear pack(DataValue value): string => hexEncode(toCbor(value));
            gear unpack(string hex): string {
                const DataValue? value: fromCbor(hexDecode(hex) ?? ('' as bytes));
                return value eq null ? 'refusé' : dataToText(value ?? dataNull());
            }

            gear Test(): void {
                std.writeLine(([0, 1, 10, 23, 24, 25, 100, 1000, 1000000, -1, -10, -100, -1000] ||> (n) => pack(dataInt(n))).join(' '));
                std.writeLine(pack(dataInt(1000000000000)));
                std.writeLine(pack(dataDouble(1.1)) + ' ' + pack(dataBool(false)) + ' ' + pack(dataBool(true)) + ' ' + pack(dataNull()));
                std.writeLine(pack(dataText('')) + ' ' + pack(dataText('a')) + ' ' + pack(dataText('IETF')) + ' ' + pack(dataBytes(hexDecode('01020304') ?? ('' as bytes))));
                std.writeLine(pack(dataList([])) + ' ' + pack(dataList([dataInt(1), dataInt(2), dataInt(3)])) + ' ' + pack(dataMap([], [])));
                std.writeLine(pack(dataMap(['a', 'b'], [dataInt(1), dataList([dataInt(2), dataInt(3)])])));

                std.writeLine(unpack('f93e00') + ' ' + unpack('f97bff') + ' ' + unpack('f9c400') + ' ' + unpack('fa47c35000') + ' ' + unpack('fb3ff199999999999a'));
                std.writeLine(unpack('fb7e37e43c8800759c').length > 5);
                std.writeLine(unpack('a26161016162820203') + ' ' + unpack('83010203') + ' ' + unpack('4401020304') + ' ' + unpack('6449455446') + ' ' + unpack('f4') + ' ' + unpack('f6'));
                std.writeLine(unpack('1b000000e8d4a51000') + ' ' + unpack('3903e7') + ' ' + unpack('1819'));

                const DataValue nested: dataMap(['liste', 'texte', 'octets'], [dataList([dataInt(-5), dataDouble(2.25), dataNull()]), dataText('héros à 100 %'), dataBytes(hexDecode('ff00') ?? ('' as bytes))]);
                const DataValue? again: fromCbor(toCbor(nested));
                std.writeLine(again eq nested);
                std.writeLine(pack(dataText('x'.repeat(30))).substring(0, 4));

                std.writeLine(unpack('1f') + ' ' + unpack('') + ' ' + unpack('18') + ' ' + unpack('ff') + ' ' + unpack('5f') + ' ' + unpack('8301020300') + ' ' + unpack('c074') + ' ' + unpack('62ffff') + ' ' + unpack('82010'));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "00 01 0a 17 1818 1819 1864 1903e8 1a000f4240 20 29 3863 3903e7\n"
            + "1b000000e8d4a51000\n"
            + "fb3ff199999999999a f4 f5 f6\n"
            + "60 6161 6449455446 4401020304\n"
            + "80 83010203 a0\n"
            + "a26161016162820203\n"
            + "1.5 65504.0 -4.0 1e+05 1.1\n"
            + "true\n"
            + "{\"a\":1,\"b\":[2,3]} [1,2,3] h'01020304' \"IETF\" false null\n"
            + "1000000000000 -1000 25\n"
            + "true\n"
            + "781e\n"
            + "refusé refusé refusé refusé refusé refusé refusé refusé refusé\n", output);
    }
}
