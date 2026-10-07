using Microsoft.EntityFrameworkCore;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Agendamentos;

/// <summary>Garante agenda futura relativa ao boot, sem datas vencidas em migration.</summary>
public static class AgendaInicial
{
    private const int QuantidadePadrao = 9;

    private static readonly TimeSpan HorarioDeSaoPaulo = TimeSpan.FromHours(-3);

    private static readonly TimeOnly[] Horarios =
    [
        new(9, 0),
        new(14, 0),
        new(19, 0),
    ];

    public static async Task GarantirAsync(WebApplication app)
    {
        await using var escopo = app.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var quantidade = Math.Clamp(
            app.Configuration.GetValue("Agenda:SlotsLivresPorCorretor", QuantidadePadrao),
            3,
            30);

        await GarantirAsync(db, DateTimeOffset.UtcNow, quantidade);
    }

    public static async Task GarantirAsync(
        SolarDbContext db,
        DateTimeOffset agora,
        int quantidade,
        CancellationToken cancellationToken = default)
    {
        var agoraUtc = agora.ToUniversalTime();

        var candidatosParaLimpeza = await db.Slots
            .Where(slot => slot.Inicio > agoraUtc && slot.LeadId == null)
            .Select(slot => new { slot.Id, slot.Inicio })
            .ToListAsync(cancellationToken);

        var idsInvalidos = candidatosParaLimpeza
            .Where(slot =>
            {
                var hora = slot.Inicio.ToOffset(HorarioDeSaoPaulo).TimeOfDay;
                return hora != new TimeSpan(9, 0, 0)
                    && hora != new TimeSpan(14, 0, 0)
                    && hora != new TimeSpan(19, 0, 0);
            })
            .Select(slot => slot.Id)
            .ToList();

        if (idsInvalidos.Count > 0)
        {
            await db.Slots
                .Where(slot => idsInvalidos.Contains(slot.Id) && slot.Inicio > agoraUtc && slot.LeadId == null)
                .ExecuteDeleteAsync(cancellationToken);
        }

        var corretores = await db.Corretores
            .Where(corretor => corretor.Ativo)
            .Select(corretor => corretor.Id)
            .ToListAsync(cancellationToken);

        foreach (var corretorId in corretores)
        {
            var existentes = await db.Slots
                .Where(slot => slot.CorretorId == corretorId && slot.Inicio > agoraUtc)
                .Select(slot => new { slot.Inicio, slot.LeadId })
                .ToListAsync(cancellationToken);

            var inicios = existentes.Select(slot => slot.Inicio).ToHashSet();
            var livres = existentes.Count(slot => slot.LeadId is null);

            foreach (var horario in ProximosHorarios(agoraUtc))
            {
                if (livres >= quantidade)
                {
                    break;
                }

                if (!inicios.Add(horario.Inicio))
                {
                    continue;
                }

                db.Slots.Add(Slot.Novo(corretorId, horario.Inicio, horario.Fim));
                livres++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static IEnumerable<(DateTimeOffset Inicio, DateTimeOffset Fim)> ProximosHorarios(
        DateTimeOffset agora)
    {
        var local = agora.ToOffset(HorarioDeSaoPaulo);

        for (var dia = local.Date; ; dia = dia.AddDays(1))
        {
            if (dia.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var horario in Horarios)
            {
                var inicio = new DateTimeOffset(
                    dia.Year,
                    dia.Month,
                    dia.Day,
                    horario.Hour,
                    horario.Minute,
                    0,
                    HorarioDeSaoPaulo);

                if (inicio <= agora)
                {
                    continue;
                }

                // Npgsql exige offset zero para timestamp with time zone. A
                // conversao para horario de Sao Paulo acontece so na borda.
                yield return (inicio.ToUniversalTime(), inicio.AddHours(1).ToUniversalTime());
            }
        }
    }
}
