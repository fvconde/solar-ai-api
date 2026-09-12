# solar-ai-api

> **Camada de Domínio, Persistência e Regras de Negócio do Solar**  
> Para a visão geral da plataforma, decisões de produto, governança completa de privacidade e diagrama de arquitetura do sistema, consulte o **[README Hub do Solar](https://github.com/fvconde/solar-ai-docs)**.

---

## 1. Papel no Ecossistema

O `solar-ai-api` é o serviço central de retaguarda do Solar. Desenvolvido em **.NET 10**, ele atua como a guardiã oficial do domínio imobiliário, concentrando:
- **Gestão do Ciclo de Vida do Lead**: Criação, atualização de preferências extraídas, pontuação de maturidade e deduplicação de contatos por telefone/e-mail.
- **Persistência Relacional**: Gerenciamento de histórico de conversas, mensagens e encaminhamentos sobre PostgreSQL com Entity Framework Core.
- **Orquestração de Turnos**: Recebimento de mensagens do chat, verificação do consentimento de privacidade, oferta de slots de agenda, serialização de concorrência e despacho ao agente cognitivo Python (`solar-ai`).
- **Segurança e Privacidade (LGPD)**: Registro do carimbo de consentimento antes de qualquer diálogo, proteção de acesso ao painel e execução da eliminação integral de dados do titular (`ON DELETE CASCADE`).
- **Atribuição Determinística de Corretores (S-37)**: Regra pura de roteamento de leads para corretores humanos com base em especialidade da trilha (moradia vs investimento), aderência de região e balanceamento de carga.

---

## 2. Stack Tecnológica

- **Runtime**: [.NET 10](https://dotnet.microsoft.com/)
- **Framework Web**: ASP.NET Core MVC (Controllers)
- **ORM / Persistência**: [Entity Framework Core 10](https://learn.microsoft.com/ef/core/) com provedor `Npgsql.EntityFrameworkCore.PostgreSQL`
- **Validação e Resiliência**: `Microsoft.AspNetCore.RateLimiting` nativo
- **Documentação de API**: OpenAPI / Swagger (`Swashbuckle.AspNetCore`)
- **Testes Automatizados**: xUnit, FluentAssertions, banco PostgreSQL dedicado (`solar_test`)

---

## 3. Estrutura de Domínio e Banco de Dados

O banco de dados relacional é estruturado em 6 tabelas com convenção `snake_case`, versionadas integralmente por migrations do EF Core:

| Tabela | Responsabilidade | Chave Primária | Relacionamentos Principais |
|---|---|---|---|
| `leads` | Armazena dados cadastrais, preferências imobiliárias consolidadas, score, status e consentimento LGPD. | `Guid` (PK) | 1:N com `conversas`, 1:N com `encaminhamentos`. |
| `conversas` | Identifica sessões de atendimento ativas ou históricas geradas pelo frontend. | `Guid` (PK) | N:1 com `leads` (`ON DELETE CASCADE`). |
| `mensagens` | Registro cronológico das falas do lead e da Lia em cada turno. Ordenação estrita por `id` sequencial (`bigint identity`) para desempate de turnos. | `bigint` (PK) | N:1 com `conversas` (`ON DELETE CASCADE`). Vínculo opcional com `slots`. |
| `corretores` | Cadastro de corretores humanos parceiros, suas regiões de atuação e especialidade (`moradia` ou `investimento`). | `Guid` (PK) | Semeada por migration com 5 corretores ativos iniciais. |
| `encaminhamentos` | Registro da atribuição de um lead a um corretor parceiro. Índice único parcial por conversa para evitar duplicações. | `Guid` (PK) | N:1 com `leads` (`ON DELETE CASCADE`), N:1 com `conversas`, N:1 com `corretores`. |
| `slots` | Horários de atendimento disponíveis e reservados por corretor. | `int` (PK identity) | N:1 com `corretores`. Campo `lead_id` (FK anulável) registra a reserva ativa. |

### Migrations e Rotinas de Boot
- **Aplicação Automática**: A API aplica migrations pendentes automaticamente na inicialização (`MigracaoDoBanco.cs`), com política de retentativas espaçadas.
- **Agenda Dinâmica (`AgendaInicial.cs`)**: Não utiliza datas estáticas em migrations. No boot, gera e garante uma grade de pelo menos 6 horários livres futuros em dias úteis para cada corretor ativo, armazenados em UTC e convertidos para o fuso de São Paulo nas bordas de apresentação.

---

## 4. Endpoints da API

### Conversas e Atendimento (`/conversas`)
- `POST /conversas/{id}/consentimento`: Registra o consentimento do titular no lead vinculado antes do início da conversa. Exige `versaoAvisoPrivacidade` compatível com a política vigente.
- `POST /conversas/{id}/mensagens`: Recebe a mensagem do lead, valida o consentimento prévio (retorna `409 Conflict` se pendente), oferta horários de agenda livres, envia a requisição ao agente via `POST /turn` e persiste atômica e transacionalmente as duas mensagens, o perfil atualizado, eventual encaminhamento e a confirmação de reserva de slot.
- `POST /conversas/{id}/contato`: Registra `nome`, `telefone` e/ou `email` no lead na fase de handoff. Realiza a deduplicação automática por telefone/e-mail no banco, fundindo históricos de conversas anteriores e preservando o consentimento mais recente.
- `GET /conversas/{id}`: Devolve o histórico completo de mensagens, perfil acumulado, eventos de sistema e identificação do corretor atribuído para reconstrução de sessão no reload do front.
- `DELETE /conversas/{id}?excluirLead={bool}`: Exclui a conversa específica. Por padrão (`excluirLead=false`), preserva o lead; se `excluirLead=true`, exige credencial de privacidade e remove o lead e todos os vínculos em cascata.

### Gestão e Eliminação LGPD (`/leads`)
- `DELETE /leads/{id}`: Endpoint administrativo de eliminação definitiva do titular (LGPD Art. 18, VI). Exige autorização restrita (`X-Chave-Privacidade`), coordena travas de concorrência com todas as conversas ativas do lead e remove em cascata todas as conversas, mensagens e encaminhamentos, desvinculando reservas de agenda pendentes.

### Painel do Corretor (`/painel`)
- `GET /painel/corretores`: Lista corretores ativos para seleção de perfil operacional.
- `GET /painel/leads`: Fila de leads qualificados ordenada por score decrescente. Exige cabeçalho de autenticação `X-Chave-Privacidade` e identificação do corretor via `X-Corretor-Id`. Suporta filtros por intenção e por correlação de atribuição (`meusLeads=true`). Não expõe contatos sensíveis diretamente na listagem da fila.

### Monitoramento e Saúde (`/health`)
- `GET /health`: Contrato padronizado `{ service: "solar-ai-api", status: "up" | "degraded" | "down", version, checks }`. Verifica a conectividade com o PostgreSQL (`postgres: "healthy"`).

---

## 5. Governança de Privacidade e Segurança

1. **Autorização Administrativa Fail-Closed (`AutorizacaoPrivacidade.cs`)**:
   Rotas de exclusão e rotas do painel exigem credencial via cabeçalho `X-Chave-Privacidade`, `X-Admin-Key` ou `Authorization: Bearer`. Caso a chave não esteja configurada no servidor (`Seguranca:ChavePrivacidade`), o sistema recusa a chamada com `503 Service Unavailable` em vez de permitir acessos indevidos.
2. **Serialização por Turno (`TravaDeConversas.cs`)**:
   Utiliza `SemaphoreSlim` indexado por identificador de conversa para impedir que mensagens simultâneas da mesma sessão leiam históricos desatualizados ou gerem condições de corrida na atualização do perfil.
3. **Limitação de Taxa (Rate Limiting)**:
   Políticas configuradas para proteção contra negação de serviço e abuso:
   - `mensagens`: Controle de vazão no chat conversacional.
   - `painel`: Proteção contra raspagem da fila de leads.
   - `exclusao`: Limitação severa de chamadas aos endpoints destrutivos de dados pessoais.
4. **Blindagem Estrutural do Contrato com o LLM**:
   O contrato tipado `TurnoRequest` espelhado com o agente Python não contém campos para `telefone` ou `email`. Dados de contato são armazenados na API e apresentados no painel humano, nunca transitando para o agente ou provedores de IA.

---

## 6. Como Rodar e Testar

### Configuração Local
Defina a string de conexão no `appsettings.Development.json` ou via variável de ambiente:
```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=solar;Username=solar;Password=solar"
  },
  "Agente": {
    "Url": "http://localhost:8000",
    "TimeoutSegundos": 45
  },
  "Seguranca": {
    "ChavePrivacidade": "chave-secreta-compartilhada-solar"
  }
}
```

### Executar a API
```bash
dotnet run --project src/Solar.Api/Solar.Api.csproj
```
A API iniciará na porta configurada (padrão `http://localhost:8080`).

### Executar a Suíte de Testes
Os testes automatizados cobrem regras de agendamento, concorrência de slots, autorização de privacidade, deduplicação de contatos e integridade referencial com banco PostgreSQL real:
```bash
dotnet test tests/Solar.Api.Tests/Solar.Api.Tests.csproj
```
Para rodar os testes de concorrência e exclusão PostgreSQL contra um container local dedicado (`solar_test`):
```bash
dotnet test tests/Solar.Api.Tests/Solar.Api.Tests.csproj --filter "FullyQualifiedName~AgendaConcorrenciaPostgresTeste|FullyQualifiedName~ExclusaoLeadPostgresTeste"
```
