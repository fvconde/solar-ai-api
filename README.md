# solar-ai-api

[![CI](https://github.com/fvconde/solar-ai-api/actions/workflows/ci.yml/badge.svg)](https://github.com/fvconde/solar-ai-api/actions/workflows/ci.yml)

API de domínio do **Solar**, um SDR imobiliário com IA: a corretora virtual **Lia** conversa com o lead, qualifica, recomenda imóveis e encaminha para um corretor humano.

Esta API é dona do banco e das regras de negócio: leads, conversas, agenda, encaminhamentos, contas, painel do corretor e governança LGPD (consentimento, exclusão do titular, retenção). A cada mensagem do lead, ela chama o agente Python ([solar-ai](https://github.com/fvconde/solar-ai)).

| Repositório | Papel |
|---|---|
| [solar-ai-front](https://github.com/fvconde/solar-ai-front) | Chat do lead e painel do corretor (Angular) |
| **solar-ai-api** | API, banco e regras de negócio (.NET) |
| [solar-ai](https://github.com/fvconde/solar-ai) | Agente da Lia (Python, LangGraph, Gemini) |

**Stack:** .NET 10 · ASP.NET Core · EF Core 10 + PostgreSQL 16 · xUnit

---

## Rodar localmente

Este repositório sobe o backend inteiro (Postgres + API + agente) com Docker.

### Pré-requisitos

- Docker Desktop (ou Docker Engine com Compose v2)
- Uma chave da API do Gemini ([Google AI Studio](https://aistudio.google.com/apikey)), usada pelo agente
- Os repositórios `solar-ai-api` e `solar-ai` clonados **lado a lado** na mesma pasta:

```bash
git clone https://github.com/fvconde/solar-ai-api.git
git clone https://github.com/fvconde/solar-ai.git
git clone https://github.com/fvconde/solar-ai-front.git   # para a interface
```

### 1. Configurar os `.env`

```bash
cp solar-ai-api/.env.example solar-ai-api/.env
cp solar-ai/.env.example solar-ai/.env
```

- `solar-ai-api/.env`: preencha `POSTGRES_PASSWORD`, `SEGURANCA__CHAVEPRIVACIDADE` (qualquer texto secreto) e `SEMENTE__SENHASUPERVISOR` (a senha que você vai usar para entrar no painel).
- `solar-ai/.env`: preencha `GEMINI_API_KEY`.

### 2. Subir

```bash
cd solar-ai-api
docker compose up --build
```

| Serviço | Endereço |
|---|---|
| API | http://localhost:8080 (Swagger em http://localhost:8080/swagger) |
| Agente | http://localhost:8000 |
| Postgres | localhost:5432 |

As migrations e a agenda dos corretores são aplicadas automaticamente no boot. Confira com `curl http://localhost:8080/health`.

Para a interface (chat e painel), siga o README do [solar-ai-front](https://github.com/fvconde/solar-ai-front).

**Painel do corretor:** entre em http://localhost:4200/entrar com `supervisor.vinculado@solar.local` e a senha definida em `SEMENTE__SENHASUPERVISOR`.

Para parar: `docker compose down` (os dados ficam no volume). Para zerar o banco: `docker compose down -v`.

---

## Testes

Os testes usam um banco PostgreSQL dedicado, `solar_test`. Com o Postgres do compose rodando e o `.env` preenchido, eles montam a conexão sozinhos:

```bash
dotnet test Solar.sln
```

Requer o .NET SDK 10. Para usar outro servidor, defina `ConnectionStrings__PostgresTest` (o nome do banco precisa ser `solar_test`).

---

## Estrutura

```
src/Solar.Api/
  Controllers/    endpoints (conversas, painel, contas, sessões, leads, health)
  Conversas/      orquestração do turno e chamada ao agente
  Agendamentos/   agenda e reserva de horários
  Encaminhamentos/ atribuição do lead a um corretor
  Seguranca/      autenticação, autorização e privacidade
  Persistencia/   DbContext e migrations
  Contracts/      contrato POST /turn, espelhado com o agente
tests/Solar.Api.Tests/
```

## Privacidade (LGPD)

- Nenhuma mensagem chega ao agente antes do consentimento registrado (`409` se pendente).
- Telefone e e-mail ficam só na API: o contrato com o agente não tem esses campos, então não chegam ao LLM.
- O titular pode excluir os próprios dados, com remoção em cascata de conversas, mensagens e encaminhamentos.
- Dados antigos são expurgados automaticamente após o prazo de retenção configurado.
- Rotas sensíveis usam autorização *fail-closed* e *rate limiting*.
