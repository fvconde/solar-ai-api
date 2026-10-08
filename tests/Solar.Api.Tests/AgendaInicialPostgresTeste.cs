using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Api.Agendamentos;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class AgendaInicialPostgresTeste
{
    [Fact]
    public async Task Boot_remove_apenas_slots_futuros_livres_fora_dos_horarios()
    {
        var corretor = CriarCorretorTeste();
        var lead = Lead.Novo(DateTimeOffset.UtcNow);

        var agora = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.FromHours(-3)).ToUniversalTime();
        var sp = TimeSpan.FromHours(-3);

        var slotFuturo11h = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 11, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 12, 0, 0, sp).ToUniversalTime());

        var slotFuturo15h = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 16, 0, 0, sp).ToUniversalTime());

        var slotFuturo9h30 = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 9, 30, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 10, 30, 0, sp).ToUniversalTime());

        var slotFuturo9h = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 9, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 10, 0, 0, sp).ToUniversalTime());

        var slotFuturo14h = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 14, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, sp).ToUniversalTime());

        var slotFuturo19h = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 19, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 20, 0, 0, sp).ToUniversalTime());

        var slotPassadoIrregular = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 7, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 8, 0, 0, sp).ToUniversalTime());

        var slotReservadoFuturoIrregular = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 16, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 17, 0, 0, sp).ToUniversalTime());

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Leads.Add(lead);
            preparacao.Slots.AddRange(
                slotFuturo11h,
                slotFuturo15h,
                slotFuturo9h30,
                slotFuturo9h,
                slotFuturo14h,
                slotFuturo19h,
                slotPassadoIrregular,
                slotReservadoFuturoIrregular);
            await preparacao.SaveChangesAsync();

            await preparacao.Slots
                .Where(s => s.Id == slotReservadoFuturoIrregular.Id && s.LeadId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, lead.Id));
        }

        try
        {
            await using (var execucao = await PostgresTestDatabase.CriarContextoAsync())
            {
                await AgendaInicial.GarantirAsync(execucao, agora, 9);
            }

            await using (var verificacao = await PostgresTestDatabase.CriarContextoAsync())
            {
                var slotsDoCorretor = await verificacao.Slots
                    .AsNoTracking()
                    .Where(s => s.CorretorId == corretor.Id)
                    .ToListAsync();

                var idsPresentes = slotsDoCorretor.Select(s => s.Id).ToHashSet();

                Assert.DoesNotContain(slotFuturo11h.Id, idsPresentes);
                Assert.DoesNotContain(slotFuturo15h.Id, idsPresentes);
                Assert.DoesNotContain(slotFuturo9h30.Id, idsPresentes);

                Assert.Contains(slotFuturo9h.Id, idsPresentes);
                Assert.Contains(slotFuturo14h.Id, idsPresentes);
                Assert.Contains(slotFuturo19h.Id, idsPresentes);

                Assert.Contains(slotPassadoIrregular.Id, idsPresentes);

                Assert.Contains(slotReservadoFuturoIrregular.Id, idsPresentes);
                var reservadoRecuperado = slotsDoCorretor.Single(s => s.Id == slotReservadoFuturoIrregular.Id);
                Assert.Equal(lead.Id, reservadoRecuperado.LeadId);
                Assert.Equal(slotReservadoFuturoIrregular.Inicio, reservadoRecuperado.Inicio);
                Assert.Equal(slotReservadoFuturoIrregular.Fim, reservadoRecuperado.Fim);
            }
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == lead.Id).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Boot_recompoe_nove_livres_sem_duplicar_e_preserva_reservas()
    {
        var corretor = CriarCorretorTeste();
        var lead = Lead.Novo(DateTimeOffset.UtcNow);

        var agora = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.FromHours(-3)).ToUniversalTime();
        var sp = TimeSpan.FromHours(-3);

        var slotReservado = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 14, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 15, 0, 0, sp).ToUniversalTime());

        var slotExistente = Slot.Novo(
            corretor.Id,
            new DateTimeOffset(2026, 10, 5, 19, 0, 0, sp).ToUniversalTime(),
            new DateTimeOffset(2026, 10, 5, 20, 0, 0, sp).ToUniversalTime());

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Leads.Add(lead);
            preparacao.Slots.AddRange(slotReservado, slotExistente);
            await preparacao.SaveChangesAsync();

            await preparacao.Slots
                .Where(s => s.Id == slotReservado.Id && s.LeadId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, lead.Id));
        }

        try
        {
            await using (var db1 = await PostgresTestDatabase.CriarContextoAsync())
            {
                await AgendaInicial.GarantirAsync(db1, agora, 9);
            }

            await using (var db2 = await PostgresTestDatabase.CriarContextoAsync())
            {
                await AgendaInicial.GarantirAsync(db2, agora, 9);
            }

            await using (var verificacao = await PostgresTestDatabase.CriarContextoAsync())
            {
                var todosSlots = await verificacao.Slots
                    .AsNoTracking()
                    .Where(s => s.CorretorId == corretor.Id && s.Inicio > agora)
                    .OrderBy(s => s.Inicio)
                    .ToListAsync();

                var livres = todosSlots.Where(s => s.LeadId == null).ToList();
                Assert.Equal(9, livres.Count);

                var horariosPermitidos = new HashSet<TimeSpan>
                {
                    new(9, 0, 0),
                    new(14, 0, 0),
                    new(19, 0, 0),
                };

                Assert.All(livres, slot =>
                {
                    var horaLocal = slot.Inicio.ToOffset(sp).TimeOfDay;
                    Assert.Contains(horaLocal, horariosPermitidos);
                    Assert.Null(slot.LeadId);
                });

                var totalInicios = todosSlots.Select(s => s.Inicio).ToList();
                Assert.Equal(totalInicios.Count, totalInicios.Distinct().Count());

                var reservadoRecuperado = todosSlots.Single(s => s.Id == slotReservado.Id);
                Assert.Equal(lead.Id, reservadoRecuperado.LeadId);
                Assert.Equal(slotReservado.Inicio, reservadoRecuperado.Inicio);
                Assert.Equal(slotReservado.Fim, reservadoRecuperado.Fim);
            }
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == lead.Id).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData(null, 9)]
    [InlineData("1", 3)]
    [InlineData("50", 30)]
    public async Task GarantirAsync_com_webapplication_aplica_quantidade_padrao_e_clamp(
        string? configValor,
        int quantidadeEsperada)
    {
        var corretor = CriarCorretorTeste();

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            await preparacao.SaveChangesAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        if (configValor is not null)
        {
            builder.Configuration["Agenda:SlotsLivresPorCorretor"] = configValor;
        }

        builder.Services.AddDbContext<SolarDbContext>(options =>
            options.UseNpgsql(PostgresTestDatabase.ObterConexaoParaAplicacao()));

        var app = builder.Build();

        try
        {
            await AgendaInicial.GarantirAsync(app);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotsDoCorretor = await verificacao.Slots
                .AsNoTracking()
                .Where(s => s.CorretorId == corretor.Id && s.LeadId == null && s.Inicio > DateTimeOffset.UtcNow.AddMinutes(-5))
                .ToListAsync();

            Assert.Equal(quantidadeEsperada, slotsDoCorretor.Count);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
            await app.DisposeAsync();
        }
    }

    private static Corretor CriarCorretorTeste()
    {
        var id = Guid.NewGuid();
        var corretor = Corretor.NovaConta(
            nome: $"Corretor Teste {id:N}",
            email: $"corretor-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-{id:N}@TESTE.COM",
            telefone: "11999999999",
            senhaHash: "fake-hash",
            perfil: PerfisDoPainel.Corretor,
            regioes: ["Sul"],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: "v1",
            em: DateTimeOffset.UtcNow);
        corretor.Aprovar(DateTimeOffset.UtcNow);
        return corretor;
    }
}
