# MDashK Wallpaper Maker

Ferramenta em C# (.NET 8, WinForms) que cria wallpapers 1920x1080 em PNG a partir de imagens.

Para compilar, corre `E:\_MWPM\build.bat`. O executável fica em `E:\_MWPM\Build`, junto com `onnxruntime.dll` e a pasta `Models`.

## Modos de enquadramento

- **Smart framing (AI)**, ativo por defeito. Deteta a personagem anime e o texto nas margens, e escolhe sozinho o zoom e o corte.
- **Regras clássicas (v1).** Usadas quando a opção está desligada ou quando os modelos não existem.
- **Preview / Adjust**, opcional. Mostra o resultado e permite ajustar cada imagem:
  - arrastar com o rato move a imagem;
  - a roda do rato faz zoom;
  - duplo clique repõe o enquadramento automático;
  - o botão **Center** centra a imagem na horizontal, mantendo o zoom e a posição vertical;
  - "Use this framing" (ou qualquer ajuste) mostra uma barra verde: a imagem vai ser processada com o enquadramento manual;
  - a caixa **AI framing** liga ou desliga a IA só para essa imagem.

  Os ajustes ficam marcados como "Manual". O botão **Process** processa sempre a lista toda: as imagens não ajustadas usam o enquadramento automático.

  No fim do processamento, as imagens processadas (e as que já eram 1920x1080) saem da lista. Ficam apenas as que precisam de tratamento manual: quadradas, demasiado pequenas, com erro, ou marcadas com "Skip".

  As originais não processadas (incluindo as que já eram 1920x1080) são movidas para `<pasta selecionada>\not_processed\<subpasta>`. Os ajustes feitos na pré-visualização acompanham o ficheiro. Se depois forem processadas a partir daí, o resultado vai para o sítio normal em `processed_output`. A pasta `not_processed` é ignorada quando se lê uma pasta com subpastas.
- **IA por imagem:** a caixa na lista (coluna "AI") desliga a IA só para essa imagem, que passa a usar as regras clássicas. A coluna "Framing" mostra o estado: Auto (AI), Auto (classic), Manual ou Skip.

## Aprendizagem (Learning)

Tudo fica na pasta `config`, junto do executável. Para levar a aplicação para outra máquina, basta copiar a pasta inteira.

| Ficheiro | Conteúdo |
|---|---|
| `config\settings.json` | opções da janela |
| `config\history.jsonl` | exemplos registados: deteções, enquadramento automático e enquadramento final, sem imagens |
| `config\calibration.json` | calibração aprendida; se não existir, usa a original |

**O que conta como exemplo** (só depois de a imagem ser processada):
- imagens ajustadas na pré-visualização ("manual");
- imagens vistas na pré-visualização e mantidas como estavam ("approved").

**Como aprende:**
- A partir de 10 exemplos corrige a tendência (espaço acima da cabeça e zoom).
- A partir de 30 recalibra todos os parâmetros, juntando as 30 imagens de referência.
- Só adota uma calibração nova se ela se aproximar mais das tuas escolhas, com validação cruzada.
- Por defeito tenta aprender após cada processamento em que haja pelo menos 5 exemplos novos.

O botão **Learning** mostra o estado e tem as opções "Recalibrate now", "Reset to original calibration" e "Open config folder".

## Resolução do wallpaper

O campo **Wallpaper size** tem predefinições (1920x1080, 1366x768, 2560x1440, 4K, 16:10, 21:9…) e aceita qualquer valor escrito, por exemplo `1600x900`. Todas as regras escalam com a resolução escolhida:

- **Pequena demais:** a imagem é recusada quando é menor do que a resolução escolhida nas duas dimensões. Uma imagem de 700x900 é recusada para 1920x1080, mas é processada para 1366x768.
- **Já na resolução:** uma imagem que já tenha o tamanho escolhido é ignorada.
- **Blur dos espelhos:** é 4 px a 1080 linhas e proporcional nas outras resoluções (2,8 px a 768, 8 px a 2160).
- **Regras clássicas:** os limites de "cortar em vez de redimensionar" (1200 e 2100 a 1080p) também escalam.
- **Ajustes manuais:** um ajuste feito para outra resolução é convertido automaticamente.
- **Aprendizagem:** cada exemplo do histórico guarda a resolução em que foi feito.

Os resultados vão para uma pasta com a resolução no nome, para que processamentos em resoluções diferentes nunca se substituam:

- **16:9** usa a altura: `processed_output_1080`, `processed_output_768`, `processed_output_2160`…
- **Outras proporções** usam o tamanho completo: `processed_output_2560x1080`, `processed_output_1920x1200`…

## waifu2x (ampliar imagens pequenas demais)

- **Janela principal:**
  - **Upscale** define o fator: 2x, 4x, 6x ou 8x.
  - O botão **waifu2x…** abre as opções: modelo, redução de ruído (nenhuma ou 0–3), dispositivo (Auto/GPU/CPU), tamanho do bloco e **Test GPU**, que confirma que a GPU dá o mesmo resultado que o processador.
- **Regras:**
  - Na **Preview / Adjust**, qualquer imagem pode ser ampliada: é obrigatório nas **pequenas demais** e opcional nas outras (por exemplo, para ganhar detalhe ao fazer zoom).
  - Nunca corre no Process automático: essas imagens continuam a ser recusadas (e movidas para `not_processed`), com a indicação do waifu2x.
  - A ampliação faz-se na **Preview / Adjust**: escolher o fator e carregar em "Upscale with waifu2x".
  - A versão ampliada fica sempre com **enquadramento manual** (barra verde), e o Process usa-a. O "Undo waifu2x" volta à original.
  - Uma imagem já ampliada pode ser ampliada de novo com outro fator; a ampliação parte sempre da original.
- **Modelos** (oficiais do waifu2x, projeto nagadomi/nunif, pasta `Models\waifu2x`):
  - `swin_unet/art`: melhor qualidade, com modelos 2x e 4x.
  - `cunet/art`: mais rápido, só 2x.
  - 6x é feito com 4x + 2x, seguido de uma redução de qualidade para 6x. 8x é feito com 4x + 2x.
- **GPU:** usa o DirectML, que funciona em qualquer placa DirectX 12; sem GPU compatível, corre no processador.
  - **Escolha da placa:** em **Auto**, as placas são experimentadas da que tem mais memória dedicada para a que tem menos, por isso a dedicada vem antes da integrada (ex.: NVIDIA antes de Intel num portátil). Nas opções também se pode escolher uma placa pelo nome.
  - **Validação:** antes de usar uma placa com um modelo, a aplicação corre um bloco na GPU e no processador e compara os dois resultados. Uma placa que não arranque, que bloqueie ou que dê valores errados não volta a ser usada até a aplicação fechar.
  - **Falha a meio:** se a GPU falhar durante uma ampliação (reinício do driver, memória de vídeo esgotada…), a ampliação termina no processador e o aviso aparece na pré-visualização.
  - **Test GPUs:** nas opções, testa todas as placas com o modelo escolhido e mostra o resultado de cada uma.
  - Os modelos foram adaptados uma vez para o DirectML: os `Pad` com valores negativos, que são cortes de margem e que o DirectML da RX 580 calculava como zeros, foram substituídos por `Slice` equivalentes. A GPU e o processador dão o mesmo resultado (diferença ~0,000001).
  - Usa-se o ONNX Runtime DirectML 1.22.1, porque a 1.24 não aceitava a RX 580.
- **Ficheiros temporários:** as versões ampliadas ficam em `%TEMP%\MDashK Wallpaper Maker\waifu2x` e são apagadas ao fechar a aplicação.

## Composição (v2)

A imagem é escalada e colocada na tela de 1920x1080.

- Onde não cobre a largura, é preenchida com cópias em espelho alternadas (espelho, normal, espelho…), com Gaussian Blur de raio 4 px e encostadas à imagem.
- Imagens baixas e largas (altura < 1080 e largura ≥ 1920) mantêm o layout v1: imagem em cima e espelho com blur em baixo.

## Calibração do enquadramento automático

Os valores vêm da análise das 30 imagens feitas à mão em `_Processed_v2` (`SmartFramer.cs`):

| Regra | Valor |
|---|---|
| Topo do corte | topo da cabeça + 3% da altura do corte |
| Altura do corte | 1.3 × (fundo do meio-corpo − topo da cabeça) |
| Zoom | puxado 25% para o zoom típico (x1.14); limitado entre x1.0 e x2.2 |
| Resolução | nunca amplia acima da resolução nativa |
| Texto / marcas de água a menos de 6% de uma margem | cortados se bastar até +12% de zoom |
| Horizontal | centrado; em paisagens largas centra no meio-corpo |
| Cabeças secundárias (ex.: chibis em baixo) | ignoradas se o topo estiver abaixo de 70% da altura |

Resultado em validação, medido como sobreposição entre o corte automático e o corte feito à mão:
- média 0.90 (as regras v1 davam 0.82);
- 27 das 30 imagens com ≥80%;
- 19 das 30 imagens com ≥90%.

## Modelos (deepghs, HuggingFace)

| Pasta | Modelo | Uso |
|---|---|---|
| `Models/head` | anime_head_detection / head_detect_v2.0_s | cabeças |
| `Models/face` | anime_face_detection / face_detect_v1.4_s | caras (alternativa quando não há cabeça) |
| `Models/halfbody` | anime_halfbody_detection / halfbody_detect_v1.0_s | meio-corpo (zoom) |
| `Models/text` | paddleocr / det/ch_PP-OCRv4_det | texto, logótipos e marcas de água |
