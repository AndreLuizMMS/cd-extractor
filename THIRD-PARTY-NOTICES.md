# Avisos de software de terceiros / Third-party notices

O Extrator de Mídias (MediaRescue) é distribuído sob a licença MIT (arquivo `LICENSE`).
O pacote de distribuição inclui ou usa os componentes abaixo, cada um sob a sua própria licença.

## FFmpeg e FFprobe (incluídos em `tools\`)

| | |
|---|---|
| Arquivos | `tools\ffmpeg.exe`, `tools\ffprobe.exe` |
| Versão | FFmpeg `n8.1.2-50-g1a748fe2cd` (ramo 8.1) |
| Origem do binário | Build "win64-lgpl" do projeto BtbN/FFmpeg-Builds, release `autobuild-2026-08-31-13-27` |
| Download | <https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-31-13-27> |
| Código-fonte | <https://github.com/FFmpeg/FFmpeg> (commit `1a748fe2cd`) e <https://ffmpeg.org/download.html> |
| Scripts de compilação do build | <https://github.com/BtbN/FFmpeg-Builds> |
| Licença | GNU Lesser General Public License, versão 3 ou posterior (LGPL-3.0-or-later). Texto: <https://www.gnu.org/licenses/lgpl-3.0.html>; também impresso por `ffmpeg.exe -L`. O arquivo `LICENSE.txt` do arquivo ZIP original do BtbN traz o texto completo e as licenças das bibliotecas embutidas. |
| Copyright | © 2000–2026 os desenvolvedores do FFmpeg (<https://ffmpeg.org/>) |

Notas:

* Usa-se a variante **LGPL** (sem componentes GPL ou "nonfree"; por exemplo, sem libx264/libx265).
* O FFmpeg é executado como **programa separado** (`ffprobe.exe` para ler metadados e `ffmpeg.exe` para a conversão opcional para MP4). O Extrator de Mídias não é ligado (linked) a ele. Você pode substituir os dois executáveis em `tools\` por qualquer build compatível do FFmpeg.
* Os binários **não** são versionados neste repositório: `build\fetch-tools.ps1` baixa o arquivo fixado e confere o SHA-256 antes de extrair. O marcador da versão usada fica em `tools\VERSION.txt`.
* "FFmpeg" é marca registrada de Fabrice Bellard, originador do projeto. Este software não é afiliado nem endossado pelo projeto FFmpeg.

## Bibliotecas .NET incluídas no `MediaRescue.exe`

| Componente | Versão | Licença |
|---|---|---|
| .NET 8 Runtime e WPF (autocontido) | 8.0.x | MIT — © .NET Foundation e colaboradores — <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf> |
| CommunityToolkit.Mvvm | 8.4.2 | MIT — © .NET Foundation e colaboradores — <https://github.com/CommunityToolkit/dotnet> |
| Serilog e Serilog.Sinks.File | 4.2.0 / 7.0.0 | Apache-2.0 — © Serilog Contributors — <https://github.com/serilog/serilog> |
| System.Management | 10.0.x | MIT — © .NET Foundation e colaboradores |

## Usados somente nos testes (não vão no pacote de distribuição)

| Componente | Licença |
|---|---|
| xUnit.net (`xunit`, `xunit.runner.visualstudio`) | Apache-2.0 |
| FluentAssertions 6.12.0 | Apache-2.0 |
| LTRData.DiscUtils.Iso9660 (DiscUtils) | MIT — usado para gerar imagens ISO de teste |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## Privacidade

Nenhum desses componentes é usado para acessar a rede. O Extrator de Mídias não faz conexões de rede,
não envia telemetria e não envia dados a ninguém.
