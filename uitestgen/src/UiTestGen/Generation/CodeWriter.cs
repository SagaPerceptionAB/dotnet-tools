using System.Text;

namespace UiTestGen.Generation;

/// <summary>Skriver kod med indrag (4 mellanslag) och <c>\n</c> som radslut. Inga mallar.</summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _text = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0) _text.Append(' ', _indent * 4).Append(text);
        _text.Append('\n');
        return this;
    }

    /// <summary>Skriver rubriken och en <c>{</c>; avslutas med <c>}</c> när det returnerade värdet kastas.</summary>
    public IDisposable Block(string header)
    {
        Line(header);
        Line("{");
        _indent++;
        return new Closer(this);
    }

    public override string ToString() => _text.ToString();

    private sealed class Closer(CodeWriter writer) : IDisposable
    {
        public void Dispose()
        {
            writer._indent--;
            writer.Line("}");
        }
    }
}
