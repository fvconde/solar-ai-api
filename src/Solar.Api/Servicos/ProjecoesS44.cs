using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Servicos;

public static class ProjecoesS44
{
    public static async Task<SessaoResponse> SessaoAsync(
        SolarDbContext db,
        Corretor conta,
        CancellationToken cancellationToken)
    {
        var supervisor = conta.Perfil == PerfisDoPainel.Supervisor;
        var corretor = conta.Perfil == PerfisDoPainel.Corretor;
        IReadOnlyList<string> filtros = supervisor
            ? conta.VinculoAtivo
                ? ["minha_fila", "sem_corretor", "visao_geral"]
                : ["sem_corretor", "visao_geral"]
            : corretor ? ["meus_leads"] : [];
        var filtroInicial = filtros.Count == 0
            ? null
            : supervisor ? conta.VinculoAtivo ? "minha_fila" : "sem_corretor" : "meus_leads";
        int? pendentes = supervisor
            ? await db.Corretores.CountAsync(c => c.StatusCorretor == StatusDoCorretor.EmAnalise, cancellationToken)
            : null;
        Guid? corretorId = corretor || (supervisor && conta.VinculoAtivo) ? conta.Id : null;

        return new SessaoResponse(
            new UsuarioResponse(conta.Id, conta.Nome, conta.Email),
            conta.Perfil,
            conta.Perfil == PerfisDoPainel.Cliente ? null : conta.StatusCorretor,
            corretorId,
            conta.VinculoAtivo,
            filtros,
            filtroInicial,
            pendentes);
    }

    public static async Task<ContaResponse> ContaAsync(
        SolarDbContext db,
        Corretor conta,
        CancellationToken cancellationToken)
    {
        var corretor = conta.Perfil == PerfisDoPainel.Cliente
            ? null
            : new CorretorContaResponse(
                conta.StatusCorretor ?? StatusDoCorretor.Aprovado,
                conta.Regioes,
                conta.Especialidades,
                conta.AprovadoEm);
        var consentimento = conta.ConsentimentoEm is { } em && conta.VersaoAvisoPrivacidade is { } versao
            ? new ConsentimentoContaResponse(em, versao)
            : null;
        var conversasSalvas = conta.Perfil == PerfisDoPainel.Cliente
            ? await db.Conversas.CountAsync(c => c.ContaId == conta.Id, cancellationToken)
            : 0;

        return new ContaResponse(
            conta.Id,
            conta.Nome,
            conta.Email,
            conta.Telefone ?? string.Empty,
            conta.Perfil,
            conta.CriadoEm,
            corretor,
            consentimento,
            conversasSalvas);
    }
}
