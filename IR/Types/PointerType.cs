namespace Rgl.IR.Types;
internal sealed record PointerType(IrType Element, bool Owned = true) : IrType
{
    public override string ToString() => $"{(Owned ? "ptr" : "uptr")}<{Element}>";
}
