# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/); versões seguem [SemVer](https://semver.org/lang/pt-BR/).
A versão publicada vem de `build\VERSION` (ou da *tag* `v*`).

## [1.0.1] - 2026-10-13

### Corrigido

* Fechar a janela sem nenhuma cópia em andamento mostrava a mensagem de erro inesperado em vez de simplesmente fechar.

### Alterado

* A mensagem "Sem disco" agora orienta o que fazer quando há um disco na bandeja mas o leitor não o detecta.

## [1.0.0] - 2026-10-12

Primeira versão. Aplicativo portátil para Windows 10/11 (64 bits), autocontido, sem instalador e sem privilégio de administrador.

### Adicionado

* **Detecção de leitores e mídia** (RF-01): leitores internos, USB e unidades de ISO montada; reage a conectar/desconectar leitor e inserir/remover disco (`WM_DEVICECHANGE` + verificação a cada 2 s); estados Nenhum leitor, Sem disco, Bandeja aberta, Lendo disco…, Disco pronto, Disco vazio, Disco não finalizado, Disco ilegível, Protegido e CD de áudio.
* **Detalhes da mídia e do conteúdo** (RF-02): leitor, tipo/estado do disco, sessões, sistema de arquivos, rótulo, série, data de gravação, formato lógico (dados, VCD, SVCD, DVD-Video, DVD-VR, AVCHD, misto); árvore de arquivos com seleção; duração, resolução e codecs via `ffprobe` em segundo plano; títulos de DVD agrupados.
* **Escolha e validação do destino** (RF-03): espaço (bytes + 5% + 50 MB), teste real de escrita, bloqueio de FAT32 com arquivo ≥ 4 GB só para esse arquivo, avisos de nuvem/rede e de pouco espaço no `C:`, lembrança do último destino.
* **Nomes e organização** (RF-04): uma pasta por disco, sugestão de nome (rótulo ou data de gravação), sanitização determinística, `Nome (2)` em colisões, caminhos longos.
* **Motor de cópia tolerante a erros** (RF-05): gravação em `.partial`, SHA-256 e verificação por releitura do destino, leitura *overlapped* com timeout e cancelamento, duas passadas com salto progressivo e orçamento de tempo, pausar/continuar/cancelar/pular/insistir mais, bloqueio de suspensão e de ejeção durante a cópia.
* **Progresso** (RF-06): barras geral e por arquivo, velocidade média móvel, ETA, contadores, progresso na barra de tarefas, som e resumo ao final, ejeção automática configurável.
* **Testar leitor** (RF-07): seis verificações com explicações e dicas para leitores USB finos, amostragem em 20 pontos, teste de superfície opcional, **Copiar relatório**.
* **Relatório e catálogo** (RF-08): `mediarescue-manifest.json` e `LEIA-ME - ERROS.txt` em cada pasta de disco; catálogo global com escrita atômica e `.bak`; impressão digital do disco e aviso de disco repetido; tela **Histórico** com busca e exportação CSV.
* **Modo lote** (RF-09) com fila lateral e resumo da sessão.
* **Retomada** (RF-10) de cópias interrompidas ("Continuar de onde parou").
* **Conversão opcional para MP4** (RF-11), desligada por padrão, sempre a partir da cópia no destino; usa o melhor codificador H.264 disponível no FFmpeg incluído (ver `docs\DESVIOS.md`).
* **Configurações** (RF-12) persistidas em `%LOCALAPPDATA%\MediaRescue\settings.json`, incluindo tema claro/escuro/automático.
* Instância única, tratamento de exceções não tratadas com liberação de trava de ejeção e do estado de energia, registro (Serilog) em `%LOCALAPPDATA%\MediaRescue\logs\` com rotação de 10 × 5 MB.
* Modo de demonstração por linha de comando (`--demo`, `--demo-state=…`, `--theme=…`, `--smoke`) usado nas capturas de tela e no teste de fumaça.
* **Empacotamento e automação**: `build\publish.ps1` (publica, renomeia para `MediaRescue.exe`, monta a pasta com `tools\`, `LEIA-ME.txt`, avisos de terceiros e licença, gera `.zip` e `.sha256`), `build\take-screenshots.ps1`, workflows de GitHub Actions `ci.yml` (build + testes unitários) e `release.yml` (testes, publicação e *Release* ao enviar uma *tag* `v*`).
* **Documentação**: `README.md` (usuário leigo e desenvolvedor), `docs\TESTE-MANUAL.md` (roteiro em hardware real e solução de problemas de disco não reconhecido), `docs\RASTREABILIDADE.md` (E-01…E-34 e RF-01…RF-12 → testes) e `docs\DESVIOS.md`.
* `LICENSE` (MIT) e `THIRD-PARTY-NOTICES.md` (FFmpeg LGPL do BtbN, bibliotecas .NET).

### Ajustes finais antes do lançamento

* **Ícone do aplicativo** (`src\MediaRescue.App\Assets\app.ico`): aparece na barra de título das três janelas, na barra de tarefas e no próprio `MediaRescue.exe` (`docs\icon-256.png` é a prévia; `build\make-icon.ps1` gera os dois).
* **Testar leitor**: o teste da bandeja agora é opcional (caixa **Testar também a bandeja**, desligada por padrão), porque abrir a bandeja ejeta o disco que estiver no leitor.
* **Disco misto (dados + áudio)**: aparece um aviso de que só a parte de dados será copiada.
* **Mover/renomear nunca sobrescreve**: se aparecer no destino um arquivo que não é nosso com o nome final, ele é preservado e a cópia ganha o próximo nome livre (`arquivo (2).avi`).
* **Datas dos arquivos**: a data do disco é aplicada à data de modificação **e** de criação no destino.
* **Cancelar e apagar o que foi copiado** agora também remove as pastas vazias que a cópia criou (inclusive a pasta do disco), em vez de deixar uma pasta vazia para trás. Pastas que ainda tenham qualquer arquivo são mantidas. Novo método `IDestinationFileSystem.DeleteDirectoryIfEmpty`.
* **Polimento visual** (conferido por capturas de tela nos temas claro e escuro, no tamanho padrão e no mínimo de 1100×700):
  * a lista de arquivos tem uma linha sob o cabeçalho, e uma linha parcialmente rolada deixou de invadir os títulos das colunas; os títulos das colunas ficam alinhados com os valores;
  * "Título DVD" e a duração não ficam mais colados (coluna **Tipo** mais larga);
  * o campo **Nome da pasta** tem largura fixa e uma dica quando está vazio (antes ficava muito estreito sem disco);
  * no **Histórico**, os botões **Abrir pasta** têm espaço entre si;
  * o **Registro** do rodapé mostra exatamente três linhas inteiras (antes a primeira aparecia cortada);
  * o progresso mostra duas barras com rótulos: **Total** e **Arquivo atual**;
  * durante a conversão para MP4 o destino fica numa linha só (como na cópia), e a coluna direita rola em vez de cortar o cartão **Última extração** quando a janela é pequena; o texto do resultado da conversão e dos problemas ganhou uma área rolável;
  * os botões **Ejetar** e **Atualizar** dizem "Indisponível durante a cópia." quando estão desativados;
  * a página **Testar leitor** passou a alinhar à esquerda, como Configurações e Histórico.
* **Versão**: o `.csproj` do app agora tem `<Version>1.0.0</Version>`, igual a `build\VERSION`.
* **Modo de demonstração**: novos estados `--demo-state=converting` e `--demo-state=converted` (conversão para MP4 em andamento e concluída); `build\take-screenshots.ps1` ganhou `-Only`, `-Themes`, `-Size` e `-FileTag` e agora gera todas as telas nos dois temas.
* `.gitignore`: `docs\screenshots` e `Assets\app.ico` são versionados explicitamente.

### Limitações conhecidas

* A extração em **leitor físico real ainda não foi executada** (o leitor de desenvolvimento reportou "sem mídia"); a validação foi feita com leitores simulados e ISOs montadas. Use `docs\TESTE-MANUAL.md`.
* Sem miniaturas de vídeo; mapa do teste de superfície apenas em texto; versão do UDF não exibida.
* Executável sem assinatura digital (SmartScreen mostra aviso na primeira execução).
* Veja `docs\DESVIOS.md` para a lista completa.
