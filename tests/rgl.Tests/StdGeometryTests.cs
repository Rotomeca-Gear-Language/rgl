using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Formes 2D, matrices, couleurs.</summary>
public class StdGeometryTests
{
    [ZigFact]
    public void Les_formes_2d()
    {
        var output = StdHarness.Main("""
            use <Geometry> only {Vector2, Rect, Circle, Segment, Polygon};

            gear Test(): void {
                Rect r: {x: 0.0, y: 0.0, width: 4.0, height: 3.0};
                std.writeLine(r.contains({x: 1.0, y: 1.0}));
                std.writeLine(r.contains({x: 5.0, y: 1.0}));
                std.writeLine(r.area());
                std.writeLine(r.perimeter());
                Rect other: {x: 3.0, y: 2.0, width: 4.0, height: 4.0};
                std.writeLine(r.intersects(other));
                std.writeLine((r.intersection(other) ?? r).width);
                std.writeLine(r.merge(other).width);
                Circle c: {center: {x: 0.0, y: 0.0}, radius: 2.0};
                std.writeLine(c.contains({x: 1.0, y: 1.0}));
                std.writeLine(c.intersectsRect({x: 1.5, y: 0.0, width: 2.0, height: 2.0}));
                Segment s: {a: {x: 0.0, y: 0.0}, b: {x: 4.0, y: 0.0}};
                std.writeLine(s.length());
                std.writeLine(s.distanceTo({x: 2.0, y: 3.0}));
                Segment t: {a: {x: 2.0, y: -1.0}, b: {x: 2.0, y: 1.0}};
                std.writeLine((s.intersection(t) ?? {x: -1.0, y: -1.0}).x);
                Polygon square: {points: [{x: 0.0, y: 0.0}, {x: 2.0, y: 0.0}, {x: 2.0, y: 2.0}, {x: 0.0, y: 2.0}]};
                std.writeLine(square.area());
                std.writeLine(square.perimeter());
                std.writeLine(square.contains({x: 1.0, y: 1.0}));
                std.writeLine(square.contains({x: 3.0, y: 1.0}));
                std.writeLine(square.bounds().width);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\nfalse\n12.0\n14.0\ntrue\n1.0\n7.0\ntrue\ntrue\n4.0\n3.0\n2.0\n4.0\n8.0\ntrue\nfalse\n2.0\n", output);
    }

    [ZigFact]
    public void Les_matrices()
    {
        var output = StdHarness.Main("""
            use <Geometry> only {Vector2, Vector3, Matrix3, Matrix4};

            gear Test(): void {
                Matrix3 t: Matrix3.translation({x: 2.0, y: 3.0});
                Matrix3 s: Matrix3.scaling({x: 2.0, y: 2.0});
                Matrix3 both: t * s;
                std.writeLine(both.transformPoint({x: 1.0, y: 1.0}).x);
                std.writeLine(both.transformPoint({x: 1.0, y: 1.0}).y);
                std.writeLine(both.determinant());
                Matrix3 back: both.inverse() ?? Matrix3.identity();
                std.writeLine(back.transformPoint({x: 4.0, y: 5.0}).x);
                Matrix3 turn: Matrix3.rotation(1.5707963267948966);
                std.writeLine(std.abs(turn.transformDirection({x: 1.0, y: 0.0}).y - 1.0) < 0.000001);
                Matrix4 m: Matrix4.translation({x: 1.0, y: 2.0, z: 3.0}) * Matrix4.scaling({x: 2.0, y: 2.0, z: 2.0});
                std.writeLine(m.transformPoint({x: 1.0, y: 1.0, z: 1.0}).z);
                std.writeLine(m.determinant());
                Matrix4 inv: m.inverse() ?? Matrix4.identity();
                std.writeLine(inv.transformPoint({x: 3.0, y: 4.0, z: 5.0}).x);
                std.writeLine(Matrix4.identity().transpose().determinant());
                std.writeLine(Matrix4.scaling({x: 0.0, y: 1.0, z: 1.0}).inverse() eq null);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("4.0\n5.0\n4.0\n1.0\ntrue\n5.0\n8.0\n1.0\n1.0\ntrue\n", output);
    }

    [ZigFact]
    public void Les_couleurs()
    {
        var output = StdHarness.Main("""
            use <Color> only {Color};

            gear Test(): void {
                Color c: Color.fromHex('#ff8000') ?? Color.BLACK;
                std.writeLine(c.toHex());
                std.writeLine((Color.fromHex('0f0') ?? Color.BLACK).toHex());
                std.writeLine(Color.fromHex('zz') eq null);
                std.writeLine(Color.RED.toHex());
                std.writeLine(Color.rgb(300, 10, 20).toHex());
                const (double hue, double saturation, double lightness) hsl: Color.RED.toHsl();
                std.writeLine(hsl.hue);
                std.writeLine(hsl.lightness);
                std.writeLine(Color.fromHsl(120.0, 1.0, 0.5).toHex());
                std.writeLine(Color.fromHsl(240.0, 1.0, 0.25).toHex());
                std.writeLine(Color.BLACK.mix(Color.WHITE, 0.5).toHex());
                std.writeLine(Color.RED.inverted().toHex());
                std.writeLine(Color.WHITE.grayscale().toHex());
                std.writeLine(Color.BLACK.lighten(1.0).toHex());
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("#ff8000\n#00ff00\ntrue\n#ff0000\n#ff0a14\n0.0\n0.5\n#00ff00\n#000080\n#808080\n#00ffff\n#ffffff\n#ffffff\n", output);
    }
}
