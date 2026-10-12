# Roteiro de teste manual (hardware real)

Este roteiro cobre o que **não dá para provar sem trocar discos de verdade** (seção 9.4 da especificação). Os testes automáticos já cobrem a lógica com discos simulados e ISOs montadas; aqui o objetivo é confirmar o comportamento com o **leitor `hp CDDVDW TS-L633M` (USB, `D:`)**, discos reais e pendrives reais.

> **Estado atual:** a extração em leitor físico real **ainda não foi executada** — na máquina de desenvolvimento o leitor informou "sem mídia" mesmo com disco inserido (veja [Disco não reconhecido](#disco-não-reconhecido--solução-de-problemas)). Faça o **Passo 0** antes de qualquer outro.

## Como usar este roteiro

* Marque `[x]` em cada item que passou. Se algo não bater com o "Resultado esperado", anote o que viu na coluna **Observações** (ou em um arquivo), **copie o relatório** de *Testar leitor* e guarde o arquivo de log mais recente (botão **Abrir log**, rodapé do app; pasta `%LOCALAPPDATA%\MediaRescue\logs`).
* Use cópias de discos que você possa perder, ou discos que você já tem copiados de outra forma, até confiar no resultado.
* Os resultados esperados usam os nomes de tela do app em português.

### Materiais

| Item | Para quê |
|---|---|
| 1 CD-R de dados saudável com vídeos (de preferência `.avi`/`.mpg`/`.wmv`) | Passo 1, 8, 9 |
| 1 DVD-Video caseiro (gravado em gravador de mesa ou programa de DVD) | Passo 2, 9 |
| 1 VCD (se existir) | Passo 3 |
| 1 disco riscado de verdade (ou com marca visível) | Passo 4 |
| Pendrive/HD externo **removível** com espaço livre (≥ 2 GB) | Passos 1–9 |
| Pendrive formatado em **FAT32**, quase cheio, ou com menos espaço que um disco | Passo 7 |
| 3 discos quaisquer (podem ser os mesmos dos passos 1–3) | Passo 9 |
| `MediaRescue.exe` da última versão (`dist\MediaRescue-<versão>-win-x64\`) | Todos |

---

## Passo 0 — O Windows enxerga o disco? (pré-requisito)

| | |
|---|---|
| **O que fazer** | Coloque um CD/DVD saudável (de preferência de uma gravadora, não gravado em casa) no leitor com o **lado da etiqueta para cima**. Espere 30–60 s. Abra o **Explorador de Arquivos → Este Computador**. |
| **Resultado esperado** | A unidade `D:` mostra o nome do disco e o espaço usado. **Se o Windows também mostra a unidade vazia/"Insira um disco", o problema não é do app**: siga o guia de [Disco não reconhecido](#disco-não-reconhecido--solução-de-problemas) antes de continuar. |
| **Resultado no app** | Abra o `MediaRescue.exe`. Em até ~1 min a caixa Leitor mostra **Disco pronto**, com tipo, rótulo e lista de arquivos. |
| **Feito** | `[ ]` Windows vê o disco  `[ ]` App mostra "Disco pronto" |

## Passo 1 — CD-R de dados saudável

| | |
|---|---|
| **Preparação** | CD-R de dados com 2 ou mais vídeos. Destino: pasta em um pendrive/HD. |
| **O que fazer** | 1) Coloque o disco. 2) Em **DESTINO**, escolha o pendrive. 3) Confira o **Nome da pasta** sugerido e mude (ex.: "Teste CD 1"). 4) Clique **EXTRAIR DISCO** e espere até o fim. |
| **Resultado esperado** | Aviso sonoro e resumo "Extração concluída". Todos os arquivos **Íntegro** (nenhum Parcial/Falhou). A pasta `<destino>\Teste CD 1\` tem a mesma estrutura do disco, mais `mediarescue-manifest.json`. **Não** há `LEIA-ME - ERROS.txt` nem arquivos `.partial`. O disco é ejetado (se "Ejetar o disco ao concluir" estiver ligado). |
| **Verifique** | Abra um vídeo da pasta de destino no seu player: ele toca do começo ao fim com som. Em *Histórico*, o disco aparece como **Íntegro**. |
| **Opcional (conferir hash)** | Com o disco ainda no leitor (se tiver ejetado, desligue a ejeção): `Get-FileHash D:\<arquivo> -Algorithm SHA256` e `Get-FileHash "<destino>\...\<arquivo>"` devem dar o mesmo valor. |
| **Feito** | `[ ]` Tudo Íntegro  `[ ]` Vídeo abre  `[ ]` Sem .partial / LEIA-ME - ERROS  `[ ]` Entrou no Histórico |

## Passo 2 — DVD-Video caseiro

| | |
|---|---|
| **Preparação** | DVD gravado em gravador de mesa/programa (pasta `VIDEO_TS`). |
| **O que fazer** | Coloque o disco e **antes de extrair** observe a lista de conteúdo. Depois extraia para o pendrive. |
| **Resultado esperado** | Formato mostrado como **DVD-Video**. Os arquivos `VTS_nn_1.VOB…` aparecem agrupados em **Título 1, Título 2…** com a **duração** (somada) e resolução (ex.: 720x480) — pode levar alguns segundos ("Lendo informações dos vídeos…" no topo da lista); a leitura dessas informações **pausa quando a cópia começa**. Arquivos de menu (`VTS_nn_0.VOB`) não contam como vídeo. A pasta `VIDEO_TS` inteira é copiada, tudo **Íntegro**. |
| **Verifique** | A duração mostrada de cada título bate (±1 min) com a duração real no player. No destino, abra `VIDEO_TS\VTS_01_1.VOB` no VLC (ou outro player): toca. |
| **Feito** | `[ ]` Títulos agrupados  `[ ]` Durações corretas  `[ ]` Copiou tudo Íntegro |

## Passo 3 — VCD (se houver)

| | |
|---|---|
| **O que fazer** | Coloque o VCD e extraia. |
| **Resultado esperado** | Formato **VCD** (pasta `MPEGAV` com `AVSEQnn.DAT`). Os `.DAT` são copiados e ficam **Íntegro**. Um VCD é lido em modo diferente de um disco de dados: o app **cai para leitura com buffer** nesses arquivos — só deve ficar um pouco mais lento. |
| **Verifique** | O `AVSEQ01.DAT` do destino abre no VLC. |
| **Feito** | `[ ]` Reconhecido como VCD  `[ ]` .DAT copiados Íntegro  `[ ]` Toca no VLC  `[ ]` Não tenho VCD (pular) |

## Passo 4 — Disco riscado

| | |
|---|---|
| **Preparação** | Disco com arranhões. **Nunca** use o único exemplar de algo insubstituível para este teste de estresse. Em *Configurações* deixe "Reduzir a velocidade do leitor quando houver erros" ligado. |
| **O que fazer** | Extraia normalmente. Durante a cópia observe o contador **Com erro** e **Irrecuperável**. Teste o botão **Pular arquivo** em um arquivo lento. Se ficar muito tempo, use **Cancelar** e confirme que a janela responde. |
| **Resultado esperado** | O app **não trava** (a janela responde a cliques o tempo todo). A barra fica vermelha se houver perdas. Ao fim: arquivos com status **Recuperado** (erro vencido), **Parcial** (com perdas) ou **Falhou**. Para cada Parcial, o resumo diz quantos KB/% faltaram e existe `LEIA-ME - ERROS.txt` na pasta do disco listando os trechos. O tempo de recuperação respeita o limite (20 min por padrão); "Insistir mais" dá +10 min. Arquivos Parcial abrem no player com um "pulo" no ponto perdido. |
| **Verifique** | Abra o `LEIA-ME - ERROS.txt`: cada arquivo Parcial tem seus intervalos perdidos. O tamanho do arquivo no destino é igual ao do original. |
| **Feito** | `[ ]` Terminou sem travar  `[ ]` Relatório de perdas correto  `[ ]` Cancelar/Pular funcionaram  `[ ]` Parcial abre no player |

## Passo 5 — Puxar o cabo USB do leitor no meio da cópia

| | |
|---|---|
| **Preparação** | Disco com vários arquivos ou um grande (DVD de 1 GB+), para ter tempo. |
| **O que fazer** | Inicie a extração. Com ~30% concluído, **puxe o cabo USB do leitor**. Espere 10 s. **Recoloque o cabo** (mesmo leitor, mesmo disco). |
| **Resultado esperado** | O app **pausa** e mostra algo como "Leitor desconectado — reconecte" (sem fechar, sem erro técnico). Ao reconectar com o **mesmo disco**, a cópia **continua do arquivo atual** (o `.partial` desse arquivo é refeito; os já concluídos não são recopiados). No fim, tudo **Íntegro** e o tempo parado não entra no cronômetro. Se desconectar 3 vezes em 2 minutos, aparece a **dica de alimentação USB** (cabo em Y / outra porta). |
| **Variação** | Reconecte com **outro disco**: o app não deve continuar; deve oferecer cancelar ou reinserir o original. |
| **Feito** | `[ ]` Pausou  `[ ]` Retomou sozinho  `[ ]` Sem arquivo incompleto com nome final  `[ ]` Dica de energia após 3 quedas  `[ ]` Outro disco → não continua |

## Passo 6 — Remover o pendrive no meio da cópia

| | |
|---|---|
| **O que fazer** | Inicie uma extração para o **pendrive**. No meio, **retire o pendrive** (sem "remover com segurança" — é o teste). Espere ~10 s. Recoloque **o mesmo pendrive**. |
| **Resultado esperado** | O app **pausa** avisando que o destino sumiu. Ao voltar o **mesmo volume**, valida por tamanho os arquivos já concluídos e **continua**. Se você colocar **outro** pendrive, o app **não** continua: cancela de forma limpa **preservando o estado** para uma retomada posterior. Se você cancelar, nenhum arquivo incompleto fica com nome final. |
| **Atenção** | Retirar um pendrive no meio da gravação pode corromper arquivos **dele**; use um pendrive de teste. Rode o **Verificar** do Windows depois, se quiser. |
| **Feito** | `[ ]` Pausou  `[ ]` Mesmo pendrive → retomou  `[ ]` Outro pendrive → não continua  `[ ]` Cancelar limpo |

## Passo 7 — Pendrive FAT32 quase cheio

| | |
|---|---|
| **Preparação** | (a) Um pendrive FAT32 com **pouco espaço livre**. (b) Se tiver, um disco com **arquivo ≥ 4 GB** (DVD de dados cheio). |
| **O que fazer** | Escolha o pendrive como destino e olhe a linha de status abaixo da lista de unidades **antes** de clicar em Extrair. |
| **Resultado esperado** | (a) Sem espaço: o botão **EXTRAIR DISCO** fica desabilitado **com o motivo ao lado**, dizendo **quanto falta** (ex.: "faltam 1,2 GB"). Desmarcar arquivos libera o botão quando couber. (b) FAT32 + arquivo ≥ 4 GB: **só esse arquivo** é bloqueado, com a explicação "formate o pendrive em exFAT ou escolha outro destino"; os demais continuam copiáveis. |
| **Feito** | `[ ]` Bloqueio por falta de espaço com quanto falta  `[ ]` Bloqueio só do arquivo ≥ 4 GB  `[ ]` Não tenho como testar (b) |

## Passo 8 — Reinserir disco já extraído

| | |
|---|---|
| **O que fazer** | Depois do Passo 1, **ejete e recoloque o mesmo CD**. |
| **Resultado esperado** | Aparece "Este disco já foi extraído em DD/MM/AAAA para `<pasta>`" com **Abrir pasta**, **Extrair de novo** e **Ejetar**. Se você apagar/mover a pasta registrada e recolocar o disco, o aviso diz que a pasta não existe mais (e não oferece abri-la). **Extrair de novo** cria `Nome (2)` e não sobrescreve a anterior. |
| **Retomada (RF-10, bônus)** | Inicie uma extração, **feche o app à força** (Gerenciador de Tarefas → Finalizar tarefa) no meio, abra de novo e recoloque o mesmo disco com o mesmo destino: deve oferecer **Continuar de onde parou**. |
| **Feito** | `[ ]` Aviso de duplicado  `[ ]` Abrir pasta  `[ ]` Pasta inexistente tratada  `[ ]` Extrair de novo → (2)  `[ ]` Retomada após fechar à força |

## Passo 9 — Modo lote com 3 discos

| | |
|---|---|
| **O que fazer** | Ligue **Modo lote**. Coloque o disco 1: digite um nome e aperte **Enter**. Aguarde copiar e ejetar. Repita com os discos 2 e 3. Depois, **Sair do modo lote**. Em uma rodada à parte, coloque **um disco já extraído**. |
| **Resultado esperado** | O app só **pergunta o nome** (com sugestão), copia, ejeta e pede "Insira o próximo disco". O painel lateral lista os 3 discos com o status de cada. Ao sair, mostra o **resumo da sessão**. Com o disco **repetido** (ou destino sem espaço, ou disco protegido), o modo lote **pausa e pergunta** — não copia sozinho. |
| **Feito** | `[ ]` 3 discos em sequência  `[ ]` Pasta distinta para cada  `[ ]` Painel lateral correto  `[ ]` Resumo da sessão  `[ ]` Duplicado pausa |

## Passo 10 — Diagnóstico completo com e sem disco

| | |
|---|---|
| **O que fazer** | (a) **Sem disco**: abra **Testar leitor** e clique **Testar leitor**. (b) **Com disco**: repita. (c) Marque "Teste completo da superfície" e rode com um disco de dados (demora vários minutos). Use **Copiar relatório** e cole no Bloco de Notas. |
| **Resultado esperado** | (a) Itens 1–3 aparecem (leitor detectado, responde a comandos, bandeja). Bandeja: **desligada por padrão** — o item aparece como **Pulado** ("Teste da bandeja desativado para não ejetar o disco que está no leitor"); só se você marcar **Testar também a bandeja** o app ejeta e verifica se a bandeja abre, e no leitor slim a "abrir e fechar" pode ficar como **Não suportado por este leitor** (normal — não é defeito). Os itens que dependem de disco indicam "sem disco" em linguagem simples, sem erro técnico. (b) Reconhece o disco (tipo, estado, sessões), **leitura de amostra de 20 pontos** com velocidade média e mínima. (c) Resultado da superfície com blocos bons/lentos/ruins (em texto). Qualquer ✖/⚠ vem com **"O que fazer"** e as **dicas para leitor USB fino**. O relatório copiado é texto legível. |
| **Atenção** | A caixa **Testar também a bandeja** (desmarcada por padrão) **ejeta o disco** — só marque se o disco não estiver sendo usado. Faça uma rodada com ela desmarcada (o disco deve continuar no leitor) e outra com ela marcada. |
| **Feito** | `[ ]` Sem disco OK  `[ ]` Com disco OK  `[ ]` Amostragem 20 pontos  `[ ]` Superfície  `[ ]` Relatório copiado |

---

## Verificações extras (opcionais, para fechar casos de borda)

| Caso | O que fazer | Resultado esperado | Feito |
|---|---|---|---|
| E-26 suspensão | Durante uma cópia longa, feche a tampa do notebook/force *Suspender* e reabra. | O app **impede suspensão por inatividade** durante a cópia; se suspender mesmo assim, ao voltar trata como queda do leitor (Passo 5) e refaz o arquivo atual. | `[ ]` |
| E-07 disco não finalizado | Disco gravado com sessão aberta (se tiver). | "Disco não finalizado": explica que deve finalizar no aparelho onde foi gravado; **sem** tentativa de cópia bruta. | `[ ]` |
| E-06 disco vazio | CD-R/DVD-R virgem. | "Disco vazio"; botão Extrair desabilitado com motivo. | `[ ]` |
| E-08 CD de áudio | CD de música. | "CD de áudio não suportado nesta versão". | `[ ]` |
| E-09 disco protegido | DVD de filme comercial. | Recusado: "disco comercial protegido, não suportado". Se a detecção não for possível, não assume proteção. | `[ ]` |
| E-23 multissessão | Disco com mais de uma sessão. | Mostra o nº de sessões e avisa que sessões antigas podem ter arquivos não visíveis. | `[ ]` |
| E-29 reprodução automática | Deixe o Windows abrir o disco em outro programa e extraia. | A leitura funciona (abre com compartilhamento); se o disco estiver em uso exclusivo, o app diz qual é o problema. | `[ ]` |
| E-34 segunda instância | Abra o `MediaRescue.exe` duas vezes. | A segunda traz a primeira para frente e fecha. | `[ ]` |
| E-33 sem permissão no dispositivo | (Difícil de provocar.) | Detalhes físicos em "—", e a cópia por sistema de arquivos continua funcionando. | `[ ]` |
| Fechar durante a cópia | Clique no X durante uma cópia. | Pede confirmação; "Ficar" continua copiando. | `[ ]` |

---

## Disco não reconhecido — solução de problemas

**Sintoma conhecido:** o leitor `hp CDDVDW TS-L633M USB Device` (`D:`) aparece no app com o nome e o firmware certos, mas o estado fica **"Sem disco"** (ou "Bandeja aberta") **mesmo com um disco dentro**. O app consulta o Windows (`IOCTL_STORAGE_CHECK_VERIFY` a cada 2 s e avisos de dispositivo): **se o Windows diz "sem mídia", o app também diz** — ele não tem como ler um disco que o sistema não enxerga. Por isso o diagnóstico começa fora do app.

Siga na ordem; pare quando o disco aparecer.

1. **O Explorador de Arquivos mostra o disco?** Em *Este Computador*, `D:` deve mostrar nome/espaço do disco. Se não mostra, o problema é do leitor, do cabo/energia ou do disco — não do app.
2. **Disco no lado certo.** Etiqueta para **cima**; o disco encaixado no pino central (em leitores slim é fácil deixar "solto" sobre o eixo). Feche a bandeja até clicar.
3. **Espere.** Discos gravados em casa podem levar de 30 s a 1 min para "girar" e o app mostra *Lendo disco…* nesse tempo. Depois clique em **Atualizar** (F5).
4. **Teste com outro disco, de preferência prensado** (um CD de música/software de fábrica ou DVD de filme). Se esse é reconhecido e o seu não, o problema é o **disco** (sujo, riscado, mal finalizado). Se nenhum é reconhecido, é o **leitor/energia**.
5. **Energia USB (causa mais comum em leitor slim).** Ligue **direto numa porta do computador** (nunca em hub nem em teclado/monitor). Troque para uma porta **traseira** (placa-mãe). Use **cabo em Y** (duas pontas USB, a segunda só para energia) ou um hub **alimentado**. Sintomas: o leitor não gira, "clica", some e volta no Windows (som de desconectar/conectar).
6. **Tente outra porta e outro cabo.** Alterne entre USB 2.0 e 3.0 — alguns leitores antigos se comportam melhor em uma ou outra. Cabo curto e de boa qualidade.
7. **Gerenciamento de energia do USB.** *Painel de Controle → Opções de Energia → Alterar configurações do plano → Alterar configurações avançadas de energia → Configurações de USB → Suspensão seletiva de USB → **Desabilitado***. Desative também "Permitir que o computador desligue este dispositivo" em *Gerenciador de Dispositivos → Controladores de barramento serial universal → (Hub USB raiz / dispositivo de armazenamento em massa) → Gerenciamento de energia*.
8. **Gerenciador de Dispositivos** (`devmgmt.msc`). Em *Unidades de DVD/CD-ROM* deve haver `hp CDDVDW TS-L633M` sem ícone amarelo. Se tiver aviso: botão direito → *Desinstalar dispositivo*, desconecte o leitor, reinicie o computador e reconecte (o Windows reinstala o driver).
9. **Gerenciamento de Disco** (`diskmgmt.msc`). Na parte de baixo, "CD-ROM 0 / DVD (D:)" deve mostrar o tamanho (ex.: 700 MB) com disco, ou "Sem mídia" sem disco.
10. **Perguntar ao Windows pelo PowerShell** (não precisa de admin):
    ```powershell
    Get-CimInstance Win32_CDROMDrive | Select-Object Caption, Drive, MediaLoaded, MediaType, Status
    Get-PnpDevice -Class CDROM | Select-Object Status, FriendlyName
    ```
    `MediaLoaded : False` com disco dentro significa que o **Windows** não lê a mídia. `Status` diferente de `OK` aponta driver/energia.
11. **Outro programa segurando o leitor.** Feche programas de gravação/DVD (Nero, Windows Media Player, VLC com o disco aberto) e o Explorador; a *Reprodução Automática* pode ter aberto algum.
12. **Rodar o relatório somente-leitura do projeto** (para anexar a um relato de problema): no repositório,
    ```powershell
    $env:MEDIARESCUE_REAL_DRIVE = '1'
    dotnet test tests\MediaRescue.Integration.Tests --filter ReportOnPhysicalDrives --logger "console;verbosity=detailed"
    ```
    Ele lista os leitores, o estado de cada um e (se houver disco legível) alguns arquivos e leituras de teste; **não ejeta, não grava e não copia** o conteúdo do disco.
13. **Em *Testar leitor*** rode o teste com e sem disco e use **Copiar relatório**. Os itens 1 e 2 (leitor detectado / responde a comandos) devem passar; o item 4 ("Reconhece o disco") é o que falha neste sintoma.
14. **Último recurso:** teste o leitor em **outro computador** ou **outro leitor** neste. Leitores slim com muitos anos de uso costumam ter a lente fraca: leem prensados e falham em CD-R/DVD-R gravados em casa. Se o leitor é a causa, um leitor/gravador externo novo costuma resolver.

**Se continuar, anote e guarde:** o texto do relatório de *Testar leitor*, a saída do passo 10, o log mais recente e o resultado do passo 4 (disco prensado reconhecido ou não). Isso separa "defeito do leitor" de "defeito do disco" de "bug do app".
