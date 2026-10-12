# Especificação — Extrator de Mídias (codinome `MediaRescue`)

Aplicativo desktop para Windows que extrai vídeos caseiros de CDs e DVDs para um pendrive/HD externo ou para o disco do computador, com interface gráfica, diagnóstico do leitor e tolerância a discos danificados.

Este documento é a fonte única de requisitos para o agente que vai implementar, testar e validar. Onde a spec diz **DEVE**, é obrigatório; **PODE** é opcional. Se algo aqui se mostrar tecnicamente inviável, o agente registra o desvio em `DESVIOS.md` com o motivo, em vez de improvisar em silêncio.

---

## 1. Contexto

- **Usuário:** pessoa física, não técnica no uso diário do app. Tem dezenas de CDs/DVDs com vídeos caseiros (apresentações escolares e afins), muitos gravados há 10–20 anos.
- **Valor do conteúdo:** insubstituível. A prioridade nº 1 é **nunca perder nem corromper dados silenciosamente**; a nº 2 é recuperar o máximo possível de discos degradados.
- **Privacidade:** os vídeos têm crianças. O app NÃO DEVE fazer nenhuma chamada de rede, telemetria ou upload. Tudo é local.
- **Idioma da interface:** português do Brasil. Código, nomes de símbolos e commits em inglês.

### 1.1 Máquina alvo (verificado em 2026-10-12)

| Item | Valor |
|---|---|
| SO | Windows 11 Pro 10.0.26300, 64 bits |
| Leitor | `hp CDDVDW TS-L633M USB Device`, unidade `D:` (slim, alimentado pela USB) |
| Disco interno | NVMe 512 GB, `C:` com **apenas ~8 GB livres** |
| `G:` | Google Drive montado (FAT32 virtual, cache no `C:`) |
| Ferramentas | `winget` presente. **Ausentes:** .NET SDK, git, ffmpeg/ffprobe. `python` é só o atalho da Store |

Consequências: o agente precisa instalar o toolchain (seção 3.2) gastando pouco disco; e o app precisa tratar "destino sem espaço" como caso comum, não raro.

---

## 2. Escopo

### 2.1 Dentro do escopo

1. Detectar leitores ópticos e o estado da mídia em tempo real.
2. Mostrar detalhes completos da mídia e dos arquivos/vídeos nela.
3. Copiar o conteúdo de forma fiel para um destino escolhido, com verificação de integridade.
4. Recuperar o máximo possível de discos com erros de leitura, informando exatamente o que foi perdido.
5. Diagnóstico do leitor (está funcionando? lê? ejeta? qual a velocidade?).
6. Modo lote para processar muitos discos em sequência.
7. Histórico/catálogo dos discos já extraídos, com detecção de disco repetido.
8. Conversão opcional para MP4 (marco M6, depois que a cópia fiel estiver sólida).

### 2.2 Fora do escopo

- **Discos com proteção contra cópia (CSS e similares).** O app DEVE detectar e recusar com mensagem clara; NÃO DEVE conter nem baixar código de quebra de proteção (libdvdcss etc.).
- CD de áudio (CDDA): detectar e informar "CD de áudio não suportado nesta versão".
- Gravação de discos, upload para nuvem, edição de vídeo.
- Blu-ray (o leitor não lê).

### 2.3 Formatos de disco que DEVEM ser reconhecidos

| Tipo | Como identificar | Tratamento |
|---|---|---|
| Disco de dados com vídeos | Arquivos `.avi .mpg .mpeg .wmv .mp4 .mov .3gp .asf .mts .m2ts .dv .mkv .flv` em ISO9660/Joliet/UDF | Copiar a árvore inteira |
| VCD | Pasta `MPEGAV` com `AVSEQnn.DAT` | Copiar; rotular como VCD; os `.DAT` são MPEG‑1 em contêiner RIFF/CDXA |
| SVCD | Pasta `MPEG2` com `AVSEQnn.MPG` | Copiar; rotular como SVCD |
| DVD‑Video caseiro | Pasta `VIDEO_TS` com `.IFO/.BUP/.VOB` | Copiar a pasta inteira; agrupar VOBs por título na UI |
| DVD‑VR (gravador de mesa) | Pasta `DVD_RTAV` com `VR_MOVIE.VRO` | Copiar |
| AVCHD em DVD (filmadora) | Pasta `BDMV/STREAM/*.MTS` | Copiar |
| Disco misto (vídeos + fotos + docs) | — | Copiar tudo; a UI destaca os vídeos |
| CD de áudio | TOC só com trilhas de áudio | Informar não suportado |
| Disco vazio | Mídia presente, estado "em branco" | Informar "disco vazio" |
| Disco não finalizado | Sessão aberta, sem sistema de arquivos legível | Informar com orientação (seção 8, E‑07) |
| DVD protegido | `CopyrightProtectionType != 0` | Recusar (seção 8, E‑09) |

Todos os arquivos do disco são copiados por padrão (não só vídeos); a UI permite desmarcar itens.

---

## 3. Stack e build

### 3.1 Decisões

- **Linguagem/UI:** C# 12, .NET 8 (LTS), **WPF**, padrão MVVM com `CommunityToolkit.Mvvm`.
- **Distribuição:** `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`. Resultado: `MediaRescue.exe` que roda sem instalar .NET.
- **Sem privilégio de administrador.** Manifesto `asInvoker`. Toda função que não funcionar sem elevação DEVE degradar com aviso, nunca travar o app.
- **Acesso ao hardware:** P/Invoke em `kernel32` (`CreateFile`, `DeviceIoControl`, `ReadFile` overlapped, `CancelIoEx`) + COM **IMAPI2** para tipo/estado da mídia. WMI (`Win32_CDROMDrive`) só para inventário.
- **Metadados de vídeo e conversão:** `ffprobe.exe`/`ffmpeg.exe` em `tools\` ao lado do executável. O app DEVE funcionar sem eles (sem duração/resolução e sem conversão, com aviso discreto).
- **Persistência:** arquivos JSON (`System.Text.Json`). Sem banco de dados.
- **Log:** `Serilog` em arquivo rotativo em `%LOCALAPPDATA%\MediaRescue\logs\`.
- **Testes:** xUnit + FluentAssertions; `LTR.DiscUtils.Iso9660` (ou equivalente) para gerar ISOs de teste.

### 3.2 Preparação do ambiente (pelo agente)

1. Instalar o SDK do .NET 8 no perfil do usuário, sem admin: script oficial `dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\dotnet"`, ou `winget install Microsoft.DotNet.SDK.8`. Baixar/instalar software exige confirmação do usuário — pedir antes.
2. ffmpeg/ffprobe: build LGPL "essentials" win64 de fonte conhecida (gyan.dev ou BtbN/GitHub), versão fixada, com conferência de SHA‑256 no script `build/fetch-tools.ps1`. Não versionar os binários.
3. Antes de cada etapa pesada, checar espaço livre no `C:`; abortar com mensagem se < 2 GB. Limpar `bin/obj` e caches de teste ao final.

### 3.3 Estrutura da solução

```
MediaRescue.sln
src/
  MediaRescue.Core/        # domínio, sem dependência de Windows: modelos, motor de cópia, nomes, catálogo
  MediaRescue.Windows/     # P/Invoke, IMAPI2, WMI, notificações de dispositivo
  MediaRescue.App/         # WPF (Views, ViewModels)
tests/
  MediaRescue.Core.Tests/         # unitários, com fakes e injeção de falhas
  MediaRescue.Integration.Tests/  # ISOs montadas, sistema de arquivos real
build/
  fetch-tools.ps1  publish.ps1  make-test-media.ps1
docs/
  TESTE-MANUAL.md  DESVIOS.md
```

**Regra de arquitetura:** `Core` só fala com hardware por interfaces — `IOpticalDriveService`, `IDiscReader` (abre arquivos do disco como streams), `IDestinationFileSystem`, `IClock`, `IMediaProbe`. É isso que permite testar erros de leitura sem disco físico.

---

## 4. Requisitos funcionais

### RF‑01 Detecção de leitores e mídia

- Listar todos os leitores ópticos (pode haver 0, 1 ou vários; inclui unidades virtuais de ISO montada).
- Reagir sem reiniciar o app a: leitor conectado/desconectado, disco inserido/removido. Usar `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL`/`DBT_DEVICEREMOVECOMPLETE`) com **polling de reserva a cada 2 s** via `IOCTL_STORAGE_CHECK_VERIFY`, pois leitores USB antigos nem sempre notificam.
- Estados exibidos: `Nenhum leitor`, `Sem disco`, `Bandeja aberta` (quando distinguível), `Lendo disco…` (spin‑up; pode levar até 60 s em disco ruim), `Disco pronto`, `Disco vazio`, `Disco não finalizado`, `Disco ilegível`, `Protegido`, `CD de áudio`.
- Ao inserir, aguardar a mídia estabilizar (duas leituras consecutivas OK do descritor de volume) antes de analisar.

### RF‑02 Detalhes da mídia

Painel com tudo que for obtível; campo indisponível mostra "—", nunca erro:

- **Leitor:** fabricante/modelo, firmware, barramento (USB/SATA), letra.
- **Disco:** tipo físico (CD‑ROM, CD‑R, CD‑RW, DVD‑ROM, DVD±R, DVD±RW, DVD‑RAM…), estado (finalizado/aberto/vazio), nº de sessões e trilhas, capacidade e espaço usado, sistema de arquivos (CDFS/UDF + versão quando disponível), rótulo do volume, número de série, data de gravação (do descritor de volume), formato lógico reconhecido (tabela 2.3).
- **Conteúdo:** árvore de arquivos com tamanho, data e tipo; total de arquivos e bytes; contagem de vídeos.
- **Vídeos (via ffprobe, em segundo plano, cancelável):** duração, resolução, taxa de quadros, codecs de vídeo/áudio, bitrate, entrelaçado sim/não. Para DVD‑Video, agrupar `VTS_nn_1..n.VOB` como um título e somar a duração; ignorar `VTS_nn_0.VOB` (menu) na contagem.
- **Miniatura** de cada vídeo (1 quadro a ~10% da duração) — PODE; nunca bloquear a UI nem a cópia por causa disso; timeout de 15 s por arquivo.
- A sondagem lê do disco, que é lento: limitar a 1 processo por vez e **pausar a sondagem quando a cópia começar**.

### RF‑03 Escolha do destino

- Seletor de pasta + lista rápida das unidades disponíveis (removíveis e fixas) com rótulo, sistema de arquivos, espaço livre/total.
- Validar antes de habilitar "Extrair": existe, é gravável (teste real de criar/apagar arquivo), não é a própria unidade óptica, não é somente leitura, tem espaço (bytes selecionados + 5% + 50 MB).
- Avisos (não bloqueiam, exceto o primeiro quando se aplica):
  - **FAT32 e arquivo ≥ 4 GB** → bloquear esses arquivos e explicar ("formate o pendrive em exFAT ou escolha outro destino").
  - Destino é unidade de nuvem/rede (`G:` Google Drive, caminho UNC) → avisar que a cópia usa cache no `C:` e pode falhar por espaço.
  - Destino no `C:` com < 10 GB livres após a cópia → aviso.
- Lembrar o último destino (se ainda existir).

### RF‑04 Nomeação e organização

- Uma pasta por disco: `<Destino>\<Nome do disco>\`, preservando a estrutura interna original.
- Nome sugerido: rótulo do volume; se vazio ou genérico (`NEW`, `CDROM`, `DVD_VIDEO`, `MY DISC`, só dígitos…), usar `Disco AAAA-MM-DD` com a data de gravação. Campo editável antes de extrair — o usuário vai querer digitar "Formatura 2009".
- Sanitizar: caracteres inválidos, nomes reservados (`CON`, `NUL`, `COM1`…), ponto/espaço no final, limite de 80 caracteres no nome da pasta.
- Colisão de pasta: nunca sobrescrever nem mesclar; sugerir `Nome (2)`.
- Caminhos longos: usar prefixo `\\?\` internamente; avisar se o caminho final passar de 240 caracteres (compatibilidade com outros programas).

### RF‑05 Motor de cópia

Requisitos de integridade:

1. Gravar em `<arquivo>.partial` e renomear só ao concluir. Nunca deixar arquivo incompleto com nome final.
2. Calcular SHA‑256 do que foi lido durante a cópia; ao terminar, `FlushFileBuffers`, **reler o destino** e comparar. Divergência → uma nova tentativa; persistindo → marcar o arquivo como `FalhaDestino` e alertar (pendrive defeituoso).
3. Preservar datas de criação/modificação.
4. Ordem de cópia: ordem física no disco quando obtível (menos seek); senão, ordem de diretório.
5. Durante a cópia: `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` para impedir suspensão; liberar ao fim.
6. Bloquear ejeção (`IOCTL_STORAGE_MEDIA_REMOVAL`) durante a cópia quando possível; liberar sempre, inclusive em falha (bloco `finally`).

Leitura tolerante a erros (o coração do app):

- Leitura em blocos de 64 KiB alinhados ao setor (2048 B), sem cache do SO quando possível (`FILE_FLAG_NO_BUFFERING`; se a abertura falhar ou o alinhamento não bater — ex. `.DAT` de VCD —, cair para leitura com buffer).
- I/O **overlapped com timeout** (padrão 30 s por bloco) e `CancelIoEx`; uma leitura travada nunca pode congelar a UI nem impedir "Cancelar".
- Erros tratados como "setor ruim": `ERROR_CRC (23)`, `ERROR_SECTOR_NOT_FOUND (27)`, `ERROR_IO_DEVICE (1117)`, `ERROR_SEM_TIMEOUT (121)`, timeout próprio. Erros tratados como "dispositivo/mídia sumiu": `ERROR_NOT_READY (21)`, `ERROR_NO_MEDIA_IN_DRIVE (1112)`, `ERROR_MEDIA_CHANGED (1110)`, `ERROR_DEVICE_NOT_CONNECTED (1167)`, `ERROR_INVALID_HANDLE` após remoção → ver E‑03/E‑04.
- Estratégia em duas passadas, por arquivo:
  - **Passada 1:** bloco falhou → 1 nova tentativa → marca o intervalo como pendente, grava zeros no lugar e **pula adiante** (salto que dobra a cada falha consecutiva, de 64 KiB até 4 MiB; volta a 64 KiB na primeira leitura boa).
  - **Passada 2:** revisita os intervalos pendentes setor a setor, até N tentativas cada (padrão 3), opcionalmente reduzindo a velocidade do leitor (`IOCTL_CDROM_SET_SPEED`). Orçamento de tempo por disco para esta passada (padrão 20 min, configurável; "Insistir mais" na UI estende).
- O arquivo de saída mantém sempre o tamanho original (zeros nos trechos irrecuperáveis), para o vídeo continuar reproduzível com um "salto".
- Resultado por arquivo: `Íntegro`, `Recuperado` (teve erro, mas tudo foi lido na passada 2), `Parcial` (com lista de intervalos perdidos, bytes e % perdidos), `Falhou` (nada lido), `Ignorado`, `FalhaDestino`.
- Arquivo `Parcial` é salvo com o nome normal, e a pasta do disco recebe `LEIA-ME - ERROS.txt` listando o que faltou. Hash de arquivo parcial é registrado como "do conteúdo recuperado".

Controles: Pausar/Continuar, Cancelar (pergunta se mantém o que já foi copiado), Pular arquivo atual.

### RF‑06 Progresso

- Barra geral (bytes) e do arquivo atual; arquivo X de Y; velocidade média móvel (10 s); tempo decorrido; ETA (mostrar "calculando…" nos primeiros 10 s e "indeterminado" durante recuperação de erros).
- Contadores ao vivo: arquivos íntegros / com erro; bytes irrecuperáveis.
- Progresso na barra de tarefas (`TaskbarItemInfo`), vermelho em erro.
- Ao terminar: som do sistema + resumo; ejetar automaticamente (configurável).

### RF‑07 Diagnóstico do leitor

Tela "Testar leitor" que roda e mostra cada verificação com ✔/✖/⚠ e explicação em linguagem simples:

1. Leitor detectado pelo Windows (modelo, firmware, barramento, status do driver).
2. Responde a comandos (abre o dispositivo, `CHECK_VERIFY`).
3. Abrir e fechar bandeja (leitores slim não fecham por comando — tratar como "não suportado", não como falha).
4. Com disco: reconhece a mídia; tipo e estado.
5. Teste de leitura por amostragem: 20 pontos distribuídos pelo disco (início, meio, fim), 1 MiB cada; reporta sucesso, velocidade média e mínima.
6. Teste de superfície completo (opcional, demorado): lê o disco inteiro e mostra mapa de blocos bons/lentos/ruins.

Dicas contextuais quando falhar — específicas para leitor USB slim: "use uma porta USB direto no computador, não em hub", "use cabo em Y/duas portas se o leitor não girar", "limpe o disco do centro para a borda", "teste com outro disco para separar defeito do leitor de defeito do disco". Botão "Copiar relatório" (texto).

### RF‑08 Relatório e catálogo

- Em cada pasta de disco: `mediarescue-manifest.json` (identidade do disco, leitor, datas, lista de arquivos com tamanho, SHA‑256, status, intervalos perdidos, versão do app) e, se houve problema, `LEIA-ME - ERROS.txt`.
- Catálogo global em `%LOCALAPPDATA%\MediaRescue\catalog.json`: um registro por disco extraído. Escrita atômica (arquivo temporário + `File.Replace`), com backup `.bak`; catálogo corrompido → restaurar do `.bak` ou recomeçar vazio, avisando, sem travar.
- **Impressão digital do disco:** SHA‑256 de (nº de série do volume + rótulo + tamanho total + lista ordenada de `caminho|tamanho|data`). Ao inserir um disco já catalogado: "Este disco já foi extraído em DD/MM/AAAA para <pasta>" com opções Abrir pasta / Extrair de novo / Ejetar. Se a pasta registrada não existir mais, dizer isso.
- Tela "Histórico": lista pesquisável com nome, data, tamanho, status, destino; abrir pasta; exportar CSV.

### RF‑09 Modo lote

Ativado por uma chave "Modo lote". Ciclo: aguarda disco → analisa → pede só o nome (com sugestão; Enter confirma; opção "não perguntar, usar sugestão") → extrai → ejeta → "Insira o próximo disco". Nunca inicia sozinho se houver bloqueio (espaço, disco repetido, protegido): pausa e pergunta. Painel lateral com a fila processada na sessão e o status de cada disco. Resumo da sessão ao sair do modo.

### RF‑10 Retomada

Um arquivo de estado do trabalho (`.mediarescue-job.json` na pasta do disco) é atualizado a cada arquivo concluído. Se o app fechar, travar ou a energia cair, ao reinserir o mesmo disco (mesma impressão digital) com o mesmo destino: oferecer "Continuar de onde parou" — arquivos concluídos e verificados não são recopiados; `.partial` é descartado e refeito.

### RF‑11 Conversão para MP4 (marco M6, opcional por disco)

- Sempre a partir da **cópia no destino**, nunca do disco. Originais são mantidos por padrão.
- Saída em `<pasta do disco>\MP4\`. H.264 (`libx264` se o build permitir; senão o encoder disponível — registrar em DESVIOS), CRF ~20, AAC 160 kb/s, `yadif` quando entrelaçado, `+faststart`, preservando proporção (DAR) de DVD/VCD.
- DVD‑Video: concatenar os VOBs de cada título (`VTS_nn_1..n`), sem o menu. VCD/SVCD/DVD‑VR: um MP4 por arquivo.
- Checar espaço antes; progresso por arquivo; cancelável; falha de conversão nunca afeta os originais; validar a saída com ffprobe (duração dentro de ±2 s ou ±2% da origem).

### RF‑12 Configurações

Persistidas em `%LOCALAPPDATA%\MediaRescue\settings.json`: último destino, ejetar ao concluir, tentativas por setor, timeout por bloco, orçamento da passada 2, reduzir velocidade em erro, verificação pós‑cópia (ligada por padrão; desligar exige confirmação), converter para MP4, tema claro/escuro (seguir o sistema). Arquivo inválido → usar padrões.

---

## 5. Interface

Janela única, mínimo 1100×700, redimensionável, DPI‑aware (PerMonitorV2), navegável por teclado, textos com contraste AA.

```
┌───────────────────────────────────────────────────────────────────────────┐
│ [Extrair] [Histórico] [Testar leitor] [Configurações]        Modo lote ◯  │
├──────────────────────┬────────────────────────────────────────────────────┤
│ LEITOR               │ CONTEÚDO DO DISCO                                  │
│ hp CDDVDW TS-L633M   │ ☑ Nome            Tipo   Duração  Resolução  Tam.  │
│ D:  ● Disco pronto   │ ☑ VIDEO_TS (Título 1)  DVD   00:42:10 720x480 1,9G │
│ [Ejetar] [Atualizar] │ ☑ Fotos\…                                          │
│                      │                                                    │
│ DISCO                │                                                    │
│ Tipo: DVD-R          ├────────────────────────────────────────────────────┤
│ Formato: DVD-Video   │ DESTINO  [E:\Vídeos da família        ] [Procurar] │
│ Rótulo: FORMATURA    │ Pendrive KINGSTON (exFAT) — 21,3 GB livres ✔       │
│ Gravado: 12/12/2009  │ Nome da pasta: [Formatura 2009              ]      │
│ Usado: 2,1 / 4,7 GB  │                                [ EXTRAIR DISCO ]   │
│ Sessões: 1           ├────────────────────────────────────────────────────┤
│ Estado: Finalizado   │ PROGRESSO  ████████░░░░ 64%  3/7 arquivos  4,1 MB/s│
│                      │ Restante ~06:12   Íntegros 3 · Com erro 0          │
├──────────────────────┴────────────────────────────────────────────────────┤
│ Registro: 14:02:11 Disco detectado em D: …                    [Abrir log] │
└───────────────────────────────────────────────────────────────────────────┘
```

Regras de UX:

- Nenhuma operação de disco na thread da UI. A janela DEVE continuar respondendo (< 100 ms) mesmo com o leitor travado em setor ruim.
- Estado vazio sempre explicado ("Insira um disco no leitor D:"), nunca uma tela em branco.
- Mensagens de erro em linguagem simples com **o que aconteceu + o que fazer**; detalhe técnico (código Win32) só em "Detalhes".
- Botão principal desabilitado sempre mostra o motivo ao lado.
- Fechar a janela durante cópia pede confirmação.
- Instância única (mutex nomeado); segunda execução traz a janela existente para frente.
- Exceção não tratada: registrar no log, mostrar diálogo amigável, liberar trava de ejeção e estado de energia.

---

## 6. Requisitos não funcionais

- **Segurança dos dados:** o app nunca apaga nem sobrescreve arquivo do usuário fora da pasta de trabalho do disco atual; só remove `.partial` e arquivos que ele mesmo criou no trabalho corrente.
- **Memória:** < 300 MB em uso normal; cópia em streaming (nunca carregar arquivo inteiro).
- **Desempenho:** em disco saudável, vazão ≥ 90% da cópia do Explorer no mesmo hardware (com verificação desligada, para comparação justa).
- **Inicialização:** janela utilizável em < 3 s.
- **Sem rede**, sem admin, sem instalador obrigatório (portátil; configurações em `%LOCALAPPDATA%`).
- **Log:** sem conteúdo de arquivos; caminhos são aceitáveis. Rotação: 10 arquivos × 5 MB.

---

## 7. Modelo de dados (resumo)

```
DriveInfo     { Letter, Vendor, Model, Firmware, BusType, IsVirtual, State }
DiscInfo      { Fingerprint, VolumeLabel, VolumeSerial, PhysicalType, MediaState, Sessions,
                Tracks, FileSystem, UsedBytes, CapacityBytes, RecordedAt, LogicalFormat, Entries[] }
DiscEntry     { RelativePath, Size, Modified, Kind(Video|Image|Audio|Other), Probe?, TitleGroup? }
VideoProbe    { Duration, Width, Height, Fps, VideoCodec, AudioCodec, Bitrate, Interlaced }
ExtractionJob { Id, Disc, DestinationRoot, FolderName, SelectedEntries[], Options, State, StartedAt, FinishedAt }
FileResult    { RelativePath, Status, BytesTotal, BytesLost, BadRanges[{Offset,Length}], Sha256, Attempts, Duration }
CatalogEntry  { Fingerprint, Name, ExtractedAt, DestinationPath, Totals, OverallStatus }
```

Todo JSON gravado leva `schemaVersion`.

---

## 8. Casos de borda (todos DEVEM ter comportamento definido e teste)

| ID | Situação | Comportamento esperado |
|---|---|---|
| E‑01 | Nenhum leitor conectado ao abrir | Tela explica; detecta sozinho quando conectar |
| E‑02 | Vários leitores (incl. ISO montada) | Seletor de leitor; cada um com seu estado |
| E‑03 | Leitor USB desconectado durante a cópia | Pausa, mensagem "Leitor desconectado — reconecte"; ao voltar com o mesmo disco (impressão digital), retoma do arquivo atual; `.partial` refeito |
| E‑04 | Disco ejetado/trocado durante a cópia | Pausa; disco diferente → não continua, oferece cancelar ou reinserir o original |
| E‑05 | Leitor sem energia suficiente (não gira, some e volta) | Após 3 reconexões em 2 min, mostrar dica de alimentação USB |
| E‑06 | Disco vazio | "Disco vazio", extrair desabilitado |
| E‑07 | Disco não finalizado / sessão aberta sem FS legível | Explicar: "gravado mas não finalizado; finalize no aparelho onde foi gravado"; sem tentativa de cópia bruta |
| E‑08 | CD de áudio ou modo misto | Áudio: não suportado; misto: copiar só a parte de dados e avisar |
| E‑09 | DVD com proteção | Recusar: "disco comercial protegido, não suportado". Se a detecção por IOCTL não for possível, não assumir proteção; erros de leitura seguem o fluxo normal |
| E‑10 | Setores ruins isolados | Fluxo de duas passadas; status `Recuperado` ou `Parcial` |
| E‑11 | Disco muito degradado (>50% ilegível) | Continua dentro do orçamento; oferece parar; nunca loop infinito |
| E‑12 | Leitura trava sem retornar erro | Timeout + `CancelIoEx`; UI responsiva; Cancelar funciona em ≤ 5 s do ponto de vista do usuário (o handle pode terminar em segundo plano) |
| E‑13 | Sistema de arquivos do disco corrompido (diretório ilegível) | Listar o que der; pastas ilegíveis aparecem com erro; o resto é copiável |
| E‑14 | Destino sem espaço antes de começar | Bloquear com quanto falta |
| E‑15 | Destino enche durante a cópia (outro programa ocupou) | Pausar, remover `.partial` atual, pedir para liberar espaço ou trocar destino, continuar |
| E‑16 | Pendrive removido durante a cópia | Pausar; ao reconectar (mesmo volume), validar arquivos já concluídos por tamanho e continuar; senão, cancelar preservando o estado |
| E‑17 | Destino FAT32 com arquivo ≥ 4 GB | Bloqueio antecipado só desses arquivos, com explicação |
| E‑18 | Destino somente leitura / sem permissão / protegido contra gravação | Detectado na validação (teste de escrita real) |
| E‑19 | Erro de gravação ou verificação divergente | 1 nova tentativa; depois `FalhaDestino` e alerta de mídia de destino defeituosa |
| E‑20 | Nomes inválidos, reservados, muito longos, Unicode, só diferença de maiúsculas | Sanitização determinística; mapa original→final no manifesto; sem colisão |
| E‑21 | Dois discos com o mesmo rótulo | Pastas distintas (`Nome (2)`); impressões digitais diferentes |
| E‑22 | Mesmo disco inserido de novo | Aviso de já extraído (RF‑08) |
| E‑23 | Disco multissessão | Usar a última sessão (o que o Windows monta); mostrar nº de sessões e avisar que sessões antigas podem conter arquivos não visíveis |
| E‑24 | Arquivo de 0 byte, disco com milhares de arquivos pequenos, pasta vazia | Copia corretamente; UI virtualizada continua fluida; pastas vazias recriadas |
| E‑25 | Antivírus/Explorer segurando arquivo do destino | Repetir a renomeação com espera progressiva (até 10 s) antes de falhar |
| E‑26 | Computador tenta suspender / usuário fecha a tampa | Suspensão por inatividade bloqueada; se suspender mesmo assim, ao voltar tratar como E‑03 |
| E‑27 | App fechado à força / queda de energia | Retomada (RF‑10); nenhum arquivo incompleto com nome final |
| E‑28 | ffprobe ausente, travado ou com saída inválida | Sem metadados para aquele arquivo; timeout; cópia não afetada |
| E‑29 | Reprodução automática do Windows abre outro programa que usa o disco | Abrir com compartilhamento de leitura; se o disco estiver em uso exclusivo, informar qual é o problema |
| E‑30 | Catálogo/configurações corrompidos | Recuperar do backup ou padrões, com aviso |
| E‑31 | Relógio do disco absurdo (1980, 2099) ou sem data | Não usar na sugestão de nome; cair para a data de hoje |
| E‑32 | Disco de 8 cm de filmadora, DVD‑RAM, DVD±RW | Tratados como os demais se o Windows montar; tipo físico mostrado |
| E‑33 | Sem permissão para abrir `\\.\D:` (IOCTLs negados) | Degradar: detalhes físicos "—", cópia por sistema de arquivos continua funcionando |
| E‑34 | Segunda instância aberta | Foca a primeira |

---

## 9. Plano de testes e validação

O agente não tem como trocar discos físicos. Por isso a validação tem três camadas, e o relatório final DEVE dizer explicitamente o que foi provado em cada uma.

### 9.1 Unitários (`Core.Tests`) — sem hardware

Com `FakeDiscReader` e `FaultInjectingStream` (falha por intervalo de offsets, falha N vezes e depois funciona, atraso, travamento até cancelar, "dispositivo sumiu" no byte X) e `FakeDestination` (disco cheio no byte X, erro de escrita, corrupção silenciosa de bytes, remoção):

- Motor de cópia: arquivo íntegro; erro transitório → `Recuperado` e hash igual ao original; erro permanente → `Parcial` com intervalos exatos, tamanho preservado, zeros só nos intervalos perdidos; salto progressivo; orçamento de tempo respeitado (com `IClock` falso); timeout; cancelar; pausar/continuar; pular arquivo.
- Verificação: corrupção no destino é detectada; `.partial` nunca sobra com nome final.
- Nomes: tabela de casos de E‑20/E‑21/E‑31 (testes parametrizados).
- Classificação de formato lógico: árvores sintéticas para cada linha da tabela 2.3.
- Agrupamento de títulos de DVD.
- Impressão digital: estável para o mesmo disco, diferente para discos diferentes.
- Catálogo/configurações: escrita atômica, recuperação de corrupção, migração de `schemaVersion`.
- Retomada: estado salvo no meio → retoma sem recopiar concluídos.
- Validação de destino: FAT32/4 GB, espaço, somente leitura, destino = origem.
- ETA/velocidade: sem divisão por zero, sem valores negativos.

Meta: cobertura de linhas ≥ 85% em `Core`; 100% dos IDs E‑xx aplicáveis ao `Core` com pelo menos um teste nomeado com o ID (ex.: `E15_DestinationFillsUp_PausesAndCleansPartial`).

### 9.2 Integração — ISOs montadas como leitor virtual

`build/make-test-media.ps1` gera vídeos sintéticos com ffmpeg (`testsrc` + `sine`, 5–10 s; `-target ntsc-dvd`, `-target ntsc-vcd`, AVI, WMV, MP4) e monta ISOs com a biblioteca de ISO: dados simples; estrutura VCD; estrutura `VIDEO_TS`; `DVD_RTAV`; misto com nomes Unicode/longos; milhares de arquivos pequenos; disco "vazio" (ISO sem arquivos); dois discos com mesmo rótulo.

Os testes montam com `Mount-DiskImage` (aparece como unidade de CD — `DriveType = CDRom`), executam detecção → análise → extração para pasta temporária → comparam hash arquivo a arquivo → desmontam **durante** uma cópia para exercitar E‑04. Desmontar sempre no `finally`. Se a montagem não for permitida no ambiente, marcar como *skipped* com motivo — nunca como aprovado.

Destinos de teste: pasta temporária NTFS; VHD pequeno FAT32/exFAT se possível sem admin (senão, simular via `IDestinationFileSystem` e registrar).

Atenção ao espaço: mídias de teste somadas ≤ 300 MB; limpar ao final.

### 9.3 Interface

- Testes de ViewModel (sem janela) para todos os estados de RF‑01 e habilitação do botão Extrair.
- Teste de fumaça automatizado: o executável publicado abre, mostra a janela principal e fecha com código 0 (com e sem `tools\ffprobe.exe`).
- Capturas de tela dos estados principais anexadas ao relatório (sem leitor, sem disco, disco pronto, copiando, erro parcial, diagnóstico, histórico).

### 9.4 Hardware real — roteiro para o usuário

O agente PODE rodar sozinho, no leitor `D:` real, o que não exige trocar discos (detecção do leitor, estado "sem disco", diagnóstico itens 1–3). O restante vai em `docs/TESTE-MANUAL.md`, como checklist passo a passo com resultado esperado, para o usuário executar:

1. CD‑R de dados saudável → extrai, tudo `Íntegro`, vídeo abre no destino.
2. DVD‑Video caseiro → títulos agrupados, duração correta.
3. VCD, se houver.
4. Disco riscado → termina sem travar; relatório lista perdas.
5. Puxar o cabo USB do leitor no meio da cópia → pausa e retoma.
6. Remover o pendrive no meio → pausa e retoma/cancela limpo.
7. Pendrive FAT32 quase cheio → bloqueio antecipado.
8. Reinserir disco já extraído → aviso de duplicado.
9. Modo lote com 3 discos seguidos.
10. Diagnóstico completo com e sem disco.

### 9.5 Critérios de aceite (definição de pronto)

- [ ] `dotnet build` sem avisos novos; `dotnet test` 100% verde (skips justificados por escrito).
- [ ] `MediaRescue.exe` único, autocontido, abre em máquina sem .NET instalado, sem pedir elevação.
- [ ] Todos os RF‑01…RF‑10 implementados; RF‑11 implementado ou explicitamente adiado em `DESVIOS.md`.
- [ ] Cada E‑01…E‑34 mapeado em uma tabela de rastreabilidade: teste automatizado, item do roteiro manual, ou "não verificável + motivo".
- [ ] Nenhuma chamada de rede no app (verificar por busca no código: `HttpClient`, `WebRequest`, sockets).
- [ ] Nenhum código de quebra de proteção de disco.
- [ ] `README.md` (como usar, em português, para leigo) e `docs/TESTE-MANUAL.md` entregues.
- [ ] Relatório final honesto: o que foi testado automaticamente, o que foi testado no leitor real, o que depende do roteiro manual, e limitações conhecidas.

---

## 10. Ordem de implementação

| Marco | Entrega | Critério de saída |
|---|---|---|
| M0 | Toolchain, solução, CI local (`build`/`test`), esqueleto WPF | App abre janela vazia; teste de fumaça passa |
| M1 | `Core`: modelos, motor de cópia tolerante a erros, verificação, nomes | Unitários de 9.1 (motor/nomes) verdes |
| M2 | `Windows`: detecção de leitor/mídia, IOCTLs, IMAPI2, leitura overlapped | Leitor real `D:` detectado; ISO montada analisada |
| M3 | UI principal: detalhes, destino, extração, progresso | Extração ponta a ponta de ISO montada com hashes iguais |
| M4 | Catálogo, duplicados, retomada, relatório, histórico | Testes de RF‑08/RF‑10 verdes |
| M5 | Diagnóstico, modo lote, configurações, polimento de mensagens | Todos os E‑xx rastreados |
| M6 | ffprobe/miniaturas completas e conversão MP4 | Conversão validada com mídias sintéticas |
| M7 | Publicação, README, roteiro manual, relatório final | Checklist 9.5 completo |

Fazer commit ao fim de cada marco (instalar git se o usuário autorizar; senão, manter um `CHANGELOG.md`).

---

## 11. Decisões em aberto (padrão adotado se o usuário não opinar)

1. **Nome do app:** `MediaRescue` é codinome; título da janela "Extrator de Mídias".
2. **Conversão MP4:** incluída como opcional (M6), desligada por padrão.
3. **Imagem ISO do disco inteiro** como alternativa à cópia de arquivos: fora da v1.
4. **Envio para nuvem:** fora da v1; o usuário aponta o destino para a pasta que quiser e sincroniza por conta própria.
