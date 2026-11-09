using System.Text;
using System.Text.Json.Nodes;

namespace Rgl.Lsp;

/// <summary>
/// Les messages JSON-RPC échangés avec l'éditeur : chacun est précédé d'un en-tête
/// <c>Content-Length: n</c>, d'une ligne vide, puis de ses n octets de JSON en UTF-8.
/// </summary>
/// <param name="input">D'où viennent les messages de l'éditeur (l'entrée standard).</param>
/// <param name="output">Où vont les réponses et notifications (la sortie standard).</param>
internal sealed class JsonRpcChannel(Stream input, Stream output)
{
    private const string ContentLength = "Content-Length:";

    private readonly Stream _input = input;
    private readonly Stream _output = output;

    /// <summary>Le message suivant ; <see langword="null"/> quand l'éditeur a fermé l'entrée.</summary>
    public JsonObject? Read()
    {
        var length = -1;
        while (_ReadHeaderLine() is { } line)
        {
            if (line.Length == 0 && length >= 0)
            {
                var body = new byte[length];
                _input.ReadExactly(body);
                return JsonNode.Parse(body) as JsonObject;
            }

            if (line.StartsWith(ContentLength, StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[ContentLength.Length..].Trim());
        }

        return null;
    }

    /// <summary>Envoie un message à l'éditeur.</summary>
    public void Write(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        _output.Write(Encoding.ASCII.GetBytes($"{ContentLength} {body.Length}\r\n\r\n"));
        _output.Write(body);
        _output.Flush();
    }

    /// <summary>Une ligne d'en-tête, sans son <c>\r\n</c> ; <see langword="null"/> à la fin de l'entrée.</summary>
    private string? _ReadHeaderLine()
    {
        var line = new StringBuilder();
        int read;
        while ((read = _input.ReadByte()) >= 0)
        {
            if (read == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)read);
        }

        return line.Length == 0 ? null : line.ToString();
    }
}
