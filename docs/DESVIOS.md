# Desvios da especificação e limitações conhecidas

Registro (conforme a seção 1 da [SPEC.md](../SPEC.md)) de tudo o que **diverge**, **não foi implementado** ou **ainda não foi verificado**, com o motivo. Cada item diz se foi **verificado no código/testes** ou apenas **declarado** pelos autores da implementação.

Legenda de impacto: **Baixo** = não afeta a segurança dos dados · **Médio** = comportamento diferente do especificado, sem perda de dados · **Alto** = limita o uso real.

---

## 1. Verificação em hardware real

| # | Item | Impacto | Detalhe |
|---|---|---|---|
| 1.1 | **Extração em leitor físico real não foi executada** | **Alto** | Na máquina de desenvolvimento o leitor `hp CDDVDW TS-L633M` (`D:`, USB) **informou "sem mídia" mesmo com disco inserido**; portanto nenhum passo do roteiro [TESTE-MANUAL.md](TESTE-MANUAL.md) foi executado. Tudo o que foi provado vem de leitores simulados (fakes com injeção de falhas), ISOs montadas e do próprio Windows. A causa provável é física (energia USB / lente / disco), não do app (veja o guia "Disco não reconhecido"), mas **isso ainda não foi confirmado**. |
| 1.2 | Detecção de disco **vazio**, **não finalizado**, **de áudio** e **protegido**, além de **ejetar** e **fechar bandeja**, estão codificadas mas **nunca foram exercitadas em mídia real** | Médio | Só testadas com fakes e unidade virtual. Em especial, a leitura de `CopyrightProtectionType` e de sessões abertas via IMAPI2/IOCTL depende do firmware do leitor. Quando a detecção não é possível o app **não assume proteção** (como pede E-09); erros de leitura seguem o fluxo normal. |
| 1.3 | ISO montada informa **dados físicos genéricos** | Baixo | Unidades virtuais não respondem a IMAPI2/IOCTL como um leitor real; os testes de integração só provam que a ausência desses dados degrada para "—" (E-33). |
| 1.4 | **E-32** (8 cm, DVD-RAM, DVD±RW) **não verificável** | Baixo | Exigiria a mídia física. Não há tratamento especial. Ver [RASTREABILIDADE.md](RASTREABILIDADE.md). |
| 1.5 | **Vazão ≥ 90% da cópia do Explorer não foi medida** | Baixo | Sem disco legível não há como comparar com o Explorador de Arquivos. |

## 2. Comportamento diferente do especificado

| # | Item | Impacto | Detalhe |
|---|---|---|---|
| 2.1 | **Datas dos arquivos** | Baixo | A spec (RF-05, item 3) pede preservar datas de **criação e modificação**. O disco só informa **uma** data por arquivo (a de modificação gravada no ISO9660/UDF). O app a aplica como data de **modificação** e, na prática, também como **criação** no destino (`WindowsDestinationFileSystem.SetTimes`). A data de criação original, se fosse diferente, não existe como dado separado. *(Verificado no código — `File.SetCreationTimeUtc` e `File.SetLastWriteTimeUtc` com o mesmo valor — e nos testes de ponta a ponta, que conferem a data de criação de cada arquivo copiado.)* |
| 2.2 | **Arquivo totalmente ilegível não é gravado** (status *Falhou*) | Médio | A spec diz que o arquivo de saída "mantém sempre o tamanho original (zeros nos trechos irrecuperáveis)". Quando **nada** pôde ser lido, o app não cria um arquivo de zeros: nada fica no destino e o item aparece como *Falhou* no relatório. Evita arquivos "fantasma" que parecem válidos. Teste: `E10_FullyUnreadableFile_Fails_AndLeavesNothingBehind`. |
| 2.3 | **Disco diferente precisa ser visto duas vezes** antes de abortar o trabalho (E-04) | Baixo | Para não abortar por uma listagem momentânea diferente durante o *spin-up* do leitor, o motor só considera "outro disco" na segunda observação seguida. Teste: `E04_ADiscStillSpinningUp_WithAMomentaryDifferentListing_DoesNotAbortTheJob`. Efeito: alguns segundos a mais para avisar. |
| 2.4 | **"Recuperado" também cobre erros resolvidos pela nova tentativa imediata** | Baixo | A spec define *Recuperado* como "teve erro, mas tudo foi lido na **passada 2**". Na prática o bloco que falha é relido uma vez na hora (parte da passada 1); se essa nova tentativa funciona o arquivo também recebe *Recuperado*. Teste: `E10_TransientError_ClearsOnTheImmediateRetry_AndIsReportedAsRecovered`. |
| 2.5 | **Arquivo de estado do trabalho é gravado com limite de frequência em trabalhos com mais de 200 itens** (RF-10) | Médio | A spec pede atualizar a cada arquivo concluído. Com milhares de arquivos pequenos isso seria quadrático, então, acima de 200 itens, o estado é gravado no máximo a cada 2 s (sempre ao cancelar/terminar). Se o app morrer, os últimos arquivos concluídos nesses ≤ 2 s não constam do estado e serão **copiados de novo** ao retomar; como o motor nunca sobrescreve arquivo existente, a segunda cópia pode ficar com sufixo numérico (ex.: `arquivo (2).avi`) ao lado da primeira — **garantido por `ExistingForeignFile_IsNeverOverwritten_TheCopyGetsASuffixInstead` e `AForeignFileAppearingAtTheFinalName_IsNeverOverwritten_TheCopyGetsTheNextFreeName` (o `Move` do destino não sobrescreve: `Move_ToAnExistingFile_Throws_AndKeepsBothFiles`), mas não testado exatamente nesse cenário de retomada**. Nada se perde nem se corrompe. Teste do limite: `BigJobs_DoNotRewriteTheStateAfterEverySmallFile_ButCancelStillSavesEverything`. |
| 2.6 | **Conversão MP4 sem `libx264`** (RF-11) | Médio | O FFmpeg incluído é a variante **LGPL** (sem libx264/x265, de propósito, por licença). A spec previa "`libx264` se o build permitir; senão o encoder disponível — registrar em DESVIOS". O app detecta os codificadores em tempo de execução e prefere, nesta ordem: `libx264` (CRF 20), `h264_mf` (Media Foundation do Windows), `libopenh264` e, por último, `mpeg4` (MPEG-4 parte 2). Os H.264 sem CRF usam **taxa de bits alvo** proporcional à resolução (não CRF ~20); o resumo da extração informa qual codificador foi usado. Testes: `Candidates_OfTheLgplBuild_AreMediaFoundationOpenH264AndMpeg4_InThatOrder`, `EncodersWithoutCrf_AimAtABitrate`. Cada candidato é validado com uma codificação de teste de um clipe curto (`EncoderTestCommand_EncodesAShortSyntheticClipWithTheSameOptions`), e só os que funcionam são oferecidos. |
| 2.7 | **Modo lote não converte para MP4** | Baixo | Decisão de implementação (teste `BatchMode_DoesNotConvert`): no modo lote só se copia; a conversão é feita no modo normal. |
| 2.8 | **Teste da bandeja do "Testar leitor" é opcional** (RF-07) | Baixo | Abrir a bandeja **ejeta o disco** que estiver no leitor; por isso a verificação 3 vem desligada e só roda quando o usuário marca **Testar também a bandeja**. Sem a marca ela aparece como *Pulado*, com o motivo. Testes: `TrayTest_IsOffByDefault_AndOnlyRunsWhenTheUserTicksTheBox`, `Diagnostics_DoesNotEjectTheDisc_UnlessTheTrayTestWasRequested`. |

## 3. Não implementado / implementação parcial

| # | Item | Impacto | Detalhe |
|---|---|---|---|
| 3.1 | **Miniaturas de vídeo** (RF-02, marcado PODE) | Baixo | A camada de sondagem sabe gerar o quadro (`IMediaProbe.ThumbnailAsync`, teste `WithTools_ThumbnailIsAPng`), mas **a interface não as exibe** (nem o modo demonstração). |
| 3.2 | **Mapa de blocos do teste de superfície só em texto** (RF-07 item 6) | Baixo | O resultado diz quantos blocos estão bons/lentos/ruins e onde se concentram, mas não há mapa gráfico. |
| 3.3 | **Versão do UDF não é exibida** (RF-02) | Baixo | O sistema de arquivos vem do Windows como "UDF"/"CDFS" (o modo demonstração mostra "UDF 1.02" como exemplo). A versão exata do UDF não é lida. |
| 3.4 | Imagem ISO do disco inteiro, envio para nuvem | — | Fora da v1 por decisão da spec (seção 11). |
| 3.5 | **Cobertura de linhas ≥ 85% em `Core` não foi medida** nesta etapa (empacotamento) | Baixo | Os projetos de teste incluem `coverlet.collector`, mas o relatório de cobertura não foi gerado aqui. |
| 3.6 | Teste de VHD FAT32/exFAT real (seção 9.2) | Baixo | Criar/montar VHD exige privilégio de administrador. FAT32/4 GB (E-17) é testado com um sistema de arquivos **simulado**; o roteiro manual (passo 7) cobre o pendrive real. |

## 4. Empacotamento e distribuição

| # | Item | Impacto | Detalhe |
|---|---|---|---|
| 4.1 | **Executável sem assinatura digital** | Médio | O Windows SmartScreen mostra o aviso azul na primeira execução ("Mais informações → Executar assim mesmo"). Documentado no README e no `LEIA-ME.txt`. Assinar exige certificado pago. |
| 4.2 | **Tamanho do `MediaRescue.exe`: ~140 MB** | Baixo | Arquivo único autocontido com .NET 8 + WPF, **sem compressão** (a spec fixa as opções de publicação; `EnableCompressionInSingleFile` reduziria o tamanho, mas aumentaria o tempo de abertura). O `.zip` final fica ~150 MB por causa do `ffmpeg.exe` e `ffprobe.exe` (~110 MB cada, descompactados). |
| 4.3 | **Extração das bibliotecas nativas na primeira execução** | Baixo | Com `IncludeNativeLibrariesForSelfExtract`, ~8 MB de DLLs nativas do WPF são extraídas para `%TEMP%\.net\MediaRescue\<hash>\` na primeira abertura. Não é "instalação" e não requer admin, mas o programa não é 100% "sem rastros" no disco. |
| 4.4 | **Versão** | Baixo | O `.csproj` do app tem `<Version>1.0.0</Version>`, o mesmo número de `build\VERSION`, para que compilações de desenvolvimento e publicadas informem a mesma versão. A publicação ainda passa a versão por linha de comando (`-p:Version`, a partir de `build\VERSION`, do argumento `-Version` ou da *tag* `v*`), então **ao mudar `build\VERSION` é preciso mudar também o `.csproj`** (não há verificação automática; recomenda-se, no futuro, centralizar a versão num único arquivo lido pelo MSBuild). |
| 4.5 | **Inicialização < 3 s** (seção 6) | Baixo | Medido com o executável publicado (sem .NET instalado): janela visível em ~1,2–1,7 s com o executável já extraído e ~2,6 s na primeira abertura após publicar; o teste de fumaça completo (abrir, carregar, sair) leva ~4,2–4,6 s. Meta atendida para "janela utilizável"; medição feita em modo `--demo` (sem leitura de hardware). |
| 4.6 | **Memória** (seção 6: < 300 MB em uso normal) | Médio | Medida com o executável publicado em modo `--demo` (leitor simulado, sem sondagem real): *working set* ~297 MB e memória privada ~225 MB com o disco de demonstração; ~317 MB / ~245 MB com `--demo-files=20000`. O *working set* inclui páginas compartilhadas do runtime/WPF; a memória privada está abaixo de 300 MB, mas o *working set* fica **no limite ou ligeiramente acima**. Não medido durante uma cópia real. |

## 5. Observações de interface (vistas nas capturas de tela, `docs\screenshots`)

Todas as telas foram revisadas em capturas nos temas **escuro e claro**, no tamanho padrão (1240×820) e no mínimo (1100×700, `MEDIARESCUE_DEMO_SIZE=1100x700`). Os seis defeitos visuais encontrados na revisão anterior (linha rolada invadindo o cabeçalho da lista, "Título DVD" colado na duração, campo **Nome da pasta** estreito, botões **Abrir pasta** colados no Histórico, primeira linha do registro cortada e dois percentuais sem rótulo) **foram corrigidos e conferidos nas capturas** (veja o `CHANGELOG.md`).

Comportamentos de interface que valem saber (não são defeitos):

1. **Janela pequena + resultado longo:** na tela **Extrair**, a coluna da direita (conteúdo, destino e progresso/resultado) **rola** em vez de cortar o cartão **Última extração** quando tudo não cabe na altura da janela; ao terminar uma cópia ou conversão a coluna rola sozinha até o resultado. Dentro do cartão, o texto da conversão para MP4 (primeiro) e a lista de problemas (depois) dividem uma área rolável própria de cerca de duas linhas, para o cartão caber na altura padrão da janela sem rolar; o texto completo está também no diálogo de resumo e no registro.
2. **Durante a conversão para MP4** o cartão **Destino** fica reduzido a uma linha ("Pasta do disco"), como na cópia.
3. As capturas `partial*` e `converted*` mostram o botão **Extrair disco** esmaecido: nesse instante o diálogo de resumo ainda está aberto e faz parte da própria operação "Extrair" (o comando só volta a ficar disponível quando o diálogo é fechado). Não é um botão desativado sem motivo.

Contraste: a razão de contraste (fórmula WCAG) foi **calculada a partir das cores dos temas** (`Themes\Dark.xaml` e `Light.xaml`): texto, texto secundário, destaque, ok, aviso e perigo sobre janela/cartão/cartão alternativo, e sobre os fundos suaves de cada tom, ficam todos **a partir de 4,5:1** (mínimo 5,0:1 no tema claro e 5,4:1 no escuro; o texto secundário tem 5,7:1 a 6,2:1 no claro e 7,4:1 a 8,3:1 no escuro). **Não foram medidos** os controles desativados (que usam opacidade reduzida de propósito) nem a cor efetivamente renderizada na tela.
## 6. Itens da spec cumpridos e verificados nesta etapa

* **Sem chamadas de rede**: busca no código-fonte por `HttpClient`, `WebRequest`, `WebClient`, `Socket`, `TcpClient`, `UdpClient`, `WebSocket` e `Dns.` não encontrou nenhuma ocorrência em `src\`.
* **Sem código de quebra de proteção**: não há libdvdcss nem equivalente; proteção apenas é detectada e recusada.
* **Executável autocontido, sem elevação**: `MediaRescue.exe --demo --smoke` sai com código 0 com todas as variáveis `DOTNET*` removidas do ambiente e sem .NET instalado na máquina (`C:\Program Files\dotnet` não existe); manifesto `asInvoker`; roda também sem a pasta `tools\`. Resultados completos no relatório de empacotamento.
