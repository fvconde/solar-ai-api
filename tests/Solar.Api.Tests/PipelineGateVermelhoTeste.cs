namespace Solar.Api.Tests;

public sealed class PipelineGateVermelhoTeste
{
    [Fact]
    public void Gate_deve_reprovar_este_teste_propositalmente_quebrado()
    {
        Assert.True(false, "S-27: prova descartavel de que teste vermelho reprova o CI.");
    }
}
