using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le module de test : assertTrue, assertEqual…</summary>
public class StdTestModuleTests
{
    [ZigFact]
    public void Les_verifications_reussies_se_taisent()
    {
        var output = StdHarness.Body("""
            std.assertTrue(1 < 2);
            std.assertFalse(2 < 1);
            std.assertEqual(3, 1 + 2);
            std.assertEqual('a', 'a');
            std.assertNotEqual(1, 2);
            std.assertNear(0.1 + 0.2, 0.3);
            int? rien: null;
            std.assertNull(rien);
            int? quelque: 4;
            std.assertNotNull(quelque);
            std.test('addition', () => { std.assertEqual(4, 2 + 2); });
            """);

        Assert.Equal("- addition\n  réussi\n", output);
    }

    [ZigFact]
    public void Un_echec_arrete_le_programme_avec_le_code_1()
    {
        var output = StdHarness.Body("""
            std.writeLine('avant');
            std.assertEqual(3, 4, 'somme');
            std.writeLine('apres');
            """, expectedCode: 1);

        Assert.StartsWith("avant\n", output);
        Assert.DoesNotContain("apres", output);
        Assert.Contains("attendu 3, reçu 4", output);
        Assert.Contains("somme", output);
    }
}
